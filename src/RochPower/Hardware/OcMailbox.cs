namespace RochPower.Hardware;

/// <summary>Voltage/frequency settings of one FIVR domain as reported by the OC mailbox.</summary>
public sealed class VfDomainSettings
{
    public int MaxRatio { get; set; }
    /// <summary>Static override target in volts (0 = none / adaptive).</summary>
    public double TargetVolts { get; set; }
    public bool OverrideMode { get; set; }
    /// <summary>Offset in volts, -1.0 .. +1.0 (11-bit signed in 1/1024 V).</summary>
    public double OffsetVolts { get; set; }

    public override string ToString() =>
        $"ratio={MaxRatio} mode={(OverrideMode ? "override" : "adaptive")} target={TargetVolts:0.000}V offset={OffsetVolts * 1000:+0;-0}mV";
}

/// <summary>
/// Intel overclocking mailbox (MSR 0x150). Used for per-domain voltage offsets
/// and static overrides. Works through the CPU FIVR/SVID path, so it does
/// not depend on the motherboard VRM controller.
/// </summary>
public sealed class OcMailbox
{
    public const uint MSR_OC_MAILBOX = 0x150;

    public const byte CMD_READ_VF = 0x10;
    public const byte CMD_WRITE_VF = 0x11;
    public const byte CMD_READ_ICCMAX = 0x16;
    public const byte CMD_WRITE_ICCMAX = 0x17;

    // Domain indices (Intel OC library: IA core, GT, ring, uncore, system agent).
    // Verified on a Raptor Lake-S / Z790 system: domain 4 carries the BIOS "SA Voltage" override.
    public const int DOMAIN_CORE = 0;   // IA cores (P-cores)
    public const int DOMAIN_GT = 1;     // Graphics
    public const int DOMAIN_RING = 2;   // Ring / LLC (cache)
    public const int DOMAIN_ECORE = 3;  // Uncore / E-core L2 on hybrid parts
    public const int DOMAIN_SA = 4;     // System agent

    private readonly IKernelDriver _drv;
    private readonly int _cpu;
    private readonly object _lock = new();

    public OcMailbox(IKernelDriver drv, int cpu) { _drv = drv; _cpu = cpu; }

    /// <summary>Executes one mailbox transaction. Returns the response code (0 = OK).</summary>
    public byte Execute(byte command, byte param1, byte param2, uint data, out uint result)
    {
        lock (_lock)
        {
            if (!WaitNotBusy()) throw new TimeoutException("OC mailbox busy.");
            ulong v = (1UL << 63) | ((ulong)command << 32) | ((ulong)param1 << 40) | ((ulong)param2 << 48) | data;
            if (!_drv.WriteMsr(MSR_OC_MAILBOX, v, _cpu)) throw new IOException("OC mailbox write failed (MSR 0x150 not accepted; mailbox disabled or CPU not supported).");
            if (!WaitNotBusy()) throw new TimeoutException("OC mailbox did not complete.");
            _drv.ReadMsr(MSR_OC_MAILBOX, out ulong r, _cpu);
            result = (uint)r;
            return (byte)((r >> 32) & 0xFF);
        }
    }

    private bool WaitNotBusy()
    {
        for (int i = 0; i < 1000; i++)
        {
            if (!_drv.ReadMsr(MSR_OC_MAILBOX, out ulong v, _cpu)) return false;
            if ((v >> 63) == 0) return true;
            Thread.SpinWait(200);
        }
        return false;
    }

    public bool IsAvailable
    {
        get
        {
            try { return Execute(CMD_READ_VF, DOMAIN_CORE, 0, 0, out _) == 0; }
            catch { return false; }
        }
    }

    public static string DescribeStatus(byte status) => status switch
    {
        0 => "OK",
        1 => "invalid command",
        2 => "illegal data",
        3 => "reserved",
        4 => "ratio exceeds limit",
        5 => "voltage exceeds limit",
        6 => "OC locked",
        7 => "invalid domain",
        _ => $"error 0x{status:X2}"
    };

    public VfDomainSettings ReadDomain(int domain)
    {
        byte st = Execute(CMD_READ_VF, (byte)domain, 0, 0, out uint d);
        if (st != 0) throw new IOException($"Mailbox read of domain {domain} failed: {DescribeStatus(st)}.");
        return Decode(d);
    }

    public static VfDomainSettings Decode(uint d)
    {
        int offRaw = (int)((d >> 21) & 0x7FF);
        if ((offRaw & 0x400) != 0) offRaw -= 0x800;
        return new VfDomainSettings
        {
            MaxRatio = (int)(d & 0xFF),
            TargetVolts = ((d >> 8) & 0xFFF) / 1024.0,
            OverrideMode = ((d >> 20) & 1) == 1,
            OffsetVolts = offRaw / 1024.0
        };
    }

    public static uint Encode(VfDomainSettings s)
    {
        uint target = (uint)Math.Clamp((int)Math.Round(s.TargetVolts * 1024), 0, 0xFFF);
        int off = Math.Clamp((int)Math.Round(s.OffsetVolts * 1024), -1024, 1023);
        uint offRaw = (uint)(off & 0x7FF);
        return (uint)(s.MaxRatio & 0xFF) | (target << 8) | ((s.OverrideMode ? 1u : 0u) << 20) | (offRaw << 21);
    }

    public void WriteDomain(int domain, VfDomainSettings s)
    {
        byte st = Execute(CMD_WRITE_VF, (byte)domain, 0, Encode(s), out _);
        if (st != 0) throw new IOException($"Mailbox write to domain {domain} rejected: {DescribeStatus(st)}.");
    }

    /// <summary>Sets an adaptive-mode voltage offset, keeping the domain ratio.</summary>
    public void SetOffset(int domain, double offsetVolts)
    {
        var s = ReadDomain(domain);
        s.OverrideMode = false;
        s.TargetVolts = 0;
        s.OffsetVolts = offsetVolts;
        WriteDomain(domain, s);
    }

    /// <summary>Sets a static override voltage (volts). 0 restores adaptive mode.</summary>
    public void SetOverride(int domain, double volts)
    {
        var s = ReadDomain(domain);
        if (volts <= 0) { s.OverrideMode = false; s.TargetVolts = 0; }
        else { s.OverrideMode = true; s.TargetVolts = volts; }
        WriteDomain(domain, s);
    }

    /// <summary>IccMax for a domain in amperes (mailbox 0x16, 1/4 A units).</summary>
    public double? ReadIccMax(int domain)
    {
        try
        {
            byte st = Execute(CMD_READ_ICCMAX, (byte)domain, 0, 0, out uint d);
            return st == 0 ? (d & 0x3FF) / 4.0 : null;
        }
        catch { return null; }
    }
}
