namespace RochPower.Hardware;

/// <summary>Result code of an SMU mailbox transaction. 0x01..0xFF are the SMU's own; the rest are ours.</summary>
public enum SmuStatus : byte
{
    Ok = 0x01,
    Failed = 0xFF,
    UnknownCommand = 0xFE,
    RejectedPrerequisite = 0xFD,
    RejectedBusy = 0xFC,
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
/// The message numbers one SMU firmware family understands. Zero means "this generation has no
/// such message" and the matching control is not offered. Numbers follow ZenStates-Core
/// (Ivan Rusanov); the ones this build was tested against are marked in the comments.
/// </summary>
public sealed class SmuMessageSet
{
    public required string Family { get; init; }
    public required SmuMailbox Rsmu { get; init; }
    public required SmuMailbox Mp1 { get; init; }
    /// <summary>Host System Management Port: dead on desktop parts, offered for the raw command line only.</summary>
    public SmuMailbox Hsmp { get; init; } = new("HSMP", 0, 0, 0);
    public bool IsApu { get; init; }
    /// <summary>Curve Optimizer range: 30 before Zen 4, 50 from Zen 4 on.</summary>
    public int CoRange { get; init; } = 30;
    /// <summary>Zen 2 addresses a core as CCD/CCX/core; Zen 3 and later as CCD/core.</summary>
    public bool CcxInCoreMask { get; init; }

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
    /// The CPU's stock limits. ZenStates labels 0xDC / 0xDB / 0xD9 fused power / VDD TDC / SoC TDC; on a
    /// 9850X3D they answer 162000 / 120000 / 180000 for a part whose stock PPT / TDC / EDC are
    /// 162 W / 120 A / 180 A, so here 0xD9 is PPT, 0xDB TDC, 0xDC EDC. They do not track a written limit.
    /// </summary>
    public uint RsmuGetStockPpt { get; init; }
    public uint RsmuGetStockTdc { get; init; }
    public uint RsmuGetStockEdc { get; init; }
    public uint RsmuIsOverclockable { get; init; }
    public uint RsmuGetBoostLimit { get; init; }
    public uint RsmuSetBoostLimitAll { get; init; }
    public uint RsmuGetDramBase { get; init; }
    public uint RsmuGetTableVersion { get; init; }
    public uint RsmuGetSustainedPower { get; init; }
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

    private static readonly SmuMailbox RsmuZen1 = new("RSMU", 0x03B1051C, 0x03B10568, 0x03B10590);
    private static readonly SmuMailbox Mp1Zen1 = new("MP1", 0x03B10528, 0x03B10564, 0x03B10598);
    private static readonly SmuMailbox RsmuZen2 = new("RSMU", 0x03B10524, 0x03B10570, 0x03B10A40);
    private static readonly SmuMailbox Mp1Zen2 = new("MP1", 0x03B10530, 0x03B1057C, 0x03B109C4);
    private static readonly SmuMailbox HsmpZen2 = new("HSMP", 0x03B10534, 0x03B10980, 0x03B109E0);
    private static readonly SmuMailbox HsmpZen5 = new("HSMP", 0x03B10934, 0x03B10980, 0x03B109E0);
    private static readonly SmuMailbox RsmuShimada = new("RSMU", 0x03B10924, 0x03B10970, 0x03B10A40);
    private static readonly SmuMailbox RsmuApu = new("RSMU", 0x03B10A20, 0x03B10A80, 0x03B10A88);
    private static readonly SmuMailbox Mp1Apu = new("MP1", 0x03B10528, 0x03B10564, 0x03B10998);
    private static readonly SmuMailbox Mp1Phoenix = new("MP1", 0x03B10528, 0x03B10578, 0x03B10998);
    private static readonly SmuMailbox Mp1Strix = new("MP1", 0x03B10928, 0x03B10978, 0x03B10998);
    private static readonly SmuMailbox None = new("none", 0, 0, 0);

