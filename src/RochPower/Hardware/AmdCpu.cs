using System.Runtime.Intrinsics.X86;
using System.Text;

namespace RochPower.Hardware;

/// <summary>One physical core as the SMU addresses it, plus the Windows threads that run on it.</summary>
public sealed record AmdCore(int Index, int Ccd, int CoreInCcd, int[] Threads)
{
    public string Label => $"Core {Index}";
    public string Location => $"CCD {Ccd} core {CoreInCcd}";
}

/// <summary>
/// AMD Zen family CPU (Ryzen desktop and mobile) through MSRs, CPUID and the SMU. Everything
/// talks to the processor itself; the board only matters for whether PBO is enabled in the BIOS.
/// </summary>
public sealed class AmdCpu
{
    public const uint MSR_PSTATE_0 = 0xC0010064;
    public const uint MSR_HW_PSTATE_STATUS = 0xC0010293;
    public const uint MSR_RAPL_POWER_UNIT = 0xC0010299;
    public const uint MSR_PKG_ENERGY_STATUS = 0xC001029B;
    public const uint MSR_PATCH_LEVEL = 0x8B;

    // SMN
    private const uint THM_CUR_TEMP = 0x00059800;
    private const uint SVI3_TEL_PLANE0_F19 = 0x00073014; // VDDCR_CPU telemetry, Zen 4 / Zen 5
    private const uint SVI2_TEL_PLANE0_AM4 = 0x0005A050; // Zen 2 / Zen 3 AM4
    private const uint SVI2_TEL_PLANE0_ZEN1 = 0x0005A00C;

    private readonly IKernelDriver _drv;

    public string BrandString { get; }
    public int Family { get; }
    public int Model { get; }
    public int Stepping { get; }
    public int PackageType { get; }
    public string CodeName { get; }
    public string Generation { get; }
    public bool IsSupported { get; }
    public uint PatchLevel { get; }

    public int LogicalCount { get; }
    public int ThreadsPerCore { get; }
    public int CoreCount { get; }
    public IReadOnlyList<AmdCore> Cores { get; private set; } = Array.Empty<AmdCore>();
    public int CcdCount { get; private set; }
    public string TopologyNote { get; private set; } = "";

    public AmdSmu Smu { get; }
    /// <summary>P0 multiplier from MSR 0xC0010064: what the TSC ticks at, for the BCLK measurement.</summary>
    public int BaseRatio { get; }

    public AmdCpu(IKernelDriver driver)
    {
        _drv = driver;
        var l0 = X86Base.CpuId(0, 0);
        string vendor = Encoding.ASCII.GetString(BitConverter.GetBytes(l0.Ebx)) + Encoding.ASCII.GetString(BitConverter.GetBytes(l0.Edx)) + Encoding.ASCII.GetString(BitConverter.GetBytes(l0.Ecx));
        if (vendor != "AuthenticAMD" && vendor != "HygonGenuine") throw new NotSupportedException($"Not an AMD CPU ({vendor}).");

        BrandString = ReadBrandString();
        var l1 = X86Base.CpuId(1, 0);
        Family = ((l1.Eax >> 8) & 0xF) + ((l1.Eax >> 20) & 0xFF);
        Model = ((l1.Eax >> 4) & 0xF) | (((l1.Eax >> 16) & 0xF) << 4);
        Stepping = l1.Eax & 0xF;
        LogicalCount = (l1.Ebx >> 16) & 0xFF;
        if (LogicalCount == 0) LogicalCount = Environment.ProcessorCount;
        var l81 = X86Base.CpuId(unchecked((int)0x80000001), 0);
        PackageType = (int)((uint)l81.Ebx >> 28);
        var l81e = X86Base.CpuId(unchecked((int)0x8000001E), 0);
        ThreadsPerCore = ((l81e.Ebx >> 8) & 0xF) + 1;
        CoreCount = Math.Max(1, LogicalCount / Math.Max(1, ThreadsPerCore));

        (CodeName, Generation, var messages) = Identify();
        IsSupported = messages != null;
        Smu = new AmdSmu(_drv, messages ?? SmuMessageSet.Zen4);

        PatchLevel = _drv.ReadMsr(MSR_PATCH_LEVEL, out ulong pl, 0) ? (uint)pl : 0;
        BaseRatio = ReadP0Ratio();
        Cores = EnumerateCores();
    }

