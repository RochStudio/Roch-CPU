using System.Runtime.InteropServices;

namespace RochPower.Hardware;

/// <summary>ASUS ATK frequency control. Uses the installed vendor COM service; no bundled driver.</summary>
public sealed class AsusBoardControl : IDisposable
{
    private readonly dynamic _vendor;
    public const uint BclkId = 0x03010011;
    public const uint VrmId = 0x03020027;
    public const uint CoreId = 0x030D0012;
    public const uint SaId = 0x030D0022;
    public const uint L2Id = 0x030D0026;
    public const uint RingId = 0x030D0028;
    public uint Baseline { get; }
    public double BaselineMHz => DecodeBclk(Baseline);
    public string VoltageDescription { get; }
    public record SvidState(uint OffsetIndex, uint TargetIndex, uint Mode);
    public SvidState? BaselineSvid { get; }
    public SvidState? BaselineSa { get; }
    public SvidState? BaselineL2 { get; }
    public SvidState? BaselineRing { get; }

    private AsusBoardControl(object vendor)
    {
        _vendor = vendor;
        uint initialized = _vendor.iAcpiInit();
        if ((initialized & 1) == 0) throw new IOException("ASUS ACPI initialization failed");
        dynamic items = _vendor.ItemsOfGroup(3u);
        bool supported = false;
        VoltageDescription = "Actual VRM voltage item unavailable";
        try
        {
            int count = checked((int)items.Count);
            if (count is < 0 or > 512) throw new IOException("Invalid ASUS item count");
            for (int i = 0; i < count; i++)
            {
                dynamic item = items.Item(i);
                try
                {
                    uint id = item.id;
                    if (id == BclkId)
                        supported = (string)item.name == "BCLK Frequency" && (uint)item.Minimum == 0xFF009C40u
                            && (uint)item.Increment == 10u && (uint)item.DefaultValue == 6000u
                            && (uint)item.Entries == 49801u;
                    if (id == VrmId)
                    {
                        VoltageDescription = $"{item.name}: minimum 0x{(uint)item.Minimum:X8}, step {item.Increment}, entries {item.Entries}";
                    }
                }
                finally { Marshal.FinalReleaseComObject(item); }
            }
        }
        finally { Marshal.FinalReleaseComObject(items); }
        if (!supported) throw new IOException("ASUS BCLK metadata does not match the validated encoding");
        Baseline = ReadRaw(BclkId);
        if (BaselineMHz is < 90 or > 110) throw new IOException("ASUS BCLK baseline is outside the supported range");
        try { BaselineSvid = ReadSvid(); }
        catch { /* An unsupported voltage descriptor must not enable voltage writes. */ }
        try { BaselineSa = ReadSvid(SaId); } catch { }
        try { BaselineL2 = ReadSvid(L2Id); } catch { }
        try { BaselineRing = ReadSvid(RingId); } catch { }
    }

    public static AsusBoardControl? TryCreate(string manufacturer, out string status)
    {
        status = "not an ASUS board";
        if (!manufacturer.Contains("ASUS", StringComparison.OrdinalIgnoreCase)) return null;
        object? vendor = null;
        try
        {
            var type = Type.GetTypeFromProgID("atkexCom.axdata", throwOnError: false);
            if (type == null) throw new IOException("ASUS control service is not installed");
            vendor = Activator.CreateInstance(type)!;
            var result = new AsusBoardControl(vendor);
            status = $"ASUS BCLK target {result.BaselineMHz:0.000} MHz; {result.VoltageDescription}";
            return result;
        }
        catch (Exception ex)
        {
            if (vendor != null) Marshal.FinalReleaseComObject(vendor);
            status = ex.Message;
            return null;
        }
    }

    public static double DecodeBclk(uint index)
    {
        if (index >= 49801) throw new IOException("Invalid ASUS BCLK index");
        return (40000.0 + index * 10.0) / 1000;
    }

    public static uint EncodeBclk(double mhz)
    {
        if (!double.IsFinite(mhz) || mhz < 90 || mhz > 110)
            throw new ArgumentOutOfRangeException(nameof(mhz), "Supported ASUS BCLK range is 90–110 MHz");
        double index = (mhz * 1000 - 40000) / 10;
        if (Math.Abs(index - Math.Round(index)) > 0.00001)
            throw new ArgumentException("ASUS BCLK requires 0.01 MHz steps");
        return checked((uint)Math.Round(index));
    }

