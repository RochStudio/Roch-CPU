using RochPower.Core;

namespace RochPower.UI;

/// <summary>Fixed display fixtures for UI QA. No hardware objects, reads or writes are used.</summary>
internal static class UiPreviewSettings
{
    internal static IReadOnlyList<Setting> Create(bool verifiedCore = false, bool coreReadFailed = false) => new[]
    {
        Row("cpu_ratio", "CPU Ratio (P-Core)", SettingGroup.Clocks, 54, "", 2),
        Row("ring_ratio", "Ring Ratio", SettingGroup.Clocks, 50, "", 1),
        Row("bclk", "Base Clock", SettingGroup.Clocks, 100.02, "MHz", 2),
        Row("core_v", "CPU Core Voltage", SettingGroup.Voltages, verifiedCore && !coreReadFailed ? 1.280 : null, "V", 3, readOnly: !verifiedCore, available: verifiedCore),
        Row("core_off", "CPU Core Voltage Offset", SettingGroup.Voltages, 0, "mV", 0),
        Row("ecore_v", "CPU E-Core L2 Voltage", SettingGroup.Voltages, null, "V", 3),
        Row("ecore_off", "CPU E-Core L2 Voltage Offset", SettingGroup.Voltages, 0, "mV", 0),
        Row("ring_v", "Ring Voltage", SettingGroup.Voltages, null, "V", 3),
        Row("ring_off", "Ring Voltage Offset", SettingGroup.Voltages, 0, "mV", 0),
        Row("sa_v", "SA Voltage", SettingGroup.Voltages, 1.189, "V", 3),
        Row("sa_off", "SA Voltage Offset", SettingGroup.Voltages, 0, "mV", 0),
        Row("gt_v", "GT (iGPU) Voltage", SettingGroup.Voltages, null, "V", 3),
        Row("gt_off", "GT (iGPU) Voltage Offset", SettingGroup.Voltages, 0, "mV", 0),
        Row("pl1", "Package Power Limit 1 (PL1)", SettingGroup.Power, 4096, "W", 0),
        Row("pl2", "Package Power Limit 2 (PL2)", SettingGroup.Power, 4096, "W", 0),
        Row("cpu_vdd2", "CPU VDD2 Voltage", SettingGroup.Board, 1.380, "V", 3),
        Row("cpu_aux", "CPU AUX", SettingGroup.Board, 1.796, "V", 3, readOnly: true),
        Row("dimma1_vdd", "DRAM DIMMA1 Voltage", SettingGroup.Memory, 1.440, "V", 3),
        Row("dimma1_vddq", "DRAM DIMMA1 VDDQ Voltage", SettingGroup.Memory, 1.410, "V", 3),
        Row("dimma1_vpp", "DRAM DIMMA1 VPP Voltage", SettingGroup.Memory, 1.800, "V", 3),
        Row("dimmb1_vdd", "DRAM DIMMB1 Voltage", SettingGroup.Memory, 1.440, "V", 3),
        Row("dimmb1_vddq", "DRAM DIMMB1 VDDQ Voltage", SettingGroup.Memory, 1.410, "V", 3),
        Row("dimmb1_vpp", "DRAM DIMMB1 VPP Voltage", SettingGroup.Memory, 1.800, "V", 3),
    };

    private static Setting Row(string id, string name, SettingGroup group, double? current, string unit, int decimals, bool readOnly = false, bool available = true) => new()
    {
        Id = id, Name = name, Group = group, Current = current, Unit = unit, Decimals = decimals,
        Available = available,
        Min = group == SettingGroup.Memory ? id.EndsWith("_vpp") ? 1.500 : 0.800 : -500,
        Max = group == SettingGroup.Memory ? id.EndsWith("_vpp") ? 2.135 : id.EndsWith("_vddq") ? 1.500 : 1.800 : 5000,
        Note = "Fixed UI fixture; not a hardware reading or recommended setting.",
        Read = () => throw new InvalidOperationException("UI preview does not read hardware."),
        Write = readOnly ? null : _ => throw new InvalidOperationException("UI preview does not write hardware."),
        RestoreDefault = () => throw new InvalidOperationException("UI preview does not restore hardware."),
    };
}
