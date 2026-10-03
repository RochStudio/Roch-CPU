using RochPower.Hardware;

int passed = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
CorePageVerificationResult Run(FakeTransport io) => CorePageProbe.Verify(io,
    () => { io.Acquires++; io.Held = io.AcquireResult; return io.AcquireResult; },
    () => { io.Releases++; io.Held = false; });

Test("page 1 core snapshot restores page 1 with only PAGE writes", () =>
{
    var io = new FakeTransport(); var r = Run(io);
    Check(r.Succeeded && r.OriginalPage == 1 && r.RestoredPage == 1 && io.Page == 1, r.Status);
    Check(io.Writes.SequenceEqual(new byte[] { 0, 1 }), "PAGE write scope differs");
    Check(r.Snapshot == new CorePageSnapshot(0x30, 1280, 0), "Wrong-page snapshot");
    Check(io.Acquires == 1 && io.Releases == 1 && !io.Held, "Bus lease lost");
});
Test("page 0 verification performs no PAGE writes", () =>
{
    var io = new FakeTransport { Page = 0 }; var r = Run(io);
    Check(r.Succeeded && io.Writes.Count == 0, r.Status);
});
Test("bus timeout performs no register access", () =>
{
    var io = new FakeTransport { AcquireResult = false }; var r = Run(io);
    Check(!r.Succeeded && io.Accesses == 0 && io.Releases == 0, "Access after lock timeout");
});
Test("unknown page refuses all changes", () =>
{
    var io = new FakeTransport { Page = 2 }; var r = Run(io);
    Check(!r.Succeeded && io.Writes.Count == 0 && io.Accesses == 1 && io.Releases == 1, r.Status);
});
Test("missing original page refuses all writes", () =>
{
    var io = new FakeTransport { FailFirstPageRead = true }; var r = Run(io);
    Check(!r.Succeeded && io.Writes.Count == 0 && io.Releases == 1, r.Status);
});
Test("selection failed after changing the page still restores original", () =>
{
    var io = new FakeTransport { SelectionReportsFailure = true }; var r = Run(io);
    Check(!r.Succeeded && io.Page == 1 && io.Writes.SequenceEqual(new byte[] { 0, 1 }), r.Status);
});
Test("selection readback mismatch prevents core reads", () =>
{
    var io = new FakeTransport { IgnoreSelection = true }; var r = Run(io);
    Check(!r.Succeeded && io.Page == 1 && io.CoreReads == 0, r.Status);
});
foreach (int failingRegister in new[] { 0, 1, 2 })
    Test($"core register {failingRegister} failure restores original", () =>
    {
        var io = new FakeTransport { FailCoreRead = failingRegister }; var r = Run(io);
        Check(!r.Succeeded && io.Page == 1 && io.Writes.SequenceEqual(new byte[] { 0, 1 }) && io.Releases == 1, r.Status);
    });
Test("invalid core register identity refuses activation and restores", () =>
{
    var io = new FakeTransport { Mode = ushort.MaxValue }; var r = Run(io);
    Check(!r.Succeeded && io.Page == 1 && r.Snapshot.HasValue, r.Status);
});
Test("invalid target refuses activation and restores", () =>
{
    var io = new FakeTransport { Target = 65535 }; var r = Run(io);
    Check(!r.Succeeded && io.Page == 1, r.Status);
});
Test("adaptive zero target is real state, not measured Vcore", () =>
{
    var io = new FakeTransport { Mode = 0, Target = 0 }; var r = Run(io);
    Check(r.Succeeded && r.Snapshot!.Value.ProgrammedTargetVolts == 0 && r.Snapshot.Value.OverrideTargetVolts == null, r.Status);
});
Test("restoration write failure is reported and never activates", () =>
{
    var io = new FakeTransport { FailRestore = true }; var r = Run(io);
    Check(!r.Succeeded && r.RestoredPage == 0 && r.Status.Contains("restoration") && io.Releases == 1, r.Status);
});
Test("exception during core read restores original and releases bus", () =>
{
    var io = new FakeTransport { ThrowCoreRead = true }; var r = Run(io);
    Check(!r.Succeeded && io.Page == 1 && io.Releases == 1, r.Status);
});
Test("page drift during snapshot is detected and original restored", () =>
{
    var io = new FakeTransport { DriftDuringSnapshot = true }; var r = Run(io);
    Check(!r.Succeeded && io.Page == 1 && r.Status.Contains("changed"), r.Status);
});
foreach (int mode in new[] { 0, 1, 2, 3 })
    Test($"mode {mode} preserves the actual programmed state and override meaning", () =>
    {
        var io = new FakeTransport { Mode = (ushort)(mode << 4), Target = 1280 }; var r = Run(io);
        Check(r.Succeeded && r.Snapshot!.Value.Mode == mode && r.Snapshot.Value.ProgrammedTargetVolts == 1.280, r.Status);
        Check(r.Snapshot!.Value.OverrideTargetVolts == (mode == 3 ? 1.280 : null), "Adaptive state was invented as an override target");
        Check(io.Page == 1 && io.Writes.SequenceEqual(new byte[] { 0, 1 }), "Mode/target verification altered state");
    });
