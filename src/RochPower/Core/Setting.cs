using System.Globalization;

namespace RochPower.Core;

public enum SettingGroup { Clocks, Voltages, Power, Memory, Board }

/// <summary>One tunable row in the main window.</summary>
public sealed class Setting
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required SettingGroup Group { get; init; }
    public string Unit { get; init; } = "";
    public double Min { get; init; }
    public double Max { get; init; }
    public int Decimals { get; init; }
    /// <summary>Reads the current setting; null means "Auto"/not set.</summary>
    public Func<double?> Read { get; init; } = () => null;
    /// <summary>Applies a value. Null makes the row read-only.</summary>
    public Action<double>? Write { get; init; }
    /// <summary>Called when the user enters 0 (restore default). Null: write DefaultValue.</summary>
    public Action? RestoreDefault { get; init; }
    public string? Note { get; init; }
    public bool Available { get; set; } = true;

    public double? DefaultValue { get; set; }
    public double? Current { get; set; }

    public bool ReadOnly => Write == null;

    public string RangeText => Unit switch
    {
        "" => $"Min:{Min.ToString("F1", CultureInfo.InvariantCulture)}, Max:{Max.ToString("F1", CultureInfo.InvariantCulture)}",
        _ => $"Min:{Format(Min)} {Unit}, Max:{Format(Max)} {Unit}"
    };

    public string Format(double v) => v.ToString("F" + Decimals, CultureInfo.InvariantCulture);

    public string CurrentText => Current is double v ? Format(v) : "Auto";

    public bool TryParse(string text, out double value)
    {
        text = text.Trim().Replace(',', '.');
        if (text.Equals("auto", StringComparison.OrdinalIgnoreCase)) { value = 0; return true; }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
