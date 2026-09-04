using RochPower.Hardware;

namespace RochPower.Core;

/// <summary>Live readings shown in the header.</summary>
public sealed class LiveStatus
{
    public int? PackageTempC { get; set; }
    public double? CoreMHz { get; set; }
    public int? CoreRatio { get; set; }
    public double? CoreVid { get; set; }
    public int? RingRatio { get; set; }
    public double? BclkMHz { get; set; }
    public double? PackageWatts { get; set; }
    /// <summary>Real Vcore at the VRM output, when a Super I/O can report it.</summary>
    public double? VcoreVrm { get; set; }
}

/// <summary>
/// Owns every hardware object and exposes the tunables as a flat list of
/// <see cref="Setting"/>s that the UI renders. All board-independent.
/// </summary>
public sealed class HardwareModel : IDisposable
{
    public event Action<string>? Log;

    public IKernelDriver? Driver { get; private set; }
    public IntelCpu? Cpu { get; private set; }
    public SmbiosInfo Smbios { get; private set; } = SmbiosInfo.Read();
    public BclkMeter? Bclk { get; private set; }
    public SmbusI801? Smbus { get; private set; }
    public SuperIo? SuperIo { get; private set; }
    public string SuperIoStatus { get; private set; } = "not probed";
    public List<Ddr5Dimm> Dimms { get; } = new();
    public List<Setting> Settings { get; } = new();

    public string DriverStatus { get; private set; } = "not loaded";
    public string SmbusStatus { get; private set; } = "not probed";
    public bool MailboxAvailable { get; private set; }
    /// <summary>True when the BIOS left the core domain in mailbox Override mode (fixed VID).</summary>
    public bool BiosCoreOverride { get; private set; }
    public bool OcLocked { get; private set; }
    public bool PowerLimitsLocked { get; private set; }
    public double? LastBclk { get; private set; }

    private double _lastEnergyJ; private DateTime _lastEnergyAt;

    private void Emit(string msg) => Log?.Invoke(msg);

