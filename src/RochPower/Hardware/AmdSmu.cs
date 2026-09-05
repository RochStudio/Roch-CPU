using System.Runtime.InteropServices;

namespace RochPower.Hardware;

/// <summary>Result code of an SMU mailbox transaction. Values 0x01..0xFF are the SMU's own; the rest are ours.</summary>
public enum SmuStatus : byte
{
    Ok = 0x01,
    Failed = 0xFF,
    UnknownCommand = 0xFE,
    RejectedPrerequisite = 0xFD,
    RejectedBusy = 0xFC,
    // local
    NotImplemented = 0x20,
    MutexTimeout = 0x30,
    MailboxBusy = 0x31,
    NoResponse = 0x32,
    PciError = 0x33,
}

/// <summary>Register triple of one SMU mailbox in SMN space.</summary>
public sealed record SmuMailbox(string Name, uint Msg, uint Rsp, uint Arg)
{
    public bool IsValid => Msg != 0 && Rsp != 0 && Arg != 0;
}

/// <summary>
/// The message numbers one SMU firmware family understands. A zero means "this generation does
/// not have that message" and the corresponding control is not offered.
/// </summary>
public sealed class SmuMessageSet
{
    public required string Family { get; init; }
    public required SmuMailbox Rsmu { get; init; }
    public required SmuMailbox Mp1 { get; init; }
    /// <summary>Host System Management Port. Fused off on most desktop parts; offered for the raw command line only.</summary>
    public SmuMailbox Hsmp { get; init; } = new("HSMP", 0, 0, 0);
    public bool IsApu { get; init; }
    /// <summary>Curve Optimizer range: 30 before Zen 4, 50 from Zen 4 on.</summary>
    public int CoRange { get; init; } = 30;
    /// <summary>Zen 2 addresses a core as CCD/CCX/core; Zen 3 and later as CCD/core.</summary>
    public bool CcxInCoreMask { get; init; }

    // RSMU
    public uint RsmuSetPpt { get; init; }
    public uint RsmuSetTdc { get; init; }
    public uint RsmuSetEdc { get; init; }
    public uint RsmuSetTctlMax { get; init; }
    public uint RsmuSetScalar { get; init; }
    public uint RsmuGetScalar { get; init; }
    public uint RsmuSetCoMargin { get; init; }
    public uint RsmuSetCoMarginAll { get; init; }
    public uint RsmuGetCoMargin { get; init; }
    /// <summary>
    /// The CPU's stock limits. ZenStates labels 0xDC / 0xDB / 0xD9 as fused power / VDD TDC / SoC TDC;
    /// on a Ryzen 7 9850X3D they answer 162000 / 120000 / 180000 for a part whose stock PPT / TDC / EDC
    /// are 162 W / 120 A / 180 A, so here they are PPT, TDC, EDC. They do not follow a written limit.
    /// </summary>
    public uint RsmuGetStockPpt { get; init; }
    public uint RsmuGetStockTdc { get; init; }
    public uint RsmuGetStockEdc { get; init; }
    public uint RsmuIsOverclockable { get; init; }
    public uint RsmuGetBoostLimit { get; init; }
    public uint RsmuSetBoostLimitAll { get; init; }
    public uint RsmuGetDramBase { get; init; }
    public uint RsmuGetTableVersion { get; init; }
    public uint RsmuTransferTable { get; init; }
    public uint RsmuGetSustainedPower { get; init; }
    // MP1
    public uint Mp1SetPpt { get; init; }
    public uint Mp1SetTdc { get; init; }
    public uint Mp1SetEdc { get; init; }
    public uint Mp1SetScalar { get; init; }
    public uint Mp1SetPboEnable { get; init; }
    public uint Mp1SetCoMargin { get; init; }
    public uint Mp1SetCoMarginAll { get; init; }
    public uint Mp1GetSustainedPower { get; init; }

    public bool HasPowerLimits => RsmuSetPpt != 0 || Mp1SetPpt != 0;
    public bool HasCurrentLimits => (RsmuSetTdc != 0 || Mp1SetTdc != 0) && (RsmuSetEdc != 0 || Mp1SetEdc != 0);
    public bool HasScalar => RsmuSetScalar != 0 || Mp1SetScalar != 0;
    public bool HasCurveOptimizer => RsmuSetCoMargin != 0 || Mp1SetCoMargin != 0;
    public bool HasCurveOptimizerReadback => RsmuGetCoMargin != 0;
    public bool HasBoostLimit => RsmuGetBoostLimit != 0;

    // Mailbox register addresses shared by the desktop generations.
    private static readonly SmuMailbox RsmuZen1 = new("RSMU", 0x03B1051C, 0x03B10568, 0x03B10590);
    private static readonly SmuMailbox Mp1Zen1 = new("MP1", 0x03B10528, 0x03B10564, 0x03B10598);
    private static readonly SmuMailbox RsmuZen2 = new("RSMU", 0x03B10524, 0x03B10570, 0x03B10A40);
    private static readonly SmuMailbox Mp1Zen2 = new("MP1", 0x03B10530, 0x03B1057C, 0x03B109C4);
    private static readonly SmuMailbox RsmuApu1 = new("RSMU", 0x03B10A20, 0x03B10A80, 0x03B10A88);
    private static readonly SmuMailbox Mp1Apu1 = new("MP1", 0x03B10528, 0x03B10564, 0x03B10998);
    private static readonly SmuMailbox Mp1Phoenix = new("MP1", 0x03B10528, 0x03B10578, 0x03B10998);
    private static readonly SmuMailbox Mp1Strix = new("MP1", 0x03B10928, 0x03B10978, 0x03B10998);
    private static readonly SmuMailbox RsmuShimada = new("RSMU", 0x03B10924, 0x03B10970, 0x03B10A40);
    private static readonly SmuMailbox HsmpZen2 = new("HSMP", 0x03B10534, 0x03B10980, 0x03B109E0);
    private static readonly SmuMailbox HsmpZen5 = new("HSMP", 0x03B10934, 0x03B10980, 0x03B109E0);

