namespace RochPower.Hardware;

/// <summary>
/// One DDR5 DIMM: its SPD5118 hub (0x50 + slot) and its on-module PMIC (0x48 + slot).
/// Voltages are set on the PMIC itself, so this works on any board whose PCH SMBus
/// reaches the DIMM slots. It does not depend on the motherboard VRM controller.
///
/// Register scales differ between PMICs (the VDD rail on overclocking kits uses a
/// 10 mV step instead of the JEDEC 5 mV step). Because a wrong scale on a write could
/// double a DIMM voltage, every rail is calibrated against the PMIC's own ADC at
/// start-up and writes are refused for rails whose scale could not be confirmed.
/// </summary>
public sealed class Ddr5Dimm
{
    public const byte SpdBase = 0x50;
    public const byte PmicBase = 0x48;

    // PMIC5000 (JESD301) registers
    public const byte R_SWA_PWR = 0x0C;     // 125 mW / LSB
    public const byte R_SWB_PWR = 0x0D;
    public const byte R_SWC_PWR = 0x0E;
    public const byte R_SWD_PWR = 0x0F;
    public const byte R_SWA_VOUT = 0x21;    // VDD  : bits 7:1, 800 mV base, 5 or 10 mV per step (calibrated)
    public const byte R_SWB_VOUT = 0x23;    // VDD second phase (0 = follows SWA)
    public const byte R_SWC_VOUT = 0x25;    // VDDQ : bits 7:1, 800 mV base, 5 mV per step
    public const byte R_SWD_VOUT = 0x27;    // VPP  : bits 7:1, 1500 mV base, 5 mV per step
    public const byte R_ADC_ENABLE = 0x30;  // bit 7 = enable, bits 6:3 = input select
    public const byte R_ADC_READ = 0x31;
    public const byte R_VENDOR_LSB = 0x3B;
    public const byte R_VENDOR_MSB = 0x3C;
    public const byte R_REVISION = 0x3D;

    // ADC input selections (verified on Raptor Lake / Z790 with a JEDEC-compliant PMIC)
    public const int ADC_SWA = 0, ADC_SWB = 1, ADC_SWC = 2, ADC_SWD = 3, ADC_VIN_BULK = 5;
    private const double AdcRailVoltsPerLsb = 0.015;   // 15 mV / LSB for SWA..SWD
    private const double CalibrationToleranceV = 0.060; // ADC accuracy + ripple

    public const double VddMinV = 0.800, VddMaxV = 1.800;
    public const double VddqMinV = 0.800, VddqMaxV = 1.435;
    public const double VppMinV = 1.500, VppMaxV = 2.135;

    private readonly SmbusI801 _bus;
    public int Slot { get; }
    public string SlotName { get; }
    public byte SpdAddress => (byte)(SpdBase + Slot);
    public byte PmicAddress => (byte)(PmicBase + Slot);
    public bool HasPmic { get; private set; }
    public string PmicVendor { get; private set; } = "?";

    /// <summary>mV per register step of the VDD (SWA) rail; 0 until calibrated.</summary>
    public int VddStepMv { get; private set; }
    public bool VddVerified => VddStepMv != 0;
    public bool VddqVerified { get; private set; }
    public bool VppVerified { get; private set; }
    public bool AdcWritable { get; private set; }
    public string CalibrationNote { get; private set; } = "not calibrated";

    public Ddr5Dimm(SmbusI801 bus, int slot)
    {
        _bus = bus; Slot = slot;
        SlotName = slot switch { 0 => "DIMMA1", 1 => "DIMMA2", 2 => "DIMMB1", 3 => "DIMMB2", _ => $"DIMM{slot}" };
    }

    /// <summary>Probe all four slots; a DIMM is present when its SPD5118 hub answers with device type 0x51.</summary>
    public static List<Ddr5Dimm> Probe(SmbusI801 bus)
    {
        var list = new List<Ddr5Dimm>();
        for (int s = 0; s < 4; s++)
        {
            var d = new Ddr5Dimm(bus, s);
            if (!bus.ReadByte(d.SpdAddress, 0x00, out byte mr0) || mr0 != 0x51) continue; // MR0 = 0x51 -> SPD5118 (DDR5)
            d.HasPmic = bus.ReadByte(d.PmicAddress, R_SWA_VOUT, out _);
            if (d.HasPmic && bus.ReadByte(d.PmicAddress, R_VENDOR_LSB, out byte vl) && bus.ReadByte(d.PmicAddress, R_VENDOR_MSB, out byte vh))
                d.PmicVendor = $"ID {vh:X2}{vl:X2}";
            if (d.HasPmic) d.Calibrate();
            list.Add(d);
        }
        return list;
    }