    public uint ReadRaw(uint id)
    {
        uint value = 0;
        uint status = _vendor.iAcpiGetItem(id, ref value);
        if ((status & 1) == 0) throw new IOException($"ASUS read 0x{id:X8} failed: 0x{status:X8}");
        return value;
    }

    public double ReadBclk() => DecodeBclk(ReadRaw(BclkId));
    public double ReadVcore() => ReadRaw(0x06020011) / 1000.0;

    public double ReadRailVoltage(uint id) => ReadRaw(id switch
    {
        CoreId => 0x06020011,
        SaId => 0x06020091,
        L2Id => 0x0602008F,
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    }) / 1000.0;

    private static bool IsSa(uint id) => id switch
    {
        SaId => true,
        CoreId or L2Id or RingId => false,
        _ => throw new ArgumentOutOfRangeException(nameof(id), "Unsupported ASUS voltage item")
    };

    public static SvidState ParseSvid(byte[] buffer, uint size, uint id = CoreId)
    {
        bool sa = IsSa(id);
        if (size != 92 || buffer.Length < size) throw new IOException("Unsupported ASUS voltage descriptor size");
        uint W(int index) => BitConverter.ToUInt32(buffer, index * 4);
        if (W(1) != 0 || W(5) != (sa ? 0u : 0x00FFFC19u) || W(6) != 1 || W(7) != (sa ? 1000u : 1999u) ||
            W(8) != 1 || W(12) != (sa ? 700u : 250u) || W(13) != 1 || W(14) != (sa ? 1101u : 1671u) ||
            W(15) != 2 || W(19) != 0 || W(20) != 1 || W(21) != 2 || W(22) != uint.MaxValue)
            throw new IOException("Unsupported ASUS voltage descriptor layout");
        var state = new SvidState(W(2), W(9), W(16));
        ValidateState(state, id);
        return state;
    }

    private static void ValidateState(SvidState state, uint id = CoreId)
    {
        bool sa = IsSa(id);
        if (state.OffsetIndex > (sa ? 999 : 1998) || state.TargetIndex > (sa ? 1100 : 1670) || state.Mode > 1)
            throw new IOException("Invalid ASUS voltage state");
    }

    public SvidState ReadSvid(uint id = CoreId)
    {
        object buffer = new byte[4096]; uint size = 4096;
        _ = IsSa(id);
        uint status = _vendor.iAcpiGetItemBufferRef(id, 1u, ref buffer, ref size);
        if ((status & 1) == 0 || buffer is not byte[] bytes) throw new IOException("ASUS voltage metadata read failed");
        return ParseSvid(bytes, size, id);
    }

    public static byte[] SvidPayload(SvidState state, uint id = CoreId)
    {
        ValidateState(state, id);
        uint[] words = [0, state.OffsetIndex, 1, state.TargetIndex, 2, state.Mode, uint.MaxValue];
        var bytes = new byte[4096];
        Buffer.BlockCopy(words, 0, bytes, 0, words.Length * 4);
        return bytes;
    }

    private void WriteSvid(SvidState state, uint id)
    {
        object buffer = SvidPayload(state, id); uint size = 4096;
        uint status = _vendor.iAcpiSetItemBufferRef(id, 1u, ref buffer, ref size);
        if ((status & 1) == 0) throw new IOException("ASUS rejected the voltage request");
        Thread.Sleep(200);
        if (ReadSvid(id) != state) throw new IOException("ASUS voltage mode/target/offset did not read back");
    }

    public void SetSvid(SvidState state, uint id = CoreId)
    {
        ValidateState(state, id);
        var before = ReadSvid(id);
        if (state == before) return;
        try { WriteSvid(state, id); }
        catch (Exception failure)
        {
            try { WriteSvid(before, id); }
            catch (Exception restore) { throw new IOException($"{failure.Message}; VOLTAGE RESTORE FAILED: {restore.Message}", failure); }
            throw;
        }
    }

