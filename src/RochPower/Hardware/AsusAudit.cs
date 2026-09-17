using System.Text;

namespace RochPower.Hardware;

internal static class AsusAudit
{
    public static int ValidateRingVoltage(string path)
    {
        var report = new StringBuilder();
        void Log(string line) { report.AppendLine(line); File.WriteAllText(path, report.ToString()); }
        Log($"ASUS Cache SVID validation {DateTime.Now:O}");
        try { RequireSingleInstance(); }
        catch (Exception ex) { Log(ex.Message); return 1; }
        using var model = new RochPower.Core.HardwareModel();
        model.Log += Log; model.Initialize();
        if (model.AsusControl is not { BaselineRing: not null } asus || model.Cpu is not { } cpu) return 1;
        var before = asus.ReadSvid(AsusBoardControl.RingId);
        uint[] otherIds = [AsusBoardControl.CoreId, AsusBoardControl.SaId, AsusBoardControl.L2Id];
        var others = otherIds.ToDictionary(id => id, id => asus.ReadSvid(id));
        var ratio = cpu.ReadRingRatio();
        bool pass = false, touched = false;
        try
        {
            Log($"Starting cache {before}; ring ratio {ratio}; CPU mailbox {cpu.Mailbox.ReadDomain(OcMailbox.DOMAIN_RING)}");
            if (model.Settings.Any(s => s.Id is "cpu_vdd2" or "cpu_aux"))
                throw new IOException("Unvalidated ASUS sensor labels are still exposed");
            Log("PASS: no incorrectly mapped VDD2/AUX rows");
            const double target = 1.300;
            if (asus.ReadCoreTarget() is not double core || core < target || core > 1.31)
                throw new IOException("This diagnostic requires the existing 1.300 V core target; nothing changed");
            var wanted = AsusBoardControl.WithVoltageTarget(AsusBoardControl.RingId, before, target);
            if (wanted == before) throw new IOException("Cache is already at the diagnostic target; nothing changed");
            var row = model.Settings.Single(s => s.Id == "ring_v");
            touched = true;
            if (!model.Apply(row, target)) throw new IOException(row.LastError);
            if (asus.ReadSvid(AsusBoardControl.RingId) != wanted) throw new IOException("Cache full-state readback mismatch");
            var mailbox = cpu.Mailbox.ReadDomain(OcMailbox.DOMAIN_RING);
            Log($"CPU mailbox after Apply: {mailbox}; shared Vcore {asus.ReadVcore():0.000} V");
            if (!mailbox.OverrideMode || Math.Abs(mailbox.TargetVolts - target) > 1.0 / 1024)
                throw new IOException("ASUS target read back but CPU cache-domain target was not confirmed");
            if (cpu.ReadRingRatio() != ratio) throw new IOException("Ring ratio changed during voltage test");
            foreach (uint id in otherIds)
                if (asus.ReadSvid(id) != others[id]) throw new IOException($"Unrelated voltage item 0x{id:X8} changed");
            pass = true;
            Log("PASS: ASUS complete state and CPU cache-domain target verified; ring ratio and other voltage targets unchanged. No separate ring rail measurement is available.");
        }
        catch (Exception ex) { Log("FAILED: " + ex.Message); }
        finally
        {
            if (touched)
                try
                {
                    asus.SetSvid(before, AsusBoardControl.RingId);
                    if (asus.ReadSvid(AsusBoardControl.RingId) != before) throw new IOException("Cache restoration mismatch");
                    Log($"Restored cache {before}; CPU mailbox {cpu.Mailbox.ReadDomain(OcMailbox.DOMAIN_RING)}; ring ratio {cpu.ReadRingRatio()}");
                    foreach (uint id in otherIds)
                        if (asus.ReadSvid(id) != others[id]) throw new IOException($"Unrelated item 0x{id:X8} no longer matches baseline");
                }
                catch (Exception ex) { pass = false; Log("RESTORE FAILED: " + ex.Message); }
        }
        return pass ? 0 : 1;
    }