    /// <summary>Summit Ridge, Naples, Whitehaven.</summary>
    public static readonly SmuMessageSet Zen = new()
    {
        Family = "Zen", Rsmu = RsmuZen1, Mp1 = Mp1Zen1, CcxInCoreMask = true,
        RsmuSetPpt = 0x58, Mp1SetPpt = 0x31, Mp1SetTdc = 0x29, Mp1SetEdc = 0x2A,
        RsmuGetDramBase = 0xC, RsmuGetTableVersion = 0xD,
        RsmuGetSustainedPower = 0x5F, Mp1GetSustainedPower = 0x36,
    };

    /// <summary>Pinnacle Ridge, Colfax.</summary>
    public static readonly SmuMessageSet ZenPlus = new()
    {
        Family = "Zen+", Rsmu = RsmuZen1, Mp1 = Mp1Zen1, CcxInCoreMask = true,
        RsmuSetPpt = 0x64, RsmuSetTdc = 0x65, RsmuSetEdc = 0x66, RsmuSetTctlMax = 0x68,
        RsmuSetScalar = 0x6A, RsmuGetScalar = 0x6F,
        RsmuGetDramBase = 0xC, RsmuGetTableVersion = 0xD,
    };

    /// <summary>Matisse, Castle Peak, Rome.</summary>
    public static readonly SmuMessageSet Zen2 = new()
    {
        Family = "Zen 2", Rsmu = RsmuZen2, Mp1 = Mp1Zen2, Hsmp = HsmpZen2, CcxInCoreMask = true,
        RsmuSetPpt = 0x53, RsmuSetTdc = 0x54, RsmuSetEdc = 0x55, RsmuSetTctlMax = 0x56,
        RsmuSetScalar = 0x58, RsmuGetScalar = 0x6C, RsmuIsOverclockable = 0x6F, RsmuGetBoostLimit = 0x6E,
        RsmuGetDramBase = 0x6, RsmuGetTableVersion = 0x8,
        Mp1SetPpt = 0x3C, Mp1SetTdc = 0x3A, Mp1SetEdc = 0x3B, Mp1SetScalar = 0x2F, Mp1SetPboEnable = 0x33,
        Mp1SetCoMargin = 0x34, Mp1SetCoMarginAll = 0x35, Mp1GetSustainedPower = 0x23,
    };

    /// <summary>Vermeer, Chagall, Milan.</summary>
    public static readonly SmuMessageSet Zen3 = new()
    {
        Family = "Zen 3", Rsmu = RsmuZen2, Mp1 = Mp1Zen2, Hsmp = HsmpZen2,
        RsmuSetPpt = 0x53, RsmuSetTdc = 0x54, RsmuSetEdc = 0x55, RsmuSetTctlMax = 0x56,
        RsmuSetScalar = 0x58, RsmuGetScalar = 0x6C, RsmuIsOverclockable = 0x6F, RsmuGetBoostLimit = 0x6E,
        RsmuSetCoMargin = 0xA, RsmuSetCoMarginAll = 0xB, RsmuGetCoMargin = 0x7C,
        RsmuGetDramBase = 0x6, RsmuGetTableVersion = 0x8,
        Mp1SetPpt = 0x3D, Mp1SetTdc = 0x3B, Mp1SetEdc = 0x3C, Mp1SetScalar = 0x2F, Mp1SetPboEnable = 0x33,
        Mp1SetCoMargin = 0x35, Mp1SetCoMarginAll = 0x36, Mp1GetSustainedPower = 0x23,
    };

    /// <summary>Zen 4 and Zen 5 share one numbering; only the mailboxes and the read-back message differ. Tested on a 9850X3D.</summary>
    private static SmuMessageSet Zen4Family(string family, SmuMailbox rsmu, SmuMailbox mp1, SmuMailbox hsmp, uint getCoMargin) => new()
    {
        Family = family, Rsmu = rsmu, Mp1 = mp1, Hsmp = hsmp, CoRange = 50,
        RsmuSetPpt = 0x56, RsmuSetTdc = 0x57, RsmuSetEdc = 0x58, RsmuSetTctlMax = 0x59,
        RsmuSetScalar = 0x5B, RsmuGetScalar = 0x6D, RsmuIsOverclockable = 0x6F,
        RsmuGetBoostLimit = 0x6E, RsmuSetBoostLimitAll = 0x70,
        RsmuSetCoMargin = 0x6, RsmuSetCoMarginAll = 0x7, RsmuGetCoMargin = getCoMargin,
        RsmuGetStockPpt = 0xD9, RsmuGetStockTdc = 0xDB, RsmuGetStockEdc = 0xDC,
        RsmuGetDramBase = 0x4, RsmuGetTableVersion = 0x5,
        Mp1SetPpt = 0x3E, Mp1SetTdc = 0x3C, Mp1SetEdc = 0x3D, Mp1SetScalar = 0x2F,
        Mp1SetCoMargin = 0x35, Mp1SetCoMarginAll = 0x36, Mp1GetSustainedPower = 0x23,
    };

