using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace RochPower.Hardware;

/// <summary>
/// Client for namazso's PawnIO driver (https://pawnio.eu): a signed kernel driver that runs
/// signed, sandboxed "modules" and exposes each module's functions by name. It is optional
/// here. WinRing0 does everything except read the AMD SMU power table, which needs a physical
/// memory mapping the WinRing0 build cannot do; when PawnIO is installed the bundled RyzenSMU
/// module reads that table in the kernel. Nothing is installed by Roch CPU: the user installs
/// PawnIO themselves, and the module blob is loaded into the already-running service.
/// </summary>
public sealed class PawnIo : IDisposable
{
    private const string DevicePath = @"\\?\GLOBALROOT\Device\PawnIO";
    private const uint DEVICE_TYPE = 41394u << 16;
    private const uint IOCTL_LOAD_BINARY = DEVICE_TYPE | (0x821 << 2);
    private const uint IOCTL_EXECUTE_FN = DEVICE_TYPE | (0x841 << 2);
    private const int FN_NAME_LENGTH = 32;

    private readonly SafeFileHandle _handle;
    private readonly object _lock = new();

    public string? LastError { get; private set; }

    private PawnIo(SafeFileHandle handle) => _handle = handle;

    /// <summary>Version of the installed PawnIO, from its uninstall entry; null when it is not installed.</summary>
    public static Version? InstalledVersion
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
                return key?.GetValue("DisplayVersion") is string s && Version.TryParse(s, out var v) ? v : null;
            }
            catch { return null; }
        }
    }

    public static bool IsInstalled => InstalledVersion != null;

    /// <summary>Opens the driver and loads one signed module from a file. Throws with the reason if either step fails.</summary>
    public static PawnIo LoadModule(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("PawnIO module not found.", path);
        var h = Native.CreateFile(DevicePath, Native.GENERIC_READ | Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            throw new IOException($"PawnIO device not reachable ({new System.ComponentModel.Win32Exception(err).Message}); is the PawnIO service running?");
        }
        byte[] bin = File.ReadAllBytes(path);
        bool ok;
        unsafe
        {
            fixed (byte* p = bin) ok = Native.DeviceIoControl(h, IOCTL_LOAD_BINARY, p, (uint)bin.Length, null, 0, out _, IntPtr.Zero);
        }
        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            throw new IOException($"PawnIO refused the module {Path.GetFileName(path)} ({new System.ComponentModel.Win32Exception(err).Message}). Only modules signed for PawnIO load; try a newer PawnIO.");
        }
        return new PawnIo(h);
    }

    /// <summary>
    /// Calls a module function: 32-byte name followed by the input longs; the output is a run of
    /// longs. Returns null on failure (LastError says why) so callers can fall back.
    /// </summary>
    public long[]? Execute(string function, long[] input, int outputCount)
    {
        var inBuf = new byte[FN_NAME_LENGTH + input.Length * 8];
        var name = Encoding.ASCII.GetBytes(function);
        Buffer.BlockCopy(name, 0, inBuf, 0, Math.Min(FN_NAME_LENGTH - 1, name.Length));
        Buffer.BlockCopy(input, 0, inBuf, FN_NAME_LENGTH, input.Length * 8);
        var outBuf = new byte[outputCount * 8];
        bool ok; uint read;
        lock (_lock)
        {
            unsafe
            {
                fixed (byte* pi = inBuf) fixed (byte* po = outBuf)
                    ok = Native.DeviceIoControl(_handle, IOCTL_EXECUTE_FN, pi, (uint)inBuf.Length, po, (uint)outBuf.Length, out read, IntPtr.Zero);
            }
            if (!ok) { LastError = $"{function}: {new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message}"; return null; }
        }
        var result = new long[Math.Min(outputCount, (int)(read / 8))];
        Buffer.BlockCopy(outBuf, 0, result, 0, result.Length * 8);
        return result;
    }

    public void Dispose() => _handle.Dispose();
}
