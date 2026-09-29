using RochPower.Core;
using RochPower.Hardware;

int passed = 0;
void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
IntelCpu Cpu(FakeDriver d) => new(d, new[] { new LogicalCpu(0, 0, 0, true), new LogicalCpu(1, 2, 1, false) });

Test("voltage request is quantized, retained and verified", () =>
{
    var d = new FakeDriver(); var mb = new OcMailbox(d, 0);
    mb.SetOverride(0, 1.3);
    var actual = mb.ReadDomain(0);
    Check(actual.OverrideMode && Math.Abs(actual.TargetVolts - 1.3) < 1.0 / 1024, "Wrong voltage readback");
});
Test("acknowledged but ignored voltage fails and restores", () =>
{
    var d = new FakeDriver { IgnoreVoltage = true }; var mb = new OcMailbox(d, 0);
    Throws<IOException>(() => mb.SetOverride(0, 1.3));
    Check(d.Domains.GetValueOrDefault(0) == 0, "Baseline changed");
});
Test("mailbox read failure cannot masquerade as zero success", () =>
{
    var d = new FakeDriver { FailMailboxResponse = true };
    Throws<IOException>(() => new OcMailbox(d, 0).ReadDomain(0));
});
Test("negative offset preserves signed hardware encoding", () =>
{
    var mb = new OcMailbox(new FakeDriver(), 0);
    mb.SetOffset(0, -0.05);
    Check(Math.Abs(mb.ReadDomain(0).OffsetVolts + 0.05) < 1.0 / 1024, "Offset sign/precision");
});
Test("E-core write uses E thread and verifies it", () =>
{
    var d = new FakeDriver(); var cpu = Cpu(d);
    cpu.WriteECoreAllCoreRatio(43);
    Check(d.Writes.Last(w => w.reg == 0x650).cpu == 1, "Used P thread for E-core write");
});
Test("ignored P-core table fails", () =>
{
    var d = new FakeDriver { IgnoreRatio = true }; var cpu = Cpu(d);
    Throws<IOException>(() => cpu.WritePCoreAllCoreRatio(54));
});
Test("ASUS single-entry E-core table preserves unused entries", () =>
{
    var d = new FakeDriver(); d.Registers[0x650] = 44;
    Cpu(d).WriteECoreAllCoreRatio(42);
    Check(d.Registers[0x650] == 42, "Filled unused E-core entries");
    Cpu(d).WriteECoreTurboTable(new[] { 44, 0, 0, 0, 0, 0, 0, 0 });
    Check(d.Registers[0x650] == 44, "Sparse table restore failed");
    Throws<ArgumentException>(() => Cpu(d).WriteECoreTurboTable(Enumerable.Repeat(44, 8).ToArray()));
});
Test("populated MSI-style E-core groups all update", () =>
{
    var d = new FakeDriver();
    Cpu(d).WriteECoreAllCoreRatio(44);
    Check(d.Registers[0x650] == 0x2C2C2C2C2C2C2C2C, "Dropped populated E-core groups");
});
Test("rejected mailbox ceiling restores P-core table", () =>
{
    var d = new FakeDriver { IgnoreVoltage = true }; d.Domains[0] = 50;
    d.Registers[0x1AD] = 0x3232323232323232;
    var cpu = Cpu(d);
    Throws<IOException>(() => cpu.WritePCoreAllCoreRatio(55));
    Check(d.Registers[0x1AD] == 0x3232323232323232, "Partial table write not restored");
});
Test("out-of-range turbo table rejected before hardware writes", () =>
{
    var d = new FakeDriver(); var cpu = Cpu(d); d.Writes.Clear();
    Throws<ArgumentException>(() => cpu.WritePCoreAllCoreRatio(256));
    Check(d.Writes.Count == 0, "Invalid request wrote hardware");
});
Test("rejected ring mailbox restores the ring register", () =>
{
    var d = new FakeDriver { IgnoreVoltage = true }; d.Registers[0x620] = 0x0832;
    d.Domains[OcMailbox.DOMAIN_RING] = 50;
    Throws<IOException>(() => Cpu(d).WriteRingRatio(49));
    Check(d.Registers[0x620] == 0x0832, "Partial ring change not restored");
});
Test("empty ring mailbox does not reject the architectural ratio control", () =>
{
    var d = new FakeDriver { IgnoreVoltage = true }; d.Registers[0x620] = 0x0832;
    Cpu(d).WriteRingRatio(43);
    Check(d.Registers[0x620] == 0x082B, "Ring register not retained");
    Check(!d.Writes.Any(w => w.reg == 0x150 && ((w.value >> 32) & 255) == 0x11), "Wrote an empty mailbox domain");
});
Test("MSI-style ring mailbox preserves voltage and updates both ceilings", () =>
{
    var d = new FakeDriver(); d.Registers[0x620] = 0x0832;
    var settings = new VfDomainSettings { MaxRatio = 50, TargetVolts = 1.3, OverrideMode = true, OffsetVolts = -0.05 };
    uint original = OcMailbox.Encode(settings);
    d.Domains[OcMailbox.DOMAIN_RING] = original;
    Cpu(d).WriteRingRatio(49);
    Check(d.Registers[0x620] == 0x0831, "MSR ceiling not updated");
    Check(d.Domains[OcMailbox.DOMAIN_RING] == ((original & ~255u) | 49u), "Mailbox ceiling or voltage changed incorrectly");
});
Test("invalid E-core count map does not invent 255 active cores", () =>
{
    var d = new FakeDriver(); d.Registers[0x651] = ulong.MaxValue;
    Check(Cpu(d).ReadECoreTurboTable().cores.All(c => c == 0), "Invalid active core count");
});
Test("ring mailbox with voltage but automatic ratio still receives its ceiling", () =>
{
    var d = new FakeDriver(); d.Registers[0x620] = 0x0832;
    uint original = OcMailbox.Encode(new VfDomainSettings { TargetVolts = 1.3, OverrideMode = true });
    d.Domains[OcMailbox.DOMAIN_RING] = original;
    Cpu(d).WriteRingRatio(49);
    Check(d.Domains[OcMailbox.DOMAIN_RING] == (original | 49u), "Populated mailbox was skipped");
});
Test("non-finite input rejected", () =>
{
    var s = new Setting { Id = "x", Name = "x", Group = SettingGroup.Clocks };
    Check(!s.TryParse("NaN", out _) && !s.TryParse("Infinity", out _) && !s.TryParse("1e999", out _), "Non-finite accepted");
});
Test("failed readback sets error and a retry clears it", () =>
{
    double hardware = 55; bool ignore = true;
    var s = new Setting { Id = "cpu_ratio", Name = "P", Group = SettingGroup.Clocks, Min = 8, Max = 120, RequireReadBack = true,
        Read = () => hardware, Write = v => { if (!ignore) hardware = v; } };
    using var hw = new HardwareModel();
    Check(!hw.Apply(s, 54) && s.LastError != null, "Ignored request reported success");
    ignore = false;
    Check(hw.Apply(s, 54) && s.LastError == null && s.Current == 54, "Retry didn't recover");
});
Test("fractional ratios do not silently round", () =>
{
    bool wrote = false;
    var s = new Setting { Id = "cpu_ratio", Name = "P", Group = SettingGroup.Clocks, Min = 8, Max = 120, Write = _ => wrote = true };
    using var hw = new HardwareModel();
    Check(!hw.Apply(s, 54.5) && !wrote, "Fractional ratio written");
});
Test("failed refresh cannot verify against stale cached data", () =>
{
    var s = new Setting { Id = "cpu_ratio", Name = "P", Group = SettingGroup.Clocks, Min = 8, Max = 120,
        Current = 55, RequireReadBack = true, Write = _ => { }, Read = () => throw new IOException("read failed") };
    using var hw = new HardwareModel();
    Check(!hw.Apply(s, 55) && s.Current == null, "Stale cached value verified a failed read");
});
Test("reset reports failure instead of unconditional success", () =>
{
    using var hw = new HardwareModel();
    hw.Settings.Add(new Setting { Id = "cpu_ratio", Name = "P", Group = SettingGroup.Clocks, Min = 8, Max = 120,
        Current = 54, DefaultValue = 55, RequireReadBack = true, Write = _ => { }, Read = () => 54 });
    Check(hw.RestoreAllDefaults() == 1, "Reset suppressed failure");
});
const string restrictedEvent = """
<Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event"><System>
<Provider Name="Microsoft-Windows-Hyper-V-Hypervisor"/><EventID>12550</EventID>
<TimeCreated SystemTime="2026-09-16T23:58:10Z"/></System><EventData>
<Data Name="Msr">0x150</Data><Data Name="IsWrite">1</Data><Data Name="ImageName">WinRing0x64.sys</Data>
</EventData></Event>
""";
var bootUtc = new DateTime(2026, 9, 16, 23, 51, 10, DateTimeKind.Utc);
Test("Windows event identifies a blocked voltage transaction", () =>
{
    var access = HypervisorRestrictions.Parse(restrictedEvent, bootUtc);
    Check(access is { Register: 0x150, IsWrite: true, Driver: "WinRing0x64.sys" }, "Wrong event data");
    Check(HypervisorRestrictions.VoltageBlockReason(new[] { access! }) != null, "Voltage restriction not reported");
});
Test("previous-boot restrictions do not disable a new boot", () =>
{
    Check(HypervisorRestrictions.Parse(restrictedEvent, bootUtc.AddDays(1)) == null, "Stale restriction retained");
});
Test("unrelated register restrictions do not disable voltage", () =>
{
    var access = HypervisorRestrictions.Parse(restrictedEvent.Replace("0x150", "0x607"), bootUtc);
    Check(HypervisorRestrictions.VoltageBlockReason(new[] { access! }) == null, "Unrelated event disabled voltage");
    Check(HypervisorRestrictions.Parse(restrictedEvent.Replace("<EventID>12550", "<EventID>99"), bootUtc) == null, "Wrong event accepted");
});
Test("missing or malformed log data is not treated as a confirmed block", () =>
{
    Check(HypervisorRestrictions.Parse("invalid xml", bootUtc) == null, "Malformed event accepted");
    Check(HypervisorRestrictions.Parse(restrictedEvent.Replace("0x150", "invalid"), bootUtc) == null, "Invalid register accepted");
    Check(HypervisorRestrictions.VoltageBlockReason(Array.Empty<RestrictedMsrAccess>()) == null, "Empty log means blocked");
});
Test("ASUS BCLK metadata maps nominal target precisely", () =>
{
    Check(AsusBoardControl.DecodeBclk(6000) == 100, "Default mapping");
    Check(AsusBoardControl.EncodeBclk(101) == 6100, "101 MHz mapping");
    Throws<ArgumentOutOfRangeException>(() => AsusBoardControl.EncodeBclk(double.NaN));
    Throws<ArgumentException>(() => AsusBoardControl.EncodeBclk(101.001));
});
Test("ASUS BCLK steps are bounded and physical movement is verified", () =>
{
    uint raw = 6025; var writes = new List<uint>();
    AsusBoardControl.ChangeBclk(101, () => raw, v => { writes.Add(v); raw = v; },
        () => AsusBoardControl.DecodeBclk(raw) - 0.25, _ => { });
    Check(writes.SequenceEqual(new uint[] { 6075, 6100 }), "Unbounded or wrong steps");
});
Test("ASUS acknowledged but physically ignored clock restores", () =>
{
    uint raw = 6025;
    Throws<IOException>(() => AsusBoardControl.ChangeBclk(101, () => raw, v => raw = v, () => 100, _ => { }));
    Check(raw == 6025, "Failed change not rolled back");
});
Test("ASUS invalid measurement refuses all writes", () =>
{
    int writes = 0;
    Throws<IOException>(() => AsusBoardControl.ChangeBclk(101, () => 6025, _ => writes++, () => double.NaN, _ => { }));
    Check(writes == 0, "Wrote without valid measurement");
});
Test("ASUS SVID descriptors distinguish current state from defaults", () =>
{
    uint[] words = [0, 0, 999, 0x08000000, 0, 0x00FFFC19, 1, 1999,
        1, 1022, 0x08000000, 750, 250, 1, 1671,
        2, 0, 0x08000000, 0, 0, 1, 2, uint.MaxValue];
    byte[] bytes = new byte[92]; Buffer.BlockCopy(words, 0, bytes, 0, 92);
    Check(AsusBoardControl.ParseSvid(bytes, 92) == new AsusBoardControl.SvidState(999, 1022, 0), "Read defaults instead of current indices");
    Throws<IOException>(() => AsusBoardControl.ParseSvid(bytes, 88));
    bytes[48] = 0;
    Throws<IOException>(() => AsusBoardControl.ParseSvid(bytes, 92));
});
Test("ASUS Manual payload preserves offset and terminates descriptor pairs", () =>
{
    byte[] payload = AsusBoardControl.SvidPayload(new(999, 1050, 1));
    uint[] words = new uint[7]; Buffer.BlockCopy(payload, 0, words, 0, 28);
    Check(words.SequenceEqual(new uint[] { 0, 999, 1, 1050, 2, 1, uint.MaxValue }), "Wrong manual voltage encoding");
    Check(payload.Length == 4096, "Vendor expects a preallocated buffer");
    Throws<IOException>(() => AsusBoardControl.SvidPayload(new(999, 1050, 2)));
});
Test("ASUS SA descriptor uses its 700 mV minimum instead of the core minimum", () =>
{
    uint[] words = [0, 0, 0, 0x08000000, 0, 0, 1, 1000,
        1, 0, 0x08000000, 300, 700, 1, 1101,
        2, 0, 0x08000000, 0, 0, 1, 2, uint.MaxValue];
    byte[] bytes = new byte[92]; Buffer.BlockCopy(words, 0, bytes, 0, 92);
    var state = AsusBoardControl.ParseSvid(bytes, 92, AsusBoardControl.SaId);
    var target = AsusBoardControl.WithVoltageTarget(AsusBoardControl.SaId, state, 1.1);
    Check(target == new AsusBoardControl.SvidState(0, 400, 1), "SA request used core voltage encoding");
    var payload = AsusBoardControl.SvidPayload(target, AsusBoardControl.SaId);
    Check(BitConverter.ToUInt32(payload, 12) == 400, "SA payload lost the correct index");
    Throws<IOException>(() => AsusBoardControl.ParseSvid(bytes, 92, AsusBoardControl.L2Id));
});
Test("ASUS L2 voltage preserves its offset while entering Manual mode", () =>
{
    var state = new AsusBoardControl.SvidState(974, 0, 0);
    var target = AsusBoardControl.WithVoltageTarget(AsusBoardControl.L2Id, state, 1.1);
    Check(target == new AsusBoardControl.SvidState(974, 850, 1), "L2 offset changed or target mapped incorrectly");
    Throws<ArgumentException>(() => AsusBoardControl.WithVoltageTarget(AsusBoardControl.L2Id, state, 1.1005));
    Throws<ArgumentOutOfRangeException>(() => AsusBoardControl.WithVoltageTarget(AsusBoardControl.L2Id, state, double.NaN));
    Throws<ArgumentOutOfRangeException>(() => AsusBoardControl.WithVoltageTarget(0x030D0099, state, 1.1));
});
Test("ASUS SA state bounds reject core-style offsets and oversized targets", () =>
{
    Throws<IOException>(() => AsusBoardControl.SvidPayload(new(1000, 400, 1), AsusBoardControl.SaId));
    Throws<IOException>(() => AsusBoardControl.SvidPayload(new(0, 1101, 1), AsusBoardControl.SaId));
});
Test("ASUS Cache SVID matches the captured descriptor and preserves adaptive offset", () =>
{
    uint[] words = [0, 0, 999, 134217728, 0, 16776217, 1, 1999,
        1, 0, 134217728, 750, 250, 1, 1671, 2, 0, 134217728, 0, 0, 1, 2, uint.MaxValue];
    byte[] bytes = new byte[92]; Buffer.BlockCopy(words, 0, bytes, 0, 92);
    var state = AsusBoardControl.ParseSvid(bytes, 92, AsusBoardControl.RingId);
    Check(state == new AsusBoardControl.SvidState(999, 0, 0), "Cache Auto state decoded incorrectly");
    var target = AsusBoardControl.WithVoltageTarget(AsusBoardControl.RingId, state, 1.3);
    Check(target == new AsusBoardControl.SvidState(999, 1050, 1), "Cache target changed offset or selected wrong mode");
    var payload = AsusBoardControl.SvidPayload(target, AsusBoardControl.RingId);
    Check(BitConverter.ToUInt32(payload, 12) == 1050, "Cache target index differs from captured encoding");
    Throws<IOException>(() => AsusBoardControl.ParseSvid(bytes, 92, AsusBoardControl.SaId));
});
Test("DDR5 VDD above the 5 mV-step ceiling is refused, not clamped", () =>
{
    var bus = new FakePmicBus(stepMv: 5, vdd: 1.410);
    var dimm = Ddr5Dimm.Probe(bus).Single();
    Check(dimm.VddStepMv == 5 && Math.Abs(dimm.VddCeilingV - 1.435) < 1e-9, "5 mV step or its 1.435 V ceiling not detected");
    byte before = bus.Pmic[Ddr5Dimm.R_SWA_VOUT];
    Throws<ArgumentOutOfRangeException>(() => dimm.WriteVdd(1.5));
    Check(bus.Pmic[Ddr5Dimm.R_SWA_VOUT] == before, "Refused VDD request still wrote the PMIC");
    dimm.WriteVdd(1.435);
    Check(Math.Abs(dimm.ReadVdd()!.Value - 1.435) < 1e-9, "Ceiling value not written");
});
Test("DDR5 VDD at the 10 mV step reaches 1.5 V", () =>
{
    var bus = new FakePmicBus(stepMv: 10, vdd: 1.450);
    var dimm = Ddr5Dimm.Probe(bus).Single();
    Check(dimm.VddStepMv == 10 && Math.Abs(dimm.VddCeilingV - Ddr5Dimm.VddMaxV) < 1e-9, "10 mV step ceiling wrong");
    dimm.WriteVdd(1.5);
    Check(Math.Abs(dimm.ReadVdd()!.Value - 1.5) < 1e-9, "1.5 V not written at the 10 mV step");
});
Console.WriteLine($"{passed} regression checks passed; no kernel driver was opened.");