    private static string ReadBrandString()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 3; i++)
        {
            var r = X86Base.CpuId(unchecked((int)(0x80000002 + i)), 0);
            foreach (int reg in new[] { r.Eax, r.Ebx, r.Ecx, r.Edx }) sb.Append(Encoding.ASCII.GetString(BitConverter.GetBytes(reg)));
        }
        return sb.ToString().Replace("\0", "").Trim();
    }

    /// <summary>Family/model to code name and SMU message set. Package type 4 = SP3, 7 = TRX4, 2 = AM4, 1 = FL1 (Dragon Range).</summary>
    private (string code, string gen, SmuMessageSet? msgs) Identify()
    {
        switch (Family)
        {
            case 0x17:
                switch (Model)
                {
                    case 0x01: return PackageType == 4 ? ("Naples", "Zen (EPYC)", SmuMessageSet.Zen) : PackageType == 7 ? ("Whitehaven", "Zen (Threadripper)", SmuMessageSet.Zen) : ("Summit Ridge", "Zen (Ryzen 1000)", SmuMessageSet.Zen);
                    case 0x08: return PackageType is 4 or 7 ? ("Colfax", "Zen+ (Threadripper 2000)", SmuMessageSet.ZenPlus) : ("Pinnacle Ridge", "Zen+ (Ryzen 2000)", SmuMessageSet.ZenPlus);
                    case 0x11: return ("Raven Ridge", "Zen APU (Ryzen 2000G)", null);
                    case 0x18: return ("Picasso", "Zen+ APU (Ryzen 3000G)", null);
                    case 0x20: return ("Dali", "Zen APU", null);
                    case 0x31: return PackageType == 7 ? ("Castle Peak", "Zen 2 (Threadripper 3000)", SmuMessageSet.Zen2) : ("Rome", "Zen 2 (EPYC)", SmuMessageSet.Zen2);
                    case 0x60: return ("Renoir", "Zen 2 APU (Ryzen 4000)", SmuMessageSet.Apu("Zen 2 APU", 0));
                    case 0x68: return ("Lucienne", "Zen 2 APU (Ryzen 5000U)", SmuMessageSet.Apu("Zen 2 APU", 0));
                    case 0x71: return ("Matisse", "Zen 2 (Ryzen 3000)", SmuMessageSet.Zen2);
                    case 0x90 or 0x91: return ("Van Gogh", "Zen 2 APU (Steam Deck)", SmuMessageSet.Apu("Zen 2 APU", 0));
                    case 0xA0: return ("Mendocino", "Zen 2 APU (Ryzen 7020)", SmuMessageSet.Apu("Zen 2 APU", 0x2F));
                }
                break;
            case 0x19:
                switch (Model)
                {
                    case 0x01: return ("Milan", "Zen 3 (EPYC)", SmuMessageSet.Zen3);
                    case 0x08: return ("Chagall", "Zen 3 (Threadripper 5000)", SmuMessageSet.Zen3);
                    case 0x11: return ("Genoa", "Zen 4 (EPYC)", SmuMessageSet.Zen4);
                    case 0x18: return ("Storm Peak", "Zen 4 (Threadripper 7000)", SmuMessageSet.Zen4);
                    case 0x21: return ("Vermeer", "Zen 3 (Ryzen 5000)", SmuMessageSet.Zen3);
                    case 0x44: return ("Rembrandt", "Zen 3+ APU (Ryzen 6000)", SmuMessageSet.ApuPhoenix("Zen 3+ APU", 0x2F, false));
                    case 0x50: return ("Cezanne", "Zen 3 APU (Ryzen 5000G)", SmuMessageSet.Apu("Zen 3 APU", 0xC3));
                    case 0x61: return PackageType == 1 ? ("Dragon Range", "Zen 4 (Ryzen 7045HX)", SmuMessageSet.Zen4) : ("Raphael", "Zen 4 (Ryzen 7000)", SmuMessageSet.Zen4);
                    case 0x74 or 0x75: return ("Phoenix", "Zen 4 APU (Ryzen 7040 / 8000G)", SmuMessageSet.ApuPhoenix("Zen 4 APU", 0xE1, false));
                    case 0x78: return ("Phoenix 2", "Zen 4 APU (Ryzen 8000)", SmuMessageSet.ApuPhoenix("Zen 4 APU", 0xE1, false));
                    case 0x7C: return ("Hawk Point", "Zen 4 APU (Ryzen 8040)", SmuMessageSet.ApuPhoenix("Zen 4 APU", 0xE1, false));
                }
                break;
            case 0x1A:
                switch (Model)
                {
                    case 0x02: return ("Turin", "Zen 5 (EPYC)", SmuMessageSet.Zen5);
                    case 0x08: return ("Shimada Peak", "Zen 5 (Threadripper 9000)", SmuMessageSet.ShimadaPeak);
                    case 0x11: return ("Turin Dense", "Zen 5c (EPYC)", SmuMessageSet.Zen5);
                    case 0x20 or 0x24: return ("Strix Point", "Zen 5 APU (Ryzen AI 300)", SmuMessageSet.ApuPhoenix("Zen 5 APU", 0xAF, true));
                    case 0x44: return ("Granite Ridge", "Zen 5 (Ryzen 9000)", SmuMessageSet.Zen5);
                    case 0x60 or 0x68: return ("Krackan Point", "Zen 5 APU (Ryzen AI 300)", SmuMessageSet.ApuPhoenix("Zen 5 APU", 0xAF, true));
                    case 0x70: return ("Strix Halo", "Zen 5 APU (Ryzen AI Max)", SmuMessageSet.ApuPhoenix("Zen 5 APU", 0xAF, true));
                    case 0xA0: return ("Bergamo", "Zen 5c (EPYC)", SmuMessageSet.Zen5);
                }
                break;
        }
        return ($"Family {Family:X}h model {Model:X2}h", "unknown Zen generation", null);
    }

    // ------------------------------------------------------------------ topology
    /// <summary>
    /// Which CCDs exist and which cores on each are fused off, read from the SMN fuse registers the
    /// way ZenStates and ryzen_smu do. The SMU addresses a core by its physical position (CCD,
    /// core-in-CCD), so a 6-core CCD with cores 2 and 5 disabled must send 0,1,3,4,6,7.
    /// Falls back to a dense numbering if the fuses cannot be read.
    /// </summary>
    private List<AmdCore> EnumerateCores()
    {
        int coresPerCcdMax = 8;
        uint fuse1 = 0x5D218, fuse2 = 0x5D21C;
        uint coreDisableBase = 0x30081800 + 0x238;
        if (Family == 0x19)
        {
            coreDisableBase = 0x30081800 + 0x598;
            if (CodeName is "Raphael" or "Dragon Range") { coreDisableBase = 0x30081800 + 0x4D0; fuse1 += 0x1A4; fuse2 += 0x1A4; }
        }
        else if (Family == 0x17 && Model != 0x71 && Model != 0x31) { fuse1 += 0x40; fuse2 += 0x40; }
        else if (Family == 0x1A) { coreDisableBase = 0x304A03DC; fuse1 += 0x1A4; fuse2 += 0x1A4; }

        var cores = new List<AmdCore>();
        try
        {
            if (!Smu.Messages.IsApu && Smu.ReadSmn(fuse1) is uint present && Smu.ReadSmn(fuse2) is uint _)
            {
                uint ccdEnable = (present >> 22) & 0x3;
                var ccds = new List<int>();
                for (int i = 0; i < 2; i++) if (((ccdEnable >> i) & 1) == 1) ccds.Add(i);
                if (ccds.Count == 0) ccds.Add(0);
                foreach (int ccd in ccds)
                {
                    uint disabled = Smu.ReadSmn(((uint)ccd << 25) + coreDisableBase) is uint d ? d & 0xFF : 0;
                    for (int c = 0; c < coresPerCcdMax; c++)
                        if (((disabled >> c) & 1) == 0) cores.Add(new AmdCore(cores.Count, ccd, c, Array.Empty<int>()));
                }
                CcdCount = ccds.Count;
                if (cores.Count != CoreCount)
                {
                    TopologyNote = $"fuse map lists {cores.Count} cores on {ccds.Count} CCD(s) but CPUID reports {CoreCount}; using a dense numbering";
                    cores.Clear();
                }
                else TopologyNote = $"{ccds.Count} CCD(s), core map from fuses";
            }
        }
        catch (Exception ex) { TopologyNote = "fuse read failed: " + ex.Message; cores.Clear(); }

        if (cores.Count == 0)
        {
            CcdCount = Math.Max(1, (int)Math.Ceiling(CoreCount / 8.0));
            int perCcd = (int)Math.Ceiling(CoreCount / (double)CcdCount);
            for (int i = 0; i < CoreCount; i++) cores.Add(new AmdCore(i, i / perCcd, i % perCcd, Array.Empty<int>()));
            if (TopologyNote.Length == 0) TopologyNote = "dense core numbering (no fuse map)";
        }

        // Windows numbers logical processors in APIC order: core 0's threads first, then core 1's.
        for (int i = 0; i < cores.Count; i++)
        {
            var threads = Enumerable.Range(i * ThreadsPerCore, ThreadsPerCore).Where(t => t < LogicalCount && t < 64).ToArray();
            cores[i] = cores[i] with { Threads = threads };
        }
        return cores;
    }

    // ------------------------------------------------------------------ MSR readings
    private int ReadP0Ratio()
    {
        if (!_drv.ReadMsr(MSR_PSTATE_0, out ulong v, 0)) return 0;
        return RatioFromPstate(v);
    }

    /// <summary>Multiplier encoded in a P-state or HW P-state status register: Zen 5 uses 5 MHz steps, earlier parts FID/DID.</summary>
    private int RatioFromPstate(ulong v)
    {
        if (Family >= 0x1A) return (int)Math.Round((v & 0xFFF) * 5 / 100.0);
        int fid = (int)(v & 0xFF), did = (int)((v >> 8) & 0x3F);
        return did == 0 ? 0 : (int)Math.Round(fid * 200.0 / did / 100.0);
    }

    private double MHzFromPstate(ulong v)
    {
        if (Family >= 0x1A) return (v & 0xFFF) * 5.0;
        int fid = (int)(v & 0xFF), did = (int)((v >> 8) & 0x3F);
        return did == 0 ? 0 : fid * 200.0 / did;
    }

    /// <summary>Current multiplier of one core (its first thread), from the hardware P-state status.</summary>
    public double ReadCoreMHz(AmdCore core)
    {
        if (core.Threads.Length == 0 || !_drv.ReadMsr(MSR_HW_PSTATE_STATUS, out ulong v, core.Threads[0])) return 0;
        return MHzFromPstate(v);
    }

    /// <summary>Highest current core clock across the package, at BCLK = 100.</summary>
    public double ReadMaxCoreMHz() => Cores.Select(ReadCoreMHz).DefaultIfEmpty(0).Max();

    public double ReadPackageEnergyJoules()
    {
        if (!_drv.ReadMsr(MSR_RAPL_POWER_UNIT, out ulong u, 0)) throw new IOException("RDMSR 0xC0010299 failed.");
        double unit = 1.0 / (1 << (int)((u >> 8) & 0x1F));
        if (!_drv.ReadMsr(MSR_PKG_ENERGY_STATUS, out ulong e, 0)) throw new IOException("RDMSR 0xC001029B failed.");
        return (e & 0xFFFFFFFF) * unit;
    }

    // ------------------------------------------------------------------ SMN readings
    /// <summary>Tctl from the thermal monitor block, in degrees C (with the range-select offset applied).</summary>
    public double? ReadTemperature()
    {
        if (Smu.ReadSmn(THM_CUR_TEMP) is not uint v) return null;
        double t = (v >> 21) * 0.125;
        if ((v & 0x80000) != 0) t -= 49;
        // Zen / Zen+ X parts report Tctl with a fixed offset above Tdie.
        if (BrandString.Contains("2700X")) t -= 10;
        else if (BrandString.Contains("1600X") || BrandString.Contains("1700X") || BrandString.Contains("1800X")) t -= 20;
        else if (BrandString.Contains("Threadripper 19") || BrandString.Contains("Threadripper 29")) t -= 27;
        return t;
    }

    /// <summary>Core rail VID the SMU is requesting, in volts, from the SVI telemetry register.</summary>
    public double? ReadCoreVid()
    {
        if (Family >= 0x19 && CodeName is not ("Vermeer" or "Chagall" or "Milan" or "Cezanne"))
        {
            if (Smu.ReadSmn(SVI3_TEL_PLANE0_F19) is not uint v) return null;
            uint vid = (v >> 6) & 0x1FF;
            return vid == 0 ? null : Math.Round(0.245 + vid * 0.005, 3);
        }
        uint addr = Family == 0x17 && Model is 0x01 or 0x08 or 0x11 or 0x18 or 0x20 ? SVI2_TEL_PLANE0_ZEN1 : SVI2_TEL_PLANE0_AM4;
        if (Smu.ReadSmn(addr) is not uint w) return null;
        uint vid2 = w >> 24;
        return vid2 == 0 ? null : Math.Round(1.55 - vid2 * 0.00625, 3);
    }
}