    /// <summary>Raphael, Dragon Range, Genoa, Storm Peak.</summary>
    public static readonly SmuMessageSet Zen4 = Zen4Family("Zen 4", RsmuZen2, Mp1Zen2, HsmpZen2, 0xD5);
    /// <summary>Granite Ridge, Turin, Bergamo.</summary>
    public static readonly SmuMessageSet Zen5 = Zen4Family("Zen 5", RsmuZen2, Mp1Zen2, HsmpZen5, 0xD5);
    /// <summary>Shimada Peak (Threadripper 9000): RSMU moved, MP1 unknown, different read-back message.</summary>
    public static readonly SmuMessageSet ShimadaPeak = Zen4Family("Zen 5 (Shimada Peak)", RsmuShimada, None, None, 0xA3);

    /// <summary>Renoir, Lucienne, Cezanne, Van Gogh, Mendocino. Numbers per ZenStates, untested here.</summary>
    public static SmuMessageSet Apu(string family, uint getCoMargin) => new()
    {
        Family = family, Rsmu = RsmuApu, Mp1 = Mp1Apu, IsApu = true,
        RsmuSetPpt = 0x32, RsmuSetTdc = 0x38, RsmuSetEdc = 0x3A, RsmuSetTctlMax = 0x37,
        RsmuSetScalar = 0x3F, RsmuGetScalar = 0xF, RsmuIsOverclockable = 0x82, RsmuGetBoostLimit = 0x42,
        RsmuSetCoMargin = 0x52, RsmuSetCoMarginAll = 0xB1, RsmuGetCoMargin = getCoMargin,
        RsmuGetStockPpt = 0x13, RsmuGetStockTdc = 0x15,
        RsmuGetDramBase = 0x66, RsmuGetTableVersion = 0x6,
        Mp1SetPpt = 0x15, Mp1SetTdc = 0x1A, Mp1SetEdc = 0x1C, Mp1SetScalar = 0x49,
        Mp1SetCoMargin = 0x54, Mp1SetCoMarginAll = 0x55, Mp1GetSustainedPower = 0x5B,
    };