    /// <summary>Summit Ridge, Naples, Whitehaven.</summary>
    public static readonly SmuMessageSet Zen = new()
    {
        Family = "Zen", Rsmu = RsmuZen1, Mp1 = Mp1Zen1, CcxInCoreMask = true,
        RsmuSetPpt = 0x58,
        Mp1SetPpt = 0x31, Mp1SetTdc = 0x29, Mp1SetEdc = 0x2A,
        RsmuGetDramBase = 0xC, RsmuGetTableVersion = 0xD, RsmuTransferTable = 0xA,
        RsmuGetSustainedPower = 0x5F, Mp1GetSustainedPower = 0x36,
    };

    /// <summary>Pinnacle Ridge, Colfax.</summary>
    public static readonly SmuMessageSet ZenPlus = new()
    {
        Family = "Zen+", Rsmu = RsmuZen1, Mp1 = Mp1Zen1, CcxInCoreMask = true,
        RsmuSetPpt = 0x64, RsmuSetTdc = 0x65, RsmuSetEdc = 0x66, RsmuSetTctlMax = 0x68,
        RsmuSetScalar = 0x6A, RsmuGetScalar = 0x6F,
        RsmuGetDramBase = 0xC, RsmuGetTableVersion = 0xD, RsmuTransferTable = 0xA,
    };

    /// <summary>Matisse, Castle Peak, Rome.</summary>
    public static readonly SmuMessageSet Zen2 = new()
    {
        Family = "Zen 2", Rsmu = RsmuZen2, Mp1 = Mp1Zen2, Hsmp = HsmpZen2, CcxInCoreMask = true, CoRange = 30,
        RsmuSetPpt = 0x53, RsmuSetTdc = 0x54, RsmuSetEdc = 0x55, RsmuSetTctlMax = 0x56,
        RsmuSetScalar = 0x58, RsmuGetScalar = 0x6C, RsmuIsOverclockable = 0x6F, RsmuGetBoostLimit = 0x6E,
        RsmuGetDramBase = 0x6, RsmuGetTableVersion = 0x8, RsmuTransferTable = 0x5,
        Mp1SetPpt = 0x3C, Mp1SetTdc = 0x3A, Mp1SetEdc = 0x3B, Mp1SetScalar = 0x2F, Mp1SetPboEnable = 0x33,
        Mp1SetCoMargin = 0x34, Mp1SetCoMarginAll = 0x35, Mp1GetSustainedPower = 0x23,
    };

    /// <summary>Vermeer, Chagall, Milan.</summary>
    public static readonly SmuMessageSet Zen3 = new()
    {
        Family = "Zen 3", Rsmu = RsmuZen2, Mp1 = Mp1Zen2, Hsmp = HsmpZen2, CoRange = 30,
        RsmuSetPpt = 0x53, RsmuSetTdc = 0x54, RsmuSetEdc = 0x55, RsmuSetTctlMax = 0x56,
        RsmuSetScalar = 0x58, RsmuGetScalar = 0x6C, RsmuIsOverclockable = 0x6F, RsmuGetBoostLimit = 0x6E,
        RsmuSetCoMargin = 0xA, RsmuSetCoMarginAll = 0xB, RsmuGetCoMargin = 0x7C,
        RsmuGetDramBase = 0x6, RsmuGetTableVersion = 0x8, RsmuTransferTable = 0x5,
        Mp1SetPpt = 0x3D, Mp1SetTdc = 0x3B, Mp1SetEdc = 0x3C, Mp1SetScalar = 0x2F, Mp1SetPboEnable = 0x33,
        Mp1SetCoMargin = 0x35, Mp1SetCoMarginAll = 0x36, Mp1GetSustainedPower = 0x23,
    };

    /// <summary>Raphael, Dragon Range, Genoa, Storm Peak (Zen 4) and Granite Ridge, Turin (Zen 5): same RSMU numbering.</summary>
    public static readonly SmuMessageSet Zen4 = new()
    {
        Family = "Zen 4 / Zen 5", Rsmu = RsmuZen2, Mp1 = Mp1Zen2, Hsmp = HsmpZen2, CoRange = 50,
        RsmuSetPpt = 0x56, RsmuSetTdc = 0x57, RsmuSetEdc = 0x58, RsmuSetTctlMax = 0x59,
        RsmuSetScalar = 0x5B, RsmuGetScalar = 0x6D, RsmuIsOverclockable = 0x6F,
        RsmuGetBoostLimit = 0x6E, RsmuSetBoostLimitAll = 0x70,
        RsmuSetCoMargin = 0x6, RsmuSetCoMarginAll = 0x7, RsmuGetCoMargin = 0xD5,
        RsmuGetStockPpt = 0xD9, RsmuGetStockTdc = 0xDB, RsmuGetStockEdc = 0xDC,
        RsmuGetDramBase = 0x4, RsmuGetTableVersion = 0x5, RsmuTransferTable = 0x3,
        Mp1SetPpt = 0x3E, Mp1SetTdc = 0x3C, Mp1SetEdc = 0x3D, Mp1SetScalar = 0x2F,
        Mp1SetCoMargin = 0x35, Mp1SetCoMarginAll = 0x36, Mp1GetSustainedPower = 0x23,
    };

