using System.Reflection;
using System.Runtime.CompilerServices;
using RochPower.Core;
using RochPower.Hardware;

var productAssembly = typeof(HardwareModel).Assembly;
Console.WriteLine("TESTED_ASSEMBLY " + System.Text.Json.JsonSerializer.Serialize(new
{
    path = productAssembly.Location,
    sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(productAssembly.Location))),
    version = productAssembly.GetName().Version?.ToString()
}));
int passed = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
void Test(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error)
    {
        Console.Error.WriteLine("FAIL " + name);
        Console.Error.WriteLine(error);
        // Report an assertion failure as a console test failure, rather than letting
        // Windows show an unhandled-exception dialog for this owned inert test host.
        Environment.Exit(1);
    }
}
void SetField(object instance, string name, object? value) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
void SetProperty(object instance, string name, object? value) => SetField(instance, "<" + name + ">k__BackingField", value);
HardwareModel FakeModel(EcFakeDriver driver, SuperIo sio)
{
    // Bypass all production constructors: no Initialize, SMBIOS, CPUID topology,
    // WinRing0Driver, application entry point, hardware detection or driver lifecycle.
    var model = (HardwareModel)RuntimeHelpers.GetUninitializedObject(typeof(HardwareModel));
    var cpu = (IntelCpu)RuntimeHelpers.GetUninitializedObject(typeof(IntelCpu));
    SetField(cpu, "_drv", driver); SetProperty(cpu, "Mailbox", new OcMailbox(driver, 0));
    var smbios = new SmbiosInfo(); SetProperty(smbios, "BoardManufacturer", "Micro-Star International");
    SetProperty(model, "Driver", driver); SetProperty(model, "SuperIo", sio);
    SetProperty(model, "Cpu", cpu); SetProperty(model, "Smbios", smbios);
    SetProperty(model, "MailboxAvailable", true); SetProperty(model, "CoreVoltageStatus", "not probed");
    SetProperty(model, "Settings", new List<Setting>
    {
        new() { Id = "core_off", Name = "Core offset", Group = SettingGroup.Voltages, Current = -20, DefaultValue = -10 },
        new() { Id = "dimm0_vdd", Name = "DIMM VDD", Group = SettingGroup.Memory, Current = 1.4, DefaultValue = 1.35 }
    });
    return model;
}
void WithModel(Action<EcFakeDriver, HardwareModel> test)
{
    var driver = new EcFakeDriver { SimulateMailboxReads = true };
    using var sio = SuperIo.ReuseVerifiedMsiEcAddress(driver, 0x4E, 0x0A20, "Nuvoton NCT6687D");
    FakeStartup.ReplaceMutex(sio, "_isaMutex");
    var model = FakeModel(driver, sio);
    try { test(driver, model); }
    finally { model.CoreVoltage?.Dispose(); }
    Check(driver.RegulatorWrites.All(w => w.reg == 0), "Startup/refresh wrote a voltage or mode register");
    Check(driver.MailboxCommands.All(command => command == OcMailbox.CMD_READ_VF), "Startup/refresh issued a mailbox target/mode command");
}
CorePageVerificationResult Initialize(HardwareModel model)
{
    // Use the same private implementation called by the public fixed-factory entry.
    // Only the factory's mutex handles differ: fake EC, isolated unnamed mutexes.
    var factoryType = typeof(HardwareModel).GetNestedType("CoreVoltageStartupFactory", BindingFlags.NonPublic)!;
    var factory = Delegate.CreateDelegate(factoryType, typeof(FakeStartup).GetMethod(nameof(FakeStartup.Create))!);
    return (CorePageVerificationResult)typeof(HardwareModel).GetMethod("InitializeCoreVoltageCore", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(model, new object[] { factory })!;
}
Setting BuildCoreRow(HardwareModel model)
{
    // The production Intel row builder's mailbox reads go to the fake driver,
    // which rejects every mailbox target/mode command before changing anything.
    model.Settings.Clear();
    typeof(HardwareModel).GetMethod("BuildIntelSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, null);
    var core = model.Settings.Single(s => s.Id == "core_v");
    model.Settings.Clear(); model.Settings.Add(core); // Refresh only the affected core target.
    return core;
}
Test("automatic PAGE 1 startup needs no prior core row; normal row builder enables genuine target", () => WithModel((driver, model) =>
{
    var previous = model.Settings.ToArray(); var result = Initialize(model);
    Check(result.Succeeded && model.CoreVoltage != null && ReferenceEquals(result, model.CoreVoltageInitializationResult), result.Status);
    Check(model.CoreVoltageStatus == "Core voltage ready.", "Successful startup status was not concise");
    Check(model.Settings.SequenceEqual(previous) && previous[0].Current == -20 && previous[0].DefaultValue == -10
        && previous[1].Current == 1.4 && previous[1].DefaultValue == 1.35, "Startup mutated unrelated target/default settings");
    var core = BuildCoreRow(model); model.RefreshAll(captureDefaults: true);
    Check(core.Available && !core.ReadOnly && core.Write != null && core.RestoreDefault != null, "Normal startup row stayed disabled or bypassed paired writers");
    Check(core.Current == 1.280 && core.DefaultValue == 1.280 && model.CoreVoltage!.VerifiedBaseline!.Value.TargetVolts == 1.280,
        "Target/default was invented or came from fake mailbox's different 0.900 V value");
    Check(driver.Page == 1 && driver.RegulatorWrites.SequenceEqual(new[] { (reg: (byte)0, value: (ushort)0), (reg: (byte)0, value: (ushort)1) }),
        "Startup/refresh failed PAGE restoration or selected more than once");
}));
Test("automatic PAGE 0 startup/default capture never write PAGE", () => WithModel((driver, model) =>
{
    driver.Page = 0; var result = Initialize(model); var core = BuildCoreRow(model); model.RefreshAll(captureDefaults: true);
    Check(result.Succeeded && result.OriginalPage == 0 && result.RestoredPage == 0 && core.Available && core.Current == 1.280 && core.DefaultValue == 1.280
        && !model.CoreVoltage!.TargetReadIsCached && driver.RegulatorWrites.Count == 0, result.Status);
}));
foreach (int mode in new[] { 0, 1, 2 })
    Test($"startup adaptive mode {mode} is editable Auto with genuine null default", () => WithModel((driver, model) =>
    {
        driver.Mode = (ushort)(mode << 4); var result = Initialize(model); var core = BuildCoreRow(model); model.RefreshAll(captureDefaults: true);
        Check(result.Succeeded && core.Available && !core.ReadOnly && core.Current == null && core.DefaultValue == null
            && model.CoreVoltage!.VerifiedBaseline!.Value.TargetMv == 1280 && !model.CoreVoltageTargetReadFailed && driver.Page == 1,
            "Adaptive state was fabricated as override, disabled, or confused with failed read");
    }));
Test("startup adaptive zero stays real Auto, rather than an unread target", () => WithModel((driver, model) =>
{
    driver.Mode = 0; driver.Target = 0; var result = Initialize(model); var core = BuildCoreRow(model); model.RefreshAll(captureDefaults: true);
    Check(result.Succeeded && core.Available && core.Current == null && core.DefaultValue == null && !model.CoreVoltageTargetReadFailed && driver.Page == 1, result.Status);
}));
Test("startup success is cached once without further I/O/page changes/baseline changes/logs", () => WithModel((driver, model) =>
{
    var log = new List<string>(); model.Log += log.Add;
    var first = Initialize(model); var control = model.CoreVoltage;
    var startupLog = log.ToArray();
    Check(startupLog.Count(line => line.StartsWith("CPU core regulator startup:")) == 1
        && startupLog.Where(line => line.StartsWith("CPU core startup I/O: "))
            .SequenceEqual(first.DiagnosticLines.Select(line => "CPU core startup I/O: " + line))
        && startupLog.Length == 1 + first.DiagnosticLines.Count,
        "Startup summary/diagnostic trace was duplicated or incomplete");
    int reads = driver.IoReads, writes = driver.IoWrites; driver.Target = 1300;
    for (int i = 0; i < 5; i++) Check(ReferenceEquals(first, Initialize(model)), "Startup result regenerated");
    Check(first.Succeeded && ReferenceEquals(control, model.CoreVoltage) && control!.VerifiedBaseline!.Value.TargetMv == 1280
        && driver.IoReads == reads && driver.IoWrites == writes && log.SequenceEqual(startupLog), "Repeated initialization re-probed, changed the baseline, or duplicated startup logs");
}));
Test("concurrent startup calls perform one bounded attempt and share the recorded result", () => WithModel((driver, model) =>
{
    var log = new List<string>(); model.Log += log.Add;
    var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() => Initialize(model))).ToArray(); Task.WaitAll(tasks);
    var first = tasks[0].Result;
    Check(first.Succeeded && tasks.All(task => ReferenceEquals(task.Result, first))
        && log.Count(line => line.StartsWith("CPU core regulator startup:")) == 1
        && log.Where(line => line.StartsWith("CPU core startup I/O: "))
            .SequenceEqual(first.DiagnosticLines.Select(line => "CPU core startup I/O: " + line))
        && log.Count == 1 + first.DiagnosticLines.Count
        && driver.RegulatorReads.Count(register => register == 0xF0) == 1
        && driver.RegulatorWrites.SequenceEqual(new[] { (reg: (byte)0, value: (ushort)0), (reg: (byte)0, value: (ushort)1) }),
        "Concurrent initialization performed multiple probes/logs/page changes");
}));
Test("PAGE 1 polling preserves cached target without PAGE writes; PAGE 0 polling becomes fresh", () => WithModel((driver, model) =>
{
    Check(Initialize(model).Succeeded, "Startup failed"); var core = BuildCoreRow(model); model.RefreshAll(captureDefaults: true); driver.RegulatorWrites.Clear();
    for (int i = 0; i < 4; i++) model.Refresh(core);
    Check(core.Current == 1.280 && model.CoreVoltage!.TargetReadIsCached && model.CoreVoltageTargetReadStatus.Contains("PAGE 1") && driver.RegulatorWrites.Count == 0,
        "Cached polling selected PAGE or read another rail");
    driver.Page = 0; driver.Target = 1300; model.Refresh(core);
    Check(core.Current == 1.300 && core.DefaultValue == 1.280 && !model.CoreVoltage!.TargetReadIsCached && driver.RegulatorWrites.Count == 0,
        "Fresh target/default or polling PAGE behavior was incorrect");
}));
Test("polling failure is unread; startup default stays genuine and adaptive null distinct", () => WithModel((driver, model) =>
{
    Check(Initialize(model).Succeeded, "Startup failed"); var core = BuildCoreRow(model); model.RefreshAll(captureDefaults: true);
    driver.RegulatorWrites.Clear(); driver.Page = 0; driver.FailModeResultOnce = true; model.RefreshAll(captureDefaults: true);
    Check(core.Current == null && core.DefaultValue == 1.280 && model.CoreVoltageTargetReadFailed
        && model.CoreVoltageTargetReadStatus.StartsWith("Core target not read:") && driver.RegulatorWrites.Count == 0,
        "Read failure fabricated Auto/default or selected PAGE");
    driver.Mode = 0; driver.Target = 0; model.Refresh(core);
    Check(core.Current == null && !model.CoreVoltageTargetReadFailed && core.DefaultValue == 1.280 && driver.RegulatorWrites.Count == 0,
        "Real adaptive null was confused with failed read");
}));
Test("unsupported polling PAGE fails closed and never retries startup selection", () => WithModel((driver, model) =>
{
    var initial = Initialize(model); var core = BuildCoreRow(model); model.RefreshAll(captureDefaults: true);
    driver.RegulatorWrites.Clear(); driver.Page = 2; model.Refresh(core); model.Refresh(core);
    Check(core.Current == null && core.DefaultValue == 1.280 && model.CoreVoltageTargetReadFailed && driver.RegulatorWrites.Count == 0
        && ReferenceEquals(initial, Initialize(model)), "Unsupported polling PAGE retried discovery or exposed another rail");
}));
Test("strict initial PAGE read failure is recorded once and never recovers silently during refresh", () => WithModel((driver, model) =>
{
    driver.FailPageResultOnce = true; var first = Initialize(model);
    Check(!first.Succeeded && first.OriginalPage == null && model.CoreVoltage == null && driver.Page == 1 && driver.RegulatorWrites.Count == 0, first.Status);
    var core = BuildCoreRow(model); int reads = driver.IoReads, writes = driver.IoWrites;
    Check(!core.Available && core.ReadOnly && core.Write == null && core.RestoreDefault == null, "Failed startup row can write an unverified rail");
    model.RefreshAll(captureDefaults: true); model.Refresh(core);
    Check(ReferenceEquals(first, Initialize(model)) && driver.IoReads == reads && driver.IoWrites == writes
        && core.Current == null && core.DefaultValue == null && model.CoreVoltageStatus.StartsWith("Core voltage unavailable:"),
        "Unavailable polling/failed initialization retried page access");
}));
foreach (string failure in new[] { "mode-read", "target-read", "secondary-read", "select-after-change", "select-mismatch", "restore-after-change", "restore-mismatch", "snapshot-drift" })
    Test($"startup {failure} leaves an unavailable row and one recorded attempt", () => WithModel((driver, model) =>
    {
        switch (failure)
        {
            case "mode-read": driver.FailModeResultOnce = true; break;
            case "target-read": driver.FailTargetResultOnce = true; break;
            case "secondary-read": driver.FailSecondaryResultOnce = true; break;
            case "select-after-change": driver.FailPageSelectOnce = true; break;
            case "select-mismatch": driver.IgnorePageSelectOnce = true; break;
            case "restore-after-change": driver.FailPageRestoreOnce = true; break;
            case "restore-mismatch": driver.IgnorePageRestoreOnce = true; break;
            case "snapshot-drift": driver.CorePageDriftOnSecondaryOnce = true; break;
        }
        var result = Initialize(model); var core = BuildCoreRow(model);
        Check(!result.Succeeded && model.CoreVoltage == null && !core.Available && core.Write == null && core.RestoreDefault == null
            && driver.Page == (failure == "restore-mismatch" ? 0 : 1), result.Status);
        int reads = driver.IoReads, writes = driver.IoWrites;
        Check(ReferenceEquals(result, Initialize(model)) && driver.IoReads == reads && driver.IoWrites == writes, "Failure retried startup");
    }));
