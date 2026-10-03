using System.Reflection;
using RochPower.Hardware;

int passed = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
void Test(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
void Present(object dimm) => dimm.GetType().GetField("<HasPmic>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(dimm, true);
double? Observe(Func<double?> work, out Type? failure)
{
    try { failure = null; return work(); }
    catch (Exception ex) { failure = ex.GetType(); return null; }
}
void CheckAdcTrace(Failure failure)
{
    var oldBus = new FakeBus { NextFailure = failure }; var newBus = new FakeBus { NextFailure = failure };
    var before = new Ddr5DimmLegacy(oldBus, 0); var after = new Ddr5Dimm(newBus, 0); Present(before); Present(after);
    double? oldValue = Observe(() => before.ReadAdcVolts(0), out var oldError);
    double? newValue = Observe(() => after.ReadAdcVolts(0), out var newError);
    Check(oldValue == newValue && oldError == newError, "Legacy ADC return/exception semantics changed");
    Check(oldBus.Trace.SequenceEqual(newBus.Trace), "ADC transport operation order/count changed: " + string.Join(", ", newBus.Trace));
    if (failure != Failure.None) Check(!after.VddMeasurement.LatestAttemptSucceeded && after.VddMeasurement.LastError != null, "Failure was presented as a successful measured sample");
}
Test("calibration trace identical and cache holds actual ADC values, not PMIC targets", () =>
{
    var oldBus = new FakeBus(); var newBus = new FakeBus();
    var before = new Ddr5DimmLegacy(oldBus, 0); var after = new Ddr5Dimm(newBus, 0); Present(before); Present(after);
    before.Calibrate(); after.Calibrate();
    Check(oldBus.Trace.SequenceEqual(newBus.Trace), "Calibration added/reordered a bus operation");
    Check(after.VddStepMv == before.VddStepMv && after.VddqStepMv == before.VddqStepMv && after.VppVerified == before.VppVerified && after.CalibrationNote == before.CalibrationNote, "Calibration behavior changed");
    Check(Math.Abs(after.VddMeasurement.LastSuccessfulVolts!.Value - 1.395) < 0.000001 && after.ReadVdd() == 1.4, "Measured VDD was substituted with its programmed target");
    Check(Math.Abs(after.VddqMeasurement.LastSuccessfulVolts!.Value - 1.35) < 0.000001 && Math.Abs(after.VppMeasurement.LastSuccessfulVolts!.Value - 1.8) < 0.000001, "ADC rail mappings are incorrect");
    Check(after.VddMeasurement.LatestAttemptSucceeded && after.VddMeasurement.SampledAtUtc.HasValue && after.VddMeasurement.Source == "PMIC ADC SWA", "Actual observation metadata is missing");
});
Test("existing voltage-write verification trace unchanged and updates only the measured rail", () =>
{
    var oldBus = new FakeBus(); var newBus = new FakeBus();
    var before = new Ddr5DimmLegacy(oldBus, 0); var after = new Ddr5Dimm(newBus, 0); Present(before); Present(after);
    before.Calibrate(); after.Calibrate(); oldBus.Trace.Clear(); newBus.Trace.Clear();
    var oldQ = after.VddqMeasurement; var oldP = after.VppMeasurement; var oldA = after.VddMeasurement;
    before.WriteVdd(1.44); after.WriteVdd(1.44);
    Check(oldBus.Trace.SequenceEqual(newBus.Trace), "Apply verification added/reordered a bus operation");
    Check(Math.Abs(after.VddMeasurement.LastSuccessfulVolts!.Value - 1.44) < 0.000001 && after.VddMeasurement.SampledAtUtc > oldA.SampledAtUtc, "Existing verification observation was not captured");
    Check(ReferenceEquals(oldQ, after.VddqMeasurement) && ReferenceEquals(oldP, after.VppMeasurement), "VDD observation contaminated another rail");
});
Test("cache getters have zero bus access and retain original timestamps", () =>
{
    var bus = new FakeBus(); var dimm = new Ddr5Dimm(bus, 0); Present(dimm); dimm.Calibrate(); bus.Trace.Clear();
    var observed = dimm.VddMeasurement;
    for (int i = 0; i < 100; i++) Check(ReferenceEquals(observed, dimm.VddMeasurement) && dimm.VddqMeasurement.Source == "PMIC ADC SWC" && dimm.VppMeasurement.Source == "PMIC ADC SWD", "Reading cache mutated a record");
    Check(bus.Trace.Count == 0 && dimm.VddMeasurement.SampledAtUtc == observed.SampledAtUtc, "Getter polled hardware or made a sample look fresh");
});
foreach (var failure in Enum.GetValues<Failure>()) Test("legacy ADC trace/return preserved: " + failure, () => CheckAdcTrace(failure));
Test("latest failure preserves old successful value/time but is marked Not read", () =>
{
    var bus = new FakeBus(); var dimm = new Ddr5Dimm(bus, 0); Present(dimm); dimm.Calibrate();
    var before = dimm.VddqMeasurement; var untouched = dimm.VddMeasurement;
    bus.NextFailure = Failure.FailAdcRead; Check(dimm.ReadAdcVolts(2) == null, "Failed observation fabricated a value");
    var failed = dimm.VddqMeasurement;
    Check(!failed.LatestAttemptSucceeded && failed.LastSuccessfulVolts == before.LastSuccessfulVolts && failed.SampledAtUtc == before.SampledAtUtc && failed.LastAttemptAtUtc > before.LastAttemptAtUtc && failed.LastError != null, "Failed cache lost history or appeared current");
    Check(ReferenceEquals(untouched, dimm.VddMeasurement), "Failure leaked to another rail");
    Check(dimm.ReadAdcVolts(2) != null && dimm.VddqMeasurement.LatestAttemptSucceeded && dimm.VddqMeasurement.LastError == null, "Later genuine sample did not clear error");
});
Test("failed config restoration retains last good sample and records failure without altering return", () =>
{
    var bus = new FakeBus(); var dimm = new Ddr5Dimm(bus, 0); Present(dimm); dimm.Calibrate();
    var before = dimm.VppMeasurement; bus.NextFailure = Failure.FailRestoreWrite;
    Check(dimm.ReadAdcVolts(3) != null, "Cache changed the established ADC return behavior");
    Check(!dimm.VppMeasurement.LatestAttemptSucceeded && dimm.VppMeasurement.LastError!.Contains("restore") && dimm.VppMeasurement.SampledAtUtc == before.SampledAtUtc, "Failed restore was shown as a new valid observation");
});
Test("unknown ADC inputs are excluded from all DIMM rail caches", () =>
{
    var bus = new FakeBus(); var dimm = new Ddr5Dimm(bus, 0); Present(dimm); dimm.Calibrate();
    var a = dimm.VddMeasurement; var c = dimm.VddqMeasurement; var d = dimm.VppMeasurement;
    dimm.ReadAdcVolts(1); dimm.ReadAdcVolts(5); dimm.ReadAdcVolts(99);
    Check(ReferenceEquals(a, dimm.VddMeasurement) && ReferenceEquals(c, dimm.VddqMeasurement) && ReferenceEquals(d, dimm.VppMeasurement), "Another ADC input was relabelled as a rail");
});
Test("missing PMIC remains Not read and performs no bus operation", () =>
{
    var bus = new FakeBus(); var dimm = new Ddr5Dimm(bus, 0);
    Check(!dimm.VddMeasurement.LatestAttemptSucceeded && dimm.VddMeasurement.SampledAtUtc == null && dimm.ReadAdcVolts(0) == null && bus.Trace.Count == 0, "Missing PMIC fabricated telemetry or accessed hardware");
});
Test("set-point reads do not change measurement values or timestamps", () =>
{
    var bus = new FakeBus(); var dimm = new Ddr5Dimm(bus, 0); Present(dimm); dimm.Calibrate();
    var measurement = dimm.VddMeasurement; bus.Trace.Clear(); dimm.ReadVdd();
    Check(ReferenceEquals(measurement, dimm.VddMeasurement) && bus.Trace.SequenceEqual(new[] { "R 48:21" }), "Target read refreshed measured telemetry or added ADC access");
});
Console.WriteLine($"{passed} simulated PMIC cache checks passed; transport traces match the frozen pre-cache implementation and no real SMBus was constructed.");

enum Failure { None, FailInitialRead, FailSelectionWrite, FailSelectionReadBack, FailAdcRead, FailRestoreWrite, ThrowAdcRead, ThrowRestoreWrite }

sealed class FakeBus : ISmbus
{
    private readonly Dictionary<byte, byte> _registers = new() { [0x30] = 0, [0x21] = 120, [0x23] = 0, [0x25] = 110, [0x27] = 120 };
    public List<string> Trace { get; } = new();
    public Failure NextFailure { get; set; }
    public string Description => "Simulated PMIC; no native hardware";
    private bool Consume(Failure failure) { if (NextFailure != failure) return false; NextFailure = Failure.None; return true; }
    public bool ReadByte(byte address, byte command, out byte value)
    {
        Trace.Add($"R {address:X2}:{command:X2}"); value = 0;
        if (address != 0x48) throw new Exception("Unexpected simulated SMBus address");
        if (command == 0x30 && _registers[0x30] == 0 && Consume(Failure.FailInitialRead)) return false;
        if (command == 0x30 && _registers[0x30] != 0 && Consume(Failure.FailSelectionReadBack)) return false;
        if (command == 0x31)
        {
            if (Consume(Failure.FailAdcRead)) return false;
            if (Consume(Failure.ThrowAdcRead)) throw new IOException("Synthetic ADC read failure");
            int select = (_registers[0x30] >> 3) & 0xF;
            double volts = select switch
            {
                0 => (800 + 10 * (_registers[0x21] >> 1)) / 1000.0,
                2 => (800 + 10 * (_registers[0x25] >> 1)) / 1000.0,
                3 => (1500 + 5 * (_registers[0x27] >> 1)) / 1000.0,
                _ => 1.2
            };
            value = (byte)Math.Round(volts / 0.015); return true;
        }
        value = _registers.GetValueOrDefault(command); return true;
    }
    public bool WriteByte(byte address, byte command, byte value)
    {
        Trace.Add($"W {address:X2}:{command:X2}={value:X2}");
        if (address != 0x48) throw new Exception("Unexpected simulated SMBus address");
        if (command == 0x30 && value != 0 && Consume(Failure.FailSelectionWrite)) return false;
        if (command == 0x30 && value == 0 && Consume(Failure.FailRestoreWrite)) return false;
        if (command == 0x30 && value == 0 && Consume(Failure.ThrowRestoreWrite)) throw new IOException("Synthetic ADC restore failure");
        _registers[command] = value; return true;
    }
    public bool ReadWord(byte address, byte command, out ushort value) { value = 0; throw new Exception("Unexpected simulated word read"); }
    public void Dispose() { }
}