    /// <summary>Granite Ridge, Turin, Bergamo: Zen 4 numbering, HSMP mailbox moved.</summary>
    public static readonly SmuMessageSet Zen5 = new()
    {
        Family = "Zen 5", Rsmu = RsmuZen2, Mp1 = Mp1Zen2, Hsmp = HsmpZen5, CoRange = 50,
        RsmuSetPpt = 0x56, RsmuSetTdc = 0x57, RsmuSetEdc = 0x58, RsmuSetTctlMax = 0x59,
        RsmuSetScalar = 0x5B, RsmuGetScalar = 0x6D, RsmuIsOverclockable = 0x6F,
        RsmuGetBoostLimit = 0x6E, RsmuSetBoostLimitAll = 0x70,
        RsmuSetCoMargin = 0x6, RsmuSetCoMarginAll = 0x7, RsmuGetCoMargin = 0xD5,
        RsmuGetStockPpt = 0xD9, RsmuGetStockTdc = 0xDB, RsmuGetStockEdc = 0xDC,
        RsmuGetDramBase = 0x4, RsmuGetTableVersion = 0x5, RsmuTransferTable = 0x3,
        Mp1SetPpt = 0x3E, Mp1SetTdc = 0x3C, Mp1SetEdc = 0x3D, Mp1SetScalar = 0x2F,
        Mp1SetCoMargin = 0x35, Mp1SetCoMarginAll = 0x36, Mp1GetSustainedPower = 0x23,
    };

    /// <summary>Shimada Peak (Threadripper 9000): RSMU moved, MP1 unknown.</summary>
    public static readonly SmuMessageSet ShimadaPeak = new()
    {
        Family = "Zen 5 (Shimada Peak)", Rsmu = RsmuShimada, Mp1 = new SmuMailbox("MP1", 0, 0, 0), CoRange = 50,
        RsmuSetPpt = 0x56, RsmuSetTdc = 0x57, RsmuSetEdc = 0x58, RsmuSetTctlMax = 0x59,
        RsmuSetScalar = 0x5B, RsmuGetScalar = 0x6D, RsmuIsOverclockable = 0x6F, RsmuSetBoostLimitAll = 0x70,
        RsmuSetCoMargin = 0x6, RsmuSetCoMarginAll = 0x7, RsmuGetCoMargin = 0xA3,
        RsmuGetDramBase = 0x4, RsmuGetTableVersion = 0x5, RsmuTransferTable = 0x3,
    };

    /// <summary>Renoir, Lucienne, Cezanne, Van Gogh, Rembrandt, Mendocino.</summary>
    public static SmuMessageSet Apu(string name, uint getCoMargin) => new()
    {
        Family = name, Rsmu = RsmuApu1, Mp1 = Mp1Apu1, IsApu = true, CoRange = 30,
        RsmuSetPpt = 0x32, RsmuSetTdc = 0x38, RsmuSetEdc = 0x3A, RsmuSetTctlMax = 0x37,
        RsmuSetScalar = 0x3F, RsmuGetScalar = 0xF, RsmuIsOverclockable = 0x82, RsmuGetBoostLimit = 0x42,
        RsmuSetCoMargin = 0x52, RsmuSetCoMarginAll = 0xB1, RsmuGetCoMargin = getCoMargin,
        RsmuGetStockPpt = 0x13, RsmuGetStockTdc = 0x15, // fused fast limit / VDD TDC per ZenStates; not verified here
        RsmuGetDramBase = 0x66, RsmuGetTableVersion = 0x6, RsmuTransferTable = 0x65,
        Mp1SetPpt = 0x15, Mp1SetTdc = 0x1A, Mp1SetEdc = 0x1C, Mp1SetScalar = 0x49,
        Mp1SetCoMargin = 0x54, Mp1SetCoMarginAll = 0x55, Mp1GetSustainedPower = 0x5B,
    };

    /// <summary>Phoenix, Hawk Point, Strix Point, Strix Halo, Krackan Point.</summary>
    public static SmuMessageSet ApuPhoenix(string name, uint getCoMargin, bool strix) => new()
    {
        Family = name, Rsmu = RsmuApu1, Mp1 = strix ? Mp1Strix : Mp1Phoenix, IsApu = true, CoRange = 50,
        RsmuSetPpt = 0x32, RsmuSetTdc = 0x38, RsmuSetEdc = 0x3A, RsmuSetTctlMax = 0x37,
        RsmuSetScalar = 0x3E, RsmuGetScalar = 0xF, RsmuIsOverclockable = 0x82, RsmuGetBoostLimit = 0x42, RsmuSetBoostLimitAll = 0x47,
        RsmuSetCoMargin = 0x53, RsmuSetCoMarginAll = 0x5D, RsmuGetCoMargin = getCoMargin,
        RsmuGetStockPpt = 0x13, RsmuGetStockTdc = 0x15, // fused fast limit / VDD TDC per ZenStates; not verified here
        RsmuGetDramBase = 0x66, RsmuGetTableVersion = 0x6, RsmuTransferTable = 0x65,
        Mp1SetPpt = 0x15, Mp1SetTdc = 0x1A, Mp1SetEdc = 0x1C, Mp1SetScalar = 0x63,
        Mp1SetCoMargin = 0x4B, Mp1SetCoMarginAll = 0x4C, Mp1GetSustainedPower = 0x5F,
    };
}

/// <summary>
/// Where PPT / TDC / EDC live in the SMU power table, as float indices (-1 = not known). Every
/// index is checked after a limit write (VerifyLayout): a float that does not follow the value
/// just written is dropped from read-back for the rest of the session, one field at a time.
/// </summary>
public sealed record PmTableLayout(string Name, int PptLimit, int PptValue, int TdcLimit, int TdcValue, int EdcLimit, int EdcValue, int ThmLimit, int ThmValue, int VddcrCpu, int SocketPower)
{
    // SoC side and memory clocks, from ZenStates-Core's power table definitions (byte offset / 4).
    public int Fclk { get; init; } = -1;
    public int Uclk { get; init; } = -1;
    public int Mclk { get; init; } = -1;
    public int VddcrSoc { get; init; } = -1;
    /// <summary>Live SoC rail telemetry, where it is separate from the set-point.</summary>
    public int VddcrSocLive { get; init; } = -1;
    public int VddMisc { get; init; } = -1;
    public int CldoVddp { get; init; } = -1;
    public int VddgIod { get; init; } = -1;
    public int VddgCcd { get; init; } = -1;