/// <summary>One DDR5 DIMM in slot A2: SPD5118 hub at 0x51 and a PMIC at 0x49 whose VDD/VDDQ step is fixed.</summary>
sealed class FakePmicBus : ISmbus
{
    public readonly byte[] Pmic = new byte[256];
    private readonly int _stepMv;
    public FakePmicBus(int stepMv, double vdd)
    {
        _stepMv = stepMv;
        Pmic[Ddr5Dimm.R_SWA_VOUT] = Code(vdd, 800, stepMv);
        Pmic[Ddr5Dimm.R_SWC_VOUT] = Code(vdd, 800, stepMv);
        Pmic[Ddr5Dimm.R_SWD_VOUT] = Code(1.8, 1500, 5);
    }
    private static byte Code(double v, int baseMv, int step) => (byte)((int)Math.Round((v * 1000 - baseMv) / step) << 1);
    private static double Volts(byte raw, int baseMv, int step) => (baseMv + step * (raw >> 1)) / 1000.0;
    public string Description => "fake PMIC bus";
    public bool ReadByte(byte address, byte command, out byte value)
    {
        value = 0;
        if (address == 0x51) { value = command == 0 ? (byte)0x51 : (byte)0; return true; }
        if (address != 0x49) return false;
        if (command == Ddr5Dimm.R_ADC_READ)
        {
            int select = (Pmic[Ddr5Dimm.R_ADC_ENABLE] >> 3) & 0xF;
            double v = select switch
            {
                Ddr5Dimm.ADC_SWA => Volts(Pmic[Ddr5Dimm.R_SWA_VOUT], 800, _stepMv),
                Ddr5Dimm.ADC_SWC => Volts(Pmic[Ddr5Dimm.R_SWC_VOUT], 800, _stepMv),
                Ddr5Dimm.ADC_SWD => Volts(Pmic[Ddr5Dimm.R_SWD_VOUT], 1500, 5),
                _ => 0
            };
            value = (byte)Math.Round(v / 0.015);
            return true;
        }
        value = Pmic[command];
        return true;
    }
    public bool WriteByte(byte address, byte command, byte value) { if (address != 0x49) return false; Pmic[command] = value; return true; }
    public bool ReadWord(byte address, byte command, out ushort value) { value = 0; return false; }
    public void Dispose() { }
}

