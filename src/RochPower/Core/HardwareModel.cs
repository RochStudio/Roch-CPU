using System.Runtime.Intrinsics.X86;
using System.Text;
using RochPower.Hardware;

namespace RochPower.Core;

/// <summary>Live readings shown in the header.</summary>
public sealed class LiveStatus
{
    public int? PackageTempC { get; set; }
    public double? CoreMHz { get; set; }
    public int? CoreRatio { get; set; }
    public int? ECoreRatio { get; set; }
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
    /// <summary>Intel LGA1700 path. Null on AMD.</summary>
    public IntelCpu? Cpu { get; private set; }
    /// <summary>AMD Zen path. Null on Intel.</summary>
    public AmdCpu? Amd { get; private set; }
    public bool IsAmd => Amd != null;
    public SmbiosInfo Smbios { get; private set; } = SmbiosInfo.Read();
    public BclkMeter? Bclk { get; private set; }
    public ISmbus? Smbus { get; private set; }
    public SuperIo? SuperIo { get; private set; }
    public string SuperIoStatus { get; private set; } = "not probed";
    /// <summary>Base-clock control, when the board's clock generator is reachable. Null otherwise.</summary>
    public BclkController? BclkControl { get; private set; }
    public AsusBoardControl? AsusControl { get; private set; }
    /// <summary>CPU VDD2 control, when the board's regulator is reachable. Null otherwise.</summary>
    public Vdd2Rail? Vdd2 { get; private set; }
    /// <summary>MSI board-side CPU-core regulator control. The Intel mailbox alone is insufficient on these boards.</summary>
    public MsiCoreVoltage? CoreVoltage { get; private set; }
    public string CoreVoltageStatus { get; private set; } = "not probed";
    public List<Ddr5Dimm> Dimms { get; } = new();
    public List<Setting> Settings { get; } = new();

    public string DriverStatus { get; private set; } = "not loaded";
    public string SmbusStatus { get; private set; } = "not probed";
    public bool MailboxAvailable { get; private set; }
    public string? VoltageAccessRestriction { get; private set; }
    /// <summary>True when the BIOS left the core domain in mailbox Override mode (fixed VID).</summary>
    public bool BiosCoreOverride { get; private set; }
    public bool OcLocked { get; private set; }
    public bool PowerLimitsLocked { get; private set; }
    public double? LastBclk { get; private set; }

    // AMD
    public bool SmuAvailable { get; private set; }
    public string SmuStatus { get; private set; } = "not probed";
    /// <summary>IsOverclockable flags from the SMU: bit 0 OC allowed, bit 1 power limits, bit 2 PBO. Null when not answered.</summary>
    public uint? OcCapabilities { get; private set; }
    public bool PboAllowed => OcCapabilities is not uint c || (c & 0x4) != 0;
    /// <summary>The CPU's fused stock PPT / TDC / EDC, what "0" restores on those rows.</summary>
    public (double? ppt, double? tdc, double? edc) StockLimits { get; private set; }

    // ---- header text that does not care which vendor is underneath
    public string CpuName => Cpu?.BrandString ?? Amd?.BrandString ?? "No CPU access";
    /// <summary>Intel shows the generation under the CPU name; on AMD the brand string already says it all.</summary>
    public string CpuGeneration => Cpu?.Generation ?? "";
    public string CoreSummary
    {
        get
        {
            if (Cpu is { } c)
                return c.ECoreCount > 0 ? $"{c.PCoreCount}P + {c.ECoreCount}E / {c.LogicalCpus.Count}" : $"{c.PCoreCount} / {c.LogicalCpus.Count}";
            if (Amd is { } a)
                return $"{a.CoreCount} / {a.LogicalCount}";
            return "";
        }
    }
    public int BaseRatio => Cpu?.BaseRatio ?? Amd?.BaseRatio ?? 0;
    public uint MicrocodeRevision => Cpu?.MicrocodeRevision ?? Amd?.PatchLevel ?? 0;

    private double _lastEnergyJ; private DateTime _lastEnergyAt;

    private void Emit(string msg) => Log?.Invoke(msg);

    private static bool IsAmdVendor()
    {
        var l0 = X86Base.CpuId(0, 0);
        string vendor = Encoding.ASCII.GetString(BitConverter.GetBytes(l0.Ebx)) + Encoding.ASCII.GetString(BitConverter.GetBytes(l0.Edx)) + Encoding.ASCII.GetString(BitConverter.GetBytes(l0.Ecx));
        return vendor is "AuthenticAMD" or "HygonGenuine";
    }

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
            if (IsAmdVendor()) InitializeAmd(); else InitializeIntel();

            try
            {
                Bclk = new BclkMeter(Driver);
                Emit("BCLK: " + Bclk.Status);
            }
            catch (Exception ex) { Emit("BCLK meter error: " + ex.Message); }