    public void SetCoreVoltage(double volts)
    {
        if (!double.IsFinite(volts) || volts < 0.6 || volts > 1.7) throw new ArgumentOutOfRangeException(nameof(volts));
        SetSvid(ReadSvid() with { TargetIndex = checked((uint)Math.Round(volts * 1000 - 250)), Mode = 1 });
    }

    public double? ReadCoreTarget()
        => ReadVoltageTarget(CoreId);

    public double? ReadVoltageTarget(uint id)
    {
        var state = ReadSvid(id);
        return state.Mode == 1 ? (state.TargetIndex + (IsSa(id) ? 700 : 250)) / 1000.0 : null;
    }

    public static SvidState WithVoltageTarget(uint id, SvidState state, double volts)
    {
        bool sa = IsSa(id);
        ValidateState(state, id);
        if (!double.IsFinite(volts) || volts < 0.7 || volts > 1.52)
            throw new ArgumentOutOfRangeException(nameof(volts));
        double mv = volts * 1000;
        if (Math.Abs(mv - Math.Round(mv)) > 0.000001) throw new ArgumentException("ASUS voltage requires 1 mV steps");
        return state with { TargetIndex = checked((uint)Math.Round(mv - (sa ? 700 : 250))), Mode = 1 };
    }

    public void SetRailVoltage(uint id, double volts) => SetSvid(WithVoltageTarget(id, ReadSvid(id), volts), id);

    // The main DIP module's live Apply method (dip4.dll 0x5235F8) passes option 2.
    // Its separate staging method (0x52369C) passes option 1; staging alone can
    // acknowledge and read back without changing the physical clock.
    // On the tested Z790-A BIOS 3202, option 2 also did not move the clock.
    // Keep this diagnostic-only until physical verification succeeds.
    private void WriteBclk(uint index)
    {
        _ = DecodeBclk(index);
        uint status = _vendor.iAcpiSetItem(BclkId, index, 2u);
        if ((status & 1) == 0) throw new IOException($"ASUS BCLK setter failed: 0x{status:X8}");
        Thread.Sleep(150);
        if (ReadRaw(BclkId) != index) throw new IOException("ASUS did not retain the requested BCLK target");
    }

    public void SetBclk(double target, Func<double?> measure, Action<string> log)
        => ChangeBclk(target, () => ReadRaw(BclkId), WriteBclk, measure, log);

    public static void ChangeBclk(double target, Func<uint> read, Action<uint> write,
                                  Func<double?> measure, Action<string> log)
    {
        uint goal = EncodeBclk(target), start = read();
        double current = DecodeBclk(start);
        if (goal == start) return;
        var before = measure();
        if (before is not double measured || !double.IsFinite(measured) || Math.Abs(measured - current) > 0.5)
            throw new IOException($"BCLK measurement disagrees with ASUS target ({before:0.000} vs {current:0.000} MHz); nothing changed");
        // The unhalted counter can under-read a nominal clock due to pauses or spread spectrum.
        // Verify movement relative to the measured baseline, and report the absolute reading too.
        double measurementOffset = measured - current;
        try
        {
            while (Math.Abs(target - current) > 0.005)
            {
                current += Math.Clamp(target - current, -0.5, 0.5);
                uint step = EncodeBclk(current);
                write(step);
                if (read() != step) throw new IOException("ASUS did not retain the requested BCLK target");
                var actual = measure();
                log($"ASUS BCLK target {current:0.000} MHz; measured {actual:0.000} MHz");
                if (actual is not double value || !double.IsFinite(value) || Math.Abs(value - current - measurementOffset) > 0.15)
                    throw new IOException("Physical BCLK did not follow the requested change");
            }
        }
        catch (Exception failure)
        {
            try
            {
                write(start);
                if (read() != start) throw new IOException("Restored target was not retained");
                log($"ASUS BCLK target restored to {DecodeBclk(start):0.000} MHz; measured {measure():0.000} MHz");
            }
            catch (Exception restore) { throw new IOException($"{failure.Message}; RESTORE FAILED: {restore.Message}", failure); }
            throw;
        }
    }

    public void Restore(Func<double?> measure, Action<string> log) => SetBclk(BaselineMHz, measure, log);
    public void Dispose() => Marshal.FinalReleaseComObject(_vendor);
}
