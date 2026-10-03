namespace RochPower.Core;

/// <summary>A verified PMIC register grid, supplied by the existing capability detection.</summary>
public readonly record struct MemoryVoltageGrid(int BaseMillivolts, int StepMillivolts);

/// <summary>An honest shared-editor projection; constructing it never changes any target.</summary>
public sealed record MemoryVoltageDisplay(string Text, bool IsMixed, bool CanEdit, string? Reason);

/// <summary>The currently supported intersection for one rail across detected DIMMs.</summary>
public sealed record MemoryVoltageGroup(
    string Rail,
    IReadOnlyList<Setting> Members,
    IReadOnlyList<double> SupportedValues,
    bool CanSync,
    string? Reason)
{
    public double? Minimum => SupportedValues.Count == 0 ? null : SupportedValues[0];
    public double? Maximum => SupportedValues.Count == 0 ? null : SupportedValues[^1];
    public double? Step => SupportedValues.Count < 2 ? null : SupportedValues[1] - SupportedValues[0];
}

/// <summary>
/// Links staged text for the same voltage rail across DIMMs. This helper never calls a setting's
/// Read, Write or RestoreDefault delegate and never changes Current or DefaultValue. The caller
/// retains its existing explicit Apply workflow and hardware checks.
/// </summary>
public sealed class MemoryVoltageLink
{
    private const double Tolerance = 0.0000001;
    private readonly Setting[] _settings;
    private readonly IReadOnlyDictionary<string, MemoryVoltageGrid> _grids;

    /// <summary>Sync is the default. Changing mode does not align or overwrite staged values.</summary>
    public bool IsSynced { get; private set; } = true;

    public MemoryVoltageLink(IEnumerable<Setting> settings, IReadOnlyDictionary<string, MemoryVoltageGrid> grids)
    {
        _settings = settings.Where(s => TryGetRail(s, out _)).ToArray();
        _grids = new Dictionary<string, MemoryVoltageGrid>(grids, StringComparer.OrdinalIgnoreCase);
    }

    public void SetSynced(bool synced) => IsSynced = synced;

    /// <summary>
    /// Projects the existing same-rail drafts into one editor without selecting, staging or
    /// aligning a DIMM. Auto is only a display state; TryStage still requires Split for Auto/0.
    /// </summary>
    public MemoryVoltageDisplay GetDisplay(MemoryVoltageGroup group, IReadOnlyDictionary<Setting, string> drafts)
    {
        double? common = null;
        foreach (var member in group.Members)
        {
            string text = drafts.TryGetValue(member, out string? draft) ? draft : member.CurrentText;
            if (!member.TryParse(text, out double value) || common is double previous && Math.Abs(previous - value) >= Tolerance)
                return new("Mixed", true, group.CanSync, group.Reason);
            common = value;
        }
        if (common is not double target) return new("Mixed", true, group.CanSync, group.Reason);
        return new(target == 0 ? "Auto" : group.Members[0].Format(target), false, group.CanSync, group.Reason);
    }

    /// <summary>Recognizes DIMM rails only; CPU VDD2 and other voltage controls remain independent.</summary>
    public static bool TryGetRail(Setting setting, out string rail)
    {
        rail = "";
        if (setting.Group != SettingGroup.Memory || setting.Unit != "V") return false;
        int separator = setting.Id.LastIndexOf('_');
        if (separator <= 4) return false;
        string dimm = setting.Id[..separator];
        if (!dimm.StartsWith("dimm", StringComparison.OrdinalIgnoreCase) ||
            !dimm[4..].All(char.IsLetterOrDigit)) return false;
        string suffix = setting.Id[(separator + 1)..].ToUpperInvariant();
        if (suffix is not ("VDD" or "VDDQ" or "VPP")) return false;
        rail = suffix;
        return true;
    }