    /// <summary>Rembrandt, Phoenix, Hawk Point, Strix Point, Strix Halo, Krackan Point. Numbers per ZenStates, untested here.</summary>
    public static SmuMessageSet ApuPhoenix(string family, uint getCoMargin, bool strix) => new()
    {
        Family = family, Rsmu = RsmuApu, Mp1 = strix ? Mp1Strix : Mp1Phoenix, IsApu = true, CoRange = 50,
        RsmuSetPpt = 0x32, RsmuSetTdc = 0x38, RsmuSetEdc = 0x3A, RsmuSetTctlMax = 0x37,
        RsmuSetScalar = 0x3E, RsmuGetScalar = 0xF, RsmuIsOverclockable = 0x82, RsmuGetBoostLimit = 0x42, RsmuSetBoostLimitAll = 0x47,
        RsmuSetCoMargin = 0x53, RsmuSetCoMarginAll = 0x5D, RsmuGetCoMargin = getCoMargin,
        RsmuGetStockPpt = 0x13, RsmuGetStockTdc = 0x15,
        RsmuGetDramBase = 0x66, RsmuGetTableVersion = 0x6,
        Mp1SetPpt = 0x15, Mp1SetTdc = 0x1A, Mp1SetEdc = 0x1C, Mp1SetScalar = 0x63,
        Mp1SetCoMargin = 0x4B, Mp1SetCoMarginAll = 0x4C, Mp1GetSustainedPower = 0x5F,
    };
}

/// <summary>
/// Where the limits live in the SMU power table, as float indices (-1 = not known). After every
/// limit write the matching float is checked; one that does not follow is dropped from read-back.
/// </summary>
public sealed record PmTableLayout(string Name, int PptLimit, int PptValue, int TdcLimit, int TdcValue, int EdcLimit, int EdcValue, int ThmLimit, int ThmValue, int SocketPower)
{
    /// <summary>ryzen_smu's layout: limit/value pairs for PPT, TDC, THM, FIT, EDC from the top.</summary>
    public static readonly PmTableLayout Zen2Zen3 = new("Zen 2 / Zen 3 desktop", 0, 1, 2, 3, 8, 9, 4, 5, -1);
    /// <summary>
    /// Measured on a 9850X3D (table 0x00620105) with PPT 300 / TDC 200 / EDC 480 / Tctl 95 in force:
    /// 300 sat at float 2, 200 at 8, 95 at 10, 480 at 63, each followed by its live value; 26 is total socket power.
    /// </summary>
    public static readonly PmTableLayout Zen5 = new("Zen 5 desktop", 2, 3, 8, 9, 63, 64, 10, 11, 26);
    /// <summary>Zen 4 shares the first part of that layout (LibreHardwareMonitor's indices agree); the EDC position is not known.</summary>
    public static readonly PmTableLayout Zen4 = new("Zen 4 desktop (PPT/TDC/THM only)", 2, 3, 8, 9, -1, -1, 10, 11, 26);
}

/// <summary>
/// AMD System Management Unit access: SMN registers through the north bridge index/data pair,
/// the RSMU / MP1 mailboxes, and the power table the SMU publishes in DRAM. The path Ryzen
/// Master and every third-party Ryzen tool uses; nothing here is board specific.
/// </summary>
public sealed class AmdSmu : IDisposable
{
    private const ushort SMN_INDEX = 0x60, SMN_DATA = 0x64; // D0F0 index/data pair
    private const int MailboxArgs = 6;
    private const int MailboxTimeoutIterations = 8192;

    private readonly IKernelDriver _drv;
    private readonly Mutex? _pciMutex;
    private readonly object _lock = new();
    private PawnIo? _pawn;
    private readonly HashSet<string> _untrusted = new();

    public SmuMessageSet Messages { get; }
    public uint Version { get; private set; }
    public string VersionText => Version == 0 ? "unknown" : $"{Version >> 24 & 0xFF}.{Version >> 16 & 0xFF}.{Version >> 8 & 0xFF}.{Version & 0xFF}";
    public string Status { get; private set; } = "not probed";

    public uint TableVersion { get; private set; }
    public ulong TableAddress { get; private set; }
    public int TableSize { get; private set; }
    public PmTableLayout? Layout { get; private set; }
    public float[]? Table { get; private set; }
    /// <summary>True once PawnIO holds the RyzenSMU module; without it the table cannot be read.</summary>
    public bool TableReadable => _pawn != null;
    public string? LastTableError { get; private set; }

    public AmdSmu(IKernelDriver drv, SmuMessageSet messages)
    {
        _drv = drv;
        Messages = messages;
        // HWiNFO, Ryzen Master, LibreHardwareMonitor and ZenStates all take this mutex around SMN
        // accesses; two tools racing the index/data pair corrupt each other's reads.
        try { _pciMutex = new Mutex(false, @"Global\Access_PCI"); }
        catch (UnauthorizedAccessException) { try { _pciMutex = Mutex.OpenExisting(@"Global\Access_PCI"); } catch { _pciMutex = null; } }
        catch { _pciMutex = null; }
    }

    // ------------------------------------------------------------------ SMN
    private bool Acquire()
    {
        try { return _pciMutex?.WaitOne(5000) ?? true; }
        catch (AbandonedMutexException) { return true; }
        catch { return false; }
    }

    private void Release() { try { _pciMutex?.ReleaseMutex(); } catch { } }