    /// <summary>Opens the driver and probes the hardware. Never throws: failures are logged and the affected rows become unavailable.</summary>
    public void Initialize()
    {
        try
        {
            var drv = WinRing0Driver.Open();
            Driver = drv;
            DriverStatus = $"{drv.Name} (driver v{drv.GetDriverVersion():X})";
            Emit($"Driver: {DriverStatus}");
        }
        catch (Exception ex)
        {
            DriverStatus = "unavailable";
            Emit("Driver error: " + ex.Message);
            Emit("Running in read-only / offline mode.");
        }

        if (Driver != null)
        {
            try
            {
                Cpu = new IntelCpu(Driver);
                Emit($"CPU: {Cpu.BrandString} - {Cpu.Generation}, {Cpu.PCoreCount}P + {Cpu.ECoreCount}E cores, base ratio {Cpu.BaseRatio}, TjMax {Cpu.TjMax} C");
                if (!Cpu.IsLga1700Family) Emit("Warning: this CPU is not a known LGA1700 (12th-14th Gen desktop) part. Ratio/voltage controls may not behave as expected.");
                OcLocked = Cpu.IsOcLocked;
                if (OcLocked) Emit("Warning: BIOS has set OC Lock (MSR 0x194 bit 20). Ratio and voltage changes will be rejected on this board/chipset.");
                MailboxAvailable = Cpu.Mailbox.IsAvailable;
                if (!MailboxAvailable) Emit("OC mailbox (MSR 0x150) not responding; FIVR voltage rows disabled.");
                else
                {
                    try
                    {
                        var core = Cpu.Mailbox.ReadDomain(OcMailbox.DOMAIN_CORE);
                        BiosCoreOverride = core.OverrideMode;
                        if (BiosCoreOverride)
                            Emit($"BIOS has the core voltage in Override mode ({core.TargetVolts:0.000} V fixed VID). MSI boards also pin the VRM output in this mode, so " +
                                 "CPU Core Voltage here moves only the VID the CPU requests and the real Vcore stays where the BIOS put it. " +
                                 "Set the BIOS CPU Core Voltage Mode to Adaptive or Auto to control Vcore from here. " +
                                 "Watch the Vcore tile against VID to confirm which is happening.");
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Emit("CPU init error: " + ex.Message); }

            try
            {
                Bclk = new BclkMeter(Driver);
                Emit("BCLK: " + Bclk.Status);
            }
            catch (Exception ex) { Emit("BCLK meter error: " + ex.Message); }

            try
            {
                Smbus = SmbusI801.TryCreate(Driver, out string st);
                SmbusStatus = st;
                Emit("SMBus: " + st);
                if (Smbus != null)
                {
                    Dimms.AddRange(Ddr5Dimm.Probe(Smbus));
                    if (Dimms.Count == 0) Emit("No DDR5 SPD5118 hubs found on the SMBus (DDR4 board, or DIMM SMBus segment not routed through the PCH).");
                    foreach (var d in Dimms)
                    {
                        Emit($"{d.SlotName}: SPD at 0x{d.SpdAddress:X2}, PMIC {(d.HasPmic ? $"at 0x{d.PmicAddress:X2} ({d.PmicVendor})" : "not reachable")}");
                        if (d.HasPmic) Emit($"{d.SlotName}: {d.CalibrationNote}");
                    }
                }
            }
            catch (Exception ex) { Emit("SMBus error: " + ex.Message); }

            try
            {
                SuperIo = Hardware.SuperIo.TryCreate(Driver, out string sio);
                SuperIoStatus = sio;
                Emit("Super I/O: " + sio);
                if (SuperIo != null)
                {
                    var rails = SuperIo.ReadRails();
                    Emit("Board rails: " + (rails.Count == 0 ? "none readable" : string.Join(", ", rails.Select(r => $"{r.Name} {r.Volts:0.000} V"))));
                }
            }
            catch (Exception ex) { Emit("Super I/O error: " + ex.Message); }
        }

        BuildSettings();
        RefreshAll(captureDefaults: true);
    }

    private void BuildSettings()
    {
        Settings.Clear();
        var cpu = Cpu;

        // ---------------- clocks ----------------
        Settings.Add(new Setting
        {
            Id = "cpu_ratio", Name = "CPU Ratio (P-Core)", Group = SettingGroup.Clocks, Min = 8, Max = 120, Decimals = 2,
            Read = () => cpu?.ReadPCoreAllCoreRatio(),
            Write = cpu == null ? null : v => cpu.WritePCoreAllCoreRatio((int)Math.Round(v)),
            Note = "All-core P-core multiplier (MSR 0x1AD turbo ratio table). Use Per Core Ratio for the per-active-core-count table.",
            Available = cpu != null
        });
        Settings.Add(new Setting
        {
            Id = "ecore_ratio", Name = "E-Core Ratio", Group = SettingGroup.Clocks, Min = 8, Max = 120, Decimals = 2,
            Read = () => cpu is { ECoreCount: > 0 } ? cpu.ReadECoreAllCoreRatio() : null,
            Write = cpu is { ECoreCount: > 0 } ? v => cpu.WriteECoreAllCoreRatio((int)Math.Round(v)) : null,
            Note = "All-core E-core multiplier (MSR 0x650).",
            Available = cpu is { ECoreCount: > 0 }
        });
        Settings.Add(new Setting
        {
            Id = "ring_ratio", Name = "Ring Ratio", Group = SettingGroup.Clocks, Min = 4, Max = 120, Decimals = 1,
            Read = () => cpu?.ReadRingRatio().max,
            Write = cpu == null ? null : v => cpu.WriteRingRatio((int)Math.Round(v)),
            Note = "Maximum ring/uncore multiplier. Written to the OC mailbox ring domain and MSR 0x620; on Alder/Raptor Lake only the mailbox value takes effect.",
            Available = cpu != null
        });
        Settings.Add(new Setting
        {
            Id = "bclk", Name = "Base Clock", Group = SettingGroup.Clocks, Min = 10, Max = 655.25, Decimals = 2,
            Read = () => LastBclk,
            Write = null,
            Note = "Measured from TSC vs ACPI timer. Read-only: BCLK programming needs the Intel ICC (clock controller) interface, which is board firmware specific.",
            Available = Bclk?.IsAvailable == true
        });

        // ---------------- FIVR voltages via OC mailbox ----------------
        var mb = cpu?.Mailbox;
        bool mbOk = mb != null && MailboxAvailable;
        void AddDomain(string idPrefix, string name, int domain, double minV, double maxV, bool available)
        {
            Settings.Add(new Setting
            {
                Id = idPrefix + "_v", Name = name + " Voltage", Group = SettingGroup.Voltages, Unit = "V", Min = minV, Max = maxV, Decimals = 3,
                Read = () =>
                {
                    if (!mbOk || !available) return null;
                    var s = mb!.ReadDomain(domain);
                    return s.OverrideMode ? s.TargetVolts : null;
                },
                Write = mbOk && available ? v => mb!.SetOverride(domain, v) : null,
                RestoreDefault = mbOk && available ? () => mb!.SetOverride(domain, 0) : null,
                Note = $"Static override voltage for domain {domain} through the OC mailbox: the VID the CPU requests from the VRM. The board's load-line adds its own offset on top (the VID readout shows the request). Auto = adaptive (default). Enter 0 to go back to Auto.",
                Available = mbOk && available
            });
            Settings.Add(new Setting
            {
                Id = idPrefix + "_off", Name = name + " Voltage Offset", Group = SettingGroup.Voltages, Unit = "mV", Min = -500, Max = 500, Decimals = 0,
                Read = () => (!mbOk || !available) ? null : Math.Round(mb!.ReadDomain(domain).OffsetVolts * 1000),
                Write = mbOk && available ? v => mb!.SetOffset(domain, v / 1000.0) : null,
                Note = $"Adaptive offset for FIVR domain {domain} (mailbox 0x150). Negative values undervolt.",
                Available = mbOk && available
            });
        }
        bool DomainReadable(int d) { try { mb!.ReadDomain(d); return true; } catch { return false; } }
        AddDomain("core", "CPU Core", OcMailbox.DOMAIN_CORE, 0.600, 1.720, mbOk && DomainReadable(OcMailbox.DOMAIN_CORE));
        AddDomain("ecore", "CPU E-Core L2", OcMailbox.DOMAIN_ECORE, 0.600, 1.520, mbOk && cpu is { ECoreCount: > 0 } && DomainReadable(OcMailbox.DOMAIN_ECORE));
        AddDomain("ring", "Ring", OcMailbox.DOMAIN_RING, 0.600, 1.520, mbOk && DomainReadable(OcMailbox.DOMAIN_RING));
        AddDomain("sa", "SA", OcMailbox.DOMAIN_SA, 0.600, 1.520, mbOk && DomainReadable(OcMailbox.DOMAIN_SA));
        AddDomain("gt", "GT (iGPU)", OcMailbox.DOMAIN_GT, 0.600, 1.520, mbOk && DomainReadable(OcMailbox.DOMAIN_GT));

        // ---------------- power limits ----------------
        if (cpu != null)
        {
            try { PowerLimitsLocked = cpu.ReadPackagePowerLimits().locked; } catch { }
        }
        Settings.Add(new Setting
        {
            Id = "pl1", Name = "Package Power Limit 1 (PL1)", Group = SettingGroup.Power, Unit = "W", Min = 10, Max = 4095, Decimals = 0,
            Read = () => cpu?.ReadPackagePowerLimits().pl1,
            Write = cpu != null && !PowerLimitsLocked ? v => cpu.WritePackagePowerLimits(v, null) : null,
            Note = PowerLimitsLocked ? "Locked by BIOS (MSR 0x610 bit 63)." : "Long duration package power limit (MSR 0x610).",
            Available = cpu != null
        });
        Settings.Add(new Setting
        {
            Id = "pl2", Name = "Package Power Limit 2 (PL2)", Group = SettingGroup.Power, Unit = "W", Min = 10, Max = 4095, Decimals = 0,
            Read = () => cpu?.ReadPackagePowerLimits().pl2,
            Write = cpu != null && !PowerLimitsLocked ? v => cpu.WritePackagePowerLimits(null, v) : null,
            Note = PowerLimitsLocked ? "Locked by BIOS (MSR 0x610 bit 63)." : "Short duration package power limit (MSR 0x610).",
            Available = cpu != null
        });

        // ---------------- board VRM rails ----------------
        // These have no CPU-side register at all: the motherboard's regulators produce them, and
        // the only way to set them is that board's own VRM protocol. Shown live, read-only, rather
        // than offered as a field that would silently do nothing.
        if (SuperIo != null)
        {
            var sio = SuperIo;
            void AddBoardRail(string id, string name, int rail, string note)
            {
                if (sio.ReadVoltage(rail) is not double v || v < 0.05) return;
                Settings.Add(new Setting
                {
                    Id = id, Name = name, Group = SettingGroup.Board, Unit = "V", Min = 0, Max = 5, Decimals = 3,
                    Read = () => sio.ReadVoltage(rail), Write = null, Note = note, Available = true
                });
            }
            AddBoardRail("cpu_vdd2", "CPU VDD2 Voltage", Hardware.SuperIo.RailVdd2,
                "Memory-controller input rail, measured at the board. It is produced by the motherboard VRM and has no CPU register, so it cannot be set from here - only from the BIOS or the board vendor's own tool.");
            AddBoardRail("cpu_aux", "CPU AUX Voltage", Hardware.SuperIo.RailAux,
                "CPU AUX (VCCIN AUX) rail, measured at the board. Board VRM only, same as VDD2: no CPU-side path to set it.");
        }

        // ---------------- DDR5 PMIC ----------------
        foreach (var d in Dimms)
        {
            var dimm = d;
            string unverified = " Scale could not be verified against the PMIC ADC (secure-mode PMIC), so this row is read-only.";
            Settings.Add(new Setting
            {
                Id = $"{dimm.SlotName.ToLowerInvariant()}_vdd", Name = $"DRAM {dimm.SlotName} Voltage", Group = SettingGroup.Memory, Unit = "V",
                Min = Ddr5Dimm.VddMinV, Max = Ddr5Dimm.VddMaxV, Decimals = 3,
                Read = dimm.ReadVdd, Write = dimm.VddVerified ? dimm.WriteVdd : null,
                Note = $"VDD set-point on the PMIC of {dimm.SlotName} (0x{dimm.PmicAddress:X2}, {dimm.PmicVendor}), register step {(dimm.VddVerified ? dimm.VddStepMv + " mV" : "unknown")}." + (dimm.VddVerified ? "" : unverified),
                Available = dimm.HasPmic
            });
            Settings.Add(new Setting
            {
                Id = $"{dimm.SlotName.ToLowerInvariant()}_vddq", Name = $"DRAM {dimm.SlotName} VDDQ Voltage", Group = SettingGroup.Memory, Unit = "V",
                Min = Ddr5Dimm.VddqMinV, Max = Ddr5Dimm.VddqMaxV, Decimals = 3,
                Read = dimm.ReadVddq, Write = dimm.VddqVerified ? dimm.WriteVddq : null,
                Note = "VDDQ set-point (PMIC SWC rail, 5 mV steps)." + (dimm.VddqVerified ? "" : unverified), Available = dimm.HasPmic
            });
            Settings.Add(new Setting
            {
                Id = $"{dimm.SlotName.ToLowerInvariant()}_vpp", Name = $"DRAM {dimm.SlotName} VPP Voltage", Group = SettingGroup.Memory, Unit = "V",
                Min = Ddr5Dimm.VppMinV, Max = Ddr5Dimm.VppMaxV, Decimals = 3,
                Read = dimm.ReadVpp, Write = dimm.VppVerified ? dimm.WriteVpp : null,
                Note = "VPP set-point (PMIC SWD rail, 5 mV steps)." + (dimm.VppVerified ? "" : unverified), Available = dimm.HasPmic
            });
        }
    }

    public void RefreshAll(bool captureDefaults = false)
    {
        foreach (var s in Settings)
        {
            if (!s.Available) { s.Current = null; continue; }
            try { s.Current = s.Read(); }
            catch (Exception ex) { s.Current = null; Emit($"{s.Name}: read failed - {ex.Message}"); }
            if (captureDefaults) s.DefaultValue = s.Current;
        }
    }

    public void Refresh(Setting s)
    {
        if (!s.Available) return;
        try { s.Current = s.Read(); }
        catch (Exception ex) { Emit($"{s.Name}: read failed - {ex.Message}"); }
    }

    /// <summary>Average VID over a few samples; idle VID jitters, so one reading is not enough.</summary>
    private double? SampleVid()
    {
        if (Cpu == null) return null;
        try
        {
            double sum = 0;
            for (int i = 0; i < 4; i++) { sum += Cpu.ReadPerfStatus(Cpu.FirstPThread).vid; Thread.Sleep(25); }
            return sum / 4;
        }
        catch { return null; }
    }

    /// <summary>
    /// Mean and spread of the measured Vcore. The spread matters: the rail moves on its own with
    /// idle load, and without knowing that band a noise blip reads as a successful change.
    /// </summary>
    private (double mean, double spread)? SampleVcore(int samples = 6)
    {
        if (SuperIo == null) return null;
        try
        {
            var vals = new List<double>(samples);
            for (int i = 0; i < samples; i++)
            {
                if (SuperIo.ReadVcore() is double v) vals.Add(v);
                Thread.Sleep(30);
            }
            return vals.Count == 0 ? null : (vals.Average(), vals.Max() - vals.Min());
        }
        catch { return null; }
    }

    /// <summary>
    /// True when the row drives the core voltage, so an apply can be checked against the rail
    /// the VRM actually produces rather than trusting that the CPU's request was honoured.
    /// </summary>
    private static bool IsCoreVoltageRow(Setting s) => s.Id is "core_v" or "core_off";

    /// <summary>Applies one value with the MSI-style rule that 0 restores the default captured at start-up.</summary>
    public bool Apply(Setting s, double value)
    {
        if (s.Write == null) { Emit($"{s.Name}: read-only."); return false; }
        bool verify = IsCoreVoltageRow(s) && SuperIo != null;
        double? vidBefore = verify ? SampleVid() : null;
        var railBefore = verify ? SampleVcore() : null;
        try
        {
            if (value == 0)
            {
                if (s.RestoreDefault != null) s.RestoreDefault();
                else if (s.DefaultValue is double d) s.Write(d);
                else { Emit($"{s.Name}: no default captured; nothing changed."); return false; }
                Refresh(s);
                Emit($"{s.Name}: restored default ({s.CurrentText}{(s.Unit == "" ? "" : " " + s.Unit)}).");
                return true;
            }
            if (value < s.Min || value > s.Max)
            {
                Emit($"{s.Name}: {s.Format(value)} is outside {s.RangeText}.");
                return false;
            }
            s.Write(value);
            Refresh(s);
            Emit($"{s.Name}: set to {s.Format(value)}{(s.Unit == "" ? "" : " " + s.Unit)} (now {s.CurrentText}).");
            if (verify) VerifyAgainstRail(s, vidBefore, railBefore);
            return true;
        }
        catch (Exception ex)
        {
            Emit($"{s.Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The CPU accepting a voltage request does not mean the board acted on it: with the BIOS core
    /// voltage in Override mode the VRM is pinned and the request goes nowhere. Compare the VID the
    /// CPU now asks for against the rail the Super I/O measures, and say so when they disagree.
    /// </summary>
    private void VerifyAgainstRail(Setting s, double? vidBefore, (double mean, double spread)? railBefore)
    {
        Thread.Sleep(300);
        double? vidAfter = SampleVid();
        var railAfter = SampleVcore();
        if (vidBefore is not double v0 || vidAfter is not double v1 || railBefore is not { } r0 || railAfter is not { } r1) return;
        double dVid = (v1 - v0) * 1000, dRail = (r1.mean - r0.mean) * 1000;
        if (Math.Abs(dVid) < 15) return; // request barely moved: nothing to check

        // A real change must be in the same direction as the request, clear of the rail's own
        // noise band, and a decent fraction of what was asked for. Magnitude alone is not enough:
        // idle Vcore wanders by tens of millivolts on its own.
        double noise = Math.Max(Math.Max(r0.spread, r1.spread) * 1000, 12);
        bool sameDirection = Math.Sign(dRail) == Math.Sign(dVid);
        bool followed = sameDirection && Math.Abs(dRail) > noise && Math.Abs(dRail) >= Math.Abs(dVid) * 0.35;

        if (followed)
        {
            Emit($"{s.Name}: verified at the rail - VID {dVid:+0;-0} mV, measured Vcore {dRail:+0;-0} mV (now {r1.mean:0.000} V).");
            CoreVoltageIgnored = false;
            return;
        }
        CoreVoltageIgnored = true;
        Emit($"{s.Name}: NOT APPLIED at the rail. The CPU now requests {dVid:+0;-0} mV but the measured Vcore moved {dRail:+0;-0} mV " +
             $"(still {r1.mean:0.000} V, rail noise +/-{noise:0} mV). The board is holding the VRM at a fixed voltage, so the CPU's request is ignored. " +
             "Set CPU Core Voltage Mode to Adaptive (or Auto) in the BIOS; if it is already there, this board only accepts Vcore from its own VRM tool.");
    }

    /// <summary>Set when an apply was measured not to reach the rail.</summary>
    public bool CoreVoltageIgnored { get; private set; }

    public void RestoreAllDefaults()
    {
        foreach (var s in Settings.Where(s => s.Available && !s.ReadOnly))
        {
            bool changed = s.Current != s.DefaultValue;
            if (changed) Apply(s, 0);
        }
    }

    // ---------------------------------------------------------------- live
    public LiveStatus ReadLive()
    {
        var st = new LiveStatus();
        if (Cpu == null) return st;
        try { st.PackageTempC = Cpu.ReadPackageTemperature(); } catch { }
        try
        {
            var (ratio, vid) = Cpu.ReadPerfStatus(Cpu.FirstPThread);
            st.CoreVid = vid;
            st.CoreRatio = Math.Max(ratio, Cpu.ReadMaxCurrentPRatio());
            st.CoreMHz = st.CoreRatio * (LastBclk ?? 100.0);
        }
        catch { }
        try { st.RingRatio = Cpu.ReadCurrentRingRatio(); } catch { }
        try
        {
            double e = Cpu.ReadPackageEnergyJoules();
            var now = DateTime.UtcNow;
            if (_lastEnergyAt != default)
            {
                double dt = (now - _lastEnergyAt).TotalSeconds;
                double de = e - _lastEnergyJ;
                if (de < 0) de += 4294967296.0 * (1.0 / 65536); // 32-bit wrap at the common 1/65536 J unit
                if (dt > 0.2) st.PackageWatts = de / dt;
            }
            _lastEnergyJ = e; _lastEnergyAt = now;
        }
        catch { }
        st.BclkMHz = LastBclk;
        try { st.VcoreVrm = SuperIo?.ReadVcore(); } catch { }
        return st;
    }

    /// <summary>Blocking ~60 ms measurement; call from a background thread.</summary>
    public double? MeasureBclk()
    {
        if (Bclk is not { IsAvailable: true } || Cpu == null || Cpu.BaseRatio == 0) return null;
        try
        {
            LastBclk = Bclk.MeasureBclkMHz(Cpu.BaseRatio);
            return LastBclk;
        }
        catch (Exception ex) { Emit("BCLK measure failed: " + ex.Message); return null; }
    }

    public void Dispose()
    {
        SuperIo?.Dispose();
        Bclk?.Dispose();
        Smbus?.Dispose();
        Driver?.Dispose();
    }
}