foreach (ushort boundary in new ushort[] { 600, 1720 })
    Test($"existing target range boundary {boundary} mV remains valid", () =>
    {
        var io = new FakeTransport { Target = boundary, Secondary = boundary }; var r = Run(io);
        Check(r.Succeeded && r.Snapshot!.Value.TargetMv == boundary && r.Snapshot.Value.SecondaryTargetMv == boundary && io.Page == 1, r.Status);
    });
foreach (ushort invalid in new ushort[] { 599, 1721 })
    Test($"target {invalid} mV outside existing range fails closed", () =>
    {
        var io = new FakeTransport { Target = invalid }; var r = Run(io);
        Check(!r.Succeeded && io.Page == 1 && io.Releases == 1, r.Status);
    });
foreach (ushort invalid in new ushort[] { 599, 1721, ushort.MaxValue })
    Test($"secondary target {invalid} mV refuses activation and restores", () =>
    {
        var io = new FakeTransport { Secondary = invalid }; var r = Run(io);
        Check(!r.Succeeded && io.Page == 1 && io.Releases == 1, r.Status);
    });
Test("override mode with zero target refuses activation", () =>
{
    var io = new FakeTransport { Mode = 0x30, Target = 0 }; var r = Run(io);
    Check(!r.Succeeded && io.Page == 1 && io.Releases == 1, r.Status);
});
foreach (bool throws in new[] { false, true })
{
    Test($"selection readback {(throws ? "exception" : "missing result")} prevents every core read and restores PAGE", () =>
    {
        var io = new FakeTransport(); io.FailPageRead(2, throws); var r = Run(io);
        Check(!r.Succeeded && io.CoreReads == 0 && io.Page == 1 && r.RestoredPage == 1 && io.Releases == 1, r.Status);
    });
    Test($"post-snapshot PAGE {(throws ? "exception" : "missing result")} cannot activate a readable-looking target", () =>
    {
        var io = new FakeTransport(); io.FailPageRead(3, throws); var r = Run(io);
        Check(!r.Succeeded && r.Snapshot.HasValue && io.Page == 1 && r.RestoredPage == 1 && io.Releases == 1, r.Status);
    });
    Test($"restoration pre-read {(throws ? "exception" : "missing result")} still attempts saved PAGE and verifies it", () =>
    {
        var io = new FakeTransport(); io.FailPageRead(4, throws); var r = Run(io);
        Check(r.Succeeded && r.PageRestorationAttempted && r.RestoredPage == 1 && io.Page == 1
            && io.Writes.SequenceEqual(new byte[] { 0, 1 }) && io.Releases == 1, r.Status);
    });
    Test($"restoration final readback {(throws ? "exception" : "missing result")} refuses activation and releases bus", () =>
    {
        var io = new FakeTransport(); io.FailPageRead(5, throws); var r = Run(io);
        Check(!r.Succeeded && r.Snapshot.HasValue && io.Page == 1 && r.Status.Contains("restoration") && io.Releases == 1, r.Status);
    });
}
Test("restoration acknowledged without physical restore refuses activation", () =>
{
    var io = new FakeTransport { IgnoreRestore = true }; var r = Run(io);
    Check(!r.Succeeded && r.RestoredPage == 0 && r.PageRestorationAttempted && io.Releases == 1, r.Status);
});
Test("selection exception after physical PAGE change still restores original and releases bus", () =>
{
    var io = new FakeTransport { ThrowSelection = true }; var r = Run(io);
    Check(!r.Succeeded && io.Page == 1 && r.RestoredPage == 1 && io.CoreReads == 0 && io.Releases == 1, r.Status);
});
Test("restoration exception after physical change refuses activation and releases bus", () =>
{
    var io = new FakeTransport { ThrowRestore = true }; var r = Run(io);
    Check(!r.Succeeded && io.Page == 1 && r.Snapshot.HasValue && io.Releases == 1, r.Status);
});
Test("acquisition exception does not access transport or release an unowned lock", () =>
{
    var io = new FakeTransport(); var r = CorePageProbe.Verify(io,
        () => { io.Acquires++; throw new IOException("Synthetic acquire failure"); },
        () => io.Releases++);
    Check(!r.Succeeded && io.Acquires == 1 && io.Accesses == 0 && io.Releases == 0, r.Status);
});
Test("release exception refuses activation even after verified snapshot and restore", () =>
{
    var io = new FakeTransport(); var r = CorePageProbe.Verify(io,
        () => { io.Acquires++; io.Held = true; return true; },
        () => { io.Releases++; io.Held = false; throw new IOException("Synthetic release failure"); });
    Check(!r.Succeeded && r.Snapshot.HasValue && r.RestoredPage == 1 && io.Page == 1
        && io.Releases == 1 && !io.Held && r.Status.Contains("lock release"), r.Status);
});
Test("PAGE 0 snapshot drift is rejected and original PAGE 0 restored", () =>
{
    var io = new FakeTransport { Page = 0, DriftDuringSnapshot = true }; var r = Run(io);
    Check(!r.Succeeded && r.OriginalPage == 0 && r.RestoredPage == 0 && io.Page == 0
        && io.Writes.SequenceEqual(new byte[] { 0 }) && io.Releases == 1, r.Status);
});
foreach (int failingRegister in new[] { 0, 1, 2 })
    Test($"PAGE 0 core register {failingRegister} failure releases bus without PAGE writes", () =>
    {
        var io = new FakeTransport { Page = 0, FailCoreRead = failingRegister }; var r = Run(io);
        Check(!r.Succeeded && r.OriginalPage == 0 && r.RestoredPage == 0 && io.Writes.Count == 0 && io.Releases == 1, r.Status);
    });