            try
            {
                string st;
                Smbus = IsAmd ? SmbusPiix4.TryCreate(Driver, out st) : SmbusI801.TryCreate(Driver, out st);
                SmbusStatus = st;
                Emit("SMBus: " + st);
                if (Smbus != null)
                {
                    Dimms.AddRange(Ddr5Dimm.Probe(Smbus));
                    if (Dimms.Count == 0)
                        Emit(IsAmd
                            ? "No DDR5 SPD5118 hubs answer on the FCH SMBus port the BIOS left selected. The DIMMs may sit on another port of the FCH mux, which needs an MMIO write this driver cannot do; the memory rows stay hidden."
                            : "No DDR5 SPD5118 hubs found on the SMBus (DDR4 board, or DIMM SMBus segment not routed through the PCH).");
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

                    // The clock generator sits on the EC's own I2C bus, so it is only reachable
                    // where that mailbox exists. Nothing is written here; the controller reads its
                    // baseline and does not touch the clock until a value is applied.
                    if (EcMailbox.IsSupported(SuperIo))
                    {
                        var mailbox = new EcMailbox(SuperIo);
                        bool msiBoard = Smbios.BoardManufacturer.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase)
                                     || Smbios.SystemManufacturer.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase);
                        if (Cpu != null && msiBoard)
                        {
                            CoreVoltage = MsiCoreVoltage.TryCreate(mailbox, out string coreVoltageStatus);
                            CoreVoltageStatus = coreVoltageStatus;
                            Emit("CPU core regulator: " + coreVoltageStatus);
                        }
                        if (Cpu is { } bclkCpu && Bclk is { IsAvailable: true } bclkMeter)
                        {
                            var control = new BclkController(new EcClockGen(mailbox),
                                                             () => bclkMeter.MeasureBclkFromCycles(bclkCpu.FirstPThread));
                            if (control.CaptureBaseline()) BclkControl = control;
                            Emit("Clock generator: " + control.Status);
                        }
                        var vdd2 = new Vdd2Rail(mailbox, () => SuperIo.ReadVoltage(Hardware.SuperIo.RailVdd2));
                        if (vdd2.CaptureBaseline()) Vdd2 = vdd2;
                        Emit("CPU VDD2: " + vdd2.Status);
                    }
                }
            }
            catch (Exception ex) { Emit("Super I/O error: " + ex.Message); }
        }

        if (Cpu != null)
        {
            AsusControl = AsusBoardControl.TryCreate(Smbios.BoardManufacturer, out string asusStatus);
            if (Smbios.BoardManufacturer.Contains("ASUS", StringComparison.OrdinalIgnoreCase))
                Emit("ASUS control: " + asusStatus);
        }
        BuildSettings();
        RefreshAll(captureDefaults: true);
    }

    private void InitializeIntel()
    {
        try
        {
            Cpu = new IntelCpu(Driver!);
            if (Cpu.HypervisorPresent)
                Emit("Hypervisor detected. Register readback may be virtualized and does not prove a tuning change reached the CPU. VBS/Memory Integrity can restrict overclocking access.");
            Emit($"CPU: {Cpu.BrandString} - {Cpu.Generation}, {Cpu.PCoreCount}P + {Cpu.ECoreCount}E cores, base ratio {Cpu.BaseRatio}, TjMax {Cpu.TjMax} C");
            if (!Cpu.IsLga1700Family) Emit("Warning: this CPU is not a known LGA1700 (12th-14th Gen desktop) part. Ratio/voltage controls may not behave as expected.");
            OcLocked = Cpu.IsOcLocked;
            if (OcLocked) Emit("Warning: BIOS has set OC Lock (MSR 0x194 bit 20). Ratio and voltage changes will be rejected on this board/chipset.");
            MailboxAvailable = Cpu.Mailbox.IsAvailable;
            if (Cpu.HypervisorPresent)
            {
                var restrictions = HypervisorRestrictions.ReadCurrentBoot(out string status);
                Emit(status);
                VoltageAccessRestriction = HypervisorRestrictions.VoltageBlockReason(restrictions);
                if (VoltageAccessRestriction != null) MailboxAvailable = false;
            }
            if (!MailboxAvailable) Emit(VoltageAccessRestriction ?? "OC mailbox (MSR 0x150) not responding; FIVR voltage rows disabled.");
            else
            {
                try
                {
                    var core = Cpu.Mailbox.ReadDomain(OcMailbox.DOMAIN_CORE);
                    BiosCoreOverride = core.OverrideMode;
                    if (BiosCoreOverride)
                        Emit($"BIOS has the core voltage in Override mode ({core.TargetVolts:0.000} V). " +
                             "The board regulator path will also be probed before CPU Core Voltage is made writable; the measured Vcore rail is checked after each change.");
                }
                catch { }
            }
        }
        catch (Exception ex) { Emit("CPU init error: " + ex.Message); }
    }

    private void InitializeAmd()
    {
        try
        {
            Amd = new AmdCpu(Driver!);
            Emit($"CPU: {Amd.BrandString} - {Amd.CodeName} ({Amd.Generation}), family {Amd.Family:X}h model {Amd.Model:X2}h stepping {Amd.Stepping}, " +
                 $"{Amd.CoreCount} cores / {Amd.LogicalCount} threads, P0 ratio {Amd.BaseRatio}, microcode 0x{Amd.PatchLevel:X8}");
            Emit($"Topology: {Amd.TopologyNote}: " + string.Join(", ", Amd.Cores.Select(c => $"{c.Label}={c.Location}")));
            if (!Amd.IsSupported) { Emit("Warning: unknown Zen generation. The SMU message numbers of Zen 4 / Zen 5 are assumed; nothing is written until the SMU answers the test message."); }

            SmuAvailable = Amd.Smu.Probe();
            SmuStatus = Amd.Smu.Status;
            Emit("SMU: " + SmuStatus);
            if (!SmuAvailable) return;

            OcCapabilities = Amd.Smu.ReadOcCapabilities();
            if (OcCapabilities is uint caps)
                Emit($"SMU overclocking capabilities 0x{caps:X}: OC {((caps & 1) != 0 ? "allowed" : "locked")}, power limits {((caps & 2) != 0 ? "adjustable" : "locked")}, PBO {((caps & 4) != 0 ? "available" : "not available (enable Precision Boost Overdrive in the BIOS)")}");
            StockLimits = Amd.Smu.ReadStockLimits();
            if (StockLimits.ppt != null || StockLimits.tdc != null || StockLimits.edc != null)
                Emit($"Stock limits fused into this CPU: PPT {StockLimits.ppt?.ToString("0") ?? "n/a"} W, TDC {StockLimits.tdc?.ToString("0") ?? "n/a"} A, EDC {StockLimits.edc?.ToString("0") ?? "n/a"} A (what 0 writes on those rows).");
            if (Amd.Smu.ReadSustainedLimits() is { } sl) Emit($"Platform sustained limits: {sl.power} W, {sl.temp} C");

            if (Amd.Smu.LocateTable(out string ts))
            {
                Emit("SMU " + ts);
                bool pawn = Amd.Smu.TryAttachPawnIo(Path.Combine(AppContext.BaseDirectory, "pawnio", "RyzenSMU.bin"), out string ps);
                Emit("Power table: " + ps);
                if (!pawn) Emit("PPT/TDC/EDC rows show Auto (the BIOS value is not readable) until a value is written here.");
                else if (!Amd.Smu.RefreshTable()) Emit("Power table read failed: " + Amd.Smu.LastTableError);
                else if (Amd.Smu.Layout == null) Emit("The limit positions in this table version are not known; PPT/TDC/EDC rows show Auto until written.");
                else Emit($"Limits in force from the table ({Amd.Smu.Layout.Name}): PPT {Amd.Smu.PptLimit?.ToString("0") ?? "?"} W (drawing {Amd.Smu.PptValue:0.0}), TDC {Amd.Smu.TdcLimit?.ToString("0") ?? "?"} A (drawing {Amd.Smu.TdcValue:0.0}), EDC {Amd.Smu.EdcLimit?.ToString("0") ?? "?"} A (drawing {Amd.Smu.EdcValue:0.0}), Tctl max {Amd.Smu.ThmLimit?.ToString("0") ?? "?"} C, socket {Amd.Smu.SocketPower:0.0} W");
            }
            else Emit("SMU power table: " + ts);
        }
        catch (Exception ex) { Emit("CPU init error: " + ex.Message); }
    }

    private void BuildSettings()
    {
        Settings.Clear();
        if (Amd != null) BuildAmdSettings();
        else { BuildIntelSettings(); BuildBoardRails(); }
        BuildDimms();
    }

    // ------------------------------------------------------------------ Intel rows
    private void BuildIntelSettings()
    {
        var cpu = Cpu;

        // ---------------- clocks ----------------
        Settings.Add(new Setting
        {
            Id = "cpu_ratio", Name = "CPU Ratio (P-Core)", Group = SettingGroup.Clocks, Min = 8, Max = 120, Decimals = 2,
            RequireReadBack = true,
            Read = () => cpu?.ReadPCoreAllCoreRatio(),
            Write = cpu == null ? null : v => cpu.WritePCoreAllCoreRatio((int)Math.Round(v)),
            Note = "All-core P-core multiplier (MSR 0x1AD turbo ratio table). Use Per Core Ratio for the per-active-core-count table.",
            Available = cpu != null
        });
        Settings.Add(new Setting
        {
            Id = "ecore_ratio", Name = "E-Core Ratio", Group = SettingGroup.Clocks, Min = 8, Max = 120, Decimals = 2,
            RequireReadBack = true,
            Read = () => cpu is { ECoreCount: > 0 } ? cpu.ReadECoreAllCoreRatio() : null,
            Write = cpu is { ECoreCount: > 0 } ? v => cpu.WriteECoreAllCoreRatio((int)Math.Round(v)) : null,
            Note = "All-core E-core multiplier (MSR 0x650).",
            Available = cpu is { ECoreCount: > 0 }
        });
        Settings.Add(new Setting
        {
            Id = "ring_ratio", Name = "Ring Ratio", Group = SettingGroup.Clocks, Min = 4, Max = 120, Decimals = 1,
            RequireReadBack = true,
            Read = () => cpu?.ReadRingRatio().max,
            Write = cpu == null ? null : v => cpu.WriteRingRatio((int)Math.Round(v)),
            Note = "Maximum ring/uncore multiplier. Verifies MSR 0x620 and updates the additional mailbox ceiling when one is reported. The running ring clock can be lower than this limit.",
            Available = cpu != null
        });
        AddBclkRow();

        // ---------------- FIVR voltages via OC mailbox ----------------
        var mb = cpu?.Mailbox;
        bool mbOk = mb != null && MailboxAvailable;
        bool msiBoard = Smbios.BoardManufacturer.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase)
                     || Smbios.SystemManufacturer.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase);
        void WriteOverride(int domain, double volts)
        {
            if (domain != OcMailbox.DOMAIN_CORE || CoreVoltage == null)
            {
                mb!.SetOverride(domain, volts);
                return;
            }

            // MSI programs the board regulator first and the CPU request second. Keep a complete
            // regulator snapshot so a rejected OC-mailbox write cannot leave the two disagreeing.
            var before = CoreVoltage.ReadState();
            var mailboxBefore = mb!.ReadDomain(domain);
            CoreVoltage.SetOverride(volts);
            try
            {
                mb.SetOverride(domain, volts);
                var after = mb.ReadDomain(domain);
                if (!after.OverrideMode || Math.Abs(after.TargetVolts - volts) > 0.0011)
                    throw new IOException($"Intel mailbox did not retain {volts:0.000} V (read back {after.TargetVolts:0.000} V)");
            }
            catch
            {
                try { mb.WriteDomain(domain, mailboxBefore); } catch { }
                try { CoreVoltage.Restore(before); } catch { }
                throw;
            }
        }
        void ClearOverride(int domain)
        {
            if (domain != OcMailbox.DOMAIN_CORE || CoreVoltage == null)
            {
                mb!.SetOverride(domain, 0);
                return;
            }

            var before = CoreVoltage.ReadState();
            var mailboxBefore = mb!.ReadDomain(domain);
            CoreVoltage.DisableOverride();
            try
            {
                mb.SetOverride(domain, 0);
                if (mb.ReadDomain(domain).OverrideMode)
                    throw new IOException("Intel mailbox remained in override mode");
            }
            catch
            {
                try { mb.WriteDomain(domain, mailboxBefore); } catch { }
                try { CoreVoltage.Restore(before); } catch { }
                throw;
            }
        }
        void AddDomain(string idPrefix, string name, int domain, double minV, double maxV, bool available)
        {
            bool rowAvailable = available && (domain != OcMailbox.DOMAIN_CORE || !msiBoard || CoreVoltage != null);
            Settings.Add(new Setting
            {
                Id = idPrefix + "_v", Name = name + " Voltage", Group = SettingGroup.Voltages, Unit = "V", Min = minV, Max = maxV, Decimals = 3,
                Read = () =>
                {
                    if (!mbOk || !rowAvailable) return null;
                    if (domain == OcMailbox.DOMAIN_CORE && CoreVoltage != null)
                        return CoreVoltage.ReadTargetVolts();
                    var s = mb!.ReadDomain(domain);
                    // MSI Dragon Power shows the programmed target even when firmware leaves the
                    // mode bit adaptive. In particular, the E-core L2 domain reports 1.050 V in
                    // target bits 19:8 while bit 20 is clear; hiding it as Auto loses a real value.
                    if (s.TargetVolts > 0) return s.TargetVolts;

                    // This editor represents the programmed target, never live VID or a shared rail.
                    // Showing telemetry here makes Apply skip a real requested override.
                    return null;
                },
                Write = mbOk && rowAvailable ? v => WriteOverride(domain, v) : null,
                RestoreDefault = mbOk && rowAvailable ? () => ClearOverride(domain) : null,
                Note = VoltageAccessRestriction ?? (domain == OcMailbox.DOMAIN_CORE && CoreVoltage != null
                    ? "CPU-core override written to both the MSI board regulator and Intel OC mailbox, in the same order as MSI Dragon Power. Both targets are read back; a failure rolls the regulator back. Enter 0 to return to adaptive mode."
                    : domain == OcMailbox.DOMAIN_RING
                    ? "Programmed ring override through Intel OC mailbox domain 2. Auto means no explicit target. Live VID is shown separately and is not measured Vcore."
                    : $"Programmed voltage target for domain {domain} through the Intel OC mailbox. Entering a value selects override mode; the board's load-line still affects the measured rail. Enter 0 to return to adaptive mode."),
                Available = mbOk && rowAvailable
            });
            Settings.Add(new Setting
            {
                Id = idPrefix + "_off", Name = name + " Voltage Offset", Group = SettingGroup.Voltages, Unit = "mV", Min = -500, Max = 500, Decimals = 0,
                Read = () => (!mbOk || !available) ? null : Math.Round(mb!.ReadDomain(domain).OffsetVolts * 1000),
                Write = mbOk && available ? v => mb!.SetOffset(domain, v / 1000.0) : null,
                Note = VoltageAccessRestriction ?? $"Adaptive offset for FIVR domain {domain} (mailbox 0x150). Negative values undervolt.",
                Available = mbOk && available
            });
        }
        bool DomainReadable(int d) { try { mb!.ReadDomain(d); return true; } catch { return false; } }
        AddDomain("core", "CPU Core", OcMailbox.DOMAIN_CORE, 0.600, 1.720, mbOk && DomainReadable(OcMailbox.DOMAIN_CORE));
        if (AsusControl is { BaselineSvid: not null } asus)
        {
            int index = Settings.FindIndex(s => s.Id == "core_v");
            Settings[index] = new Setting
            {
                Id = "core_v", Name = "CPU Core Voltage target", Group = SettingGroup.Voltages, Unit = "V",
                Min = 0.6, Max = 1.7, Decimals = 3, Read = asus.ReadCoreTarget, Write = asus.SetCoreVoltage,
                RestoreDefault = () => asus.SetSvid(asus.BaselineSvid!), RequireReadBack = true,
                Note = "ASUS Global Core SVID Manual request, not a measured voltage. The applied target is read back from ASUS; measured Vcore is shown below it. The difference can vary with board regulation and load, even with BIOS settings on Auto. No automatic voltage compensation is applied. Enter 0 to restore the starting ASUS voltage state."
            };
            index = Settings.FindIndex(s => s.Id == "core_off");
            Settings[index] = new Setting
            {
                Id = "core_off", Name = "CPU Core Voltage Offset", Group = SettingGroup.Voltages, Unit = "mV",
                Min = -500, Max = 500, Decimals = 0, RequireReadBack = true,
                Read = () => (double)asus.ReadSvid().OffsetIndex - 999,
                Write = v =>
                {
                    var state = asus.ReadSvid();
                    if (state.Mode != 0) throw new IOException("ASUS core voltage is in Manual mode; restore CPU Core to Auto before changing its adaptive offset.");
                    asus.SetSvid(state with { OffsetIndex = checked((uint)Math.Round(v + 999)) });
                },
                Note = "ASUS adaptive SVID offset. Available for changes while CPU Core is in Auto/adaptive mode."
            };
        }
        // Not gated on the E-core count: the L2 rail for that cluster exists and is settable even
        // when the E-cores are switched off in the BIOS, which is exactly what the vendor tool shows.
        AddDomain("ecore", "CPU E-Core L2", OcMailbox.DOMAIN_ECORE, 0.600, 1.520, mbOk && DomainReadable(OcMailbox.DOMAIN_ECORE));
        AddDomain("ring", "Ring", OcMailbox.DOMAIN_RING, 0.600, 1.520, mbOk && DomainReadable(OcMailbox.DOMAIN_RING));
        AddDomain("sa", "SA", OcMailbox.DOMAIN_SA, 0.600, 1.520, mbOk && DomainReadable(OcMailbox.DOMAIN_SA));
        AddDomain("gt", "GT (iGPU)", OcMailbox.DOMAIN_GT, 0.600, 1.520, mbOk && DomainReadable(OcMailbox.DOMAIN_GT));

        if (AsusControl is { } asusRails)
        {
            void AddAsusRail(string prefix, string name, uint id, AsusBoardControl.SvidState? baseline)
            {
                if (baseline == null) return;
                int index = Settings.FindIndex(s => s.Id == prefix + "_v");
                Settings[index] = new Setting
                {
                    Id = prefix + "_v", Name = name + " Voltage" + (id == AsusBoardControl.RingId ? " target" : ""), Group = SettingGroup.Voltages, Unit = "V",
                    Min = 0.7, Max = 1.52, Decimals = 3, RequireReadBack = true,
                    Read = () => asusRails.ReadVoltageTarget(id), Write = v => asusRails.SetRailVoltage(id, v),
                    RestoreDefault = () => asusRails.SetSvid(baseline, id),
                    Note = "ASUS Manual voltage target through the board's dedicated control. Mode, target and offset are read back together; a failed request restores the previous state. Enter 0 to restore the startup state. " +
                        (id == AsusBoardControl.RingId
                            ? "Cache SVID request: ring and cores share the Vcore rail. There is no separate measured ring voltage; lowering this request alone may not lower Vcore."
                            : "The Log shows the separate ASUS rail reading.")
                };
            }
            AddAsusRail("sa", "SA", AsusBoardControl.SaId, asusRails.BaselineSa);
            AddAsusRail("ecore", "CPU E-Core L2", AsusBoardControl.L2Id, asusRails.BaselineL2);
            AddAsusRail("ring", "Ring", AsusBoardControl.RingId, asusRails.BaselineRing);
        }

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
    }

    private void AddBclkRow()
    {
        var control = BclkControl;
        Settings.Add(new Setting
        {
            Id = "bclk", Name = "Base Clock", Group = SettingGroup.Clocks,
            Min = control != null ? BclkController.MinBclkMHz : 10,
            Max = control != null ? BclkController.MaxBclkMHz : 655.25,
            Decimals = 2,
            Read = () => LastBclk,
            DefaultValue = control?.BaselineMHz,
            Write = control == null ? null : v =>
            {
                if (!control.SetBclk(v, Emit)) throw new InvalidOperationException(control.Status);
                MeasureBclk();
            },
            RestoreDefault = control == null ? null : () =>
            {
                if (!control.Restore()) throw new InvalidOperationException(control.Status);
                MeasureBclk();
            },
            Note = IsAmd
                ? "Measured base clock: the time-stamp counter, which Ryzen runs at base clock x the P0 multiplier, " +
                  "timed against the ACPI timer, which does not move with base clock. Read-only: no Windows write path " +
                  "to the board's clock generator has been verified on AM5."
                : AsusControl != null && control == null
                ? "Measured physical BCLK. The ASUS scalar target read back during testing, but the physical clock did not move. Live writes remain unavailable until a working clock-generator interface is verified."
                : control == null
                ? "Real core clocks counted against the ACPI timer, so it tracks a BCLK change made anywhere - including " +
                  "one made in the board vendor's tool while this is running. Read-only here: the write path goes through " +
                  "the board's own clock generator, which is only reachable where the board's EC exposes it."
                : "Set through the board's clock generator, over the EC's own I2C bus. Every step is measured against the " +
                  "ACPI timer, which does not move with the base clock, and anything that lands off target puts back the " +
                  "value found at start-up. There is no ceiling on what you can ask for - where a board gives up is not " +
                  "something this can know - but base clock scales the memory controller, the ring and the PCIe/DMI " +
                  "reference with it, so a little goes a long way and past a point the machine simply stops. " +
                  "Enter 0 to put back the start-up value.",
            Available = Bclk?.IsAvailable == true && BaseRatio > 0
        });
    }

    // ------------------------------------------------------------------ AMD rows
    // What was last written here, for the rows whose firmware has no read-back path.
    private readonly Dictionary<string, double> _lastWritten = new();
    private readonly Dictionary<int, int> _lastCo = new();
    private DateTime _tableReadAt;

    /// <summary>Refreshes the SMU power table at most every 300 ms; three rows read from it in a row.</summary>
    private bool TableFresh()
    {
        if (Amd?.Smu is not { TableReadable: true } smu) return false;
        if ((DateTime.UtcNow - _tableReadAt).TotalMilliseconds < 300 && smu.Table != null) return true;
        bool ok = smu.RefreshTable();
        if (ok) _tableReadAt = DateTime.UtcNow;
        return ok || smu.Table != null;
    }

    private void BuildAmdSettings()
    {
        var amd = Amd!;
        var smu = amd.Smu;
        var msgs = smu.Messages;
        bool live = SmuAvailable;
        bool pbo = live && PboAllowed;
        string pboNote = pbo ? "" : " PBO is reported as unavailable by the SMU: enable Precision Boost Overdrive in the BIOS, or the write will be rejected.";

        // ---------------- clocks ----------------
        Settings.Add(new Setting
        {
            Id = "fmax", Name = "FMax", Group = SettingGroup.Clocks, Unit = "MHz", Min = 1000, Max = 8000, Decimals = 0,
            Read = () => live ? smu.ReadBoostLimitMHz() : null,
            Write = live && msgs.RsmuSetBoostLimitAll != 0 ? v => smu.SetBoostLimitMHz((int)Math.Round(v)) : null,
            Note = "Ceiling for the all-core boost clock (the SMU's boost limit, what the BIOS calls Max CPU Boost Clock Override / FMax). Raising it only helps if the CPU has thermal, current and power headroom left. 0 restores the start-up value.",
            Available = live && msgs.HasBoostLimit
        });
        AddBclkRow();

        // ---------------- power / current limits ----------------
        double? Limit(string id, Func<float?> field)
        {
            if (TableFresh() && field() is float f && f > 0) return Math.Round(f);
            return _lastWritten.TryGetValue(id, out double v) ? v : null;
        }
        void WriteLimit(string id, string label, Action<double> write, Func<float?> field, double value)
        {
            write(value);
            _lastWritten[id] = value;
            bool? followed = smu.VerifyLayout(id, field, value);
            _tableReadAt = default;
            if (followed == true) Emit($"{label}: the power table confirms {value:0}.");
            else if (followed == false)
                Emit($"{label}: the SMU accepted {value:0} but the power table float this build expected for {label} did not follow. The limit is applied; this row now shows what is written here instead of reading it back. Please report your CPU and table version 0x{smu.TableVersion:X8}.");
        }
        string tableNote = smu.TableReadable && smu.Layout != null
            ? " Read back from the SMU power table through PawnIO."
            : " The SMU has no message that reports the limit in force and, without PawnIO, its power table cannot be read, so the row starts as Auto (whatever the BIOS set) and then shows what was written here.";
        Action? Stock(string id, double? stock, Action<double> write) => stock is double v ? () => { write(v); _lastWritten[id] = v; } : null;
        // What the CPU is drawing against each limit; only where the table itself can be read.
        Func<double?>? Now(Func<float?> field) => live && smu.TableReadable && smu.Layout != null
            ? () => TableFresh() && field() is float f && f >= 0 ? f : null
            : null;
        string StockNote(double? v, string unit) => v is double d ? $" Entering 0 writes the CPU's stock value ({d:0} {unit}); the BIOS value itself only comes back with a reboot." : "";
        Settings.Add(new Setting
        {
            Id = "ppt", Name = "PPT (Package Power Tracking)", Group = SettingGroup.Power, Unit = "W", Min = 5, Max = 2000, Decimals = 0,
            Read = () => live ? Limit("ppt", () => smu.PptLimit) : null,
            Live = Now(() => smu.PptValue),
            Write = live && msgs.HasPowerLimits ? v => WriteLimit("ppt", "PPT", smu.SetPpt, () => smu.PptLimit, v) : null,
            RestoreDefault = live ? Stock("ppt", StockLimits.ppt, smu.SetPpt) : null,
            Note = "Total socket power the boost algorithm may use, in watts (SMU message SetPPTLimit)." + tableNote + StockNote(StockLimits.ppt, "W") + pboNote,
            Available = live && msgs.HasPowerLimits
        });
        Settings.Add(new Setting
        {
            Id = "tdc", Name = "TDC (Thermal Design Current)", Group = SettingGroup.Power, Unit = "A", Min = 5, Max = 2000, Decimals = 0,
            Read = () => live ? Limit("tdc", () => smu.TdcLimit) : null,
            Live = Now(() => smu.TdcValue),
            Write = live && msgs.HasCurrentLimits ? v => WriteLimit("tdc", "TDC", smu.SetTdc, () => smu.TdcLimit, v) : null,
            RestoreDefault = live ? Stock("tdc", StockLimits.tdc, smu.SetTdc) : null,
            Note = "Sustained current the VRM may deliver on the core rail, in amperes, thermally limited (SetTDCVDDLimit)." + tableNote + StockNote(StockLimits.tdc, "A") + pboNote,
            Available = live && msgs.HasCurrentLimits
        });
        Settings.Add(new Setting
        {
            Id = "edc", Name = "EDC (Electrical Design Current)", Group = SettingGroup.Power, Unit = "A", Min = 5, Max = 2000, Decimals = 0,
            Read = () => live ? Limit("edc", () => smu.EdcLimit) : null,
            Live = Now(() => smu.EdcValue),
            Write = live && msgs.HasCurrentLimits ? v => WriteLimit("edc", "EDC", smu.SetEdc, () => smu.EdcLimit, v) : null,
            RestoreDefault = live ? Stock("edc", StockLimits.edc, smu.SetEdc) : null,
            Note = "Peak current the VRM may deliver on the core rail, in amperes (SetEDCVDDLimit)." + tableNote + StockNote(StockLimits.edc, "A") + pboNote,
            Available = live && msgs.HasCurrentLimits
        });
        Settings.Add(new Setting
        {
            Id = "tctl", Name = "Thermal Limit (Tctl max)", Group = SettingGroup.Power, Unit = "C", Min = 50, Max = 115, Decimals = 0,
            Read = () => live ? Limit("tctl", () => smu.ThmLimit) : null,
            Live = Now(() => smu.ThmValue),
            Write = live && msgs.RsmuSetTctlMax != 0 ? v => WriteLimit("tctl", "Tctl max", smu.SetTctlMax, () => smu.ThmLimit, v) : null,
            Note = "Temperature the boost algorithm holds the CPU to, in degrees C (SetTctlMax). Lower it to trade a little clock for a quieter, cooler CPU." + tableNote,
            Available = live && msgs.RsmuSetTctlMax != 0
        });

        // ---------------- PBO ----------------
        Settings.Add(new Setting
        {
            Id = "scalar", Name = "PBO Scalar", Group = SettingGroup.Pbo, Unit = "x", Min = 1, Max = 10, Decimals = 0,
            Read = () => live ? smu.ReadScalar() : null,
            Write = live && msgs.HasScalar ? v => smu.SetScalar(v) : null,
            Note = "Precision Boost Overdrive scalar, 1x to 10x: how far past the silicon's fused voltage/reliability envelope the boost algorithm may sustain. 1 is stock." + (msgs.RsmuGetScalar != 0 ? " Read back from the SMU; a read-back of 0 means the SMU is in manual overclock mode." : "") + pboNote,
            Available = live && msgs.HasScalar
        });
        int range = msgs.CoRange;
        Settings.Add(new Setting
        {
            Id = "co_all", Name = "Curve Optimizer (all cores)", Group = SettingGroup.Pbo, Unit = "", Min = -range, Max = range, Decimals = 0,
            Read = () =>
            {
                if (!live || !msgs.HasCurveOptimizerReadback) return _lastWritten.TryGetValue("co_all", out double v) ? v : null;
                var margins = amd.Cores.Select(ReadCurveOptimizer).ToList();
                if (margins.Any(m => m == null)) return null;
                return margins.Distinct().Count() == 1 ? margins[0] : null;
            },
            Write = live && msgs.HasCurveOptimizer ? v =>
            {
                int m = (int)Math.Round(v);
                smu.SetCurveOptimizerAll(m);
                _lastWritten["co_all"] = m;
                foreach (var c in amd.Cores) _lastCo[c.Index] = m;
            } : null,
            RestoreDefault = live && msgs.HasCurveOptimizer ? () =>
            {
                smu.SetCurveOptimizerAll(0);
                _lastWritten["co_all"] = 0;
                foreach (var c in amd.Cores) _lastCo[c.Index] = 0;
            } : null,
            Note = $"One Curve Optimizer offset for every core, in counts ({-range} to +{range}; one count is roughly 3 to 5 mV). Negative undervolts. " +
                   "Auto means the cores currently differ: open the Curve Optimizer window for the per-core values. Entering 0 sets every core to 0." + pboNote,
            Available = live && msgs.HasCurveOptimizer
        });
    }

    /// <summary>Per-core Curve Optimizer margin from the SMU, or what was last written here when the firmware cannot report it.</summary>
    public int? ReadCurveOptimizer(AmdCore core)
    {
        if (Amd == null || !SmuAvailable) return null;
        int? m = Amd.Smu.Messages.HasCurveOptimizerReadback ? Amd.Smu.ReadCurveOptimizer(core.Ccd, core.CoreInCcd) : null;
        if (m is int v) return v;
        return _lastCo.TryGetValue(core.Index, out int last) ? last : null;
    }

    public void ApplyCurveOptimizer(AmdCore core, int margin)
    {
        if (Amd == null || !SmuAvailable) throw new InvalidOperationException("SMU not available.");
        Amd.Smu.SetCurveOptimizer(core.Ccd, core.CoreInCcd, margin);
        _lastCo[core.Index] = margin;
        int? rb = Amd.Smu.Messages.HasCurveOptimizerReadback ? Amd.Smu.ReadCurveOptimizer(core.Ccd, core.CoreInCcd) : null;
        Emit($"Curve Optimizer {core.Label} ({core.Location}): set to {margin}" + (rb is int r ? $" (SMU reports {r})" : "") + ".");
        if (rb is int r2 && r2 != margin) throw new IOException($"the SMU accepted {margin} but reports {r2}.");
    }

    // ------------------------------------------------------------------ shared rows
    private void BuildBoardRails()
    {
        // These have no CPU-side register at all: the motherboard's regulators produce them, and
        // the only way to set them is that board's own VRM protocol. Shown live, read-only, rather
        // than offered as a field that would silently do nothing.
        if (SuperIo == null) return;
        var sio = SuperIo;
        void AddBoardRail(string id, string name, int rail, string note)
        {
            if (!sio.NamedRails.Any(r => r.index == rail)) return;
            if (sio.ReadVoltage(rail) is not double v || v < 0.05) return;
            Settings.Add(new Setting
            {
                Id = id, Name = name, Group = SettingGroup.Board, Unit = "V", Min = 0, Max = 5, Decimals = 3,
                Read = () => sio.ReadVoltage(rail), Write = null, Note = note, Available = true
            });
        }
        AddVdd2Row(sio);
        AddBoardRail("cpu_aux", "CPU AUX Voltage", Hardware.SuperIo.RailAux,
            "CPU AUX rail, measured at the board. It is produced by the motherboard VRM, like CPU VDD2, and most likely " +
            "sits on the same EC I2C bus - but which device and register carry it has not been established, and this " +
            "does not write a regulator it has not verified. Set it in the BIOS.");
    }

    /// <summary>
    /// CPU VDD2. Measured at the Super I/O either way; settable as well where the regulator on the
    /// EC's I2C bus answers, which is what the vendor tool drives.
    /// </summary>
    private void AddVdd2Row(SuperIo sio)
    {
        // MSI EC channel numbers are not labels for ASUS banked-Nuvoton inputs.
        if (!sio.NamedRails.Any(r => r.index == Hardware.SuperIo.RailVdd2)) return;
        if (sio.ReadVoltage(Hardware.SuperIo.RailVdd2) is not double v || v < 0.05) return;
        var rail = Vdd2;
        Settings.Add(new Setting
        {
            Id = "cpu_vdd2", Name = "CPU VDD2 Voltage", Group = SettingGroup.Board, Unit = "V",
            Min = rail != null ? Vdd2Rail.MinVolts : 0,
            Max = rail != null ? Vdd2Rail.MaxVolts : 5,
            Decimals = 3,
            Read = () => sio.ReadVoltage(Hardware.SuperIo.RailVdd2),
            DefaultValue = rail?.BaselineVolts,
            Write = rail == null ? null : t =>
            {
                if (!rail.SetVolts(t, Emit)) throw new InvalidOperationException(rail.Status);
            },
            RestoreDefault = rail == null ? null : () =>
            {
                if (!rail.Restore()) throw new InvalidOperationException(rail.Status);
            },
            Note = rail == null
                ? "CPU VDD2 (the memory controller's supply), measured at the board through the Super I/O."
                : "CPU VDD2, the memory controller's supply. Set on the board's regulator over the EC's I2C bus, one " +
                  "step at a time, with the rail measured after each step; anything that lands off target puts back " +
                  "the value found at start-up. The reading is the Super I/O's measurement of the rail, so it sits a " +
                  "little below what the regulator was asked for. Enter 0 to put back the start-up value.",
            Available = true
        });
    }

    private void BuildDimms()
    {
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
                Note = $"VDDQ set-point (PMIC SWC rail), register step {(dimm.VddqVerified ? dimm.VddqStepMv + " mV" : "unknown")}." + (dimm.VddqVerified ? "" : unverified), Available = dimm.HasPmic
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
        catch (Exception ex) { s.Current = null; Emit($"{s.Name}: read failed - {ex.Message}"); }
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
        if (SuperIo == null && AsusControl == null) return null;
        try
        {
            var vals = new List<double>(samples);
            for (int i = 0; i < samples; i++)
            {
                if ((AsusControl?.ReadVcore() ?? SuperIo?.ReadVcore()) is double v && v is > 0.4 and < 2.5) vals.Add(v);
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

    /// <summary>Applies one value. Zero restores startup defaults.</summary>
    public bool Apply(Setting s, double value)
    {
        s.LastError = null;
        s.LastResult = null;
        bool Fail(string message) { s.LastError = message; Emit($"{s.Name}: {message}"); return false; }
        if (!s.Available || s.Write == null) return Fail("unavailable or read-only.");
        if (!double.IsFinite(value)) return Fail("enter a finite number.");
        if (s.Id is "cpu_ratio" or "ecore_ratio" or "ring_ratio" && value != Math.Truncate(value))
            return Fail("enter a whole-number multiplier.");
        // A detected Super I/O does not necessarily have a valid Vcore channel. Do not claim a
        // rail check unless an actual plausible sample was captured. The MSI regulator path also
        // performs its own target-register readback.
        var railBefore = IsCoreVoltageRow(s) ? SampleVcore() : null;
        bool verify = railBefore != null;
        double? vidBefore = verify ? SampleVid() : null;
        double? settingBefore = s.Current;
        try
        {
            if (value == 0)
            {
                if (s.RestoreDefault != null) s.RestoreDefault();
                else if (s.DefaultValue is double d) s.Write(d);
                else return Fail("no default captured; nothing changed.");
                Refresh(s);
                if (s.RequireReadBack && s.DefaultValue is double expected && s.Current != expected)
                    return Fail($"reset could not be verified (expected {s.Format(expected)}, read {s.CurrentText}).");
                s.LastResult = "restored";
                if (IsCoreVoltageRow(s)) CoreVoltageIgnored = false;
                Emit($"{s.Name}: restored default ({s.CurrentText}{(s.Unit == "" ? "" : " " + s.Unit)}).");
                return true;
            }
            if (value < s.Min || value > s.Max)
            {
                return Fail($"{s.Format(value)} is outside {s.RangeText}.");
            }
            s.Write(value);
            Refresh(s);
            if (s.RequireReadBack && (s.Current is not double actual || Math.Abs(actual - value) > 0.5 * Math.Pow(10, -s.Decimals)))
                return Fail($"request was not retained (requested {s.Format(value)}, read back {s.CurrentText}).");
            Emit($"{s.Name}: set to {s.Format(value)}{(s.Unit == "" ? "" : " " + s.Unit)} (now {s.CurrentText}).");
            if (s.Id == "core_v" && CoreVoltage != null)
            {
                CoreVoltageIgnored = false;
                Emit($"{s.Name}: MSI regulator and Intel mailbox both read back {s.CurrentText} V.");
            }
            if (s.Id == "core_v" && AsusControl is { } asusControl)
            {
                CoreVoltageIgnored = false;
                Emit($"ASUS Manual SVID target {s.CurrentText} V retained; ASUS measured Vcore {asusControl.ReadVcore():0.000} V. Measured rail and requested target are different quantities; load-line behavior affects the rail.");
            }
            else if (s.Id is "sa_v" or "ecore_v" && AsusControl is { } asusRail)
            {
                uint id = s.Id == "sa_v" ? AsusBoardControl.SaId : AsusBoardControl.L2Id;
                Emit($"ASUS {s.Name} target {s.CurrentText} V retained; board sensor {asusRail.ReadRailVoltage(id):0.000} V.");
            }
            else if (s.Id == "ring_v" && AsusControl is { } asusRing)
            {
                Emit($"ASUS Cache SVID target {s.CurrentText} V retained. Shared Vcore sensor {asusRing.ReadVcore():0.000} V; this is not a separate ring-voltage measurement.");
            }
            else if (verify && VerifyAgainstRail(s, settingBefore, vidBefore, railBefore) == false)
                return Fail("request read back; Vcore response could not be verified (see Log).");
            s.LastResult = Cpu != null && (s.RequireReadBack || s.Group == SettingGroup.Voltages)
                ? "read back" : "applied";
            if (Cpu != null && s.Group == SettingGroup.Voltages && !verify && !(AsusControl != null && s.Id is "sa_v" or "ecore_v" or "ring_v"))
                Emit($"{s.Name}: register readback verified; actual rail voltage is unverified on this board.");
            return true;
        }
        catch (Exception ex)
        {
            Refresh(s);
            return Fail(ex.Message);
        }
    }

    /// <summary>
    /// The CPU accepting a voltage request does not prove the board acted on it. Compare the VID
    /// the CPU now asks for against the rail the Super I/O measures, and say so when they disagree.
    /// </summary>
    private bool? VerifyAgainstRail(Setting s, double? settingBefore, double? vidBefore, (double mean, double spread)? railBefore)
    {
        Thread.Sleep(300);
        double? vidAfter = SampleVid();
        var railAfter = SampleVcore();
        if (vidBefore is not double v0 || vidAfter is not double v1 || railBefore is not { } r0 || railAfter is not { } r1) return null;
        double dVid = (v1 - v0) * 1000, dRail = (r1.mean - r0.mean) * 1000;
        // For a fixed core override, compare the rail with the regulator target that was actually
        // changed. VID can move even during a no-op reconciliation because the mailbox and board
        // had previously been left out of sync. Offset rows still use the VID delta.
        double expectedMv = s.Id == "core_v" && settingBefore is double oldTarget && s.Current is double newTarget
            ? (newTarget - oldTarget) * 1000
            : dVid;
        if (Math.Abs(expectedMv) < 5) return true; // target did not move: register readback is the verification

        // A real change must be in the same direction as the request, clear of the rail's own
        // noise band, and a decent fraction of what was asked for. Magnitude alone is not enough:
        // idle Vcore wanders by tens of millivolts on its own.
        double noise = Math.Max(Math.Max(r0.spread, r1.spread) * 1000, 12);
        bool sameDirection = Math.Sign(dRail) == Math.Sign(expectedMv);
        bool followed = sameDirection && Math.Abs(dRail) > noise && Math.Abs(dRail) >= Math.Abs(expectedMv) * 0.35;

        if (followed)
        {
            Emit($"{s.Name}: verified at the rail - target {expectedMv:+0;-0} mV, VID {dVid:+0;-0} mV, measured Vcore {dRail:+0;-0} mV (now {r1.mean:0.000} V).");
            CoreVoltageIgnored = false;
            return true;
        }
        CoreVoltageIgnored = true;
        Emit($"{s.Name}: rail response unverified. The requested target moved {expectedMv:+0;-0} mV (VID {dVid:+0;-0} mV), and measured Vcore moved {dRail:+0;-0} mV " +
             $"(now {r1.mean:0.000} V, noise threshold {noise:0} mV). Load changes and monitor resolution can obscure the response; these samples do not establish the cause. " +
             "The programmed target remains changed. Check measured Vcore under a consistent load or restore the starting setting.");
        return false;
    }

    /// <summary>Set when an apply was measured not to reach the rail.</summary>
    public bool CoreVoltageIgnored { get; private set; }

    public int RestoreAllDefaults()
    {
        int failures = 0;
        foreach (var s in Settings.Where(s => s.Available && !s.ReadOnly))
        {
            bool changed = s.Current != s.DefaultValue;
            if (changed && !Apply(s, 0)) failures++;
        }
        return failures;
    }

    // ---------------------------------------------------------------- live
    public LiveStatus ReadLive()
    {
        var st = new LiveStatus();
        st.BclkMHz = LastBclk;
        try { st.VcoreVrm = AsusControl?.ReadVcore() ?? SuperIo?.ReadVcore(); } catch { }
        if (Cpu != null)
        {
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
            try { st.ECoreRatio = Cpu.ReadMaxCurrentERatio(); } catch { }
            try { st.PackageWatts = PowerFromEnergy(Cpu.ReadPackageEnergyJoules()); } catch { }
        }
        else if (Amd != null)
        {
            try { st.PackageTempC = Amd.ReadTemperature() is double t ? (int)Math.Round(t) : null; } catch { }
            try
            {
                double mhz = Amd.ReadMaxCoreMHz() * ((LastBclk ?? 100.0) / 100.0);
                st.CoreMHz = mhz; st.CoreRatio = (int)Math.Round(mhz / 100.0);
            }
            catch { }
            try { st.CoreVid = Amd.ReadCoreVid(); } catch { }
            try { st.PackageWatts = PowerFromEnergy(Amd.ReadPackageEnergyJoules()); } catch { }
        }
        return st;
    }

    /// <summary>Package power from two RAPL energy counter samples; the first call only primes the counter.</summary>
    private double? PowerFromEnergy(double joules)
    {
        var now = DateTime.UtcNow;
        double? watts = null;
        if (_lastEnergyAt != default)
        {
            double dt = (now - _lastEnergyAt).TotalSeconds;
            double de = joules - _lastEnergyJ;
            if (de < 0) de += 4294967296.0 * (1.0 / 65536); // 32-bit wrap at the common 1/65536 J unit
            if (dt > 0.2) watts = de / dt;
        }
        _lastEnergyJ = joules; _lastEnergyAt = now;
        return watts;
    }

    /// <summary>
    /// Blocking measurement; call from a background thread.
    ///
    /// Counts real core clocks (CPU_CLK_UNHALTED.CORE) against the ACPI timer. The older
    /// TSC-against-ACPI-timer method is only a fallback because it cannot see a BCLK change made
    /// after boot: the TSC runs from a fixed crystal here, so it keeps reporting the boot-time
    /// value however far BCLK is moved. Comparing against a vendor tool's live readback on a
    /// Z790MPOWER at 101.00 MHz: cycle counting gives 101.02, the TSC method still says 99.84.
    /// </summary>
    public double? MeasureBclk()
    {
        if (Bclk is not { IsAvailable: true } || BaseRatio == 0) return null;
        try
        {
            // Ryzen's TSC ticks at base clock x the P0 multiplier. The cycle-counter method below
            // reads Intel-only MSRs, so it is not tried there.
            if (IsAmd) { LastBclk = Bclk.MeasureBclkMHz(BaseRatio); return LastBclk; }
            int core = Cpu?.FirstPThread ?? 0;
            LastBclk = Bclk.MeasureBclkFromCycles(core) ?? Bclk.MeasureBclkMHz(BaseRatio);
            return LastBclk;
        }
        catch (Exception ex) { Emit("BCLK measure failed: " + ex.Message); return null; }
    }

    public void Dispose()
    {
        AsusControl?.Dispose();
        CoreVoltage?.Dispose();
        SuperIo?.Dispose();
        Bclk?.Dispose();
        Smbus?.Dispose();
        Amd?.Smu.Dispose();
        Driver?.Dispose();
    }
}
