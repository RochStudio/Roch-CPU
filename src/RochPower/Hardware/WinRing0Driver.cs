using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RochPower.Hardware;

/// <summary>
/// Client for the OpenLibSys WinRing0 1.2 kernel driver (WinRing0x64.sys).
/// The driver is installed as a demand-start service the first time it is
/// needed and removed again when this object is disposed, unless it was
/// already running (another monitoring tool may own it).
/// </summary>
public sealed unsafe class WinRing0Driver : IKernelDriver
{
    private const string DeviceName = @"\\.\WinRing0_1_2_0";
    private const string ServiceName = "RochCpuRing0";
    private const string DriverFile = "WinRing0x64.sys";

    private const uint OLS_TYPE = 40000;
    private static uint CtlCode(uint function, uint access) => (OLS_TYPE << 16) | (access << 14) | (function << 2);
    private const uint FILE_ANY_ACCESS = 0, FILE_READ_ACCESS = 1, FILE_WRITE_ACCESS = 2;

    private static readonly uint IOCTL_GET_DRIVER_VERSION = CtlCode(0x800, FILE_ANY_ACCESS);
    private static readonly uint IOCTL_READ_MSR = CtlCode(0x821, FILE_ANY_ACCESS);
    private static readonly uint IOCTL_WRITE_MSR = CtlCode(0x822, FILE_ANY_ACCESS);
    private static readonly uint IOCTL_READ_IO_PORT_BYTE = CtlCode(0x833, FILE_READ_ACCESS);
    private static readonly uint IOCTL_READ_IO_PORT_WORD = CtlCode(0x834, FILE_READ_ACCESS);
    private static readonly uint IOCTL_READ_IO_PORT_DWORD = CtlCode(0x835, FILE_READ_ACCESS);
    private static readonly uint IOCTL_WRITE_IO_PORT_BYTE = CtlCode(0x836, FILE_WRITE_ACCESS);
    private static readonly uint IOCTL_WRITE_IO_PORT_WORD = CtlCode(0x837, FILE_WRITE_ACCESS);
    private static readonly uint IOCTL_WRITE_IO_PORT_DWORD = CtlCode(0x838, FILE_WRITE_ACCESS);
    private static readonly uint IOCTL_READ_PCI_CONFIG = CtlCode(0x851, FILE_READ_ACCESS);
    private static readonly uint IOCTL_WRITE_PCI_CONFIG = CtlCode(0x852, FILE_WRITE_ACCESS);

    private SafeFileHandle? _handle;
    private bool _installedByUs;
    private readonly object _ioLock = new();

    public string Name => "WinRing0 1.2";
    public bool IsOpen => _handle is { IsInvalid: false, IsClosed: false };
    public string? LastError { get; private set; }

    /// <summary>Opens the driver, installing it from the application directory if needed.</summary>
    public static WinRing0Driver Open()
    {
        var d = new WinRing0Driver();
        if (d.TryOpenDevice())
        {
            // A previous instance that was killed (task manager, crash) leaves our service behind.
            // Adopt it so it is removed again when this instance exits - but only if nothing else
            // is using it, or a CLI probe run alongside the window would unload the driver under it.
            d._installedByUs = OurServiceExists() && IsOnlyInstance();
            return d;
        }

        string path = Path.Combine(AppContext.BaseDirectory, DriverFile);
        if (!File.Exists(path))
            throw new FileNotFoundException($"{DriverFile} was not found next to the application. It is required for MSR / port / PCI access.", path);

        d.InstallAndStart(path);
        if (!d.TryOpenDevice())
            throw new InvalidOperationException($"The WinRing0 driver was installed but the device {DeviceName} could not be opened. " +
                                                "Another driver may be blocking it, or Windows (Core Isolation / Memory Integrity, or Defender) refused to load it. " +
                                                $"Last error: {d.LastError}");
        return d;
    }