    /// <summary>ryzen_smu's layout: limit/value pairs for PPT, TDC, THM, FIT, EDC from the top. SoC offsets per ZenStates (0x240903).</summary>
    public static readonly PmTableLayout Zen2 = new("Zen 2 desktop", 0, 1, 2, 3, 8, 9, 4, 5, -1, -1)
        { Fclk = 48, Uclk = 50, Mclk = 51, VddcrSoc = 45, CldoVddp = 125, VddgIod = 126 };
    /// <summary>Same limit block as Zen 2; SoC offsets per ZenStates (0x380805).</summary>
    public static readonly PmTableLayout Zen3 = new("Zen 3 desktop", 0, 1, 2, 3, 8, 9, 4, 5, -1, -1)
        { Fclk = 48, Uclk = 50, Mclk = 51, VddcrSoc = 45, CldoVddp = 137, VddgIod = 138, VddgCcd = 139 };
    /// <summary>
    /// Measured on a Ryzen 7 9850X3D, table 0x00620105, with PPT 300 / TDC 200 / EDC 480 / Tctl 95 in
    /// force: 300 sat at float 2, 200 at 8, 95 at 10, 480 at 63, each followed by its live value.
    /// Floats 20/21/22/26 are core / SoC / misc / total power, as LibreHardwareMonitor lists for Zen 4.
    /// The SoC block matches ZenStates' 0x620105 definition and the values on the bench: FCLK 2000 at 71,
    /// UCLK 2000 at 75, MCLK 4000 at 79, VDDCR_SOC 1.300 at 83 (the BIOS set-point; 53 holds the same),
    /// VDD_MISC 1.100 at 58, VDDG IOD/CCD and VDDP ~1.05 at 259/261/269. Float 54 read 1.205 while HWiNFO
    /// showed the SVI3 SoC telemetry at 1.203 V, so 54 is the live rail.
    /// </summary>
    public static readonly PmTableLayout Zen5 = new("Zen 5 desktop", 2, 3, 8, 9, 63, 64, 10, 11, -1, 26)
        { Fclk = 71, Uclk = 75, Mclk = 79, VddcrSoc = 83, VddcrSocLive = 54, VddMisc = 58, CldoVddp = 269, VddgIod = 259, VddgCcd = 261 };
    /// <summary>Zen 4 shares the first part of the Zen 5 layout (per LibreHardwareMonitor's indices); the EDC position is not known. SoC offsets per ZenStates (0x5401xx).</summary>
    public static readonly PmTableLayout Zen4 = new("Zen 4 desktop (PPT/TDC/THM only)", 2, 3, 8, 9, -1, -1, 10, 11, -1, 26)
        { Fclk = 70, Uclk = 74, Mclk = 78, VddcrSoc = 52, VddMisc = 56, CldoVddp = 268 };
}

/// <summary>
/// AMD System Management Unit access: SMN register space through the north bridge index/data
/// pair, the RSMU and MP1 mailboxes, and the power table the SMU publishes in DRAM. This is the
/// path Ryzen Master and every third-party Ryzen tool uses; nothing here is board specific.
/// </summary>
public sealed class AmdSmu : IDisposable
{
    // D0F0 index/data pair for SMN (System Management Network) reads and writes.
    private const ushort SMN_INDEX = 0x60;
    private const ushort SMN_DATA = 0x64;
    private const int MailboxArgs = 6;
    private const int MailboxTimeoutIterations = 8192;

    private readonly IKernelDriver _drv;
    private readonly Mutex? _pciMutex;
    private readonly object _lock = new();

    public SmuMessageSet Messages { get; }
    public uint Version { get; private set; }
    public string VersionText => Version == 0 ? "unknown" : $"{Version >> 24 & 0xFF}.{Version >> 16 & 0xFF}.{Version >> 8 & 0xFF}.{Version & 0xFF}";
    public bool Responding { get; private set; }
    public string Status { get; private set; } = "not probed";

    // Power table
    public uint TableVersion { get; private set; }
    public ulong TableAddress { get; private set; }
    public int TableSize { get; private set; }
    public PmTableLayout? Layout { get; private set; }
    public float[]? Table { get; private set; }
    /// <summary>Set once a limit written with the SMU was seen to move the matching table float.</summary>
    public bool LayoutVerified { get; private set; }
    /// <summary>Fields whose table float did not follow a written limit: wrong for this firmware, no longer read back.</summary>
    private readonly HashSet<string> _untrusted = new();
    public bool FieldTrusted(string field) => !_untrusted.Contains(field);
    public bool LayoutContradicted => _untrusted.Count > 0;
    /// <summary>Set once the driver refused the physical read: no point asking again every refresh.</summary>
    public bool TableUnreadable { get; private set; }
    /// <summary>PawnIO RyzenSMU module, when PawnIO is installed: reads the table in the kernel.</summary>
    private PawnIo? _pawn;
    public bool TableViaPawnIo => _pawn != null;
    public string TableSource => _pawn != null ? "PawnIO RyzenSMU module" : "WinRing0 physical read";

    public AmdSmu(IKernelDriver drv, SmuMessageSet messages)
    {
        _drv = drv;
        Messages = messages;
        // Shared with HWiNFO, Ryzen Master, LibreHardwareMonitor and ZenStates, all of which take
        // this mutex around SMN accesses. Two tools racing the index/data pair corrupt each other.
        try { _pciMutex = new Mutex(false, @"Global\Access_PCI"); }
        catch (UnauthorizedAccessException) { try { _pciMutex = Mutex.OpenExisting(@"Global\Access_PCI"); } catch { _pciMutex = null; } }
        catch { _pciMutex = null; }
    }

    // ------------------------------------------------------------------ SMN
    private bool Acquire(int ms = 5000)
    {
        try { return _pciMutex?.WaitOne(ms) ?? true; }
        catch (AbandonedMutexException) { return true; }
        catch { return false; }
    }

    private void Release() { try { _pciMutex?.ReleaseMutex(); } catch { } }

