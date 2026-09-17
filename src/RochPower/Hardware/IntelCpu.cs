using System.Runtime.Intrinsics.X86;
using System.Text;

namespace RochPower.Hardware;

public sealed record LogicalCpu(int Index, uint ApicId, int CoreId, bool IsPCore);

/// <summary>
/// Intel hybrid (Alder Lake / Raptor Lake, LGA1700) CPU access through MSRs.
/// Nothing here is board specific: everything talks to the CPU itself.
/// </summary>
public sealed class IntelCpu
{
    public const uint MSR_PLATFORM_INFO = 0xCE;
    public const uint MSR_IA32_BIOS_SIGN_ID = 0x8B;
    public const uint MSR_FLEX_RATIO = 0x194;
    public const uint MSR_IA32_PERF_STATUS = 0x198;
    public const uint MSR_IA32_PERF_CTL = 0x199;
    public const uint MSR_IA32_THERM_STATUS = 0x19C;
    public const uint MSR_TEMPERATURE_TARGET = 0x1A2;
    public const uint MSR_TURBO_RATIO_LIMIT = 0x1AD;
    public const uint MSR_TURBO_RATIO_LIMIT_CORES = 0x1AE;
    public const uint MSR_IA32_PACKAGE_THERM_STATUS = 0x1B1;
    public const uint MSR_RAPL_POWER_UNIT = 0x606;
    public const uint MSR_PKG_POWER_LIMIT = 0x610;
    public const uint MSR_PKG_ENERGY_STATUS = 0x611;
    public const uint MSR_UNCORE_RATIO_LIMIT = 0x620;
    public const uint MSR_UNCORE_PERF_STATUS = 0x621;
    public const uint MSR_ATOM_TURBO_RATIO_LIMIT = 0x650;
    public const uint MSR_ATOM_TURBO_RATIO_LIMIT_CORES = 0x651;

    private readonly IKernelDriver _drv;

    public string BrandString { get; }
    public int Family { get; }
    public int Model { get; }
    public int Stepping { get; }
    public bool IsHybrid { get; }
    public bool HypervisorPresent { get; }
    public IReadOnlyList<LogicalCpu> LogicalCpus { get; }
    public int PCoreCount { get; }
    public int ECoreCount { get; }
    public int FirstPThread { get; }
    public int FirstEThread { get; }
    public string Generation { get; }
    public bool IsLga1700Family { get; }
    public OcMailbox Mailbox { get; }

    public int BaseRatio { get; }
    public int MinRatio { get; }
    public bool RatioLimitsProgrammable { get; }
    public bool TdpProgrammable { get; }
    public int TjMax { get; }
    public uint MicrocodeRevision { get; }

    public IntelCpu(IKernelDriver driver) : this(driver, EnumerateTopology()) { }