    private bool SmnReadNoLock(uint address, out uint value)
    {
        if (!_drv.WritePciConfig(0, 0, 0, SMN_INDEX, address)) { value = 0; return false; }
        bool ok = _drv.ReadPciConfig(0, 0, 0, SMN_DATA, out value);
        return ok || value == 0xFFFFFFFF; // the driver client calls all-ones "absent"; here it is a legal value
    }

    private bool SmnWriteNoLock(uint address, uint value) =>
        _drv.WritePciConfig(0, 0, 0, SMN_INDEX, address) && _drv.WritePciConfig(0, 0, 0, SMN_DATA, value);

    /// <summary>Reads one SMN register under the global PCI mutex; null on failure.</summary>
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
            if (SmnReadNoLock(mb.Rsp, out uint rsp) && rsp != 0) return true;
        return false;
    }

    /// <summary>
    /// One transaction: wait for the previous response, clear the response register, write the
    /// arguments and the message, wait, then read the status and the arguments back.
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

    /// <summary>Sends to the RSMU and, when that mailbox lacks the message or refuses, to MP1.</summary>
    private SmuStatus SendEither(uint rsmuMsg, uint mp1Msg, uint arg)
    {
        var st = SmuStatus.NotImplemented;
        if (rsmuMsg != 0) st = SendRsmu(rsmuMsg, Args(arg));
        if (st != SmuStatus.Ok && mp1Msg != 0) st = SendMp1(mp1Msg, Args(arg));
        return st;
    }

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
        if (st != SmuStatus.Ok || a[0] != 8) { Status = $"RSMU mailbox at 0x{Messages.Rsmu.Msg:X8} not answering ({Describe(st)})"; return false; }
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
    /// <summary>Limits take milliwatts / milliamperes. Mobile SMUs want the MP1 variant first.</summary>
    private void SetLimit(string name, uint rsmuMsg, uint mp1Msg, double value)
    {
        uint raw = (uint)Math.Round(value * 1000);
        var st = Messages.IsApu && mp1Msg != 0 ? SendMp1(mp1Msg, Args(raw)) : SmuStatus.NotImplemented;
        if (st != SmuStatus.Ok) st = SendEither(rsmuMsg, mp1Msg, raw);
        if (st != SmuStatus.Ok) Throw(name, st);
    }

    public void SetPpt(double watts) => SetLimit("PPT", Messages.RsmuSetPpt, Messages.Mp1SetPpt, watts);
    public void SetTdc(double amps) => SetLimit("TDC", Messages.RsmuSetTdc, Messages.Mp1SetTdc, amps);
    public void SetEdc(double amps) => SetLimit("EDC", Messages.RsmuSetEdc, Messages.Mp1SetEdc, amps);

    public void SetTctlMax(double celsius)
    {
        var st = SendEither(Messages.RsmuSetTctlMax, 0, (uint)Math.Round(celsius));
        if (st != SmuStatus.Ok) Throw("Tctl max", st);
    }

    /// <summary>
    /// The CPU's stock PPT (W), TDC (A) and EDC (A) as fused into the part. Constants: writing 150 W
    /// and asking again still gives 162 on a 9850X3D, so they are what "0" restores, not a read-back.
    /// </summary>
    public (double? ppt, double? tdc, double? edc) ReadStockLimits()
    {
        double? Q(uint msg)
        {
            if (msg == 0) return null;
            var a = Args();
            return SendRsmu(msg, a) == SmuStatus.Ok && a[0] != 0 ? a[0] / 1000.0 : null;
        }
        return (Q(Messages.RsmuGetStockPpt), Q(Messages.RsmuGetStockTdc), Q(Messages.RsmuGetStockEdc));
    }

    /// <summary>Sustained power limit (W) and thermal limit (C) the platform configured.</summary>
    public (int power, int temp)? ReadSustainedLimits()
    {
        var a = Args();
        var st = Messages.RsmuGetSustainedPower != 0 ? SendRsmu(Messages.RsmuGetSustainedPower, a)
               : Messages.Mp1GetSustainedPower != 0 ? SendMp1(Messages.Mp1GetSustainedPower, a) : SmuStatus.NotImplemented;
        if (st != SmuStatus.Ok) return null;
        int power = (int)((a[0] >> 16) & 0xFF), temp = (int)(a[0] & 0xFF);
        return power > 0 ? (power, temp) : null;
    }

