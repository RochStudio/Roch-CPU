using System.Runtime.Intrinsics.X86;
using System.Text;

namespace RochPower.Hardware;

/// <summary>Read-only register audit and explicit, separate hardware-validation commands.</summary>
internal static class IntelAudit
{
    public static int ValidateECore(string path, int target)
    {
        if (target < 8 || target > 44) throw new ArgumentOutOfRangeException(nameof(target));
        var report = new StringBuilder();
        void Log(string text) { report.AppendLine(text); File.WriteAllText(path, report.ToString()); }
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var processes = System.Diagnostics.Process.GetProcessesByName(self.ProcessName);
        try
        {
            if (processes.Any(p => p.Id != self.Id))
            { Log("Validation refused: close other Roch CPU instances."); return 1; }
        }
        finally { foreach (var p in processes) p.Dispose(); }
        using var driver = WinRing0Driver.Open();
        var cpu = new IntelCpu(driver);
        if (cpu.ECoreCount == 0) { Log("No E-cores detected."); return 1; }
        int[] before = cpu.ReadECoreTurboTable().ratios;
        int baseline = before.Where(r => r > 0).DefaultIfEmpty(0).Min();
        if (baseline == 0 || target > baseline + 1) { Log("Test refused: no usable baseline or target is more than one step above the current E-core ceiling."); return 1; }
        bool pass = false, restored = false;
        Log($"E-core validation {DateTime.Now:O}; target x{target}; original {string.Join(",", before)}; hypervisor {cpu.HypervisorPresent}");
        try
        {
            int Peak() => WinRing0Driver.RunOnCpu(cpu.FirstEThread, () =>
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                int peak = 0;
                double work = 1.001;
                while (timer.ElapsedMilliseconds < 600)
                {
                    for (int i = 0; i < 300000; i++) work = Math.Sqrt(work + 1.00001);
                    if (timer.ElapsedMilliseconds > 200) peak = Math.Max(peak, cpu.ReadPerfStatus(cpu.FirstEThread).ratio);
                }
                GC.KeepAlive(work);
                return peak;
            });
            int lower = Math.Max(8, Math.Min(baseline, target) - 2);
            cpu.WriteECoreAllCoreRatio(lower);
            int lowerPeak = Peak();
            bool lowerPass = cpu.ReadECoreAllCoreRatio() == lower && lowerPeak == lower;
            Log($"E-core lower ceiling x{lower}; active peak x{lowerPeak}: {(lowerPass ? "PASS" : "FAIL")}");
            cpu.WriteECoreAllCoreRatio(target);
            int peak = Peak();
            pass = lowerPass && cpu.ReadECoreAllCoreRatio() == target && peak == target;
            Log($"E-core target read back x{cpu.ReadECoreAllCoreRatio()}; active peak x{peak}: {(pass ? "PASS" : "Target clock not demonstrated")}");
        }
        catch (Exception ex) { Log("E-core: FAIL " + ex.Message); }
        finally
        {
            try
            {
                if (!cpu.ReadECoreTurboTable().ratios.SequenceEqual(before)) cpu.WriteECoreTurboTable(before);
                restored = cpu.ReadECoreTurboTable().ratios.SequenceEqual(before);
                Log(restored ? "Original E-core table verified: " + string.Join(",", before) : "RESTORE FAILED");
            }
            catch (Exception ex) { Log("RESTORE FAILED: " + ex.Message); }
        }
        return pass && restored ? 0 : 2;
    }

    /// <summary>Tests the architectural ring register independently of the board's OC mailbox.</summary>
    public static int ValidateRing(string path)
    {
        var report = new StringBuilder();
        void Log(string text) { report.AppendLine(text); File.WriteAllText(path, report.ToString()); }
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var processes = System.Diagnostics.Process.GetProcessesByName(self.ProcessName);
        try
        {
            if (processes.Any(p => p.Id != self.Id))
            {
                Log("Validation refused: close all other Roch CPU instances before changing hardware.");
                return 1;
            }
        }
        finally { foreach (var p in processes) p.Dispose(); }
        using var driver = WinRing0Driver.Open();
        var cpu = new IntelCpu(driver);
        if (!driver.ReadMsr(IntelCpu.MSR_UNCORE_RATIO_LIMIT, out ulong before, cpu.FirstPThread))
        { Log("Ring snapshot failed: " + driver.LastError); return 1; }
        int Peak() => WinRing0Driver.RunOnCpu(cpu.FirstPThread, () =>
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            int peak = 0;
            double work = 1.001;
            while (timer.ElapsedMilliseconds < 600)
            {
                for (int i = 0; i < 300000; i++) work = Math.Sqrt(work + 1.00001);
                if (timer.ElapsedMilliseconds > 200) peak = Math.Max(peak, cpu.ReadCurrentRingRatio());
            }
            GC.KeepAlive(work);
            return peak;
        });
        bool pass = false, restored = false;
        try
        {
            int baseline = Peak();
            int target = Math.Max(4, Math.Min((int)(before & 0x7F), baseline) - 2);
            if (baseline <= target) { Log("No usable ring baseline; no changes made."); return 1; }
            int min = Math.Min((int)((before >> 8) & 0x7F), target);
            ulong request = (before & ~0x7F7FUL) | (uint)target | ((ulong)min << 8);
            Log($"Ring-only validation {DateTime.Now:O}: original 0x{before:X16}, active peak x{baseline}, test ceiling x{target}");
            if (!driver.WriteMsr(IntelCpu.MSR_UNCORE_RATIO_LIMIT, request, cpu.FirstPThread))
                throw new IOException("Ring write failed: " + driver.LastError);
            if (!driver.ReadMsr(IntelCpu.MSR_UNCORE_RATIO_LIMIT, out ulong actual, cpu.FirstPThread))
                throw new IOException("Ring readback failed: " + driver.LastError);
            int peak = Peak();
            pass = actual == request && peak > 0 && peak <= target;
            Log($"Register 0x{actual:X16}; active peak x{peak}: {(pass ? "PASS" : "FAIL - the requested ceiling was not demonstrated")}");
        }
        catch (Exception ex) { Log(ex.Message); }
        finally
        {
            restored = driver.WriteMsr(IntelCpu.MSR_UNCORE_RATIO_LIMIT, before, cpu.FirstPThread)
                && driver.ReadMsr(IntelCpu.MSR_UNCORE_RATIO_LIMIT, out ulong actual, cpu.FirstPThread) && actual == before;
            Log(restored ? $"Original ring register restored: 0x{before:X16}" : "RESTORE FAILED: " + driver.LastError);
        }
        return pass && restored ? 0 : 2;
    }

    /// <summary>Briefly lowers the P-core ceiling and checks it under single-thread load; always restores snapshots.</summary>
    public static int Validate(string path, double volts)
    {
        if (!double.IsFinite(volts) || volts < 0.6 || volts > 1.35) throw new ArgumentOutOfRangeException(nameof(volts));
        var report = new StringBuilder();
        void Log(string text) { report.AppendLine(text); File.WriteAllText(path, report.ToString()); }
        using var thisProcess = System.Diagnostics.Process.GetCurrentProcess();
        var otherInstances = System.Diagnostics.Process.GetProcessesByName(thisProcess.ProcessName);
        try
        {
            if (otherInstances.Any(p => p.Id != thisProcess.Id))
            {
                Log("Validation refused: close all other Roch CPU instances before changing hardware.");
                return 1;
            }
        }
        finally { foreach (var p in otherInstances) p.Dispose(); }
        using var driver = WinRing0Driver.Open();
        var cpu = new IntelCpu(driver);
        int failures = 0;
        Log($"Intel validation {DateTime.Now:O}; hypervisor: {cpu.HypervisorPresent}");
        using var sensor = SuperIo.TryCreate(driver, out string sensorStatus);
        Log("Vcore sensor: " + sensorStatus);
        void SampleLoadedVoltage(string label)
        {
            WinRing0Driver.RunOnCpu(cpu.FirstPThread, () =>
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                double vidMin = double.PositiveInfinity, vidMax = 0, railMin = double.PositiveInfinity, railMax = 0, work = 1.001;
                double railSum = 0;
                int railCount = 0;
                long nextSample = 1000;
                while (timer.ElapsedMilliseconds < 3000)
                {
                    for (int i = 0; i < 300000; i++) work = Math.Sqrt(work + 1.00001);
                    if (timer.ElapsedMilliseconds < nextSample) continue;
                    nextSample = timer.ElapsedMilliseconds + 200; // Allow the monitor to refresh between samples.
                    double vid = cpu.ReadPerfStatus(cpu.FirstPThread).vid;
                    if (vid > 0) { vidMin = Math.Min(vidMin, vid); vidMax = Math.Max(vidMax, vid); }
                    if (sensor?.ReadVcore() is double rail && rail is > 0.4 and < 1.9)
                    { railMin = Math.Min(railMin, rail); railMax = Math.Max(railMax, rail); railSum += rail; railCount++; }
                }
                GC.KeepAlive(work);
                Log($"{label}: VID {(double.IsFinite(vidMin) ? $"{vidMin:0.000}–{vidMax:0.000} V" : "unavailable")}; " +
                    $"measured Vcore {(railCount > 0 ? $"{railMin:0.000}–{railMax:0.000} V, mean {railSum / railCount:0.000} V ({railCount} samples)" : "unavailable")}.");
                return 0;
            });
        }
        int PeakRatio() => WinRing0Driver.RunOnCpu(cpu.FirstPThread, () =>
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            int peak = 0;
            double work = 1.001;
            while (timer.ElapsedMilliseconds < 600)
            {
                for (int i = 0; i < 300000; i++) work = Math.Sqrt(work + 1.00001);
                if (timer.ElapsedMilliseconds > 200) peak = Math.Max(peak, cpu.ReadPerfStatus(cpu.FirstPThread).ratio);
            }
            GC.KeepAlive(work);
            return peak;
        });
        var (beforeP, _) = cpu.ReadPCoreTurboTable();
        var beforeCore = cpu.Mailbox.ReadDomain(OcMailbox.DOMAIN_CORE);
        try
        {
            int baseline = PeakRatio();
            int target = Math.Max(8, Math.Min(beforeP.Min(), baseline) - 2);
            Log($"P-core baseline: table {string.Join(",", beforeP)}, active peak x{baseline}; test ceiling x{target}");
            try
            {
                cpu.WritePCoreAllCoreRatio(target);
                int peak = PeakRatio();
                int read = cpu.ReadPCoreAllCoreRatio();
                bool pass = read == target && peak <= target && peak > 0 && baseline > target;
                Log($"P-core: table x{read}, active peak x{peak}: {(pass ? "PASS" : "FAIL - actual clock did not demonstrate the requested ceiling")}");
                if (!pass) failures++;
            }
            catch (Exception ex) { Log("P-core: FAIL " + ex.Message); failures++; }
            finally
            {
                cpu.WritePCoreTurboTable(beforeP);
                Log("P-core table restored: " + string.Join(",", cpu.ReadPCoreTurboTable().ratios));
            }
            if (cpu.ECoreCount > 0)
            {
                var (beforeE, _) = cpu.ReadECoreTurboTable();
                try { cpu.WriteECoreTurboTable(beforeE); Log("E-core existing-value write/readback: PASS on E thread " + cpu.FirstEThread); }
                catch (Exception ex) { Log("E-core existing-value write: FAIL " + ex.Message); failures++; }
            }
            try
            {
                SampleLoadedVoltage("Before override");
                cpu.Mailbox.SetOverride(OcMailbox.DOMAIN_CORE, volts);
                Log("Core voltage target readback: " + cpu.Mailbox.ReadDomain(OcMailbox.DOMAIN_CORE));
                SampleLoadedVoltage("With override");
                Log("VID is a request. Only the separate Vcore sensor measures the rail; readback success alone does not verify it.");
            }
            catch (Exception ex) { Log("Core voltage: FAIL " + ex.Message); failures++; }
            finally
            {
                cpu.Mailbox.WriteDomain(OcMailbox.DOMAIN_CORE, beforeCore);
                Log("Core mailbox restored: " + cpu.Mailbox.ReadDomain(OcMailbox.DOMAIN_CORE));
                SampleLoadedVoltage("After restore");
            }
        }
        catch (Exception ex) { Log("STOPPED: " + ex.Message); failures++; }
        Log($"Result: {failures} failed checks.");
        return failures == 0 ? 0 : 2;
    }

    public static int Run(string path)
    {
        var report = new StringBuilder();
        void Log(string text) { report.AppendLine(text); File.WriteAllText(path, report.ToString()); }
        try
        {
            using var driver = WinRing0Driver.Open();
            var cpu = new IntelCpu(driver);
            Log($"Intel audit {DateTime.Now:O}: {cpu.BrandString}");
            Log($"Hypervisor present: {((uint)X86Base.CpuId(1, 0).Ecx >> 31) != 0}");
            Log($"P thread {cpu.FirstPThread}, E thread {cpu.FirstEThread}; {cpu.PCoreCount}P + {cpu.ECoreCount}E");
            uint[] registers = [0x150, 0x194, 0x198, 0x199, 0x1AD, 0x1AE, 0x1A0, 0x620, 0x621, 0x650, 0x651, 0x770, 0x771, 0x774];
            foreach (var core in cpu.LogicalCpus.Where(c => c.IsPCore).GroupBy(c => c.CoreId).Select(g => g.First())
                .Concat(cpu.LogicalCpus.Where(c => !c.IsPCore)))
            {
                Log($"CPU {core.Index} {(core.IsPCore ? "P" : "E")} core {core.CoreId}:");
                foreach (uint reg in registers)
                    Log(driver.ReadMsr(reg, out ulong value, core.Index)
                        ? $"  0x{reg:X3}: 0x{value:X16}" : $"  0x{reg:X3}: read failed ({driver.LastError})");
            }
            foreach (int thread in new[] { cpu.FirstPThread, cpu.FirstEThread }.Where(t => t >= 0))
            {
                var mailbox = new OcMailbox(driver, thread);
                foreach (int domain in Enumerable.Range(0, 6))
                {
                    try
                    {
                        byte status = mailbox.Execute(OcMailbox.CMD_READ_VF, (byte)domain, 0, 0, out uint data);
                        Log($"CPU {thread} mailbox domain {domain}: status 0x{status:X2}, data 0x{data:X8}, {OcMailbox.Decode(data)}");
                    }
                    catch (Exception ex) { Log($"CPU {thread} mailbox domain {domain}: {ex.Message}"); }
                }
            }
            return 0;
        }
        catch (Exception ex) { Log(ex.ToString()); return 1; }
    }
}