    internal IntelCpu(IKernelDriver driver, IReadOnlyList<LogicalCpu> topology)
    {
        _drv = driver;
        BrandString = ReadBrandString();
        var l1 = X86Base.CpuId(1, 0);
        HypervisorPresent = ((uint)l1.Ecx & (1u << 31)) != 0;
        int family = (l1.Eax >> 8) & 0xF, model = (l1.Eax >> 4) & 0xF;
        int extFamily = (l1.Eax >> 20) & 0xFF, extModel = (l1.Eax >> 16) & 0xF;
        if (family == 0xF) family += extFamily;
        if (family == 6 || family == 0xF) model |= extModel << 4;
        Family = family; Model = model; Stepping = l1.Eax & 0xF;

        var l7 = X86Base.CpuId(7, 0);
        IsHybrid = ((l7.Edx >> 15) & 1) == 1;

        (Generation, IsLga1700Family) = Model switch
        {
            0x97 => ("12th Gen (Alder Lake-S)", true),
            0x9A => ("12th Gen (Alder Lake-P)", false),
            0xB7 => ("13th/14th Gen (Raptor Lake-S)", true),
            0xBF => ("13th/14th Gen (Raptor Lake-S, Alder Lake die)", true),
            0xBA => ("13th Gen (Raptor Lake-P)", false),
            _ => ($"Family 6 Model 0x{Model:X2}", false)
        };

        LogicalCpus = topology;
        PCoreCount = LogicalCpus.Where(c => c.IsPCore).Select(c => c.CoreId).Distinct().Count();
        ECoreCount = LogicalCpus.Where(c => !c.IsPCore).Select(c => c.CoreId).Distinct().Count();
        FirstPThread = LogicalCpus.FirstOrDefault(c => c.IsPCore)?.Index ?? 0;
        FirstEThread = LogicalCpus.FirstOrDefault(c => !c.IsPCore)?.Index ?? -1;

        // Intel documents the loaded microcode revision in the high dword of
        // IA32_BIOS_SIGN_ID after CPUID leaf 1 has serialised the update signature.
        MicrocodeRevision = WinRing0Driver.RunOnCpu(FirstPThread, () =>
        {
            _drv.WriteMsr(MSR_IA32_BIOS_SIGN_ID, 0, FirstPThread);
            _ = X86Base.CpuId(1, 0);
            return _drv.ReadMsr(MSR_IA32_BIOS_SIGN_ID, out ulong signature, FirstPThread)
                ? (uint)(signature >> 32)
                : 0;
        });

        Mailbox = new OcMailbox(_drv, FirstPThread);

        if (_drv.ReadMsr(MSR_PLATFORM_INFO, out ulong pi, FirstPThread))
        {
            BaseRatio = (int)((pi >> 8) & 0xFF);
            MinRatio = (int)((pi >> 40) & 0xFF);
            RatioLimitsProgrammable = ((pi >> 28) & 1) == 1;
            TdpProgrammable = ((pi >> 29) & 1) == 1;
        }
        TjMax = _drv.ReadMsr(MSR_TEMPERATURE_TARGET, out ulong tt, FirstPThread) ? (int)((tt >> 16) & 0xFF) : 100;
        if (TjMax == 0) TjMax = 100;
    }