    public MemoryVoltageGroup? GetGroup(Setting source)
    {
        if (!_settings.Contains(source) || !TryGetRail(source, out string rail)) return null;
        var members = _settings.Where(s => TryGetRail(s, out string r) && r == rail).ToArray();
        if (members.Length < 2) return Blocked("At least two detected DIMMs are needed.");
        if (members.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != members.Length)
            return Blocked("Duplicate DIMM rail identifiers prevent safe linking.");
        foreach (var member in members)
        {
            if (!member.Available) return Blocked($"{member.Name} is unavailable; use Split DIMMs for independent edits.");
            if (member.ReadOnly) return Blocked($"{member.Name} is read-only; use Split DIMMs for independent edits.");
            if (!_grids.TryGetValue(member.Id, out var grid) || grid.StepMillivolts <= 0 || grid.BaseMillivolts <= 0)
                return Blocked($"{member.Name} has no verified PMIC step; use Split DIMMs for independent edits.");
            if (!double.IsFinite(member.Min) || !double.IsFinite(member.Max) || member.Min > member.Max)
                return Blocked($"{member.Name} has no valid voltage range.");
        }

        // There are only 128 possible PMIC register codes. Enumerating the first DIMM's grid
        // avoids rounding a request differently on two PMICs with different calibrated steps.
        var firstGrid = _grids[members[0].Id];
        var common = new List<double>();
        for (int code = 0; code <= 127; code++)
        {
            double value = ((long)firstGrid.BaseMillivolts + (long)code * firstGrid.StepMillivolts) / 1000.0;
            if (members.All(s => IsSupported(s, value))) common.Add(value);
        }
        if (common.Count == 0) return Blocked("The DIMMs have no common supported target for this rail.");
        return new MemoryVoltageGroup(rail, members, common.ToArray(), true, null);

        MemoryVoltageGroup Blocked(string reason) => new(rail, members, Array.Empty<double>(), false, reason);
    }

    /// <summary>
    /// Returns text updates without applying them. In Sync, every corresponding DIMM must support
    /// the exact target; no clamping, rounding, partial group updates or cross-rail linking occurs.
    /// </summary>
    public bool TryStage(Setting source, string text, out IReadOnlyDictionary<Setting, string> updates, out string? error)
    {
        updates = new Dictionary<Setting, string>();
        error = null;
        var group = GetGroup(source);
        if (group == null) { error = "This is not a detected DIMM voltage setting."; return false; }
        if (!source.Available || source.ReadOnly) { error = $"{source.Name} is not writable."; return false; }

        if (!IsSynced)
        {
            // Split retains the existing parser and Apply validation; text staging changes no hardware.
            updates = new Dictionary<Setting, string> { [source] = text };
            return true;
        }
        if (!group.CanSync) { error = group.Reason; return false; }
        if (!source.TryParse(text, out double value)) { error = $"Enter a valid {group.Rail} target."; return false; }
        if (value == 0)
        {
            error = "Use Split DIMMs to restore individual startup values; those values may differ.";
            return false;
        }
        if (!group.SupportedValues.Any(v => Math.Abs(v - value) < Tolerance))
        {
            error = $"{group.Rail} must use a common supported target from {group.Minimum:0.000} to {group.Maximum:0.000} V" +
                (group.Step is double step ? $" in {step * 1000:0.###} mV steps." : ".");
            return false;
        }
        updates = group.Members.ToDictionary(s => s, s => s.Format(value));
        return true;
    }

    /// <summary>
    /// Validates dirty Sync groups before the caller's explicit Apply. Untouched mixed readings
    /// remain intact, so changing mode alone does not prevent applying an unrelated control.
    /// </summary>
    public bool ValidateForApply(IReadOnlyDictionary<Setting, string> targets, out string? error)
    {
        error = null;
        if (!IsSynced) return true;
        foreach (var source in _settings.GroupBy(s => { TryGetRail(s, out string rail); return rail; }).Select(g => g.First()))
        {
            var group = GetGroup(source)!;
            if (!group.Members.Any(s => targets.TryGetValue(s, out string? text) && IsEdited(s, text))) continue;
            if (!group.CanSync) { error = group.Reason; return false; }
            double? common = null;
            foreach (var member in group.Members)
            {
                string text = targets.TryGetValue(member, out string? staged) ? staged : member.CurrentText;
                if (!TryStage(member, text, out _, out error)) return false;
                member.TryParse(text, out double value);
                if (common is double previous && Math.Abs(previous - value) >= Tolerance)
                {
                    error = $"{group.Rail} targets differ across DIMMs. Edit one target to synchronize this rail, or use Split DIMMs.";
                    return false;
                }
                common = value;
            }
        }
        return true;
    }

    private bool IsSupported(Setting setting, double value)
    {
        if (value < setting.Min - Tolerance || value > setting.Max + Tolerance) return false;
        var grid = _grids[setting.Id];
        double code = (value * 1000 - grid.BaseMillivolts) / grid.StepMillivolts;
        return code >= -Tolerance && code <= 127 + Tolerance && Math.Abs(code - Math.Round(code)) < Tolerance;
    }

    private static bool IsEdited(Setting setting, string text)
    {
        if (setting.LastError != null && setting.Available && !setting.ReadOnly) return true;
        return !setting.TryParse(text, out double target) || !setting.TryParse(setting.CurrentText, out double current) ||
            Math.Abs(target - current) >= Tolerance;
    }
}