sealed class FakeDriver : IKernelDriver
{
    public string Name => "simulation";
    public bool IsOpen => true;
    public bool IgnoreVoltage, IgnoreRatio, FailMailboxResponse;
    public Dictionary<uint, ulong> Registers = new() { [0x650] = 0x2B2B2B2B2B2B2B2B };
    public Dictionary<int, uint> Domains = new();
    public List<(uint reg, ulong value, int cpu)> Writes = new();
    private ulong response;
    private int responseReads;
    private bool mailboxWritten;
    public bool ReadMsr(uint index, out ulong value, int cpu = -1)
    {
        value = index == 0x150 ? response : Registers.GetValueOrDefault(index);
        if (index == 0x150 && mailboxWritten && FailMailboxResponse && ++responseReads == 2) return false;
        return true;
    }
    public bool WriteMsr(uint index, ulong value, int cpu = -1)
    {
        Writes.Add((index, value, cpu));
        if (index == 0x150)
        {
            int command = (int)((value >> 32) & 255), domain = (int)((value >> 40) & 255);
            if (command == 0x11 && !IgnoreVoltage) Domains[domain] = (uint)value;
            response = command == 0x10 ? Domains.GetValueOrDefault(domain) : 0;
            mailboxWritten = true; responseReads = 0;
        }
        else if (!(IgnoreRatio && index == 0x1AD)) Registers[index] = value;
        return true;
    }
    public void Dispose() { }
    public byte ReadIoPortByte(ushort p) => throw new NotSupportedException();
    public ushort ReadIoPortWord(ushort p) => throw new NotSupportedException();
    public uint ReadIoPortDword(ushort p) => throw new NotSupportedException();
    public void WriteIoPortByte(ushort p, byte v) => throw new NotSupportedException();
    public void WriteIoPortWord(ushort p, ushort v) => throw new NotSupportedException();
    public void WriteIoPortDword(ushort p, uint v) => throw new NotSupportedException();
    public bool ReadPciConfig(byte b, byte d, byte f, ushort o, out uint v) { v = 0; throw new NotSupportedException(); }
    public bool WritePciConfig(byte b, byte d, byte f, ushort o, uint v) => throw new NotSupportedException();
}