foreach (string invalid in new[] { "unknown-page", "mode-sentinel", "low-target", "high-target", "invalid-secondary", "override-zero" })
    Test($"startup rejects {invalid} without activating a wrong/stale rail", () => WithModel((driver, model) =>
    {
        switch (invalid)
        {
            case "unknown-page": driver.Page = 2; break;
            case "mode-sentinel": driver.Mode = ushort.MaxValue; break;
            case "low-target": driver.Target = 599; break;
            case "high-target": driver.Target = 1721; break;
            case "invalid-secondary": driver.Secondary = ushort.MaxValue; break;
            case "override-zero": driver.Target = 0; break;
        }
        var result = Initialize(model); var core = BuildCoreRow(model);
        Check(!result.Succeeded && model.CoreVoltage == null && !core.Available && core.Write == null && core.DefaultValue == null
            && driver.Page == (invalid == "unknown-page" ? 2 : 1), result.Status);
        if (invalid == "unknown-page") Check(driver.RegulatorWrites.Count == 0, "Unknown original PAGE changed");
    }));
foreach (string gate in new[] { "missing-driver", "closed-driver", "missing-Intel", "mailbox-unavailable", "voltage-restriction", "missing-EC", "unsupported-EC", "unconfirmed-MSI" })
    Test($"public startup {gate} gate prevents all EC access and records one refusal", () => WithModel((driver, model) =>
    {
        switch (gate)
        {
            case "missing-driver": SetProperty(model, "Driver", null); break;
            case "closed-driver": driver.DriverOpen = false; break;
            case "missing-Intel": SetProperty(model, "Cpu", null); break;
            case "mailbox-unavailable": SetProperty(model, "MailboxAvailable", false); break;
            case "voltage-restriction": SetProperty(model, "VoltageAccessRestriction", "Voltage access is restricted by this boot."); break;
            case "missing-EC": SetProperty(model, "SuperIo", null); break;
            case "unsupported-EC":
                var wrong = (SuperIo)RuntimeHelpers.GetUninitializedObject(typeof(SuperIo)); SetProperty(wrong, "Kind", SuperIoKind.NuvotonBank); SetProperty(model, "SuperIo", wrong); break;
            case "unconfirmed-MSI": SetProperty(model.Smbios, "BoardManufacturer", "ASUS"); break;
        }
        var result = model.InitializeCoreVoltage(); // Public fixed-factory gate returns before constructing a controller.
        Check(!result.Succeeded && model.CoreVoltage == null && result.OriginalPage == null && result.Snapshot == null && !result.PageSelectionAttempted
            && driver.IoReads == 0 && driver.IoWrites == 0 && driver.MailboxCommands.Count == 0, "Capability gate bypassed: " + result.Status);
        Check(ReferenceEquals(result, model.InitializeCoreVoltage()) && driver.IoReads == 0 && driver.IoWrites == 0, "Refusal silently retried");
    }));