    private bool SmnReadNoLock(uint address, out uint value)
    {
        if (!_drv.WritePciConfig(0, 0, 0, SMN_INDEX, address)) { value = 0; return false; }
        bool ok = _drv.ReadPciConfig(0, 0, 0, SMN_DATA, out value);
        // The driver client reports 0xFFFFFFFF as "absent"; for a data register that is a legal value.
        return ok || value == 0xFFFFFFFF;
    }

    private bool SmnWriteNoLock(uint address, uint value) =>
        _drv.WritePciConfig(0, 0, 0, SMN_INDEX, address) && _drv.WritePciConfig(0, 0, 0, SMN_DATA, value);

    /// <summary>Reads one SMN register under the global PCI mutex. Returns null on failure.</summary>
    public uint? ReadSmn(uint address)
    {
        lock (_lock)
        {
            if (!Acquire()) return null;
            try { return SmnReadNoLock(address, out uint v) ? v : null; }
            finally { Release(); }
        }
    }

    // ------------------------------------------------------------------ mailbox
    private bool WaitReady(SmuMailbox mb)
    {
        for (int i = 0; i < MailboxTimeoutIterations; i++)
        {
            if (SmnReadNoLock(mb.Rsp, out uint rsp) && rsp != 0) return true;
        }
        return false;
    }

    /// <summary>
    /// One mailbox transaction: wait until the previous command has a response, clear the response
    /// register, write the arguments and the message, wait for the response, read the arguments back.
    /// </summary>
    public SmuStatus Send(SmuMailbox mb, uint message, uint[] args)
    {
        if (!mb.IsValid || message == 0) return SmuStatus.NotImplemented;
        if (args.Length != MailboxArgs) throw new ArgumentException("SMU mailbox takes exactly 6 arguments.", nameof(args));
        lock (_lock)
        {
            if (!Acquire()) return SmuStatus.MutexTimeout;
            try
            {
                if (!WaitReady(mb)) return SmuStatus.MailboxBusy;
                if (!SmnWriteNoLock(mb.Rsp, 0)) return SmuStatus.PciError;
                for (int i = 0; i < MailboxArgs; i++)
                    if (!SmnWriteNoLock(mb.Arg + (uint)(i * 4), args[i])) return SmuStatus.PciError;
                if (!SmnWriteNoLock(mb.Msg, message)) return SmuStatus.PciError;
                if (!WaitReady(mb)) return SmuStatus.NoResponse;
                if (!SmnReadNoLock(mb.Rsp, out uint status)) return SmuStatus.PciError;
                if (status > 0xFF) return SmuStatus.Failed;
                if ((SmuStatus)status == SmuStatus.Ok)
                    for (int i = 0; i < MailboxArgs; i++)
                        if (!SmnReadNoLock(mb.Arg + (uint)(i * 4), out args[i])) return SmuStatus.PciError;
                return (SmuStatus)status;
            }
            finally { Release(); }
        }
    }

    private static uint[] Args(params uint[] a)
    {
        var r = new uint[MailboxArgs];
        Array.Copy(a, r, Math.Min(a.Length, MailboxArgs));
        return r;
    }

    public SmuStatus SendRsmu(uint message, uint[] args) => Send(Messages.Rsmu, message, args);
    public SmuStatus SendMp1(uint message, uint[] args) => Send(Messages.Mp1, message, args);
    public SmuStatus SendHsmp(uint message, uint[] args) => Send(Messages.Hsmp, message, args);

    public static string Describe(SmuStatus s) => s switch
    {
        SmuStatus.Ok => "OK",
        SmuStatus.Failed => "SMU reports failure",
        SmuStatus.UnknownCommand => "unknown command for this SMU firmware",
        SmuStatus.RejectedPrerequisite => "rejected: prerequisite not met (PBO disabled in BIOS, or the board locked the limit)",
        SmuStatus.RejectedBusy => "rejected: SMU busy",
        SmuStatus.NotImplemented => "not available on this CPU generation",
        SmuStatus.MutexTimeout => "another tool holds the PCI bus lock",
        SmuStatus.MailboxBusy => "mailbox never became ready",
        SmuStatus.NoResponse => "no response from the SMU",
        SmuStatus.PciError => "SMN access failed",
        _ => $"status 0x{(byte)s:X2}"
    };

    private static void Throw(string what, SmuStatus s) => throw new IOException($"{what}: {Describe(s)}.");

    // ------------------------------------------------------------------ probing
    /// <summary>Test message (0x1) and firmware version (0x2) on the RSMU; fills Status.</summary>
    public bool Probe()
    {
        var a = Args(7);
        var st = SendRsmu(0x1, a);
        Responding = st == SmuStatus.Ok && a[0] == 8;
        if (!Responding) { Status = $"RSMU mailbox at 0x{Messages.Rsmu.Msg:X8} not answering ({Describe(st)})"; return false; }
        a = Args();
        if (SendRsmu(0x2, a) == SmuStatus.Ok) Version = a[0];
        Status = $"{Messages.Family} SMU firmware {VersionText} ({Messages.Rsmu.Name} 0x{Messages.Rsmu.Msg:X8})";
        return true;
    }

    /// <summary>IsOverclockable flags: bit 0 OC enabled, bit 1 power limits, bit 2 PBO.</summary>
    public uint? ReadOcCapabilities()
    {
        var a = Args();
        return SendRsmu(Messages.RsmuIsOverclockable, a) == SmuStatus.Ok ? a[0] : null;
    }

    // ------------------------------------------------------------------ limits
    private void SetLimit(string name, uint rsmuMsg, uint mp1Msg, double value)
    {
        uint raw = (uint)Math.Round(value * 1000); // mW / mA
        var st = SmuStatus.NotImplemented;
        if (Messages.IsApu && mp1Msg != 0)
        {
            // Mobile SMUs want the MP1 variant first.
            st = SendMp1(mp1Msg, Args(raw));
            if (st != SmuStatus.Ok && rsmuMsg != 0) st = SendRsmu(rsmuMsg, Args(raw));
        }
        else
        {
            if (rsmuMsg != 0) st = SendRsmu(rsmuMsg, Args(raw));
            if (st != SmuStatus.Ok && mp1Msg != 0) st = SendMp1(mp1Msg, Args(raw));
        }
        if (st != SmuStatus.Ok) Throw(name, st);
    }