    // ---------------------------------------------------------------- raw access
    public bool ReadRegister(byte reg, out byte value) => _bus.ReadByte(PmicAddress, reg, out value);
    public bool ReadSpdRegister(byte reg, out byte value) => _bus.ReadByte(SpdAddress, reg, out value);

    /// <summary>
    /// Reads the PMIC ADC for one input selection, restoring the previous ADC configuration
    /// afterwards. Only the ADC mux is touched; regulator outputs are not affected.
    /// </summary>
    public byte? ReadAdc(int select)
    {
        if (!HasPmic || !_bus.ReadByte(PmicAddress, R_ADC_ENABLE, out byte saved)) return null;
        try
        {
            byte cfg = (byte)(0x80 | ((select & 0xF) << 3));
            if (!_bus.WriteByte(PmicAddress, R_ADC_ENABLE, cfg)) return null;
            Thread.Sleep(15);
            if (!_bus.ReadByte(PmicAddress, R_ADC_ENABLE, out byte back) || back != cfg) return null; // not writable (secure mode)
            return _bus.ReadByte(PmicAddress, R_ADC_READ, out byte v) ? v : null;
        }
        finally { _bus.WriteByte(PmicAddress, R_ADC_ENABLE, saved); }
    }

    public double? ReadAdcVolts(int select) => ReadAdc(select) is byte b ? b * AdcRailVoltsPerLsb : null;

    // ---------------------------------------------------------------- calibration
    /// <summary>Compares each rail's register decode with the ADC and records which scales are trustworthy.</summary>
    public void Calibrate()
    {
        VddStepMv = 0; VddqVerified = false; VppVerified = false; AdcWritable = false;
        if (!HasPmic) { CalibrationNote = "no PMIC"; return; }
        double? adcA = ReadAdcVolts(ADC_SWA), adcC = ReadAdcVolts(ADC_SWC), adcD = ReadAdcVolts(ADC_SWD);
        if (adcA is null || adcC is null || adcD is null)
        {
            CalibrationNote = "PMIC ADC not writable (secure mode) - voltage scales unverified, writes disabled";
            return;
        }
        AdcWritable = true;
        if (!ReadRegister(R_SWA_VOUT, out byte ra) || !ReadRegister(R_SWC_VOUT, out byte rc) || !ReadRegister(R_SWD_VOUT, out byte rd))
        {
            CalibrationNote = "PMIC register read failed";
            return;
        }
        double vdd5 = Decode(ra, 800, 5), vdd10 = Decode(ra, 800, 10);
        bool m5 = Math.Abs(vdd5 - adcA.Value) <= CalibrationToleranceV;
        bool m10 = Math.Abs(vdd10 - adcA.Value) <= CalibrationToleranceV;
        if (m5 && !m10) VddStepMv = 5;
        else if (m10 && !m5) VddStepMv = 10;
        VddqVerified = Math.Abs(Decode(rc, 800, 5) - adcC.Value) <= CalibrationToleranceV;
        VppVerified = Math.Abs(Decode(rd, 1500, 5) - adcD.Value) <= CalibrationToleranceV;
        CalibrationNote = $"ADC: VDD {adcA:0.000} V, VDDQ {adcC:0.000} V, VPP {adcD:0.000} V -> " +
                          $"VDD step {(VddVerified ? VddStepMv + " mV" : "unknown")}, VDDQ {(VddqVerified ? "ok" : "mismatch")}, VPP {(VppVerified ? "ok" : "mismatch")}";
    }

    private static double Decode(byte raw, int baseMv, int stepMv) => (baseMv + stepMv * (raw >> 1)) / 1000.0;
    private static byte Encode(double volts, int baseMv, int stepMv) =>
        (byte)(Math.Clamp((int)Math.Round((volts * 1000 - baseMv) / stepMv), 0, 127) << 1);

    private double? ReadRail(byte reg, int baseMv, int stepMv) =>
        HasPmic && stepMv > 0 && _bus.ReadByte(PmicAddress, reg, out byte raw) ? Decode(raw, baseMv, stepMv) : null;