Test("verified MSI system identity is accepted when board maker text is absent", () => WithModel((driver, model) =>
{
    SetProperty(model.Smbios, "BoardManufacturer", ""); SetProperty(model.Smbios, "SystemManufacturer", "Micro-Star International");
    var result = Initialize(model); Check(result.Succeeded && model.CoreVoltage != null && driver.Page == 1, result.Status);
}));
Console.WriteLine($"{passed} inert startup model checks passed; no production constructor, real driver, mailbox target command or live hardware access. Bus mutexes were isolated before acquisition.");

static class FakeStartup
{
    public static void ReplaceMutex(object owner, string name)
    {
        var field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((Mutex?)field.GetValue(owner))?.Dispose(); field.SetValue(owner, new Mutex(false));
    }
    public static MsiCoreVoltage? Create(EcMailbox mailbox, out CorePageVerificationResult result)
    {
        var control = (MsiCoreVoltage)Activator.CreateInstance(typeof(MsiCoreVoltage), BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { mailbox }, null)!;
        ReplaceMutex(control, "_sequenceMutex"); ReplaceMutex(control, "_ecSequenceMutex");
        object?[] arguments = { control, null };
        var created = (MsiCoreVoltage?)typeof(MsiCoreVoltage).GetMethod("CompleteStartupVerification", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, arguments);
        result = (CorePageVerificationResult)arguments[1]!;
        return created;
    }
}