    public void SetPpt(double watts) => SetLimit("PPT", Messages.RsmuSetPpt, Messages.Mp1SetPpt, watts);
    public void SetTdc(double amps) => SetLimit("TDC", Messages.RsmuSetTdc, Messages.Mp1SetTdc, amps);
    public void SetEdc(double amps) => SetLimit("EDC", Messages.RsmuSetEdc, Messages.Mp1SetEdc, amps);

    /// <summary>
    /// The CPU's stock PPT (W), TDC (A) and EDC (A) as fused into the part. These are constants: they
    /// do not report the limit currently in force (verified on a 9850X3D by writing 150 W and reading
    /// back 162 W), so they serve as the value "0" restores, not as a read-back.
    /// </summary>
    public (double? ppt, double? tdc, double? edc) ReadStockLimits()
    {
        double? Q(uint msg)
        {
            if (msg == 0) return null;
            var a = Args();
            return SendRsmu(msg, a) == SmuStatus.Ok && a[0] != 0 ? a[0] / 1000.0 : null; // mW / mA, like the Set messages
        }
        return (Q(Messages.RsmuGetStockPpt), Q(Messages.RsmuGetStockTdc), Q(Messages.RsmuGetStockEdc));
    }

    /// <summary>Sustained power limit (W) and thermal limit (C) the platform configured, when readable.</summary>
    public (int power, int temp)? ReadSustainedLimits()
    {
        var a = Args();
        SmuStatus st = SmuStatus.NotImplemented;
        if (Messages.RsmuGetSustainedPower != 0) st = SendRsmu(Messages.RsmuGetSustainedPower, a);
        else if (Messages.Mp1GetSustainedPower != 0) st = SendMp1(Messages.Mp1GetSustainedPower, a);
        if (st != SmuStatus.Ok) return null;
        int power = (int)((a[0] >> 16) & 0xFF), temp = (int)(a[0] & 0xFF);
        return power > 0 ? (power, temp) : null;
    }

    // ------------------------------------------------------------------ scalar
    /// <summary>PBO scalar 1..10 (x). Returns null if unreadable; 0 means the SMU is in manual OC mode.</summary>
    public double? ReadScalar()
    {
        if (Messages.RsmuGetScalar == 0) return null;
        var a = Args();
        if (SendRsmu(Messages.RsmuGetScalar, a) != SmuStatus.Ok) return null;
        float f = BitConverter.Int32BitsToSingle((int)a[0]);
        if (float.IsNaN(f) || f < 0 || f > 10) return null;
        return Math.Round(f, 2);
    }

    public void SetScalar(double scalar)
    {
        uint raw = (uint)Math.Round(scalar * 100);
        if (Messages.Mp1SetPboEnable != 0) SendMp1(Messages.Mp1SetPboEnable, Args()); // Zen 2 / Zen 3: allow PBO overdrive
        var st = SmuStatus.NotImplemented;
        if (Messages.RsmuSetScalar != 0) st = SendRsmu(Messages.RsmuSetScalar, Args(raw));
        if (st != SmuStatus.Ok && Messages.Mp1SetScalar != 0) st = SendMp1(Messages.Mp1SetScalar, Args(raw));
        if (st != SmuStatus.Ok) Throw("PBO scalar", st);
    }

    // ------------------------------------------------------------------ curve optimizer
    /// <summary>
    /// The core address the SMU wants: CCD in bits 31:28, CCX in 27:24 (Zen 2 only), core in 23:20.
    /// APUs take the plain core index instead.
    /// </summary>
    public uint CoreMask(int ccd, int coreInCcd)
    {
        if (Messages.IsApu) return (uint)coreInCcd;
        if (Messages.CcxInCoreMask) return ((uint)ccd << 28) | ((uint)(coreInCcd / 4) << 24) | ((uint)(coreInCcd % 4) << 20);
        return ((uint)ccd << 28) | ((uint)(coreInCcd % 8) << 20);
    }

    /// <summary>Margin as the SMU encodes it: 16-bit two's complement in the low half of the argument.</summary>
    private static uint MarginArg(int margin) => (uint)(margin < 0 ? 0x10000 + margin : margin) & 0xFFFF;

    public void SetCurveOptimizer(int ccd, int coreInCcd, int margin)
    {
        margin = Math.Clamp(margin, -Messages.CoRange, Messages.CoRange);
        var args = Args((CoreMask(ccd, coreInCcd) & 0xFFF00000) | MarginArg(margin));
        var st = Messages.Mp1SetCoMargin != 0 ? SendMp1(Messages.Mp1SetCoMargin, args)
               : Messages.RsmuSetCoMargin != 0 ? SendRsmu(Messages.RsmuSetCoMargin, args) : SmuStatus.NotImplemented;
        if (st != SmuStatus.Ok) Throw($"Curve Optimizer core {ccd}/{coreInCcd}", st);
    }

    public void SetCurveOptimizerAll(int margin)
    {
        margin = Math.Clamp(margin, -Messages.CoRange, Messages.CoRange);
        var args = Args(MarginArg(margin));
        var st = Messages.Mp1SetCoMarginAll != 0 ? SendMp1(Messages.Mp1SetCoMarginAll, args)
               : Messages.RsmuSetCoMarginAll != 0 ? SendRsmu(Messages.RsmuSetCoMarginAll, args) : SmuStatus.NotImplemented;
        if (st != SmuStatus.Ok) Throw("Curve Optimizer (all cores)", st);
    }

