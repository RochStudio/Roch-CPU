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

    private const uint MSR_IA32_MPERF = 0xE7, MSR_IA32_APERF = 0xE8, MSR_IA32_PERF_STATUS = 0x198;

    /// <summary>
    /// BCLK measured from the core's own clock instead of the TSC.
    ///
    /// This exists because measuring the TSC is wrong on this platform. Since Skylake the TSC is
    /// driven by a fixed reference and its rate does NOT follow a runtime BCLK change, so the
    /// TSC method reports the boot-time BCLK for ever and silently misses every adjustment.
    /// APERF/MPERF give the real core frequency as a ratio against the TSC, and dividing that by
    /// the multiplier the core is actually running gives the true BCLK.
    /// </summary>
    public double? MeasureBclkFromCore(int cpuIndex, int sampleMs = 200)
    {
        if (!IsAvailable) return null;
        double tscHz = MeasureTscHz(60, cpuIndex);
        return WinRing0Driver.RunOnCpu(cpuIndex, () =>
        {
            var old = Thread.CurrentThread.Priority;
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            try
            {
                // Hold the core busy so it settles on a steady turbo ratio; an idle core parks at
                // a low multiplier and the division below would then be against the wrong number.
                double x = 1.0001;
                var warm = Stopwatch.StartNew();
                while (warm.ElapsedMilliseconds < 60) x = Math.Sqrt(x * 1.000001 + 1e-9);

                if (!_drv.ReadMsr(MSR_IA32_APERF, out ulong a0) || !_drv.ReadMsr(MSR_IA32_MPERF, out ulong m0)) return (double?)null;
                var ratios = new List<int>();
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < sampleMs)
                {
                    for (int i = 0; i < 20000; i++) x = Math.Sqrt(x * 1.000001 + 1e-9);
                    if (_drv.ReadMsr(MSR_IA32_PERF_STATUS, out ulong ps)) ratios.Add((int)((ps >> 8) & 0xFF));
                }
                if (!_drv.ReadMsr(MSR_IA32_APERF, out ulong a1) || !_drv.ReadMsr(MSR_IA32_MPERF, out ulong m1)) return (double?)null;
                GC.KeepAlive(x); // the busy loop must not be optimised away

                double dA = a1 - a0, dM = m1 - m0;
                if (dM <= 0 || dA <= 0 || ratios.Count == 0) return (double?)null;
                double ratio = ratios.Average();
                if (ratio < 4) return (double?)null;
                double coreHz = tscHz * (dA / dM);
                return coreHz / ratio / 1_000_000.0;
            }
            finally { Thread.CurrentThread.Priority = old; }
        });
    }

    /// <summary>
    /// Millions of fixed loop iterations completed per second, timed by the ACPI power-management
    /// timer. That crystal runs at 3.579545 MHz regardless of BCLK, the TSC or any performance
    /// counter, so this measures how fast the core is genuinely executing without trusting any of
    /// them. Compare two runs: a real clock change moves this number proportionally.
    /// </summary>
    public double MeasureWorkRate(int cpuIndex, int sampleMs = 300)
    {
        uint mask = _timer32Bit ? 0xFFFFFFFF : 0xFFFFFF;
        uint targetTicks = (uint)(PmTimerHz * sampleMs / 1000.0);
        return WinRing0Driver.RunOnCpu(cpuIndex, () =>
        {
            var old = Thread.CurrentThread.Priority;
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            try
            {
                double x = 1.0001;
                // warm up so the core is at its turbo ratio before the timed section
                for (int i = 0; i < 3_000_000; i++) x = x * 1.0000001 + 1e-9;

                uint p0 = ReadPmTimer();
                long iterations = 0;
                uint p1;
                do
                {
                    for (int i = 0; i < 200_000; i++) x = x * 1.0000001 + 1e-9;
                    iterations += 200_000;
                    p1 = ReadPmTimer();
                } while (((p1 - p0) & mask) < targetTicks);
                GC.KeepAlive(x);
                double seconds = ((p1 - p0) & mask) / PmTimerHz;
                return iterations / seconds / 1_000_000.0;
            }
            finally { Thread.CurrentThread.Priority = old; }
        });
    }

    private const uint IA32_FIXED_CTR1 = 0x30A, IA32_FIXED_CTR_CTRL = 0x38D, IA32_PERF_GLOBAL_CTRL = 0x38F;

    /// <summary>
    /// BCLK from the core's unhalted cycle counter, timed by the ACPI timer.
    ///
    /// The obvious measurements are all blind to a runtime BCLK change on this platform, which is
    /// why they were wrong: the TSC runs from a fixed crystal and does not follow BCLK, and
    /// MPERF increments in proportion to BCLK just as APERF does, so their ratio cancels. The
    /// fixed-function counter CPU_CLK_UNHALTED.CORE counts real core clocks, so dividing it by
    /// elapsed ACPI-timer seconds gives the true core frequency, and that over the running
    /// multiplier is the true BCLK. The counter only advances while the core is unhalted, hence
    /// the busy loop.
    /// </summary>
    public double? MeasureBclkFromCycles(int cpuIndex, int sampleMs = 150)
    {
        if (!IsAvailable) return null;
        uint mask = _timer32Bit ? 0xFFFFFFFF : 0xFFFFFF;
        uint targetTicks = (uint)(PmTimerHz * sampleMs / 1000.0);
        return WinRing0Driver.RunOnCpu(cpuIndex, () =>
        {
            if (!_drv.ReadMsr(IA32_FIXED_CTR_CTRL, out ulong ctrlOld) ||
                !_drv.ReadMsr(IA32_PERF_GLOBAL_CTRL, out ulong globalOld)) return (double?)null;
            var old = Thread.CurrentThread.Priority;
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            try
            {
                // fixed counter 1 = CPU_CLK_UNHALTED.CORE; bits 7:4 are its control, OS+USR = 0x3
                _drv.WriteMsr(IA32_FIXED_CTR_CTRL, (ctrlOld & ~0xF0UL) | 0x30UL);
                _drv.WriteMsr(IA32_PERF_GLOBAL_CTRL, globalOld | (1UL << 33));

                double x = 1.0001;
                for (int i = 0; i < 500_000; i++) x = x * 1.0000001 + 1e-9;   // reach turbo

                if (!_drv.ReadMsr(IA32_FIXED_CTR1, out ulong c0)) return (double?)null;
                uint p0 = ReadPmTimer();
                var ratios = new List<int>();
                uint p1;
                do
                {
                    for (int i = 0; i < 50_000; i++) x = x * 1.0000001 + 1e-9;
                    if (_drv.ReadMsr(MSR_IA32_PERF_STATUS, out ulong ps)) ratios.Add((int)((ps >> 8) & 0xFF));
                    p1 = ReadPmTimer();
                } while (((p1 - p0) & mask) < targetTicks);
                if (!_drv.ReadMsr(IA32_FIXED_CTR1, out ulong c1)) return (double?)null;
                GC.KeepAlive(x);

                double seconds = ((p1 - p0) & mask) / PmTimerHz;
                double cycles = c1 - c0;
                if (cycles <= 0 || seconds <= 0 || ratios.Count == 0) return (double?)null;
                double ratio = ratios.Average();
                if (ratio < 4) return (double?)null;
                return cycles / seconds / ratio / 1_000_000.0;
            }
            finally
            {
                _drv.WriteMsr(IA32_PERF_GLOBAL_CTRL, globalOld);   // leave the counters as we found them
                _drv.WriteMsr(IA32_FIXED_CTR_CTRL, ctrlOld);
                Thread.CurrentThread.Priority = old;
            }
        });
    }

    private byte ReadCmos(byte reg) { _drv.WriteIoPortByte(0x70, reg); return _drv.ReadIoPortByte(0x71); }

    /// <summary>Seconds from the RTC, read outside its update window so the value is settled.</summary>
    private byte RtcSeconds()
    {
        var guard = Stopwatch.StartNew();
        while ((ReadCmos(0x0A) & 0x80) != 0 && guard.ElapsedMilliseconds < 50) { }
        return ReadCmos(0x00);
    }

    /// <summary>
    /// Work rate timed against the real-time clock instead of the ACPI timer.
    ///
    /// This exists because the ACPI PM timer, the TSC and APERF/MPERF are not independent of each
    /// other: on a board whose BCLK adjustment moves the shared platform reference, all three
    /// scale together and a real clock change becomes invisible to every one of them. The RTC
    /// runs from its own 32.768 kHz watch crystal on the battery circuit, so it cannot be dragged
    /// along. If the core genuinely speeds up, the work done per RTC second rises with it.
    /// </summary>
    public double MeasureWorkRateRtc(int cpuIndex, int seconds = 6)
    {
        return WinRing0Driver.RunOnCpu(cpuIndex, () =>
        {
            var old = Thread.CurrentThread.Priority;
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            try
            {
                double x = 1.0001;
                for (int i = 0; i < 3_000_000; i++) x = x * 1.0000001 + 1e-9; // reach turbo first

                byte last = RtcSeconds();
                var edge = Stopwatch.StartNew();
                while (RtcSeconds() == last && edge.ElapsedMilliseconds < 2000) { }  // align to a tick
                last = RtcSeconds();

                long iterations = 0;
                int ticks = 0;
                while (ticks < seconds)
                {
                    for (int i = 0; i < 100_000; i++) x = x * 1.0000001 + 1e-9;
                    iterations += 100_000;
                    byte now = RtcSeconds();
                    if (now != last) { last = now; ticks++; }
                }
                GC.KeepAlive(x);
                return iterations / (double)ticks / 1_000_000.0;
            }
            finally { Thread.CurrentThread.Priority = old; }
        });
    }

    public void Dispose()
    {
        if (_code != IntPtr.Zero) Native.VirtualFree(_code, UIntPtr.Zero, Native.MEM_RELEASE);
    }
}