    /// <summary>True when no other copy of this program is running, so the service is ours to remove.</summary>
    private static bool IsOnlyInstance()
    {
        try
        {
            using var me = System.Diagnostics.Process.GetCurrentProcess();
            return System.Diagnostics.Process.GetProcessesByName(me.ProcessName).Length <= 1;
        }
        catch { return false; }
    }

    private static bool OurServiceExists()
    {
        IntPtr scm = Native.OpenSCManager(null, null, Native.SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero) return false;
        try
        {
            IntPtr svc = Native.OpenService(scm, ServiceName, Native.SERVICE_ALL_ACCESS);
            if (svc == IntPtr.Zero) return false;
            Native.CloseServiceHandle(svc);
            return true;
        }
        finally { Native.CloseServiceHandle(scm); }
    }

    private bool TryOpenDevice()
    {
        var h = Native.CreateFile(DeviceName, Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        if (h.IsInvalid)
        {
            LastError = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            h.Dispose();
            return false;
        }
        _handle = h;
        return true;
    }

    private void InstallAndStart(string driverPath)
    {
        IntPtr scm = Native.OpenSCManager(null, null, Native.SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager failed (run as administrator).");
        try
        {
            IntPtr svc = Native.CreateService(scm, ServiceName, "Roch CPU ring-0 driver", Native.SERVICE_ALL_ACCESS,
                Native.SERVICE_KERNEL_DRIVER, Native.SERVICE_DEMAND_START, Native.SERVICE_ERROR_NORMAL, driverPath,
                null, IntPtr.Zero, null, null, null);
            if (svc == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == Native.ERROR_SERVICE_EXISTS || err == Native.ERROR_SERVICE_MARKED_FOR_DELETE)
                {
                    svc = Native.OpenService(scm, ServiceName, Native.SERVICE_ALL_ACCESS);
                    if (svc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenService failed.");
                }
                else throw new Win32Exception(err, "CreateService failed for the WinRing0 driver.");
            }
            else _installedByUs = true;

            try
            {
                if (!Native.StartService(svc, 0, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != Native.ERROR_SERVICE_ALREADY_RUNNING)
                    {
                        var ex = new Win32Exception(err, "StartService failed for the WinRing0 driver. " +
                            "Windows may have blocked the driver (Memory Integrity / vulnerable driver blocklist) or Defender quarantined WinRing0x64.sys.");
                        if (_installedByUs) { Native.DeleteService(svc); _installedByUs = false; }
                        throw ex;
                    }
                }
            }
            finally { Native.CloseServiceHandle(svc); }
        }
        finally { Native.CloseServiceHandle(scm); }
    }

    public uint GetDriverVersion()
    {
        uint version = 0;
        Ioctl(IOCTL_GET_DRIVER_VERSION, null, 0, &version, 4);
        return version;
    }

    // ------------------------------------------------------------------ MSR
    public bool ReadMsr(uint index, out ulong value, int cpu = -1)
    {
        ulong v = 0;
        bool ok = RunOnCpu(cpu, () =>
        {
            uint idx = index; ulong outv = 0;
            bool r = Ioctl(IOCTL_READ_MSR, &idx, 4, &outv, 8);
            v = outv;
            return r;
        });
        value = v;
        return ok;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WriteMsrInput { public uint Register; public ulong Value; }

    public bool WriteMsr(uint index, ulong value, int cpu = -1)
    {
        return RunOnCpu(cpu, () =>
        {
            var input = new WriteMsrInput { Register = index, Value = value };
            return Ioctl(IOCTL_WRITE_MSR, &input, (uint)sizeof(WriteMsrInput), null, 0);
        });
    }

    // ------------------------------------------------------------ port I/O
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WriteIoPortInput { public uint Port; public uint Value; }

    private uint ReadPort(uint code, ushort port)
    {
        uint p = port, v = 0;
        if (!Ioctl(code, &p, 4, &v, 4)) throw new IOException($"Port read 0x{port:X} failed: {LastError}");
        return v;
    }

    private void WritePort(uint code, ushort port, uint value)
    {
        var input = new WriteIoPortInput { Port = port, Value = value };
        if (!Ioctl(code, &input, (uint)sizeof(WriteIoPortInput), null, 0)) throw new IOException($"Port write 0x{port:X} failed: {LastError}");
    }

    public byte ReadIoPortByte(ushort port) => (byte)ReadPort(IOCTL_READ_IO_PORT_BYTE, port);
    public ushort ReadIoPortWord(ushort port) => (ushort)ReadPort(IOCTL_READ_IO_PORT_WORD, port);
    public uint ReadIoPortDword(ushort port) => ReadPort(IOCTL_READ_IO_PORT_DWORD, port);
    public void WriteIoPortByte(ushort port, byte value) => WritePort(IOCTL_WRITE_IO_PORT_BYTE, port, value);
    public void WriteIoPortWord(ushort port, ushort value) => WritePort(IOCTL_WRITE_IO_PORT_WORD, port, value);
    public void WriteIoPortDword(ushort port, uint value) => WritePort(IOCTL_WRITE_IO_PORT_DWORD, port, value);

    // ------------------------------------------------------------ PCI config
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct ReadPciInput { public uint PciAddress; public uint Offset; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WritePciInput { public uint PciAddress; public uint Offset; public uint Data; }

    private static uint PciAddress(byte bus, byte device, byte function) => ((uint)bus << 8) | ((uint)(device & 0x1F) << 3) | (uint)(function & 7);

    public bool ReadPciConfig(byte bus, byte device, byte function, ushort offset, out uint value)
    {
        var input = new ReadPciInput { PciAddress = PciAddress(bus, device, function), Offset = offset };
        uint v = 0;
        bool ok = Ioctl(IOCTL_READ_PCI_CONFIG, &input, (uint)sizeof(ReadPciInput), &v, 4);
        value = v;
        return ok && v != 0xFFFFFFFF;
    }

    public bool WritePciConfig(byte bus, byte device, byte function, ushort offset, uint value)
    {
        var input = new WritePciInput { PciAddress = PciAddress(bus, device, function), Offset = offset, Data = value };
        return Ioctl(IOCTL_WRITE_PCI_CONFIG, &input, (uint)sizeof(WritePciInput), null, 0);
    }

    // ------------------------------------------------------------- plumbing
    private bool Ioctl(uint code, void* input, uint inSize, void* output, uint outSize)
    {
        if (!IsOpen) { LastError = "driver not open"; return false; }
        lock (_ioLock)
        {
            bool ok = Native.DeviceIoControl(_handle!, code, input, inSize, output, outSize, out _, IntPtr.Zero);
            if (!ok) LastError = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return ok;
        }
    }

    /// <summary>Pins the calling thread to one logical CPU for the duration of <paramref name="action"/>.</summary>
    public static T RunOnCpu<T>(int cpu, Func<T> action)
    {
        if (cpu < 0 || cpu >= 64) return action();
        Thread.BeginThreadAffinity();
        UIntPtr previous = Native.SetThreadAffinityMask(Native.GetCurrentThread(), (UIntPtr)(1UL << cpu));
        try { return action(); }
        finally
        {
            if (previous != UIntPtr.Zero) Native.SetThreadAffinityMask(Native.GetCurrentThread(), previous);
            Thread.EndThreadAffinity();
        }
    }

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
        if (!_installedByUs) return;
        _installedByUs = false;
        if (!IsOnlyInstance()) return; // another copy started while we ran; leave the driver loaded
        IntPtr scm = Native.OpenSCManager(null, null, Native.SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero) return;
        try
        {
            IntPtr svc = Native.OpenService(scm, ServiceName, Native.SERVICE_ALL_ACCESS);
            if (svc == IntPtr.Zero) return;
            try
            {
                var status = new Native.SERVICE_STATUS();
                Native.ControlService(svc, Native.SERVICE_CONTROL_STOP, ref status);
                Native.DeleteService(svc);
            }
            finally { Native.CloseServiceHandle(svc); }
        }
        finally { Native.CloseServiceHandle(scm); }
    }
}