    // ---------------------------------------------------------------- reads
    /// <summary>VDD set-point. Uses the calibrated step; falls back to the 5 mV JEDEC decode when unverified.</summary>
    public double? ReadVdd() => ReadRail(R_SWA_VOUT, 800, VddVerified ? VddStepMv : 5);
    public double? ReadVddq() => ReadRail(R_SWC_VOUT, 800, 5);
    public double? ReadVpp() => ReadRail(R_SWD_VOUT, 1500, 5);

    /// <summary>Power draw per rail in mW (SWA+SWB = VDD, SWC = VDDQ, SWD = VPP); null when not reported.</summary>
    public (double? vdd, double? vddq, double? vpp) ReadPowerMw()
    {
        if (!HasPmic) return (null, null, null);
        double? vdd = null, vddq = null, vpp = null;
        if (_bus.ReadByte(PmicAddress, R_SWA_PWR, out byte a))
        {
            vdd = a * 125.0;
            if (_bus.ReadByte(PmicAddress, R_SWB_PWR, out byte b)) vdd += b * 125.0;
        }
        if (_bus.ReadByte(PmicAddress, R_SWC_PWR, out byte c)) vddq = c * 125.0;
        if (_bus.ReadByte(PmicAddress, R_SWD_PWR, out byte d)) vpp = d * 125.0;
        return (vdd, vddq, vpp);
    }

    // ---------------------------------------------------------------- writes
    private void WriteRail(byte reg, byte value, string name, int adcSelect, double expectedVolts)
    {
        if (!HasPmic) throw new InvalidOperationException($"{SlotName}: no PMIC.");
        if (!_bus.WriteByte(PmicAddress, reg, value))
            throw new IOException($"{SlotName}: SMBus write to PMIC register 0x{reg:X2} failed.");
        Thread.Sleep(5);
        if (!_bus.ReadByte(PmicAddress, reg, out byte back) || (back & 0xFE) != (value & 0xFE))
            throw new IOException($"{SlotName}: PMIC rejected the new {name} setting (read back 0x{back:X2}). " +
                                  "The PMIC is probably in Secure Mode (vendor locked); the voltage can then only be changed by the BIOS.");
        Thread.Sleep(20);
        double? measured = ReadAdcVolts(adcSelect);
        if (measured is double m && Math.Abs(m - expectedVolts) > CalibrationToleranceV + 0.03)
            throw new IOException($"{SlotName}: {name} register accepted but the ADC measures {m:0.000} V instead of {expectedVolts:0.000} V. " +
                                  "Check the value with another monitoring tool before going further.");
    }

    public void WriteVdd(double volts)
    {
        if (!VddVerified) throw new InvalidOperationException($"{SlotName}: VDD scale not verified against the PMIC ADC; write refused for safety.");
        if (volts < VddMinV || volts > VddMaxV) throw new ArgumentOutOfRangeException(nameof(volts), $"VDD must be {VddMinV:0.000}-{VddMaxV:0.000} V.");
        byte v = Encode(volts, 800, VddStepMv);
        WriteRail(R_SWA_VOUT, v, "VDD", ADC_SWA, Decode(v, 800, VddStepMv));
        // Second VDD phase (SWB) mirrors SWA on dual-rail PMICs; 0 means it follows SWA and must be left alone.
        if (_bus.ReadByte(PmicAddress, R_SWB_VOUT, out byte swb) && swb != 0) _bus.WriteByte(PmicAddress, R_SWB_VOUT, v);
    }

    public void WriteVddq(double volts)
    {
        if (!VddqVerified) throw new InvalidOperationException($"{SlotName}: VDDQ scale not verified against the PMIC ADC; write refused for safety.");
        if (volts < VddqMinV || volts > VddqMaxV) throw new ArgumentOutOfRangeException(nameof(volts), $"VDDQ must be {VddqMinV:0.000}-{VddqMaxV:0.000} V.");
        byte v = Encode(volts, 800, 5);
        WriteRail(R_SWC_VOUT, v, "VDDQ", ADC_SWC, Decode(v, 800, 5));
    }

    public void WriteVpp(double volts)
    {
        if (!VppVerified) throw new InvalidOperationException($"{SlotName}: VPP scale not verified against the PMIC ADC; write refused for safety.");
        if (volts < VppMinV || volts > VppMaxV) throw new ArgumentOutOfRangeException(nameof(volts), $"VPP must be {VppMinV:0.000}-{VppMaxV:0.000} V.");
        byte v = Encode(volts, 1500, 5);
        WriteRail(R_SWD_VOUT, v, "VPP", ADC_SWD, Decode(v, 1500, 5));
    }
}
