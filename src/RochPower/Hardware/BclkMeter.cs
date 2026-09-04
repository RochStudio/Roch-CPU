using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RochPower.Hardware;

/// <summary>
/// Measures the real base clock by comparing the CPU time-stamp counter (which runs
/// at BCLK x base ratio) against the ACPI power-management timer (a fixed 3.579545 MHz
/// reference that does not move with BCLK). This is how monitoring tools report a
/// BCLK such as 100.01 MHz instead of assuming 100.
/// </summary>
public sealed class BclkMeter : IDisposable
{
    private const double PmTimerHz = 3_579_545.0;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong RdtscDelegate();

    private readonly IKernelDriver _drv;
    private readonly IntPtr _code;
    private readonly RdtscDelegate _rdtsc;
    private readonly ushort _pmTimerPort;
    private readonly bool _timer32Bit;

    public bool IsAvailable => _pmTimerPort != 0;
    public string Status { get; }

    public BclkMeter(IKernelDriver drv)
    {
        _drv = drv;
        // x64: rdtsc ; shl rdx,32 ; or rax,rdx ; ret
        byte[] code = { 0x0F, 0x31, 0x48, 0xC1, 0xE2, 0x20, 0x48, 0x09, 0xD0, 0xC3 };
        _code = Native.VirtualAlloc(IntPtr.Zero, (UIntPtr)(uint)code.Length, Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_EXECUTE_READWRITE);
        if (_code == IntPtr.Zero) throw new InvalidOperationException("VirtualAlloc failed for the rdtsc stub.");
        Marshal.Copy(code, 0, _code, code.Length);
        _rdtsc = Marshal.GetDelegateForFunctionPointer<RdtscDelegate>(_code);

        (_pmTimerPort, _timer32Bit, Status) = LocatePmTimer();
    }

    private static (ushort port, bool ext, string status) LocatePmTimer()
    {
        byte[]? fadt = FirmwareTables.Get(FirmwareTables.ACPI, FirmwareTables.Signature("FACP"));
        if (fadt == null || fadt.Length < 80) return (0, false, "ACPI FADT not available");
        uint pmTmrBlk = BitConverter.ToUInt32(fadt, 76);
        uint flags = fadt.Length >= 116 ? BitConverter.ToUInt32(fadt, 112) : 0;
        bool ext = ((flags >> 8) & 1) == 1;
        if (pmTmrBlk == 0 && fadt.Length >= 220)
        {
            // X_PM_TMR_BLK generic address structure at 208: space id, bit width, bit offset, access size, address (8 bytes)
            byte spaceId = fadt[208];
            ulong addr = BitConverter.ToUInt64(fadt, 212);
            if (spaceId == 1 && addr != 0 && addr < 0x10000) pmTmrBlk = (uint)addr;
        }
        if (pmTmrBlk == 0 || pmTmrBlk > 0xFFFF) return (0, ext, "ACPI PM timer not in I/O space");
        return ((ushort)pmTmrBlk, ext, $"ACPI PM timer at I/O 0x{pmTmrBlk:X4}");
    }

    private uint ReadPmTimer()
    {
        uint v = _drv.ReadIoPortDword(_pmTimerPort);
        return _timer32Bit ? v : v & 0xFFFFFF;
    }

    /// <summary>Returns the measured TSC frequency in Hz over roughly <paramref name="sampleMs"/> milliseconds.</summary>
    public double MeasureTscHz(int sampleMs = 60, int cpu = 0)
    {
        if (!IsAvailable) throw new InvalidOperationException(Status);
        uint mask = _timer32Bit ? 0xFFFFFFFF : 0xFFFFFF;
        uint targetTicks = (uint)(PmTimerHz * sampleMs / 1000.0);
        return WinRing0Driver.RunOnCpu(cpu, () =>
        {
            var old = Thread.CurrentThread.Priority;
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            try
            {
                // Warm up the driver path so the first sample is not slower than the last one.
                for (int i = 0; i < 4; i++) ReadPmTimer();

                // Bracket every port read with rdtsc and use the midpoint, so IOCTL latency cancels out.
                ulong a = _rdtsc();
                uint p0 = ReadPmTimer();
                ulong b = _rdtsc();
                ulong t0 = a + (b - a) / 2;
                uint p1; ulong t1;
                var guard = Stopwatch.StartNew();
                do
                {
                    a = _rdtsc();
                    p1 = ReadPmTimer();
                    b = _rdtsc();
                    t1 = a + (b - a) / 2;
                    if (guard.ElapsedMilliseconds > sampleMs * 5 + 200) throw new TimeoutException("ACPI PM timer did not advance.");
                } while (((p1 - p0) & mask) < targetTicks);
                double seconds = ((p1 - p0) & mask) / PmTimerHz;
                return (t1 - t0) / seconds;
            }
            finally { Thread.CurrentThread.Priority = old; }
        });
    }

    /// <summary>BCLK in MHz = TSC / base ratio.</summary>
    public double MeasureBclkMHz(int baseRatio, int sampleMs = 60)
    {
        if (baseRatio <= 0) throw new ArgumentOutOfRangeException(nameof(baseRatio));
        return MeasureTscHz(sampleMs) / baseRatio / 1_000_000.0;
    }

    public void Dispose()
    {
        if (_code != IntPtr.Zero) Native.VirtualFree(_code, UIntPtr.Zero, Native.MEM_RELEASE);
    }
}
