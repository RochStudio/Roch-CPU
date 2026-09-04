using System.Runtime.InteropServices;
using System.Text;

namespace RochPower.Hardware;

/// <summary>Board / BIOS identification from the raw SMBIOS table (no WMI needed).</summary>
public sealed class SmbiosInfo
{
    public string BiosVendor { get; private set; } = "";
    public string BiosVersion { get; private set; } = "";
    public string BiosDate { get; private set; } = "";
    public string SystemManufacturer { get; private set; } = "";
    public string SystemProduct { get; private set; } = "";
    public string BoardManufacturer { get; private set; } = "";
    public string BoardProduct { get; private set; } = "";
    public string BoardVersion { get; private set; } = "";

    public string MainboardModel =>
        string.IsNullOrWhiteSpace(BoardProduct) ? $"{SystemManufacturer} {SystemProduct}".Trim() : $"{BoardManufacturer} {BoardProduct}".Trim();

    public static SmbiosInfo Read()
    {
        var info = new SmbiosInfo();
        byte[]? raw = FirmwareTables.Get(FirmwareTables.RSMB, 0);
        if (raw == null || raw.Length < 8) return info;
        int length = BitConverter.ToInt32(raw, 4);
        int pos = 8, end = Math.Min(raw.Length, 8 + length);
        while (pos + 4 <= end)
        {
            byte type = raw[pos], len = raw[pos + 1];
            if (len < 4) break;
            int p = pos + len;
            var strings = new List<string>();
            while (p < end)
            {
                int s = p;
                while (p < end && raw[p] != 0) p++;
                if (p == s) { p++; break; } // double zero terminator (or no strings at all)
                strings.Add(Encoding.ASCII.GetString(raw, s, p - s));
                p++;
                if (p < end && raw[p] == 0) { p++; break; }
            }
            string Str(int idx) => idx >= 1 && idx <= strings.Count ? strings[idx - 1].Trim() : "";
            // Some firmware (AMI on MSI boards, for one) carries a second, empty copy of a
            // structure later in the table; keep the first populated record of each type.
            bool hasStrings = strings.Count > 0;
            switch (type)
            {
                case 0 when hasStrings && info.BiosVersion == "":
                    info.BiosVendor = Str(raw[pos + 4]);
                    info.BiosVersion = Str(raw[pos + 5]);
                    info.BiosDate = Str(raw[pos + 8]);
                    break;
                case 1 when hasStrings && info.SystemProduct == "":
                    info.SystemManufacturer = Str(raw[pos + 4]);
                    info.SystemProduct = Str(raw[pos + 5]);
                    break;
                case 2 when hasStrings && info.BoardProduct == "":
                    info.BoardManufacturer = Str(raw[pos + 4]);
                    info.BoardProduct = Str(raw[pos + 5]);
                    info.BoardVersion = Str(raw[pos + 6]);
                    break;
                case 127:
                    return info;
            }
            pos = p;
        }
        return info;
    }
}

/// <summary>Wrapper for GetSystemFirmwareTable.</summary>
internal static class FirmwareTables
{
    public const uint ACPI = 0x41435049; // 'ACPI'
    public const uint RSMB = 0x52534D42; // 'RSMB'

    public static uint Signature(string fourCc) => BitConverter.ToUInt32(Encoding.ASCII.GetBytes(fourCc), 0);

    public static byte[]? Get(uint provider, uint id)
    {
        uint size = Native.GetSystemFirmwareTable(provider, id, IntPtr.Zero, 0);
        if (size == 0) return null;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            uint got = Native.GetSystemFirmwareTable(provider, id, buf, size);
            if (got == 0) return null;
            var data = new byte[Math.Min(got, size)];
            Marshal.Copy(buf, data, 0, data.Length);
            return data;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
