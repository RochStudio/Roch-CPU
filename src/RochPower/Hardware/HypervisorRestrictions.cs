using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;

namespace RochPower.Hardware;

internal sealed record RestrictedMsrAccess(uint Register, bool IsWrite, DateTime TimeUtc, string Driver);

/// <summary>Reads Windows' own restricted-MSR events. Never changes virtualization settings.</summary>
internal static class HypervisorRestrictions
{
    internal const string LogName = "Microsoft-Windows-Hyper-V-Hypervisor-Operational";

    public static IReadOnlyList<RestrictedMsrAccess> ReadCurrentBoot(out string status)
    {
        var found = new List<RestrictedMsrAccess>();
        DateTime boot = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        try
        {
            var query = new EventLogQuery(LogName, PathType.LogName, "*[System[EventID=12550]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            for (int i = 0; i < 256; i++)
            {
                using var record = reader.ReadEvent();
                if (record == null) break;
                if (record.TimeCreated?.ToUniversalTime() < boot) break;
                if (Parse(record.ToXml(), boot) is { } access) found.Add(access);
            }
            status = found.Count == 0 ? "No restricted-MSR events found for this boot; absence is not proof that access is allowed."
                : $"Windows recorded {found.Count} restricted-MSR access event(s) during this boot.";
        }
        catch (Exception ex) { status = "Windows restricted-MSR log could not be read: " + ex.Message; }
        return found;
    }

    internal static RestrictedMsrAccess? Parse(string xml, DateTime bootUtc)
    {
        try
        {
            var root = XElement.Parse(xml);
            XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
            var system = root.Element(ns + "System");
            if ((string?)system?.Element(ns + "EventID") != "12550"
                || (string?)system?.Element(ns + "Provider")?.Attribute("Name") != "Microsoft-Windows-Hyper-V-Hypervisor") return null;
            if (!DateTime.TryParse((string?)system?.Element(ns + "TimeCreated")?.Attribute("SystemTime"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime time) || time < bootUtc) return null;
            string? Field(string name) => root.Element(ns + "EventData")?.Elements(ns + "Data")
                .FirstOrDefault(e => (string?)e.Attribute("Name") == name)?.Value;
            string? raw = Field("Msr"), write = Field("IsWrite");
            if (raw == null || write is not ("0" or "1")) return null;
            bool hex = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (!uint.TryParse(hex ? raw[2..] : raw, hex ? NumberStyles.HexNumber : NumberStyles.Integer,
                CultureInfo.InvariantCulture, out uint register)) return null;
            return new RestrictedMsrAccess(register, write == "1", time, Field("ImageName") ?? "unknown driver");
        }
        catch (System.Xml.XmlException) { return null; }
    }

    public static string? VoltageBlockReason(IEnumerable<RestrictedMsrAccess> accesses) =>
        accesses.FirstOrDefault(a => a.Register == OcMailbox.MSR_OC_MAILBOX && a.IsWrite) is { } access
            ? $"Windows Hyper-V restricted voltage mailbox access (MSR 0x150, event 12550, {access.Driver}, {access.TimeUtc.ToLocalTime():HH:mm:ss}). Voltage controls are unavailable in this boot."
            : null;
}