    /// <summary>Current margin of one core, or null when this firmware has no read-back message.</summary>
    public int? ReadCurveOptimizer(int ccd, int coreInCcd)
    {
        if (Messages.RsmuGetCoMargin == 0) return null;
        var a = Args(CoreMask(ccd, coreInCcd));
        if (SendRsmu(Messages.RsmuGetCoMargin, a) != SmuStatus.Ok) return null;
        uint v = a[0];
        int m = v is > 0x7FFF and < 0x10000 ? (int)v - 0x10000 : (int)v; // 16-bit or 32-bit two's complement
        return Math.Abs(m) <= 127 ? m : null;
    }

    // ------------------------------------------------------------------ boost limit
    public int? ReadBoostLimitMHz()
    {
        if (Messages.RsmuGetBoostLimit == 0) return null;
        var a = Args();
        return SendRsmu(Messages.RsmuGetBoostLimit, a) == SmuStatus.Ok && a[0] > 0 ? (int)a[0] : null;
    }

    public void SetBoostLimitMHz(int mhz)
    {
        if (Messages.RsmuSetBoostLimitAll == 0) throw new NotSupportedException("This SMU has no all-core boost limit message.");
        var st = SendRsmu(Messages.RsmuSetBoostLimitAll, Args((uint)mhz & 0xFFFFF));
        if (st != SmuStatus.Ok) Throw("Boost limit", st);
    }

    // ------------------------------------------------------------------ power table
    /// <summary>Asks the SMU where the power table lives and how big it is; picks a layout for the limits.</summary>
    public bool LocateTable(out string status)
    {
        TableAddress = 0; TableSize = 0; TableVersion = 0; Layout = null;
        if (Messages.RsmuGetTableVersion == 0 || Messages.RsmuGetDramBase == 0) { status = "no power table on this SMU"; return false; }
        var a = Args();
        if (SendRsmu(Messages.RsmuGetTableVersion, a) == SmuStatus.Ok) TableVersion = a[0];

        if (Messages == SmuMessageSet.Zen || Messages == SmuMessageSet.ZenPlus)
        {
            // Three-step handshake on the first generation.
            if (SendRsmu(Messages.RsmuGetDramBase - 1, Args()) != SmuStatus.Ok) { status = "power table address handshake refused"; return false; }
            a = Args();
            if (SendRsmu(Messages.RsmuGetDramBase, a) != SmuStatus.Ok) { status = "power table address refused"; return false; }
            TableAddress = a[0];
            SendRsmu(Messages.RsmuGetDramBase + 2, Args());
        }
        else
        {
            a = Args(1, 1);
            var st = SendRsmu(Messages.RsmuGetDramBase, a);
            if (st != SmuStatus.Ok) { status = $"power table address refused ({Describe(st)})"; return false; }
            TableAddress = ((ulong)a[1] << 32) | a[0];
        }
        if (TableAddress == 0) { status = "power table address is zero"; return false; }

        TableSize = KnownTableSize(TableVersion);
        Layout = (TableVersion >> 16) switch
        {
            0x24 => PmTableLayout.Zen2,
            0x2D or 0x38 => PmTableLayout.Zen3,
            0x54 => PmTableLayout.Zen4,
            0x62 => PmTableLayout.Zen5,
            _ => null
        };
        status = $"power table v0x{TableVersion:X8}, {TableSize} bytes at 0x{TableAddress:X}" + (Layout == null ? " (limit layout unknown for this version: PPT/TDC/EDC read-back unavailable)" : $", layout {Layout.Name}");
        return true;
    }

    private static int KnownTableSize(uint version) => version switch
    {
        0x240902 => 0x514, 0x240903 => 0x518, 0x240802 => 0x7E0, 0x240803 => 0x7E4,
        0x2D0903 => 0x594, 0x380904 => 0x5A4, 0x380905 => 0x5D0, 0x2D0803 => 0x894, 0x380804 => 0x8A4, 0x380805 => 0x8F0,
        0x540004 => 0x948, 0x540104 => 0x950, 0x540108 => 0x6BC, 0x540208 => 0x8D0,
        0x620105 => 0x724, 0x620205 => 0x994, 0x621102 => 0x724, 0x621202 => 0x994,
        _ => (version >> 16) switch { 0x73 => 0x1004, 0x5C => 0xDA8, _ => 0x994 }
    };

    /// <summary>Why the last RefreshTable returned false.</summary>
    public string? LastTableError { get; private set; }

    /// <summary>
    /// Loads the bundled RyzenSMU module into an installed PawnIO and checks it agrees with us
    /// about the table. Returns false with a reason when PawnIO is absent or refuses; the
    /// WinRing0 path stays in use then.
    /// </summary>
    public bool TryAttachPawnIo(string modulePath, out string status)
    {
        if (!PawnIo.IsInstalled) { status = "PawnIO is not installed (https://pawnio.eu); without it the SMU power table cannot be read and PPT/TDC/EDC have no read-back"; return false; }
        try
        {
            var pawn = PawnIo.LoadModule(modulePath);
            lock (_lock)
            {
                if (!Acquire()) { pawn.Dispose(); status = "PawnIO: PCI bus lock busy"; return false; }
                try
                {
                    var code = pawn.Execute("ioctl_get_code_name", Array.Empty<long>(), 1);
                    var resolved = pawn.Execute("ioctl_resolve_pm_table", Array.Empty<long>(), 2);
                    if (resolved == null || resolved.Length < 2) { pawn.Dispose(); status = $"PawnIO RyzenSMU module could not resolve the table ({pawn.LastError})"; return false; }
                    uint version = (uint)resolved[0]; ulong addr = (ulong)resolved[1];
                    if (TableVersion != 0 && version != TableVersion) { pawn.Dispose(); status = $"PawnIO reports table v0x{version:X8} but the SMU told us v0x{TableVersion:X8}; not trusting it"; return false; }
                    if (TableAddress == 0) TableAddress = addr;
                    if (TableSize == 0) TableSize = KnownTableSize(version);
                    if (TableVersion == 0) TableVersion = version;
                    _pawn = pawn;
                    TableUnreadable = false;
                    status = $"PawnIO {PawnIo.InstalledVersion} with the RyzenSMU module (code name index {(code is { Length: > 0 } ? code[0] : -1)}) reads the power table";
                    return true;
                }
                finally { Release(); }
            }
        }
        catch (Exception ex) { status = "PawnIO: " + ex.Message; return false; }
    }

