using System.Globalization;
using System.Text;
using RochPower.Core;
using RochPower.Hardware;
using RochPower.UI;

namespace RochPower;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // The diagnostic switches print to the console they were launched from; a WinExe has none of its own.
        if (args.Length > 0) Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);

        if (args.Length > 0 && args[0].Equals("--probe", StringComparison.OrdinalIgnoreCase))
            return Probe(args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "probe.txt"));

        if (args.Length > 0 && args[0].Equals("--pm-dump", StringComparison.OrdinalIgnoreCase))
            return PmDump(args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "pmtable.txt"));

        if (args.Length > 2 && args[0].Equals("--smu", StringComparison.OrdinalIgnoreCase))
            return SmuCommand(args.Skip(1).ToArray());

        if (args.Length > 2 && args[0].Equals("--apply", StringComparison.OrdinalIgnoreCase))
            return ApplySetting(args[1], args[2]);

        if (args.Length > 1 && args[0].Equals("--smn", StringComparison.OrdinalIgnoreCase))
            return SmnDump(args[1], args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 16);

        if (args.Length > 0 && args[0].Equals("--smb-regs", StringComparison.OrdinalIgnoreCase))
        {
            using var hw6 = new HardwareModel();
            hw6.Initialize();
            if (hw6.Smbus == null) { Console.WriteLine("no SMBus"); return 1; }
            var addrs = new byte[] { 0x44, 0x48, 0x4A, 0x50, 0x52, 0x70, 0x72 };
            var outBytes = new List<byte>();
            foreach (var a in addrs)
                for (int r = 0; r < 256; r++)
                    outBytes.Add(hw6.Smbus.ReadByte(a, (byte)r, out byte v) ? v : (byte)0xEE);
            File.WriteAllBytes(args.Length > 1 ? args[1] : "smb.bin", outBytes.ToArray());
            Console.WriteLine($"dumped {addrs.Length} addresses x 256 regs; Vcore {hw6.SuperIo?.ReadVoltage(SuperIo.RailVcore):0.000} V");
            return 0;
        }

        // NOTE: the EC dump/peek/poke/watch modes that lived here are gone. They polled the Super I/O EC as fast as the LPC
        // path allowed, to capture the order of Dragon Power's writes. It hard-reset this machine
        // twice (Kernel-Power 41). The NCT6687D also runs fan control and power sequencing, and
        // saturating it - especially while another tool holds the same ISA lock - hangs the board.
        // Do not reintroduce high-rate EC polling.

        if (args.Length > 0 && args[0].Equals("--smbus-reset", StringComparison.OrdinalIgnoreCase))
        {
            using var hw9 = new HardwareModel();
            hw9.Initialize();
            if (hw9.Smbus is null) { Console.WriteLine("no SMBus"); return 1; }
            // Recovery is Intel-specific: SSRESET lives in the PCH's HSTCFG register, and the
            // AMD (PIIX4) controller has no equivalent.
            if (hw9.Smbus is not SmbusI801 bus9)
            {
                Console.WriteLine($"SMBus recovery is only implemented for the Intel PCH controller; this system has {hw9.Smbus.Description}.");
                return 1;
            }
            Console.WriteLine("soft reset : " + bus9.Reset());
            Console.WriteLine("host reset : " + bus9.HardReset());
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                var found = Ddr5Dimm.Probe(bus9);
                Console.WriteLine($"attempt {attempt}: {found.Count} DDR5 module(s) " +
                                  string.Join(", ", found.Select(d => $"{d.SlotName}@0x{d.SpdAddress:X2}")));
                if (found.Count > 0) break;
                bus9.Reset();
                Thread.Sleep(300);
            }
            return 0;
        }

        if (args.Length > 0 && args[0].Equals("--vcore-test", StringComparison.OrdinalIgnoreCase))
            return VcoreTest(args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "vcore.txt"));

        if (args.Length > 0 && args[0].Equals("--sio-dump", StringComparison.OrdinalIgnoreCase))
        {
            using var hw2 = new HardwareModel();
            hw2.Log += m => Console.Error.WriteLine(m);
            hw2.Initialize();
            var sb2 = new StringBuilder();
            sb2.AppendLine(hw2.SuperIoStatus);
            if (hw2.SuperIo != null)
                foreach (var (i, v) in hw2.SuperIo.ReadAll())
                    sb2.AppendLine($"VIN{i,-2} = {v:0.000} V");
            File.WriteAllText(args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "sio.txt"), sb2.ToString());
            return 0;
        }

        if (args.Length > 0 && args[0].Equals("--smbus-scan", StringComparison.OrdinalIgnoreCase))
            return SmbusScan(args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "smbus.txt"));

        if (args.Length > 0 && args[0].Equals("--vtest", StringComparison.OrdinalIgnoreCase))
            return VoltageTest(args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "vtest.txt"));

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.ToString(), "Roch CPU - unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Application.Run(new MainForm());
        return 0;
    }

    /// <summary>
    /// Decisive A/B: does a mailbox core-voltage override move the voltage the VRM actually
    /// delivers? Raises the request by 60 mV (safe: well inside the normal range), measures the
    /// real rail with the Super I/O, and restores the previous setting.
    /// </summary>
    private static int VcoreTest(string reportPath)
    {
        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); Console.WriteLine(s); }
        using var hw = new HardwareModel();
        hw.Initialize();
        if (hw.Cpu is not { } cpu) { W("This test drives the Intel OC mailbox; not available on this CPU."); File.WriteAllText(reportPath, sb.ToString()); return 1; }
        var sio = hw.SuperIo;
        if (sio == null) { W("No Super I/O: cannot measure the real rail."); File.WriteAllText(reportPath, sb.ToString()); return 1; }
        var mb = cpu.Mailbox;

        using var stop = new CancellationTokenSource();
        int n = Math.Max(1, cpu.LogicalCpus.Count(c => c.IsPCore));
        var threads = Enumerable.Range(0, n).Select(_ => new Thread(() => { double x = 1.0001; while (!stop.IsCancellationRequested) x = Math.Sqrt(x * 1.000001 + 1e-9); }) { IsBackground = true }).ToList();
        threads.ForEach(t => t.Start());
        try
        {
            (double vid, double vcore) Measure()
            {
                Thread.Sleep(600);
                double vidSum = 0, vcSum = 0; int c = 0;
                for (int i = 0; i < 8; i++)
                {
                    var (_, vid) = cpu.ReadPerfStatus(cpu.FirstPThread);
                    vidSum += vid; vcSum += sio.ReadVoltage(SuperIo.RailVcore) ?? 0; c++;
                    Thread.Sleep(60);
                }
                return (vidSum / c, vcSum / c);
            }

            var s0 = mb.ReadDomain(OcMailbox.DOMAIN_CORE);
            var m0 = Measure();
            W($"Baseline     : mailbox {s0}");
            W($"               VID {m0.vid:0.000} V, real Vcore {m0.vcore:0.000} V");
            W("");

            // A board can clamp upward while still following downward, and the adaptive-offset path
            // is a different mechanism from a static override, so test all three before concluding.
            var results = new List<(string name, double dVid, double dRail)>();
            void Try(string name, VfDomainSettings s)
            {
                mb.WriteDomain(OcMailbox.DOMAIN_CORE, s);
                var m = Measure();
                double dVid = (m.vid - m0.vid) * 1000, dRail = (m.vcore - m0.vcore) * 1000;
                W($"{name,-22}: VID {m.vid:0.000} V ({dVid:+0;-0} mV), real Vcore {m.vcore:0.000} V ({dRail:+0;-0} mV)");
                results.Add((name, dVid, dRail));
                mb.WriteDomain(OcMailbox.DOMAIN_CORE, s0);
                Thread.Sleep(400);
            }

            double baseTarget = s0.OverrideMode ? s0.TargetVolts : m0.vid;
            Try("override +60 mV", new VfDomainSettings { MaxRatio = s0.MaxRatio, OverrideMode = true, TargetVolts = baseTarget + 0.060, OffsetVolts = s0.OffsetVolts });
            Try("override -60 mV", new VfDomainSettings { MaxRatio = s0.MaxRatio, OverrideMode = true, TargetVolts = baseTarget - 0.060, OffsetVolts = s0.OffsetVolts });
            Try("offset -60 mV", new VfDomainSettings { MaxRatio = s0.MaxRatio, OverrideMode = s0.OverrideMode, TargetVolts = s0.TargetVolts, OffsetVolts = s0.OffsetVolts - 0.060 });
            Try("adaptive, offset -60", new VfDomainSettings { MaxRatio = s0.MaxRatio, OverrideMode = false, TargetVolts = 0, OffsetVolts = -0.060 });

            mb.WriteDomain(OcMailbox.DOMAIN_CORE, s0);
            var m2 = Measure();
            W("");
            W($"Restored     : mailbox {mb.ReadDomain(OcMailbox.DOMAIN_CORE)}");
            W($"               VID {m2.vid:0.000} V, real Vcore {m2.vcore:0.000} V");
            W("");
            // What matters is whether the rail tracked the VID, not how big the step happened to be:
            // in adaptive mode the CPU clamps the request to its V/F curve, so a 60 mV ask can
            // legitimately produce a 20 mV VID move - and the rail should move that same 20 mV.
            var followed = results.Where(r => Math.Abs(r.dVid) >= 10 && Math.Sign(r.dRail) == Math.Sign(r.dVid)
                                              && Math.Abs(r.dRail) >= Math.Abs(r.dVid) * 0.6).ToList();
            if (followed.Count == 0)
                W("VERDICT: no path moved the rail. The board pins Vcore; only a VRM-side write can change it.");
            else
            {
                W("VERDICT: the rail follows the CPU. Paths verified: " + string.Join(", ", followed.Select(f => $"{f.name} (VID {f.dVid:+0;-0} mV -> rail {f.dRail:+0;-0} mV)")) + ".");
                W("The Core Voltage rows control real Vcore on this board.");
            }
        }
        catch (Exception ex) { W("ERROR: " + ex.Message); }
        finally { stop.Cancel(); threads.ForEach(t => t.Join()); }
        File.WriteAllText(reportPath, sb.ToString());
        return 0;
    }

    /// <summary>Read-only scan of the PCH SMBus for PMBus voltage regulators (VRM controllers).</summary>
    private static int SmbusScan(string reportPath)
    {
        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); }
        using var hw = new HardwareModel();
        hw.Log += m => W("  log: " + m);
        hw.Initialize();
        var bus = hw.Smbus;
        if (bus == null) { W("No SMBus."); File.WriteAllText(reportPath, sb.ToString()); return 1; }
        W($"Scanning {hw.SmbusStatus}");
        for (int a = 0x08; a <= 0x77; a++)
        {
            byte addr = (byte)a;
            bool ack = bus.ReadByte(addr, 0x00, out byte r00);
            bool ack20 = bus.ReadByte(addr, 0x20, out byte r20);
            if (!ack && !ack20) continue;
            var line = new StringBuilder($"0x{addr:X2}: r00={(ack ? r00.ToString("X2") : "--")} VOUT_MODE(20)={(ack20 ? r20.ToString("X2") : "--")}");
            foreach (var (reg, name) in new (byte, string)[] { (0x01, "OPERATION"), (0x02, "ON_OFF"), (0x98, "PMBUS_REV"), (0x19, "CAPABILITY") })
                line.Append($" {name}={(bus.ReadByte(addr, reg, out byte v) ? v.ToString("X2") : "--")}");
            foreach (var (reg, name) in new (byte, string)[] { (0x21, "VOUT_CMD"), (0x22, "VOUT_TRIM"), (0x23, "VOUT_CAL_OFS"), (0x24, "VOUT_MAX"), (0x8B, "READ_VOUT"), (0x8C, "READ_IOUT"), (0x8D, "READ_TEMP"), (0x88, "READ_VIN"), (0x96, "READ_POUT") })
                line.Append($" {name}={(bus.ReadWord(addr, reg, out ushort w) ? w.ToString("X4") : "--")}");
            W(line.ToString());
        }
        File.WriteAllText(reportPath, sb.ToString());
        return 0;
    }

    /// <summary>
    /// Diagnostic: does the CPU follow small changes through each write path? Every change is
    /// tiny (10 mV, one ratio step down) and the exact previous values are restored afterwards.
    /// </summary>
    private static int VoltageTest(string reportPath)
    {
        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); }
        using var hw = new HardwareModel();
        hw.Log += m => W("  log: " + m);
        hw.Initialize();
        if (hw.Cpu is not { } cpu) { W("This test drives the Intel OC mailbox; not available on this CPU."); File.WriteAllText(reportPath, sb.ToString()); return 1; }
        var drv = hw.Driver!;
        int thread = cpu.FirstPThread;

        // Busy-load one P-core for a moment and read its VID / ratio and the ring ratio while loaded.
        (double vid, int ratio, int ring) Sample()
        {
            return Hardware.WinRing0Driver.RunOnCpu(thread, () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                double x = 1.0001;
                while (sw.ElapsedMilliseconds < 250) x = Math.Sqrt(x * 1.000001 + 1e-9);
                double vsum = 0; int n = 0, r = 0;
                for (int i = 0; i < 20; i++)
                {
                    drv.ReadMsr(IntelCpu.MSR_IA32_PERF_STATUS, out ulong v);
                    vsum += ((v >> 32) & 0xFFFF) / 8192.0; n++; r = (int)((v >> 8) & 0xFF);
                    for (int k = 0; k < 20000; k++) x = Math.Sqrt(x + 1e-9);
                }
                drv.ReadMsr(IntelCpu.MSR_UNCORE_PERF_STATUS, out ulong u);
                return (vsum / n, r, (int)(u & 0x7F));
            });
        }

        try
        {
            var mb = cpu.Mailbox;
            var s0 = mb.ReadDomain(0);
            var base0 = Sample();
            W($"Baseline: dom0 {s0}; loaded VID {base0.vid:0.000} V at x{base0.ratio}, ring x{base0.ring}");

            // 1. Override target +10 mV (keep mode as the BIOS left it)
            var t = new VfDomainSettings { MaxRatio = s0.MaxRatio, OverrideMode = true, TargetVolts = (s0.OverrideMode ? s0.TargetVolts : base0.vid) + 0.010, OffsetVolts = s0.OffsetVolts };
            mb.WriteDomain(0, t);
            Thread.Sleep(300);
            var rb = mb.ReadDomain(0);
            var a1 = Sample();
            W($"Override +10mV: wrote {t}; readback {rb}; loaded VID {a1.vid:0.000} V (delta {(a1.vid - base0.vid) * 1000:+0;-0} mV)");
            mb.WriteDomain(0, s0);
            Thread.Sleep(200);

            // 2. Offset -10 mV in the current mode
            var o = new VfDomainSettings { MaxRatio = s0.MaxRatio, OverrideMode = s0.OverrideMode, TargetVolts = s0.TargetVolts, OffsetVolts = s0.OffsetVolts - 0.010 };
            mb.WriteDomain(0, o);
            Thread.Sleep(300);
            rb = mb.ReadDomain(0);
            var a2 = Sample();
            W($"Offset -10mV: wrote {o}; readback {rb}; loaded VID {a2.vid:0.000} V (delta {(a2.vid - base0.vid) * 1000:+0;-0} mV)");
            mb.WriteDomain(0, s0);
            Thread.Sleep(200);

            // 3. Adaptive offset -10 mV (mode bit cleared)
            var ad = new VfDomainSettings { MaxRatio = s0.MaxRatio, OverrideMode = false, TargetVolts = 0, OffsetVolts = -0.010 };
            mb.WriteDomain(0, ad);
            Thread.Sleep(300);
            rb = mb.ReadDomain(0);
            var a3 = Sample();
            W($"Adaptive offset -10mV: wrote {ad}; readback {rb}; loaded VID {a3.vid:0.000} V (delta {(a3.vid - base0.vid) * 1000:+0;-0} mV)");
            mb.WriteDomain(0, s0);
            Thread.Sleep(200);
            W($"Restored dom0: {mb.ReadDomain(0)}; loaded VID {Sample().vid:0.000} V");

            // 4. Ring: MSR 0x620 max ratio -1
            var (rmax, rmin) = cpu.ReadRingRatio();
            var d2 = mb.ReadDomain(2);
            W($"Ring baseline: 0x620 max {rmax} min {rmin}; mailbox dom2 {d2}; loaded ring x{base0.ring}");
            cpu.WriteRingRatio(rmax - 1);
            Thread.Sleep(300);
            var (rmax2, rmin2) = cpu.ReadRingRatio();
            var r1 = Sample();
            W($"0x620 max={rmax - 1}: readback max {rmax2} min {rmin2}; loaded ring x{r1.ring}");
            cpu.WriteRingRatio(rmax, rmin);
            Thread.Sleep(200);

            // 5. Ring: mailbox domain 2 ratio -1
            var d2n = new VfDomainSettings { MaxRatio = d2.MaxRatio - 1, OverrideMode = d2.OverrideMode, TargetVolts = d2.TargetVolts, OffsetVolts = d2.OffsetVolts };
            mb.WriteDomain(2, d2n);
            Thread.Sleep(300);
            var d2rb = mb.ReadDomain(2);
            var r2 = Sample();
            W($"mailbox dom2 ratio={d2.MaxRatio - 1}: readback {d2rb}; 0x620 now {cpu.ReadRingRatio()}; loaded ring x{r2.ring}");
            mb.WriteDomain(2, d2);
            cpu.WriteRingRatio(rmax, rmin);
            Thread.Sleep(200);
            W($"Restored ring: 0x620 {cpu.ReadRingRatio()}; dom2 {mb.ReadDomain(2)}; loaded ring x{Sample().ring}");

            // 6. Core ratio: 0x1AD vs mailbox dom0 ratio
            var d0 = mb.ReadDomain(0);
            W($"Core ratio: 0x1AD all-core {cpu.ReadPCoreAllCoreRatio()}, mailbox dom0 max ratio {d0.MaxRatio}, loaded x{base0.ratio}");

            // 7. Does the VRM follow the VID? Package power under an all-core load scales with V^2.
            double PowerUnderLoad(int seconds)
            {
                using var stop = new CancellationTokenSource();
                int n = Math.Max(1, cpu.LogicalCpus.Count(c => c.IsPCore));
                var threads = Enumerable.Range(0, n).Select(_ => new Thread(() => { double x = 1.0001; while (!stop.IsCancellationRequested) x = Math.Sqrt(x * 1.000001 + 1e-9); }) { IsBackground = true }).ToList();
                threads.ForEach(th => th.Start());
                Thread.Sleep(700); // settle
                double e0 = cpu.ReadPackageEnergyJoules();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Thread.Sleep(seconds * 1000);
                double e1 = cpu.ReadPackageEnergyJoules();
                stop.Cancel();
                threads.ForEach(th => th.Join());
                double de = e1 - e0; if (de < 0) de += 65536.0;
                return de / sw.Elapsed.TotalSeconds;
            }
            _ = PowerUnderLoad(1); // warm-up, discarded
            // Interleaved A/B with the load running continuously: removes the drift seen in single phases.
            {
                using var stop = new CancellationTokenSource();
                int n = Math.Max(1, cpu.LogicalCpus.Count(c => c.IsPCore));
                var threads = Enumerable.Range(0, n).Select(_ => new Thread(() => { double x = 1.0001; while (!stop.IsCancellationRequested) x = Math.Sqrt(x * 1.000001 + 1e-9); }) { IsBackground = true }).ToList();
                threads.ForEach(th => th.Start());
                Thread.Sleep(1500);
                var up = new VfDomainSettings { MaxRatio = s0.MaxRatio, OverrideMode = true, TargetVolts = (s0.OverrideMode ? s0.TargetVolts : base0.vid) + 0.040, OffsetVolts = s0.OffsetVolts };
                double Phase(VfDomainSettings s)
                {
                    mb.WriteDomain(0, s);
                    Thread.Sleep(300);
                    double e0 = cpu.ReadPackageEnergyJoules();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    Thread.Sleep(1200);
                    double de = cpu.ReadPackageEnergyJoules() - e0; if (de < 0) de += 65536.0;
                    return de / sw.Elapsed.TotalSeconds;
                }
                var baseP = new List<double>(); var upP = new List<double>();
                for (int round = 0; round < 4; round++) { baseP.Add(Phase(s0)); upP.Add(Phase(up)); }
                mb.WriteDomain(0, s0);
                stop.Cancel(); threads.ForEach(th => th.Join());
                double vidUp = 0; try { mb.WriteDomain(0, up); Thread.Sleep(200); vidUp = Sample().vid; mb.WriteDomain(0, s0); } catch { }
                double mb0 = baseP.Average(), mb1 = upP.Average();
                W($"VRM check (4 interleaved rounds, all-core load): base {string.Join("/", baseP.Select(p => p.ToString("0.0")))} W avg {mb0:0.0}; +40 mV {string.Join("/", upP.Select(p => p.ToString("0.0")))} W avg {mb1:0.0}; ratio {mb1 / mb0:0.000}, expected ~{Math.Pow(vidUp / base0.vid, 2):0.000} if the VRM follows VID ({base0.vid:0.000} -> {vidUp:0.000})");
            }
            W($"Restored dom0: {mb.ReadDomain(0)}");
        }
        catch (Exception ex) { W("ERROR: " + ex); }
        File.WriteAllText(reportPath, sb.ToString());
        return 0;
    }

    /// <summary>
    /// Raw SMU mailbox transaction for research: --smu rsmu|mp1|hsmp 0xMSG [arg0 arg1 ...].
    /// Prints the status and the six argument registers after the call. Anything sent here goes
    /// straight to the firmware; know what the message does before using it.
    /// </summary>
    private static int SmuCommand(string[] a)
    {
        using var hw = new HardwareModel();
        hw.Initialize();
        if (hw.Amd is not { } amd || !hw.SmuAvailable) { Console.WriteLine("No AMD SMU."); return 1; }
        static uint Parse(string s) => s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToUInt32(s[2..], 16) : uint.Parse(s, CultureInfo.InvariantCulture);
        var mb = a[0].ToLowerInvariant() switch { "rsmu" => amd.Smu.Messages.Rsmu, "mp1" => amd.Smu.Messages.Mp1, "hsmp" => amd.Smu.Messages.Hsmp, _ => null };
        if (mb == null) { Console.WriteLine("mailbox must be rsmu, mp1 or hsmp"); return 1; }
        uint msg = Parse(a[1]);
        var margs = new uint[6];
        for (int i = 2; i < a.Length && i - 2 < 6; i++) margs[i - 2] = Parse(a[i]);
        var st = amd.Smu.Send(mb, msg, margs);
        string line = $"{mb.Name} (msg 0x{mb.Msg:X8}) message 0x{msg:X}: {AmdSmu.Describe(st)} (0x{(byte)st:X2}); args = {string.Join(" ", margs.Select(x => $"0x{x:X8}"))}";
        Console.WriteLine(line);
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "smu.txt"), $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}"); } catch { }
        return st == Hardware.SmuStatus.Ok ? 0 : 2;
    }

    /// <summary>Reads a run of SMN registers: --smn 0x73000 [count]. Prints hex and the SVI3 / SVI2 VID decodes, to smn.txt as well.</summary>
    private static int SmnDump(string start, int count)
    {
        var sb = new StringBuilder();
        void W(string m) { sb.AppendLine(m); Console.WriteLine(m); }
        using var hw = new HardwareModel();
        hw.Initialize();
        if (hw.Amd is not { } amd) { W("No AMD CPU."); return 1; }
        uint a0 = start.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToUInt32(start[2..], 16) : uint.Parse(start, CultureInfo.InvariantCulture);
        for (int i = 0; i < count; i++)
        {
            uint a = a0 + (uint)(i * 4);
            if (amd.Smu.ReadSmn(a) is uint v)
            {
                uint svi3 = (v >> 6) & 0x1FF, svi2 = v >> 24;
                W($"0x{a:X8} = 0x{v:X8}  svi3[14:6]={svi3} -> {0.245 + svi3 * 0.005:0.000} V  svi3[24:16]={(v >> 16) & 0x1FF} -> {0.245 + ((v >> 16) & 0x1FF) * 0.005:0.000} V  svi2[31:24]={svi2} -> {1.55 - svi2 * 0.00625:0.000} V  low16={v & 0xFFFF}");
            }
            else W($"0x{a:X8} = read failed");
        }
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "smn.txt"), sb.ToString()); } catch { }
        return 0;
    }

    /// <summary>Applies one row by id through the same path the window uses: --apply ppt 150. Logs to the console and apply.txt.</summary>
    private static int ApplySetting(string id, string text)
    {
        var sb = new StringBuilder();
        void W(string m) { sb.AppendLine(m); Console.WriteLine(m); }
        using var hw = new HardwareModel();
        hw.Log += m => W("  log: " + m);
        hw.Initialize();
        var s = hw.Settings.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        int rc;
        if (s == null) { W($"no row '{id}'. Rows: {string.Join(", ", hw.Settings.Select(x => x.Id))}"); rc = 1; }
        else if (!s.TryParse(text, out double v)) { W($"'{text}' is not a number"); rc = 1; }
        else
        {
            W($"{s.Name}: before = {s.CurrentText} {s.Unit}");
            bool ok = hw.Apply(s, v);
            W($"{s.Name}: apply {(ok ? "OK" : "FAILED")}, now = {s.CurrentText} {s.Unit}");
            rc = ok ? 0 : 2;
        }
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "apply.txt"), sb.ToString()); } catch { }
        return rc;
    }

    /// <summary>Dumps the whole SMU power table as index / offset / float, for layout research on a new firmware.</summary>
    private static int PmDump(string reportPath)
    {
        var sb = new StringBuilder();
        using var hw = new HardwareModel();
        hw.Log += m => sb.AppendLine("  log: " + m);
        hw.Initialize();
        if (hw.Amd is not { } amd || !hw.SmuAvailable) { sb.AppendLine("No AMD SMU."); File.WriteAllText(reportPath, sb.ToString()); return 1; }
        var smu = amd.Smu;
        sb.AppendLine($"{amd.BrandString} - {amd.CodeName}; SMU {smu.VersionText}; table v0x{smu.TableVersion:X8} at 0x{smu.TableAddress:X} size 0x{smu.TableSize:X}");
        if (!smu.RefreshTable() || smu.Table is not { } t) { sb.AppendLine("table read failed: " + smu.LastTableError); File.WriteAllText(reportPath, sb.ToString()); return 1; }
        sb.AppendLine("index  offset  value");
        for (int i = 0; i < t.Length; i++) sb.AppendLine($"{i,5}  0x{i * 4:X4}  {t[i].ToString("0.####", CultureInfo.InvariantCulture)}");
        File.WriteAllText(reportPath, sb.ToString());
        Console.WriteLine($"wrote {t.Length} floats to {reportPath}");
        return 0;
    }

    /// <summary>
    /// Headless self-test: initialises the hardware layer, reads everything, performs
    /// no-op rewrites (same value) to confirm the write paths are accepted, and writes a report.
    /// </summary>
    private static int Probe(string reportPath)
    {
        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); }
        using var hw = new HardwareModel();
        hw.Log += m => W("  log: " + m);
        W($"Roch CPU probe {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        try
        {
            hw.Initialize();
            W("");
            W($"Driver       : {hw.DriverStatus}");
            W($"Board        : {hw.Smbios.MainboardModel} / BIOS {hw.Smbios.BiosVersion} {hw.Smbios.BiosDate}");
            if (hw.Cpu is { } cpu)
            {
                W($"CPU          : {cpu.BrandString} model 0x{cpu.Model:X} stepping {cpu.Stepping} ({cpu.Generation})");
                W($"Topology     : {cpu.PCoreCount}P + {cpu.ECoreCount}E, {cpu.LogicalCpus.Count} threads, hybrid flag={cpu.IsHybrid}");
                W($"PlatformInfo : base ratio {cpu.BaseRatio}, min ratio {cpu.MinRatio}, ratio programmable={cpu.RatioLimitsProgrammable}, TDP programmable={cpu.TdpProgrammable}, TjMax {cpu.TjMax}");
                W($"OC lock      : {hw.OcLocked}   mailbox available: {hw.MailboxAvailable}   PL locked: {hw.PowerLimitsLocked}");
                var (pr, pc) = cpu.ReadPCoreTurboTable();
                W($"P turbo table: " + string.Join(" ", pr.Select((r, i) => $"{pc[i]}c={r}")));
                if (cpu.ECoreCount > 0) { var (er, ec) = cpu.ReadECoreTurboTable(); W($"E turbo table: " + string.Join(" ", er.Select((r, i) => $"{ec[i]}c={r}"))); }
                var ring = cpu.ReadRingRatio();
                W($"Ring         : max {ring.max} min {ring.min} current {cpu.ReadCurrentRingRatio()}");
                var pl = cpu.ReadPackagePowerLimits();
                W($"Power limits : PL1 {pl.pl1} W (en={pl.pl1Enabled}) PL2 {pl.pl2} W (en={pl.pl2Enabled}) locked={pl.locked}");
                if (hw.MailboxAvailable)
                    foreach (var (id, name) in new[] { (0, "core"), (1, "gt"), (2, "ring"), (3, "uncore/ecore-l2"), (4, "sa"), (5, "dom5") })
                    {
                        try { W($"Mailbox dom {id} ({name}): {cpu.Mailbox.ReadDomain(id)}  IccMax={cpu.Mailbox.ReadIccMax(id)?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} A"); }
                        catch (Exception ex) { W($"Mailbox dom {id} ({name}): {ex.Message}"); }
                    }
                W($"BCLK         : {hw.MeasureBclk()?.ToString("0.000", CultureInfo.InvariantCulture) ?? "n/a"} MHz ({hw.Bclk?.Status})");
                var live = hw.ReadLive(); Thread.Sleep(500); live = hw.ReadLive();
                W($"Live         : {live.PackageTempC} C, x{live.CoreRatio} = {live.CoreMHz:0.0} MHz, VID {live.CoreVid:0.000} V, ring x{live.RingRatio}, {live.PackageWatts:0.0} W");

                // No-op write tests (rewrite the exact current values) to prove the write paths are accepted.
                W("");
                W("Write-path checks (no-op rewrites of current values):");
                try { cpu.WritePCoreTurboTable(pr); W("  MSR 0x1AD rewrite      : OK"); } catch (Exception ex) { W("  MSR 0x1AD rewrite      : " + ex.Message); }
                try { cpu.WriteRingRatio(ring.max); W("  MSR 0x620 rewrite      : OK"); } catch (Exception ex) { W("  MSR 0x620 rewrite      : " + ex.Message); }
                if (hw.MailboxAvailable)
                {
                    try { var s = cpu.Mailbox.ReadDomain(0); cpu.Mailbox.WriteDomain(0, s); W("  Mailbox dom 0 rewrite  : OK"); }
                    catch (Exception ex) { W("  Mailbox dom 0 rewrite  : " + ex.Message); }
                }
            }
            if (hw.Amd is { } amd)
            {
                var smu = amd.Smu;
                W($"CPU          : {amd.BrandString} family {amd.Family:X}h model {amd.Model:X2}h stepping {amd.Stepping} package type {amd.PackageType} - {amd.CodeName}, {amd.Generation}");
                W($"Topology     : {amd.CoreCount} cores / {amd.LogicalCount} threads, {amd.ThreadsPerCore} threads per core, {amd.CcdCount} CCD(s); {amd.TopologyNote}");
                W($"Cores        : " + string.Join("  ", amd.Cores.Select(c => $"{c.Label}={c.Location} thr[{string.Join(",", c.Threads)}] mask 0x{smu.CoreMask(c.Ccd, c.CoreInCcd):X8}")));
                W($"P0 ratio     : {amd.BaseRatio}   microcode 0x{amd.PatchLevel:X8}");
                W($"SMU          : {hw.SmuStatus}   OC caps {(hw.OcCapabilities is uint oc ? $"0x{oc:X}" : "n/a")}");
                if (hw.SmuAvailable)
                {
                    var (fp, ft, fe) = smu.ReadStockLimits();
                    W($"Stock limits : PPT {fp?.ToString("0") ?? "n/a"} W, TDC {ft?.ToString("0") ?? "n/a"} A, EDC {fe?.ToString("0") ?? "n/a"} A; sustained {(smu.ReadSustainedLimits() is { } sl ? $"{sl.power} W / {sl.temp} C" : "n/a")}");
                    W($"Scalar       : {smu.ReadScalar()?.ToString("0.00") ?? "n/a"}   boost limit {smu.ReadBoostLimitMHz()?.ToString() ?? "n/a"} MHz");
                    W($"Curve Opt.   : " + string.Join("  ", amd.Cores.Select(c => $"{c.Label}={(smu.ReadCurveOptimizer(c.Ccd, c.CoreInCcd)?.ToString() ?? "?")}")));
                    W($"Table        : v0x{smu.TableVersion:X8} at 0x{smu.TableAddress:X} size 0x{smu.TableSize:X} layout {smu.Layout?.Name ?? "unknown"}");
                    if (smu.RefreshTable() && smu.Table is { } t)
                    {
                        W($"  limits     : PPT {smu.PptLimit:0.##}/{smu.PptValue:0.##} W  TDC {smu.TdcLimit:0.##}/{smu.TdcValue:0.##} A  EDC {smu.EdcLimit:0.##}/{smu.EdcValue:0.##} A  THM {smu.ThmLimit:0.##} C  socket {smu.SocketPower:0.##} W");
                        var line = new StringBuilder("  table[0..63]:");
                        for (int i = 0; i < Math.Min(64, t.Length); i++) { if (i % 8 == 0) line.Append($"\n    {i,3}:"); line.Append($" {t[i],10:0.###}"); }
                        W(line.ToString());
                    }
                    else W("  table read failed: " + smu.LastTableError);
                }
                W($"Temperature  : {amd.ReadTemperature()?.ToString("0.0") ?? "n/a"} C   VID {amd.ReadCoreVid()?.ToString("0.000") ?? "n/a"} V   max core {amd.ReadMaxCoreMHz():0} MHz");
                W($"BCLK         : {hw.MeasureBclk()?.ToString("0.000", CultureInfo.InvariantCulture) ?? "n/a"} MHz ({hw.Bclk?.Status})");
                var live = hw.ReadLive(); Thread.Sleep(500); live = hw.ReadLive();
                W($"Live         : {live.PackageTempC} C, {live.CoreMHz:0.0} MHz, VID {live.CoreVid:0.000} V, {live.PackageWatts:0.0} W");
            }
            W("");
            W($"SMBus        : {hw.SmbusStatus}");
            foreach (var d in hw.Dimms)
            {
                var (pv, pq, pp) = d.ReadPowerMw();
                W($"{d.SlotName}: SPD 0x{d.SpdAddress:X2} PMIC {(d.HasPmic ? $"0x{d.PmicAddress:X2} {d.PmicVendor}" : "none")}  VDD {d.ReadVdd():0.000} VDDQ {d.ReadVddq():0.000} VPP {d.ReadVpp():0.000}  power VDD {pv} VDDQ {pq} VPP {pp} mW");
                if (d.HasPmic) W($"  calibration: {d.CalibrationNote}");
                if (d.HasPmic)
                {
                    var line = new StringBuilder($"  PMIC regs 0x00-0x3F:");
                    for (int r = 0; r < 0x40; r++)
                    {
                        if (r % 16 == 0) line.Append($"\n    {r:X2}:");
                        line.Append(d.ReadRegister((byte)r, out byte v) ? $" {v:X2}" : " --");
                    }
                    W(line.ToString());
                }
                if (d.HasPmic)
                {
                    var adc = new StringBuilder("  PMIC ADC sweep (select 0..15 -> raw):");
                    for (int sel = 0; sel < 16; sel++)
                    {
                        byte? v = d.ReadAdc(sel);
                        adc.Append($" {sel}={(v is byte b ? b.ToString() : "--")}");
                    }
                    W(adc.ToString());
                }
                var spd = new StringBuilder("  SPD5118 regs 0x00-0x0F:");
                for (int r = 0; r < 0x10; r++) spd.Append(d.ReadSpdRegister((byte)r, out byte v) ? $" {v:X2}" : " --");
                W(spd.ToString());
            }
            W("");
            W("Settings:");
            foreach (var s in hw.Settings)
                W($"  {s.Id,-16} {s.Name,-34} {(s.Available ? s.RangeText : "N/A"),-34} = {(s.Available ? s.CurrentText : "N/A")} {s.Unit} {(s.ReadOnly ? "[read-only]" : "")}");
        }
        catch (Exception ex) { W("FATAL: " + ex); }
        File.WriteAllText(reportPath, sb.ToString());
        return 0;
    }
}
