using System.Globalization;
using RochPower.Hardware;

namespace RochPower.Core;

/// <summary>A presentation of cached ADC observations, independent of staged voltage targets.</summary>
public sealed record MemoryVoltageMeasurementText(string Text, string Tooltip, bool HasCompleteSamples, bool IsMixed);

/// <summary>
/// Formats immutable PMIC ADC snapshots only. No delegate, transport, target, default or draft
/// value supplies a measurement, and no fresh hardware sampling takes place here.
/// </summary>
public static class MemoryVoltageMeasurements
{
    public static MemoryVoltageMeasurementText ForSetting(Setting setting, PmicVoltageMeasurement? measurement, DateTimeOffset? nowUtc = null)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        string detail = Detail(setting, measurement, now);
        return Valid(measurement)
            ? new($"Last measured {Volts(measurement!.LastSuccessfulVolts!.Value)} V ({Age(measurement.SampledAtUtc!.Value, now)})", detail, true, false)
            : new("Last measured: Not read", detail, false, false);
    }

    public static MemoryVoltageMeasurementText ForGroup(MemoryVoltageGroup group,
        IReadOnlyDictionary<string, PmicVoltageMeasurement> measurements, DateTimeOffset? nowUtc = null)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        var valid = new List<PmicVoltageMeasurement>();
        var details = new List<string>();
        foreach (var member in group.Members)
        {
            measurements.TryGetValue(member.Id, out var sample);
            details.Add(Detail(member, sample, now));
            if (Valid(sample)) valid.Add(sample!);
        }
        string tooltip = string.Join(Environment.NewLine, details);
        if (group.Members.Count == 0 || valid.Count != group.Members.Count)
            return new($"Last measured: Not read ({valid.Count}/{group.Members.Count} DIMMs)", tooltip, false, false);

        double minimum = valid.Min(m => m.LastSuccessfulVolts!.Value);
        double maximum = valid.Max(m => m.LastSuccessfulVolts!.Value);
        // ADC observations retain their actual values; the programmed PMIC target grid and
        // allowed editing range do not change the physical sensor observation.
        bool mixed = Math.Abs(maximum - minimum) >= 0.0000001;
        string value = mixed ? Volts(minimum) + "–" + Volts(maximum) : Volts(minimum);
        var oldest = valid.Min(m => m.SampledAtUtc!.Value);
        bool sameTime = valid.All(m => m.SampledAtUtc == valid[0].SampledAtUtc);
        string age = (sameTime ? "" : "oldest ") + Age(oldest, now);
        return new($"Last measured {value} V ({age})", tooltip, true, mixed);
    }

    private static bool Valid(PmicVoltageMeasurement? measurement) => measurement?.LatestAttemptSucceeded == true &&
        measurement.LastSuccessfulVolts is double value && double.IsFinite(value) && value >= 0;

    private static string Detail(Setting setting, PmicVoltageMeasurement? measurement, DateTimeOffset now)
    {
        if (measurement == null) return setting.Name + ": Not read; no cached PMIC ADC sample.";
        string source = string.IsNullOrWhiteSpace(measurement.Source) ? "PMIC ADC" : measurement.Source;
        if (Valid(measurement))
            return $"{setting.Name}: Last measured {Volts(measurement.LastSuccessfulVolts!.Value)} V; {source}; " +
                $"sampled {Time(measurement.SampledAtUtc!.Value)} ({Age(measurement.SampledAtUtc.Value, now)}).";

        string reason = measurement.LastError ?? "No valid nonnegative PMIC ADC sample and timestamp are available.";
        string detail = $"{setting.Name}: Not read; {source}; {reason}";
        if (measurement.LastAttemptAtUtc is { } attempted) detail += $" Latest attempt {Time(attempted)}.";
        if (measurement.LastSuccessfulVolts is double oldValue && double.IsFinite(oldValue) && oldValue >= 0 && measurement.SampledAtUtc is { } sampled)
            detail += $" Previous sample {Volts(oldValue)} V at {Time(sampled)} ({Age(sampled, now)}); latest attempt did not provide a usable reading.";
        return detail;
    }

    private static string Volts(double value) => value.ToString("F3", CultureInfo.InvariantCulture);
    private static string Time(DateTimeOffset time) => time.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
    private static string Age(DateTimeOffset sampled, DateTimeOffset now)
    {
        double seconds = (now - sampled).TotalSeconds;
        if (seconds < 0) return "sample time ahead";
        if (seconds < 60) return Math.Floor(seconds).ToString(CultureInfo.InvariantCulture) + "s";
        if (seconds < 3600) return Math.Floor(seconds / 60).ToString(CultureInfo.InvariantCulture) + "m";
        if (seconds < 86400) return Math.Floor(seconds / 3600).ToString(CultureInfo.InvariantCulture) + "h";
        return Math.Floor(seconds / 86400).ToString(CultureInfo.InvariantCulture) + "d";
    }
}