    /// <summary>Tells the SMU to refresh the table in DRAM, then reads it. Returns false if either step failed.</summary>
    public bool RefreshTable()
    {
        if (TableAddress == 0 || TableSize == 0) { LastTableError = "table not located"; return false; }
        if (_pawn != null) return RefreshTableViaPawnIo();
        if (TableUnreadable) return false;
        var args = Args(Messages.IsApu ? 3u : 0u);
        var st = SendRsmu(Messages.RsmuTransferTable, args);
        if (st == SmuStatus.RejectedPrerequisite) { Thread.Sleep(10); st = SendRsmu(Messages.RsmuTransferTable, Args(Messages.IsApu ? 3u : 0u)); }
        if (st != SmuStatus.Ok) { LastTableError = "TransferTableToDram: " + Describe(st); return false; }
        var bytes = new byte[TableSize];
        if (!_drv.ReadPhysicalMemory(TableAddress, bytes))
        {
            // The WinRing0 build every monitoring tool ships was compiled without _PHYSICAL_MEMORY_SUPPORT:
            // it maps only 0xC0000-0xFFFFF (the BIOS ROM window) and answers STATUS_INVALID_PARAMETER for
            // anything else. The SMU table lives in high DRAM, so with this driver it cannot be read at all.
            TableUnreadable = true;
            LastTableError = "the driver refuses physical reads outside the BIOS ROM window" + (_drv is WinRing0Driver w ? $" ({w.LastError})" : "") +
                             "; PPT/TDC/EDC read-back is unavailable, the rows show what was written here";
            return false;
        }
        var floats = new float[TableSize / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, floats.Length * 4);
        if (floats.All(f => f == 0)) { LastTableError = "table reads as all zeros"; return false; }
        LastTableError = null;
        Table = floats;
        return true;
    }

    /// <summary>The module refreshes (TransferTableToDram) and maps the table itself; one page per call.</summary>
    private bool RefreshTableViaPawnIo()
    {
        var pawn = _pawn!;
        lock (_lock)
        {
            if (!Acquire()) { LastTableError = "PCI bus lock busy"; return false; }
            try
            {
                if (pawn.Execute("ioctl_update_pm_table", Array.Empty<long>(), 0) == null) { LastTableError = "PawnIO " + pawn.LastError; return false; }
                int longs = (TableSize + 7) / 8;
                var raw = pawn.Execute("ioctl_read_pm_table", Array.Empty<long>(), longs);
                if (raw == null || raw.Length == 0) { LastTableError = "PawnIO " + pawn.LastError; return false; }
                var floats = new float[Math.Min(TableSize / 4, raw.Length * 2)];
                Buffer.BlockCopy(raw, 0, floats, 0, floats.Length * 4);
                if (floats.All(f => f == 0)) { LastTableError = "table reads as all zeros"; return false; }
                LastTableError = null;
                Table = floats;
                return true;
            }
            finally { Release(); }
        }
    }

    private float? Field(int index) => Table != null && index >= 0 && index < Table.Length ? Table[index] : null;

    public float? PptLimit => Layout is { } l && FieldTrusted("ppt") ? Field(l.PptLimit) : null;
    public float? PptValue => Layout is { } l ? Field(l.PptValue) : null;
    public float? TdcLimit => Layout is { } l && FieldTrusted("tdc") ? Field(l.TdcLimit) : null;
    public float? TdcValue => Layout is { } l ? Field(l.TdcValue) : null;
    public float? EdcLimit => Layout is { } l && FieldTrusted("edc") ? Field(l.EdcLimit) : null;
    public float? EdcValue => Layout is { } l ? Field(l.EdcValue) : null;
    public float? ThmLimit => Layout is { } l && FieldTrusted("tctl") ? Field(l.ThmLimit) : null;
    public float? SocketPower => Layout is { } l ? Field(l.SocketPower) : null;
    public float? Fclk => Layout is { } l ? Field(l.Fclk) : null;
    public float? Uclk => Layout is { } l ? Field(l.Uclk) : null;
    public float? Mclk => Layout is { } l ? Field(l.Mclk) : null;
    public float? VddcrSoc => Layout is { } l ? Field(l.VddcrSoc) : null;
    public float? VddcrSocLive => Layout is { } l ? Field(l.VddcrSocLive) : null;
    public float? VddMisc => Layout is { } l ? Field(l.VddMisc) : null;
    public float? CldoVddp => Layout is { } l ? Field(l.CldoVddp) : null;
    public float? VddgIod => Layout is { } l ? Field(l.VddgIod) : null;
    public float? VddgCcd => Layout is { } l ? Field(l.VddgCcd) : null;
    public float? VddcrCpu => Layout is { } l ? Field(l.VddcrCpu) : null;

    /// <summary>
    /// After a limit write, checks that the table float the layout points at took the new value.
    /// A layout that does not follow is wrong for this firmware and its read-back is switched off.
    /// </summary>
    /// <returns>true when the float followed, false when it did not, null when nothing could be checked.</returns>
    public bool? VerifyLayout(string fieldName, Func<float?> field, double written)
    {
        if (Layout == null || TableUnreadable || !FieldTrusted(fieldName)) return null;
        bool read = false;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Thread.Sleep(60);
            if (!RefreshTable()) { if (TableUnreadable) return null; continue; }
            if (field() is not float f) return null; // no index for this field on this layout
            read = true;
            if (Math.Abs(f - written) < 0.5) { LayoutVerified = true; return true; }
        }
        // Only a table that was actually read and did not follow proves the index wrong.
        if (!read) return null;
        _untrusted.Add(fieldName);
        return false;
    }

    public void Dispose() { _pawn?.Dispose(); _pciMutex?.Dispose(); }
}