Console.WriteLine($"{passed} fake-only core PAGE checks passed. No driver or hardware API is linked into this test executable.");

sealed class FakeTransport : ICorePageProbeTransport
{
    public byte Page = 1;
    public ushort Mode = 0x30, Target = 1280, Secondary = 0;
    public bool Held, AcquireResult = true, FailFirstPageRead, SelectionReportsFailure,
        IgnoreSelection, IgnoreRestore, FailRestore, ThrowSelection, ThrowRestore, ThrowCoreRead, DriftDuringSnapshot;
    public int Acquires, Releases, Accesses, CoreReads, FailCoreRead = -1;
    public readonly List<byte> Writes = new();
    private int _pageReads;
    private readonly Dictionary<int, bool> _failingPageReads = new();
    public void FailPageRead(int index, bool throws) => _failingPageReads.Add(index, throws);
    private void Access() { if (!Held) throw new Exception("Transport access outside Renesas lease"); Accesses++; }
    public byte? ReadPage()
    {
        Access(); _pageReads++;
        if (_failingPageReads.TryGetValue(_pageReads, out bool throws))
        {
            if (throws) throw new IOException("Synthetic PAGE read failure");
            return null;
        }
        if (FailFirstPageRead && _pageReads == 1) return null;
        return Page;
    }
    public bool WritePage(byte page)
    {
        Access(); if (page is not (0 or 1)) throw new Exception("Unsupported PAGE write");
        Writes.Add(page);
        if (page == 1 && FailRestore) return false;
        if (!(page == 0 && IgnoreSelection) && !(page == 1 && IgnoreRestore)) Page = page;
        if (page == 0 && ThrowSelection) throw new IOException("Synthetic select failure after physical change");
        if (page == 1 && ThrowRestore) throw new IOException("Synthetic restore failure after physical change");
        return !(page == 0 && SelectionReportsFailure);
    }
    private ushort? ReadCore(int index, ushort value)
    {
        Access(); CoreReads++; if (Page != 0) throw new Exception("Reading a non-core page");
        if (ThrowCoreRead) throw new IOException("Synthetic transport read failure");
        if (FailCoreRead == index) return null;
        if (index == 2 && DriftDuringSnapshot) Page = 1;
        return value;
    }
    public ushort? ReadMode() => ReadCore(0, Mode);
    public ushort? ReadTarget() => ReadCore(1, Target);
    public ushort? ReadSecondaryTarget() => ReadCore(2, Secondary);
}