    // ------------------------------------------------------------------ scalar
    /// <summary>PBO scalar 1..10. Null when unreadable; 0 means the SMU is in manual OC mode.</summary>
    public double? ReadScalar()
    {
        if (Messages.RsmuGetScalar == 0) return null;
        var a = Args();
        if (SendRsmu(Messages.RsmuGetScalar, a) != SmuStatus.Ok) return null;
        float f = BitConverter.Int32BitsToSingle((int)a[0]);
        return float.IsNaN(f) || f < 0 || f > 10 ? null : Math.Round(f, 2);
    }

    public void SetScalar(double scalar)
    {
        if (Messages.Mp1SetPboEnable != 0) SendMp1(Messages.Mp1SetPboEnable, Args()); // Zen 2 / Zen 3: allow PBO overdrive
        var st = SendEither(Messages.RsmuSetScalar, Messages.Mp1SetScalar, (uint)Math.Round(scalar * 100));
        if (st != SmuStatus.Ok) Throw("PBO scalar", st);
    }

    // ------------------------------------------------------------------ curve optimizer
    /// <summary>The core address the SMU wants: CCD in bits 31:28, CCX in 27:24 (Zen 2 only), core in 23:20. APUs take the plain index.</summary>
    public uint CoreMask(int ccd, int coreInCcd)
    {
        if (Messages.IsApu) return (uint)coreInCcd;
        if (Messages.CcxInCoreMask) return ((uint)ccd << 28) | ((uint)(coreInCcd / 4) << 24) | ((uint)(coreInCcd % 4) << 20);
        return ((uint)ccd << 28) | ((uint)(coreInCcd % 8) << 20);
    }

    /// <summary>Margin as the SMU encodes it: 16-bit two's complement in the low half of the argument.</summary>
    private static uint MarginArg(int margin) => (uint)(margin < 0 ? 0x10000 + margin : margin) & 0xFFFF;

    /// <summary>MP1 is preferred for the Curve Optimizer, as ZenStates does; RSMU is the fallback.</summary>
    private SmuStatus SendCo(uint mp1Msg, uint rsmuMsg, uint arg) =>
        mp1Msg != 0 ? SendMp1(mp1Msg, Args(arg)) : rsmuMsg != 0 ? SendRsmu(rsmuMsg, Args(arg)) : SmuStatus.NotImplemented;

    public void SetCurveOptimizer(int ccd, int coreInCcd, int margin)
    {
        margin = Math.Clamp(margin, -Messages.CoRange, Messages.CoRange);
        var st = SendCo(Messages.Mp1SetCoMargin, Messages.RsmuSetCoMargin, (CoreMask(ccd, coreInCcd) & 0xFFF00000) | MarginArg(margin));
        if (st != SmuStatus.Ok) Throw($"Curve Optimizer core {ccd}/{coreInCcd}", st);
    }

    public void SetCurveOptimizerAll(int margin)
    {
        margin = Math.Clamp(margin, -Messages.CoRange, Messages.CoRange);
        var st = SendCo(Messages.Mp1SetCoMarginAll, Messages.RsmuSetCoMarginAll, MarginArg(margin));
        if (st != SmuStatus.Ok) Throw("Curve Optimizer (all cores)", st);
    }

