using System.Diagnostics;
using System.Reflection;
using RochPower.Hardware;

var productAssembly = typeof(MsiCoreVoltage).Assembly;
Console.WriteLine("TESTED_ASSEMBLY " + System.Text.Json.JsonSerializer.Serialize(new
{
    path = productAssembly.Location,
    sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(productAssembly.Location))),
    version = productAssembly.GetName().Version?.ToString()
}));
int passed = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
void Test(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
void ReplaceMutex(object owner, string name)
{
    var field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    ((Mutex?)field.GetValue(owner))?.Dispose(); field.SetValue(owner, new Mutex(false));
}
SuperIo FakeSio(EcFakeDriver driver)
{
    var sio = SuperIo.ReuseVerifiedMsiEcAddress(driver, 0x4E, 0x0A20, "Nuvoton NCT6687D");
    ReplaceMutex(sio, "_isaMutex"); return sio;
}
MsiCoreVoltage? Startup(EcMailbox mailbox, out CorePageVerificationResult result)
{
    // Exercise the production startup factory's common completion using only a fake
    // mailbox. Replace all global mutex handles before acquiring any bus lease.
    var control = (MsiCoreVoltage)Activator.CreateInstance(typeof(MsiCoreVoltage), BindingFlags.Instance | BindingFlags.NonPublic,
        null, new object[] { mailbox }, null)!;
    ReplaceMutex(control, "_sequenceMutex"); ReplaceMutex(control, "_ecSequenceMutex");
    object?[] arguments = { control, null };
    var created = (MsiCoreVoltage?)typeof(MsiCoreVoltage).GetMethod("CompleteStartupVerification", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, arguments);
    result = (CorePageVerificationResult)arguments[1]!; return created;
}
CorePageVerificationResult Probe(EcFakeDriver driver)
{
    using var sio = FakeSio(driver);
    return CorePageProbe.Verify(new Transport(new EcMailbox(sio, strictIo: true)), () => true, () => { });
}

Test("strict failed initial PAGE result cannot fabricate zero or restore another page", () =>
{
    var driver = new EcFakeDriver { FailPageResultOnce = true }; var result = Probe(driver);
    Check(!result.Succeeded && result.OriginalPage == null && driver.Page == 1, result.Status);
    Check(driver.RegulatorWrites.Count == 0 && driver.EcSelectedPage == 0xFF, "Unknown original PAGE caused a write or EC index page was stranded");
});
Test("strict failed core snapshot restores the genuinely saved PAGE", () =>
{
    var driver = new EcFakeDriver { FailModeResultOnce = true }; var result = Probe(driver);
    Check(!result.Succeeded && result.OriginalPage == 1 && result.RestoredPage == 1 && result.Snapshot == null, result.Status);
    Check(driver.RegulatorWrites.SequenceEqual(new[] { (reg: (byte)0, value: (ushort)0), (reg: (byte)0, value: (ushort)1) }), "Failure changed a voltage/mode register");
});
Test("strict low-level successful probe reads actual page 0 and restores page 1", () =>
{
    var driver = new EcFakeDriver(); var result = Probe(driver);
    Check(result.Succeeded && result.Snapshot == new CorePageSnapshot(0x30, 1280, 0) && driver.Page == 1, result.Status);
    Check(driver.RegulatorWrites.All(write => write.reg == 0), "Diagnostic wrote a voltage/mode target");
});
Test("strict EC command readback failure is propagated", () =>
{
    var driver = new EcFakeDriver { FailCommandResultOnce = true }; var result = Probe(driver);
    Check(!result.Succeeded && result.OriginalPage == null && driver.RegulatorWrites.Count == 0, result.Status);
});
Test("verified controller refresh on page 1 uses genuine cached core state without PAGE writes", () =>
{
    var driver = new EcFakeDriver();
    using var sio = FakeSio(driver);
    using var control = Startup(new EcMailbox(sio), out var result);
    Check(result.Succeeded && control != null, result.Status);
    driver.RegulatorWrites.Clear();
    Check(control!.ReadTargetVolts() == 1.280 && control.TargetReadIsCached && driver.Page == 1, "Cached core target was replaced with another rail or unknown state");
    Check(driver.RegulatorWrites.Count == 0, "Periodic target refresh selected a PAGE");
});
Test("explicit apply state read preserves original PAGE", () =>
{
    var driver = new EcFakeDriver();
    using var sio = FakeSio(driver);
    using var control = Startup(new EcMailbox(sio), out var result);
    driver.RegulatorWrites.Clear(); var state = control!.ReadState();
    Check(state.TargetMv == 1280 && driver.Page == 1 && driver.RegulatorWrites.All(write => write.reg == 0), result.Status);
});
void WithControl(Action<EcFakeDriver, MsiCoreVoltage> test)
{
    var driver = new EcFakeDriver();
    using var sio = FakeSio(driver);
    using var control = Startup(new EcMailbox(sio), out var result);
    Check(result.Succeeded && control != null, result.Status);
    driver.RegulatorWrites.Clear();
    test(driver, control!);
}
void MustFail(Action action) { bool failed = false; try { action(); } catch { failed = true; } Check(failed, "Failure was incorrectly reported successful"); }
Test("automatic startup on PAGE 1 captures core target and restores PAGE with only PAGE writes", () =>
{
    var driver = new EcFakeDriver();
    using var sio = FakeSio(driver);
    using var control = Startup(new EcMailbox(sio), out var result);
    Check(control != null && result.Succeeded && control.VerifiedBaseline!.Value.TargetVolts == 1.280
        && driver.Page == 1 && driver.RegulatorWrites.SequenceEqual(new[] { (reg: (byte)0, value: (ushort)0), (reg: (byte)0, value: (ushort)1) }), result.Status);
});
Test("automatic startup on PAGE 0 captures genuine baseline without PAGE writes", () =>
{
    var driver = new EcFakeDriver { Page = 0 };
    using var sio = FakeSio(driver);
    using var control = Startup(new EcMailbox(sio), out var result);
    Check(control != null && result.Succeeded && control.VerifiedBaseline == new MsiCoreVoltage.State(0, 0x30, 1280, 0)
        && driver.Page == 0 && driver.RegulatorWrites.Count == 0, result.Status);
});
foreach (byte readback in new byte[] { 2, 3, 5 })
    Test($"strict startup PAGE readback {readback} failure cannot create a controller", () =>
    {
        var driver = new EcFakeDriver { FailPageResultAt = readback };
        using var sio = FakeSio(driver);
        using var control = Startup(new EcMailbox(sio), out var result);
        Check(control == null && !result.Succeeded && driver.Page == 1 && driver.EcSelectedPage == 0xFF
            && driver.RegulatorWrites.All(w => w.reg == 0), result.Status);
    });
Test("strict startup missing restoration pre-read still restores and verifies saved PAGE", () =>
{
    var driver = new EcFakeDriver { FailPageResultAt = 4 };
    using var sio = FakeSio(driver);
    using var control = Startup(new EcMailbox(sio), out var result);
    Check(control != null && result.Succeeded && driver.Page == 1 && driver.EcSelectedPage == 0xFF
        && driver.RegulatorWrites.All(w => w.reg == 0), result.Status);
});
foreach (string failure in new[] { "target-read", "secondary-read", "select-after-change", "select-mismatch", "restore-after-change", "restore-mismatch", "snapshot-drift" })
    Test($"strict startup {failure} fails closed and writes no target or mode", () =>
    {
        var driver = new EcFakeDriver();
        switch (failure)
        {
            case "target-read": driver.FailTargetResultOnce = true; break;
            case "secondary-read": driver.FailSecondaryResultOnce = true; break;
            case "select-after-change": driver.FailPageSelectOnce = true; break;
            case "select-mismatch": driver.IgnorePageSelectOnce = true; break;
            case "restore-after-change": driver.FailPageRestoreOnce = true; break;
            case "restore-mismatch": driver.IgnorePageRestoreOnce = true; break;
            case "snapshot-drift": driver.CorePageDriftOnSecondaryOnce = true; break;
        }
        using var sio = FakeSio(driver);
        using var control = Startup(new EcMailbox(sio), out var result);
        Check(control == null && !result.Succeeded && driver.EcSelectedPage == 0xFF && driver.RegulatorWrites.All(w => w.reg == 0), result.Status);
        Check(driver.Page == (failure == "restore-mismatch" ? 0 : 1), "Failure did not preserve/restore PAGE as transport permits");
    });
void WithIsolatedLocks(Action<EcFakeDriver, EcMailbox, MsiCoreVoltage, Mutex, Mutex> test)
{
    var driver = new EcFakeDriver();
    using var sio = FakeSio(driver);
    var mailbox = new EcMailbox(sio, strictIo: true);
    // Invoke only the controller's mutex-handle constructor around a fake mailbox. Before
    // any acquisition, replace both handles with test-unique names; the running app's bus
    // mutexes are never held, abandoned, closed or modified by these contention checks.
    using var control = (MsiCoreVoltage)Activator.CreateInstance(typeof(MsiCoreVoltage),
        BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { mailbox }, null)!;
    using var renesas = new Mutex(false, "RochCpuFakeRenesas-" + Guid.NewGuid());
    using var ec = new Mutex(false, "RochCpuFakeEc-" + Guid.NewGuid());
    foreach (var pair in new[] { (name: "_sequenceMutex", value: renesas), (name: "_ecSequenceMutex", value: ec) })
    {
        var field = typeof(MsiCoreVoltage).GetField(pair.name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((Mutex?)field.GetValue(control))?.Dispose(); field.SetValue(control, pair.value);
    }
    test(driver, mailbox, control, renesas, ec);
}
CorePageVerificationResult ProbeWithControllerLocks(EcMailbox mailbox, MsiCoreVoltage control)
{
    var acquire = typeof(MsiCoreVoltage).GetMethod("Acquire", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var release = typeof(MsiCoreVoltage).GetMethod("Release", BindingFlags.Instance | BindingFlags.NonPublic)!;
    return CorePageProbe.Verify(new Transport(mailbox), () => (bool)acquire.Invoke(control, null)!, () => release.Invoke(control, null));
}
bool CanAcquireFromAnotherThread(Mutex mutex)
{
    bool acquired = false;
    var worker = new Thread(() => { acquired = mutex.WaitOne(250); if (acquired) mutex.ReleaseMutex(); });
    worker.Start(); Check(worker.Join(2000), "Isolated mutex probe worker did not finish"); return acquired;
}
foreach (bool holdEc in new[] { false, true })
    Test($"{(holdEc ? "EC" : "Renesas")} mutex timeout is bounded, accesses no registers, and releases partial acquisition", () =>
        WithIsolatedLocks((driver, mailbox, control, renesas, ec) =>
        {
            using var ready = new ManualResetEventSlim(); using var stop = new ManualResetEventSlim();
            var held = holdEc ? ec : renesas;
            var holder = new Thread(() => { held.WaitOne(); ready.Set(); stop.Wait(5000); held.ReleaseMutex(); });
            holder.Start(); Check(ready.Wait(2000), "Holder did not acquire isolated test mutex");
            try
            {
                var timer = Stopwatch.StartNew(); var result = ProbeWithControllerLocks(mailbox, control); timer.Stop();
                Check(!result.Succeeded && driver.IoReads == 0 && driver.IoWrites == 0 && driver.RegulatorWrites.Count == 0, result.Status);
                Check(timer.ElapsedMilliseconds is >= 900 and < 4000, "Bus timeout escaped its bounded lease budget");
                if (holdEc) Check(CanAcquireFromAnotherThread(renesas), "EC timeout leaked the already acquired Renesas mutex");
            }
            finally { stop.Set(); Check(holder.Join(2000), "Holder did not release test mutex"); }
            Check(CanAcquireFromAnotherThread(renesas) && CanAcquireFromAnotherThread(ec), "Timeout leaked a shared mutex");
        }));
foreach (bool abandonEc in new[] { false, true })
    Test($"abandoned {(abandonEc ? "EC" : "Renesas")} mutex is owned, probed safely and released", () =>
        WithIsolatedLocks((driver, mailbox, control, renesas, ec) =>
        {
            var worker = new Thread(() => (abandonEc ? ec : renesas).WaitOne());
            worker.Start(); Check(worker.Join(2000), "Abandonment fixture worker did not exit");
            var result = ProbeWithControllerLocks(mailbox, control);
            Check(result.Succeeded && driver.Page == 1 && driver.RegulatorWrites.All(w => w.reg == 0), result.Status);
            Check(CanAcquireFromAnotherThread(renesas) && CanAcquireFromAnotherThread(ec), "Recovered abandoned mutex was not released");
        }));
Test("successful startup probe releases both mutexes after snapshot and restore", () =>
    WithIsolatedLocks((driver, mailbox, control, renesas, ec) =>
    {
        var result = ProbeWithControllerLocks(mailbox, control);
        Check(result.Succeeded && driver.Page == 1 && driver.RegulatorWrites.All(w => w.reg == 0), result.Status);
        Check(CanAcquireFromAnotherThread(renesas) && CanAcquireFromAnotherThread(ec), "Successful probe leaked a mutex");
    }));
Test("failed startup snapshot restores PAGE and releases both mutexes", () =>
    WithIsolatedLocks((driver, mailbox, control, renesas, ec) =>
    {
        driver.FailModeResultOnce = true; var result = ProbeWithControllerLocks(mailbox, control);
        Check(!result.Succeeded && driver.Page == 1 && driver.RegulatorWrites.All(w => w.reg == 0), result.Status);
        Check(CanAcquireFromAnotherThread(renesas) && CanAcquireFromAnotherThread(ec), "Failed probe leaked a mutex");
    }));
foreach (string missing in new[] { "_sequenceMutex", "_ecSequenceMutex" })
    Test($"missing {missing} handle fails closed before every register access", () =>
        WithIsolatedLocks((driver, mailbox, control, renesas, ec) =>
        {
            typeof(MsiCoreVoltage).GetField(missing, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(control, null);
            var result = ProbeWithControllerLocks(mailbox, control);
            Check(!result.Succeeded && driver.IoReads == 0 && driver.IoWrites == 0, result.Status);
            Check(CanAcquireFromAnotherThread(renesas) && CanAcquireFromAnotherThread(ec), "Missing handle probe acquired a partial lease");
        }));
Test("manual override preserves PAGE 1 and writes targets only on core PAGE 0", () => WithControl((driver, control) =>
{
    control.SetOverride(1.320);
    Check(driver.Page == 1 && driver.Target == 1320 && driver.Mode == 0x30, "Override or PAGE was incorrect");
    Check(control.TargetReadIsCached && control.TargetReadStatus.Contains("restored to PAGE 1"), "Post-Apply target status was not updated before the next refresh");
    driver.RegulatorWrites.Clear();
    Check(control.ReadTargetVolts() == 1.320 && control.TargetReadIsCached && driver.RegulatorWrites.Count == 0, "Applied target cache/refresh was incorrect");
}));
Test("manual adaptive-zero preserves PAGE 1 and clears existing override", () => WithControl((driver, control) =>
{
    control.DisableOverride();
    Check(driver.Page == 1 && driver.Target == 0 && driver.Mode == 0 && driver.Secondary == 0, "Adaptive reset altered PAGE or retained target");
    Check(control.TargetReadIsCached && control.TargetReadStatus.Contains("restored to PAGE 1"), "Post-reset target status was stale before refresh");
    driver.RegulatorWrites.Clear();
    Check(control.ReadTargetVolts() == null && control.TargetReadIsCached && driver.RegulatorWrites.Count == 0, "Adaptive cache was fabricated");
}));
Test("manual captured-state restore preserves original PAGE", () => WithControl((driver, control) =>
{
    var before = control.ReadState(); control.SetOverride(1.320); control.Restore(before);
    Check(driver.Page == 1 && driver.Target == 1280 && driver.Mode == 0x30 && driver.Secondary == 0, "Rollback did not restore the exact core state");
}));
Test("failed regulator target write rolls back and restores original PAGE", () => WithControl((driver, control) =>
{
    driver.FailTargetWriteOnce = true; MustFail(() => control.SetOverride(1.320));
    Check(driver.Page == 1 && driver.Target == 1280 && driver.Mode == 0x30, "Failed target write left changed state");
}));
Test("strict refresh failure propagates instead of inventing an adaptive target", () => WithControl((driver, control) =>
{
    driver.Page = 0; driver.FailModeResultOnce = true; MustFail(() => control.ReadTargetVolts());
    Check(driver.RegulatorWrites.Count == 0 && driver.Page == 0 && control.TargetReadFailed && control.TargetReadStatus.StartsWith("Core target not read:"), "Refresh failure wrote hardware or fabricated adaptive state");
    driver.Mode = 0; driver.Target = 0;
    Check(control.ReadTargetVolts() == null && !control.TargetReadFailed, "A genuine verified adaptive-null target was incorrectly marked unread");
}));
Test("pair captures mailbox before any voltage write", () => WithControl((driver, control) =>
{
    int mailboxWrites = 0;
    MustFail(() => CoreVoltagePairTransaction.Execute<int>(control, () => throw new IOException("read failure"),
        () => control.SetOverride(1.320), () => mailboxWrites++, () => { }, _ => mailboxWrites++));
    Check(driver.Page == 1 && driver.Target == 1280 && mailboxWrites == 0 && driver.RegulatorWrites.All(w => w.reg == 0), "Unread baseline permitted a target write");
}));
Test("pair success preserves original PAGE and regulator-first mailbox-second ordering", () => WithControl((driver, control) =>
{
    int mailboxTarget = 1280;
    CoreVoltagePairTransaction.Execute(control, () => mailboxTarget, () => control.SetOverride(1.320),
        () => { Check(driver.Page == 0 && driver.Target == 1320, "Mailbox preceded regulator or lost core PAGE lease"); mailboxTarget = 1320; },
        () => Check(mailboxTarget == 1320, "Mailbox target incorrect"), before => mailboxTarget = before);
    Check(driver.Page == 1 && driver.Target == 1320 && mailboxTarget == 1320, "Pair did not complete exactly");
}));
Test("pair mailbox failure restores both captured targets while preserving PAGE", () => WithControl((driver, control) =>
{
    int mailboxTarget = 1280;
    MustFail(() => CoreVoltagePairTransaction.Execute(control, () => mailboxTarget, () => control.SetOverride(1.320),
        () => { mailboxTarget = 1320; throw new IOException("rejected mailbox write"); }, () => { },
        before => { Check(driver.Page == 0, "Rollback lost the core PAGE lease"); mailboxTarget = before; }));
    Check(driver.Page == 1 && driver.Target == 1280 && mailboxTarget == 1280, "Pair rollback left changed targets");
}));
Test("pair failed mailbox readback rolls back both targets", () => WithControl((driver, control) =>
{
    int mailboxTarget = 1280;
    MustFail(() => CoreVoltagePairTransaction.Execute(control, () => mailboxTarget, () => control.SetOverride(1.320),
        () => mailboxTarget = 1320, () => throw new IOException("readback mismatch"), before => mailboxTarget = before));
    Check(driver.Page == 1 && driver.Target == 1280 && mailboxTarget == 1280, "Readback mismatch did not restore both targets");
}));
Test("pair regulator failure never writes Intel mailbox target", () => WithControl((driver, control) =>
{
    int mailboxWrites = 0; driver.FailTargetWriteOnce = true;
    MustFail(() => CoreVoltagePairTransaction.Execute(control, () => 1280, () => control.SetOverride(1.320),
        () => mailboxWrites++, () => { }, _ => mailboxWrites++));
    Check(driver.Page == 1 && driver.Target == 1280 && mailboxWrites == 0, "Failed regulator unnecessarily changed mailbox");
}));
Test("pair PAGE restoration failure is reported and rolls both targets back", () => WithControl((driver, control) =>
{
    int mailboxTarget = 1280; driver.FailPageRestoreOnce = true;
    MustFail(() => CoreVoltagePairTransaction.Execute(control, () => mailboxTarget, () => control.SetOverride(1.320),
        () => mailboxTarget = 1320, () => { }, before => mailboxTarget = before));
    Check(driver.Page == 1 && driver.Target == 1280 && mailboxTarget == 1280, "PAGE restoration failure left new target pair");
}));
Console.WriteLine($"{passed} low-level simulated EC checks passed; only a fake IKernelDriver was constructed.");

sealed class Transport(EcMailbox mailbox) : ICorePageProbeTransport
{
    public byte? ReadPage() => mailbox.ReadByte(0xC6, 0);
    public bool WritePage(byte page) => mailbox.WriteByte(0xC6, 0, page);
    public ushort? ReadMode() => mailbox.ReadWord(0xC6, 0xF0);
    public ushort? ReadTarget() => mailbox.ReadWord(0xC6, 0x21);
    public ushort? ReadSecondaryTarget() => mailbox.ReadWord(0xC6, 0x23);
}