    public static int ValidateRail(string path, uint id, double target)
    {
        var report = new StringBuilder();
        void Log(string line) { report.AppendLine(line); File.WriteAllText(path, report.ToString()); }
        Log($"ASUS rail validation {DateTime.Now:O}; item=0x{id:X8}; target={target:0.000} V");
        try { RequireSingleInstance(); }
        catch (Exception ex) { Log(ex.Message); return 1; }
        if (id != AsusBoardControl.SaId && id != AsusBoardControl.L2Id) return 1;
        using var model = new RochPower.Core.HardwareModel();
        model.Log += Log; model.Initialize();
        if (model.AsusControl is not { } asus) return 1;
        AsusBoardControl.SvidState? before = null;
        bool pass = false, touched = false;
        try
        {
            before = asus.ReadSvid(id);
            _ = AsusBoardControl.WithVoltageTarget(id, before, target);
            double Sample(string phase)
            {
                var values = new List<double>();
                for (int i = 0; i < 12; i++)
                {
                    double v = asus.ReadRailVoltage(id);
                    if (!double.IsFinite(v) || v < 0.5 || v > 1.8) throw new IOException("Invalid ASUS rail sensor reading");
                    values.Add(v); Thread.Sleep(250);
                }
                Log($"{phase} samples: {string.Join(", ", values.Select(v => v.ToString("0.000")))} V");
                values.Sort(); double median = (values[5] + values[6]) / 2;
                Log($"{phase} median {median:0.000} V");
                return median;
            }
            double baseline = Sample("Before");
            var row = model.Settings.Single(s => s.Id == (id == AsusBoardControl.SaId ? "sa_v" : "ecore_v"));
            if (Math.Abs(target - baseline) < 0.04)
                throw new IOException("Target is too close to baseline for an independent physical-response test; nothing changed");
            touched = true;
            if (!model.Apply(row, target)) throw new IOException(row.LastError);
            double measured = Sample("After");
            bool direction = Math.Sign(measured - baseline) == Math.Sign(target - baseline);
            pass = direction && Math.Abs(measured - baseline) >= 0.02 && Math.Abs(measured - target) <= 0.075;
            if (!pass) throw new IOException("ASUS target read back but physical rail response was not verified");
            Log("PASS: complete state readback and board-sensor response");
        }
        catch (Exception ex) { pass = false; Log("FAILED: " + ex.Message); }
        finally
        {
            if (touched && before != null)
                try { asus.SetSvid(before, id); Log($"Restored {asus.ReadSvid(id)}; sensor {asus.ReadRailVoltage(id):0.000} V"); }
                catch (Exception ex) { pass = false; Log("RESTORE FAILED: " + ex.Message); }
        }
        return pass ? 0 : 1;
    }

    private static void RequireSingleInstance()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var processes = System.Diagnostics.Process.GetProcessesByName(self.ProcessName);
        try
        {
            if (processes.Any(p => p.Id != self.Id))
                throw new IOException("Close other Roch CPU instances before hardware validation.");
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public static int ValidateVoltage(string path)
    {
        var report = new StringBuilder();
        void Log(string line) { report.AppendLine(line); File.WriteAllText(path, report.ToString()); }
        Log($"ASUS voltage Apply validation {DateTime.Now:O}");
        try { RequireSingleInstance(); }
        catch (Exception ex) { Log(ex.Message); return 1; }
        using var model = new RochPower.Core.HardwareModel();
        model.Log += Log;
        model.Initialize();
        if (model.AsusControl is not { BaselineSvid: not null } asus) return 1;
        var before = asus.ReadSvid();
        var row = model.Settings.Single(s => s.Id == "core_v");
        bool pass = false;
        try
        {
            foreach (double target in new[] { 1.275, 1.300 })
            {
                if (!model.Apply(row, target)) throw new IOException(row.LastError);
                var values = new List<double>();
                for (int i = 0; i < 8; i++) { values.Add(asus.ReadVcore()); Thread.Sleep(250); }
                Log($"Target {target:0.000} V; ASUS Vcore: {string.Join(", ", values.Select(v => v.ToString("0.000")))}");
            }
            pass = true;
        }
        catch (Exception ex) { Log("FAILED: " + ex.Message); }
        finally
        {
            try { asus.SetSvid(before); Log($"Starting ASUS mode/offset/target restored: {asus.ReadSvid()}"); }
            catch (Exception ex) { pass = false; Log("RESTORE FAILED: " + ex.Message); }
        }
        Log(pass ? "PASS: app Apply and complete state restoration" : "FAIL");
        return pass ? 0 : 1;
    }

    public static int Run(string path, bool testBclk)
    {
        var report = new StringBuilder();
        void Log(string line) { report.AppendLine(line); File.WriteAllText(path, report.ToString()); }
        Log($"ASUS diagnostic {DateTime.Now:O}; BCLK test={testBclk}");
        try
        {
            if (testBclk) RequireSingleInstance();
            using var driver = WinRing0Driver.Open();
            var cpu = new IntelCpu(driver);
            using var meter = new BclkMeter(driver);
            using var asus = AsusBoardControl.TryCreate(SmbiosInfo.Read().BoardManufacturer, out string status);
            Log(status);
            if (asus == null) return 1;
            Log($"VRM raw {asus.ReadRaw(AsusBoardControl.VrmId)}; measured Vcore {asus.ReadVcore():0.000} V");
            double? Measure()
            {
                var values = new List<double>();
                for (int i = 0; i < 5; i++)
                    if (meter.MeasureBclkFromCycles(cpu.FirstPThread, 250) is double v) values.Add(v);
                values.Sort();
                Log("BCLK cycle samples: " + string.Join(", ", values.Select(v => v.ToString("0.000"))));
                return values.Count >= 3 ? values[values.Count / 2] : null;
            }
            Log($"ASUS target {asus.ReadBclk():0.000} MHz; cycle measurement {Measure():0.000} MHz; TSC reference {meter.MeasureBclkMHz(cpu.BaseRatio):0.000} MHz");
            if (!testBclk) return 0;
            bool pass = false;
            try
            {
                asus.SetBclk(101, Measure, Log);
                pass = true;
                Log("101 MHz programmed target verified; physical clock followed the requested change (see measured values)");
            }
            finally
            {
                asus.Restore(Measure, Log);
                Log($"Restored target {asus.ReadBclk():0.000} MHz; measured {Measure():0.000} MHz");
            }
            return pass ? 0 : 1;
        }
        catch (Exception ex) { Log("FAILED: " + ex); return 1; }
    }
}