    /// <summary>Current margin of one core, or null when this firmware cannot report it.</summary>
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
        var st = SendRsmu(Messages.RsmuSetBoostLimitAll, Args((uint)mhz & 0xFFFFF));
        if (st != SmuStatus.Ok) Throw("FMax", st);
    }

    // ------------------------------------------------------------------ power table
    /// <summary>Asks the SMU for the table version and address and picks a layout for the limits.</summary>
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
            0x24 or 0x2D or 0x38 => PmTableLayout.Zen2Zen3,
            0x54 => PmTableLayout.Zen4,
            0x62 => PmTableLayout.Zen5,
            _ => null
        };
        status = $"power table v0x{TableVersion:X8}, {TableSize} bytes at 0x{TableAddress:X}" + (Layout == null ? ", limit layout unknown for this version" : $", layout {Layout.Name}");
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

    /// <summary>
    /// Loads the bundled RyzenSMU module into an installed PawnIO, which reads the table in the kernel.
    /// The bundled WinRing0 cannot: its physical-memory read is limited to the BIOS ROM window.
    /// </summary>
    public bool TryAttachPawnIo(string modulePath, out string status)
    {
        if (!PawnIo.IsInstalled) { status = "PawnIO is not installed (https://pawnio.eu), so the SMU power table cannot be read and PPT/TDC/EDC have no read-back"; return false; }
        try
        {
            var pawn = PawnIo.LoadModule(modulePath);
            lock (_lock)
            {
                if (!Acquire()) { pawn.Dispose(); status = "PawnIO: PCI bus lock busy"; return false; }
                try
                {
                    var resolved = pawn.Execute("ioctl_resolve_pm_table", Array.Empty<long>(), 2);
                    if (resolved == null || resolved.Length < 2) { pawn.Dispose(); status = $"PawnIO RyzenSMU module could not resolve the table ({pawn.LastError})"; return false; }
                    uint version = (uint)resolved[0];
                    if (TableVersion != 0 && version != TableVersion) { pawn.Dispose(); status = $"PawnIO reports table v0x{version:X8} but the SMU told us v0x{TableVersion:X8}; not trusting it"; return false; }
                    if (TableAddress == 0) TableAddress = (ulong)resolved[1];
                    if (TableVersion == 0) { TableVersion = version; TableSize = KnownTableSize(version); }
                    _pawn = pawn;
                    status = $"PawnIO {PawnIo.InstalledVersion} with the RyzenSMU module reads the power table";
                    return true;
                }
                finally { Release(); }
            }
        }
        catch (Exception ex) { status = "PawnIO: " + ex.Message; return false; }
    }

    /// <summary>Has the module refresh the table (TransferTableToDram) and reads it; one page per call.</summary>
    public bool RefreshTable()
    {
        if (_pawn is not { } pawn) { LastTableError = "PawnIO not attached"; return false; }
        if (TableSize == 0) { LastTableError = "table not located"; return false; }
        lock (_lock)
        {
            if (!Acquire()) { LastTableError = "PCI bus lock busy"; return false; }
            try
            {
                if (pawn.Execute("ioctl_update_pm_table", Array.Empty<long>(), 0) == null) { LastTableError = "PawnIO " + pawn.LastError; return false; }
                var raw = pawn.Execute("ioctl_read_pm_table", Array.Empty<long>(), (TableSize + 7) / 8);
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
    private float? Limit(string name, int index) => _untrusted.Contains(name) ? null : Field(index);

    public float? PptLimit => Layout is { } l ? Limit("ppt", l.PptLimit) : null;
    public float? PptValue => Layout is { } l ? Field(l.PptValue) : null;
    public float? TdcLimit => Layout is { } l ? Limit("tdc", l.TdcLimit) : null;
    public float? TdcValue => Layout is { } l ? Field(l.TdcValue) : null;
    public float? EdcLimit => Layout is { } l ? Limit("edc", l.EdcLimit) : null;
    public float? EdcValue => Layout is { } l ? Field(l.EdcValue) : null;
    public float? ThmLimit => Layout is { } l ? Limit("tctl", l.ThmLimit) : null;
    public float? SocketPower => Layout is { } l ? Field(l.SocketPower) : null;

    /// <summary>
    /// After a limit write, checks that the table float the layout points at took the new value.
    /// True: it followed. False: it did not, and that field is no longer read back. Null: nothing to check.
    /// </summary>
    public bool? VerifyLayout(string field, Func<float?> read, double written)
    {
        if (Layout == null || _pawn == null || _untrusted.Contains(field)) return null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Thread.Sleep(60);
            if (!RefreshTable()) continue;
            if (read() is not float f) return null; // no index for this field on this layout
            if (Math.Abs(f - written) < 0.5) return true;
        }
        if (Table == null) return null;
        _untrusted.Add(field);
        return false;
    }

    public void Dispose() { _pawn?.Dispose(); _pciMutex?.Dispose(); }
}