    private static string ReadBrandString()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 3; i++)
        {
            var r = X86Base.CpuId(unchecked((int)(0x80000002 + i)), 0);
            foreach (int reg in new[] { r.Eax, r.Ebx, r.Ecx, r.Edx })
                sb.Append(Encoding.ASCII.GetString(BitConverter.GetBytes(reg)));
        }
        return sb.ToString().Replace("\0", "").Trim();
    }

    private static List<LogicalCpu> EnumerateTopology()
    {
        int n = Math.Min(Environment.ProcessorCount, 64);
        var list = new List<LogicalCpu>(n);
        for (int i = 0; i < n; i++)
        {
            var info = WinRing0Driver.RunOnCpu(i, () =>
            {
                var l0 = X86Base.CpuId(0, 0);
                int maxLeaf = l0.Eax;
                bool pcore = true;
                if (maxLeaf >= 0x1A)
                {
                    var l1a = X86Base.CpuId(0x1A, 0);
                    int coreType = (l1a.Eax >> 24) & 0xFF;
                    pcore = coreType != 0x20; // 0x20 = Atom (E-core), 0x40 = Core (P-core)
                }
                var lb = X86Base.CpuId(0xB, 0);
                uint apic = (uint)lb.Edx;
                int smtShift = lb.Eax & 0x1F;
                return (apic, coreId: (int)(apic >> smtShift), pcore);
            });
            list.Add(new LogicalCpu(i, info.apic, info.coreId, info.pcore));
        }
        return list;
    }

    // ------------------------------------------------------------- helpers
    private ulong ReadOrThrow(uint msr, int cpu)
    {
        if (!_drv.ReadMsr(msr, out ulong v, cpu)) throw new IOException($"RDMSR 0x{msr:X} failed on CPU {cpu}.");
        return v;
    }

    private void WriteOrThrow(uint msr, ulong v, int cpu)
    {
        if (!_drv.WriteMsr(msr, v, cpu)) throw new IOException($"WRMSR 0x{msr:X} failed on CPU {cpu}: {_drv.LastError}. The driver or firmware rejected this register write.");
    }

    private void WriteVerified(uint msr, ulong value, int cpu)
    {
        ulong before = ReadOrThrow(msr, cpu);
        WriteOrThrow(msr, value, cpu);
        try
        {
            ulong actual = ReadOrThrow(msr, cpu);
            if (actual != value)
                throw new IOException($"MSR 0x{msr:X} ignored the request: requested 0x{value:X16}, read back 0x{actual:X16}.");
        }
        catch (Exception ex)
        {
            bool restored = _drv.WriteMsr(msr, before, cpu) && _drv.ReadMsr(msr, out ulong restoredValue, cpu) && restoredValue == before;
            throw new IOException(ex.Message + (restored ? " Previous register value restored." : " Restore could not be verified."), ex);
        }
    }

    private static ulong EncodeRatios(int[] ratios)
    {
        if (ratios.Length != 8 || ratios.Any(r => r < 8 || r > 120))
            throw new ArgumentException("Every turbo group needs an integer ratio between 8 and 120.", nameof(ratios));
        ulong value = 0;
        for (int i = 0; i < 8; i++) value |= (ulong)ratios[i] << (8 * i);
        return value;
    }

    /// <summary>MSR_FLEX_RATIO bit 20: BIOS locked overclocking (typical on non-Z chipsets).</summary>
    public bool IsOcLocked => _drv.ReadMsr(MSR_FLEX_RATIO, out ulong v, FirstPThread) && ((v >> 20) & 1) == 1;

    // ------------------------------------------------------------- ratios
    /// <summary>Turbo ratio table for P-cores: ratios[i] applies when up to cores[i] cores are active.</summary>
    public (int[] ratios, int[] cores) ReadPCoreTurboTable()
    {
        ulong r = ReadOrThrow(MSR_TURBO_RATIO_LIMIT, FirstPThread);
        int[] ratios = new int[8], cores = new int[8];
        bool haveCores = _drv.ReadMsr(MSR_TURBO_RATIO_LIMIT_CORES, out ulong c, FirstPThread);
        for (int i = 0; i < 8; i++)
        {
            ratios[i] = (int)((r >> (8 * i)) & 0xFF);
            cores[i] = haveCores ? (int)((c >> (8 * i)) & 0xFF) : i + 1;
        }
        return (ratios, cores);
    }

    public (int[] ratios, int[] cores) ReadECoreTurboTable()
    {
        if (ECoreCount == 0) return (Array.Empty<int>(), Array.Empty<int>());
        ulong r = ReadOrThrow(MSR_ATOM_TURBO_RATIO_LIMIT, FirstEThread);
        int[] ratios = new int[8], cores = new int[8];
        bool haveCores = _drv.ReadMsr(MSR_ATOM_TURBO_RATIO_LIMIT_CORES, out ulong c, FirstEThread);
        for (int i = 0; i < 8; i++)
        {
            ratios[i] = (int)((r >> (8 * i)) & 0xFF);
            int count = haveCores ? (int)((c >> (8 * i)) & 0xFF) : 0;
            cores[i] = count > 0 && count <= ECoreCount ? count : 0; // 0xFF is not 255 active cores.
        }
        return (ratios, cores);
    }

    public void WritePCoreTurboTable(int[] ratios)
    {
        ulong v = EncodeRatios(ratios);
        ulong before = ReadOrThrow(MSR_TURBO_RATIO_LIMIT, FirstPThread);
        WriteVerified(MSR_TURBO_RATIO_LIMIT, v, FirstPThread);
        // The OC mailbox carries its own ceiling for the core domain; raise it when the table goes above it.
        int wanted = ratios.Max();
        try
        {
            var d = Mailbox.ReadDomain(OcMailbox.DOMAIN_CORE);
            if (d.MaxRatio > 0 && d.MaxRatio < wanted) { d.MaxRatio = wanted; Mailbox.WriteDomain(OcMailbox.DOMAIN_CORE, d); }
        }
        catch (Exception ex)
        {
            bool restored = _drv.WriteMsr(MSR_TURBO_RATIO_LIMIT, before, FirstPThread)
                && _drv.ReadMsr(MSR_TURBO_RATIO_LIMIT, out ulong actual, FirstPThread) && actual == before;
            throw new IOException("OC mailbox ceiling could not be verified: " + ex.Message +
                (restored ? " Previous turbo table restored." : " Turbo-table restore could not be verified."), ex);
        }
    }

    public void WriteECoreTurboTable(int[] ratios)
    {
        if (ECoreCount == 0) return;
        var (current, _) = ReadECoreTurboTable();
        if (ratios.Length != 8 || !current.Any(r => r > 0))
            throw new ArgumentException("No usable eight-entry E-core table.", nameof(ratios));
        ulong v = 0;
        for (int i = 0; i < 8; i++)
        {
            // Firmware can expose one populated group and seven zero entries.
            // Keep inactive entries zero instead of inventing additional turbo groups.
            if (current[i] == 0 ? ratios[i] != 0 : ratios[i] < 8 || ratios[i] > 120)
                throw new ArgumentException("Active E-core groups require ratios 8–120; inactive groups must remain zero.", nameof(ratios));
            v |= (ulong)ratios[i] << (8 * i);
        }
        WriteVerified(MSR_ATOM_TURBO_RATIO_LIMIT, v, FirstEThread);
    }

    /// <summary>The all-core P ratio: the lowest entry of the turbo table (largest active-core group).</summary>
    public int ReadPCoreAllCoreRatio()
    {
        var (ratios, _) = ReadPCoreTurboTable();
        return ratios.Where(r => r > 0).DefaultIfEmpty(BaseRatio).Min();
    }

    public int ReadECoreAllCoreRatio()
    {
        var (ratios, _) = ReadECoreTurboTable();
        return ratios.Where(r => r > 0).DefaultIfEmpty(0).Min();
    }

    public void WritePCoreAllCoreRatio(int ratio) => WritePCoreTurboTable(Enumerable.Repeat(ratio, 8).ToArray());
    public void WriteECoreAllCoreRatio(int ratio)
    {
        if (ratio < 8 || ratio > 120) throw new ArgumentOutOfRangeException(nameof(ratio));
        var (current, _) = ReadECoreTurboTable();
        WriteECoreTurboTable(current.Select(r => r == 0 ? 0 : ratio).ToArray());
    }

    // ------------------------------------------------------------- ring
    /// <summary>
    /// Programmed ring limit. Some boards (including the tested MSI Z790) expose an
    /// additional mailbox ceiling. Only include it when the mailbox reports a nonzero ratio.
    /// This is a configured limit, not a measurement of the running ring clock.
    /// </summary>
    public (int max, int min) ReadRingRatio()
    {
        ulong v = ReadOrThrow(MSR_UNCORE_RATIO_LIMIT, FirstPThread);
        int max = (int)(v & 0x7F), min = (int)((v >> 8) & 0x7F);
        try
        {
            int mb = Mailbox.ReadDomain(OcMailbox.DOMAIN_RING).MaxRatio;
            if (mb > 0) max = Math.Min(max, mb);
        }
        catch { }
        return (max, min);
    }

    public void WriteRingRatio(int max, int? min = null)
    {
        if (max < 4 || max > 120 || min is < 0 or > 120 || min > max)
            throw new ArgumentOutOfRangeException(nameof(max), "Invalid ring ratio range.");
        ulong v = ReadOrThrow(MSR_UNCORE_RATIO_LIMIT, FirstPThread);
        ulong before = v;
        int curMin = (int)((v >> 8) & 0x7F);
        v = (v & ~0x7FUL) | (uint)(max & 0x7F);
        int newMin = min ?? Math.Min(curMin, max); // never leave min above max
        v = (v & ~(0x7FUL << 8)) | ((ulong)(newMin & 0x7F) << 8);
        WriteVerified(MSR_UNCORE_RATIO_LIMIT, v, FirstPThread);
        try
        {
            var d = Mailbox.ReadDomain(OcMailbox.DOMAIN_RING);
            // ASUS can report a completely empty domain here;
            // do not turn a verified MSR write into an unrelated failed VF write.
            // Preserve the mailbox path on boards that expose it (tested on MSI).
            if (OcMailbox.Encode(d) != 0)
            {
                d.MaxRatio = max;
                Mailbox.WriteDomain(OcMailbox.DOMAIN_RING, d);
            }
        }
        catch (Exception ex)
        {
            bool restored = _drv.WriteMsr(MSR_UNCORE_RATIO_LIMIT, before, FirstPThread)
                && _drv.ReadMsr(MSR_UNCORE_RATIO_LIMIT, out ulong actual, FirstPThread) && actual == before;
            throw new IOException($"Ring ratio mailbox failed ({ex.Message}). " +
                (restored ? "Previous ring register restored." : "Ring-register restore could not be verified."), ex);
        }
    }

    public int ReadCurrentRingRatio() =>
        _drv.ReadMsr(MSR_UNCORE_PERF_STATUS, out ulong v, FirstPThread) ? (int)(v & 0x7F) : 0;

    // ------------------------------------------------------------- live status
    /// <summary>Current ratio (bits 15:8) and VID in volts (bits 47:32, 1/8192 V) on a logical CPU.</summary>
    public (int ratio, double vid) ReadPerfStatus(int cpu)
    {
        ulong v = ReadOrThrow(MSR_IA32_PERF_STATUS, cpu);
        return ((int)((v >> 8) & 0xFF), ((v >> 32) & 0xFFFF) / 8192.0);
    }

    /// <summary>Highest current ratio across P-core threads.</summary>
    public int ReadMaxCurrentPRatio()
    {
        int max = 0;
        foreach (var c in LogicalCpus.Where(c => c.IsPCore))
            if (_drv.ReadMsr(MSR_IA32_PERF_STATUS, out ulong v, c.Index)) max = Math.Max(max, (int)((v >> 8) & 0xFF));
        return max;
    }

    public int? ReadMaxCurrentERatio()
    {
        int? max = null;
        foreach (var c in LogicalCpus.Where(c => !c.IsPCore))
            if (_drv.ReadMsr(MSR_IA32_PERF_STATUS, out ulong v, c.Index))
                max = Math.Max(max ?? 0, (int)((v >> 8) & 0xFF));
        return max;
    }

    public int ReadPackageTemperature()
    {
        ulong v = ReadOrThrow(MSR_IA32_PACKAGE_THERM_STATUS, FirstPThread);
        int delta = (int)((v >> 16) & 0x7F);
        return TjMax - delta;
    }

    public int ReadCoreTemperature(int cpu)
    {
        ulong v = ReadOrThrow(MSR_IA32_THERM_STATUS, cpu);
        return TjMax - (int)((v >> 16) & 0x7F);
    }

    // ------------------------------------------------------------- power limits
    private double PowerUnit()
    {
        ulong u = ReadOrThrow(MSR_RAPL_POWER_UNIT, FirstPThread);
        return 1.0 / (1 << (int)(u & 0xF));
    }

    public (double pl1, double pl2, bool locked, bool pl1Enabled, bool pl2Enabled) ReadPackagePowerLimits()
    {
        double unit = PowerUnit();
        ulong v = ReadOrThrow(MSR_PKG_POWER_LIMIT, FirstPThread);
        double pl1 = (v & 0x7FFF) * unit;
        double pl2 = ((v >> 32) & 0x7FFF) * unit;
        return (pl1, pl2, ((v >> 63) & 1) == 1, ((v >> 15) & 1) == 1, ((v >> 47) & 1) == 1);
    }

    public void WritePackagePowerLimits(double? pl1, double? pl2)
    {
        double unit = PowerUnit();
        ulong v = ReadOrThrow(MSR_PKG_POWER_LIMIT, FirstPThread);
        if (((v >> 63) & 1) == 1) throw new InvalidOperationException("Package power limits are locked by the BIOS (MSR 0x610 bit 63).");
        if (pl1 is double p1)
        {
            ulong raw = (ulong)Math.Round(p1 / unit) & 0x7FFF;
            v = (v & ~0x7FFFUL) | raw | (1UL << 15);
        }
        if (pl2 is double p2)
        {
            ulong raw = (ulong)Math.Round(p2 / unit) & 0x7FFF;
            v = (v & ~(0x7FFFUL << 32)) | (raw << 32) | (1UL << 47);
        }
        WriteVerified(MSR_PKG_POWER_LIMIT, v, FirstPThread);
    }

    public double ReadPackageEnergyJoules()
    {
        ulong u = ReadOrThrow(MSR_RAPL_POWER_UNIT, FirstPThread);
        double energyUnit = 1.0 / (1 << (int)((u >> 8) & 0x1F));
        ulong e = ReadOrThrow(MSR_PKG_ENERGY_STATUS, FirstPThread);
        return (e & 0xFFFFFFFF) * energyUnit;
    }
}
