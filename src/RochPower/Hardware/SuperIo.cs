namespace RochPower.Hardware;

public enum SuperIoKind
{
    None,
    /// <summary>Nuvoton NCT6683/6686/6687 - EC address space. MSI 600/700-series.</summary>
    NuvotonEc,
    /// <summary>Nuvoton NCT679xD - banked register access. ASUS, and some ASRock/Gigabyte.</summary>
    NuvotonBank,
    /// <summary>ITE IT86xx/87xx. Gigabyte, and some ASRock.</summary>
    Ite
}

/// <summary>One board voltage rail as the Super I/O reports it.</summary>
public sealed record BoardRail(string Name, int Index, double Volts);

/// <summary>
/// Motherboard hardware monitor behind the LPC Super I/O at 0x2E / 0x4E.
///
/// The CPU's own registers cannot see the voltage the board's regulators actually deliver, so
/// this is what lets Roch CPU check that a core-voltage change reached the rail instead of only
/// changing what the CPU requests. Read-only throughout.
///
/// Three families cover the LGA1700 boards in circulation: the Nuvoton EC-space parts MSI uses,
/// the banked Nuvoton parts ASUS favours, and the ITE parts common on Gigabyte and ASRock.
/// An unrecognised chip is not an error: the rail check is simply skipped.
/// </summary>
public sealed class SuperIo : IDisposable
{
    private const byte NUVOTON_ENTER = 0x87, NUVOTON_EXIT = 0xAA;
    private const byte CHIP_ID_REGISTER = 0x20, CHIP_REVISION_REGISTER = 0x21;
    private const byte DEVICE_SELECT_REGISTER = 0x07, BASE_ADDRESS_REGISTER = 0x60;
    private const byte NUVOTON_HWM_LDN = 0x0B, ITE_HWM_LDN = 0x04;

    // EC address space offsets from the monitor base (Nuvoton 668x)
    private const int EC_PAGE = 0x04, EC_INDEX = 0x05, EC_DATA = 0x06;
    private const byte EC_PAGE_FREE = 0xFF;
    // Banked / indexed access offsets (Nuvoton 679x, ITE)
    private const int IDX = 0x05, DAT = 0x06;
    private const byte BANK_SELECT = 0x4E;

    private readonly IKernelDriver _drv;
    private readonly ushort _sioPort, _base;
    private readonly object _lock = new();
    private readonly Mutex? _isaMutex;

    public SuperIoKind Kind { get; }
    public string Name { get; }
    public ushort BaseAddress => _base;

    /// <summary>Channel index carrying Vcore for this family.</summary>
    public int VcoreIndex => Kind switch { SuperIoKind.NuvotonEc => 2, _ => 0 };

    /// <summary>
    /// Rails whose board wiring is known. Only populated for the MSI EC parts, where the mapping
    /// was verified against the vendor tool; elsewhere the channel order is board specific and
    /// naming a rail would be a guess, so only Vcore is used.
    /// </summary>
    public IReadOnlyList<(string name, int index)> NamedRails { get; }

    public const int RailVcore = 2, RailVdd2 = 4, RailSa = 5, RailAux = 6; // MSI EC channel numbers

    private SuperIo(IKernelDriver drv, ushort sioPort, ushort baseAddr, SuperIoKind kind, string name)
    {
        _drv = drv; _sioPort = sioPort; _base = baseAddr; Kind = kind; Name = name;
        NamedRails = kind == SuperIoKind.NuvotonEc
            ? new[] { ("CPU VDD2 Voltage", RailVdd2), ("CPU AUX Voltage", RailAux) }
            : Array.Empty<(string, int)>();
        try { _isaMutex = new Mutex(false, "Global\\Access_ISABUS.HTP.Method"); } catch { _isaMutex = null; }
    }

    // ---------------------------------------------------------------- detection
    public static SuperIo? TryCreate(IKernelDriver drv, out string status)
    {
        var problems = new List<string>();
        foreach (ushort port in new ushort[] { 0x2E, 0x4E })
        {
            try
            {
                if (TryNuvoton(drv, port, out var nuvoton, out string nStatus)) { status = nStatus; return nuvoton; }
                if (nStatus.Length > 0) problems.Add(nStatus);
                if (TryIte(drv, port, out var ite, out string iStatus)) { status = iStatus; return ite; }
                if (iStatus.Length > 0) problems.Add(iStatus);
            }
            catch (Exception ex) { problems.Add($"0x{port:X2}: {ex.Message}"); }
        }
        status = problems.Count > 0
            ? "No supported Super I/O (" + string.Join("; ", problems) + "). Board rail readings and the Vcore check are unavailable."
            : "No supported Super I/O on the LPC bus. Board rail readings and the Vcore check are unavailable.";
        return null;
    }

    private static bool TryNuvoton(IKernelDriver drv, ushort port, out SuperIo? result, out string status)
    {
        result = null; status = "";
        ushort value = (ushort)(port + 1);
        drv.WriteIoPortByte(port, NUVOTON_ENTER);
        drv.WriteIoPortByte(port, NUVOTON_ENTER);
        byte Read(byte reg) { drv.WriteIoPortByte(port, reg); return drv.ReadIoPortByte(value); }
        void Write(byte reg, byte v) { drv.WriteIoPortByte(port, reg); drv.WriteIoPortByte(value, v); }

        byte id = Read(CHIP_ID_REGISTER), rev = Read(CHIP_REVISION_REGISTER);
        ushort chip = (ushort)((id << 8) | rev);
        var (kind, name) = IdentifyNuvoton(chip);
        if (kind == SuperIoKind.None) { drv.WriteIoPortByte(port, NUVOTON_EXIT); return false; }

        Write(DEVICE_SELECT_REGISTER, NUVOTON_HWM_LDN);
        ushort addr = (ushort)((Read(BASE_ADDRESS_REGISTER) << 8) | Read((byte)(BASE_ADDRESS_REGISTER + 1)));
        Thread.Sleep(1);
        ushort verify = (ushort)((Read(BASE_ADDRESS_REGISTER) << 8) | Read((byte)(BASE_ADDRESS_REGISTER + 1)));
        drv.WriteIoPortByte(port, NUVOTON_EXIT);

        if (addr != verify || addr < 0x100 || addr == 0xFFFF)
        {
            status = $"{name} at LPC 0x{port:X2} but its monitor base did not read back stable (0x{addr:X4}/0x{verify:X4})";
            return false;
        }
        result = new SuperIo(drv, port, addr, kind, name);
        status = $"{name} at LPC 0x{port:X2}, hardware monitor at 0x{addr:X4}";
        return true;
    }

    private static (SuperIoKind, string) IdentifyNuvoton(ushort chip) => chip switch
    {
        0xC732 => (SuperIoKind.NuvotonEc, "Nuvoton NCT6683D"),
        0xD440 => (SuperIoKind.NuvotonEc, "Nuvoton NCT6686D"),
        0xD592 => (SuperIoKind.NuvotonEc, "Nuvoton NCT6687D"),
        0xC803 => (SuperIoKind.NuvotonBank, "Nuvoton NCT6791D"),
        0xC911 => (SuperIoKind.NuvotonBank, "Nuvoton NCT6792D"),
        0xD121 => (SuperIoKind.NuvotonBank, "Nuvoton NCT6793D"),
        0xD352 => (SuperIoKind.NuvotonBank, "Nuvoton NCT6795D"),
        0xD423 => (SuperIoKind.NuvotonBank, "Nuvoton NCT6796D"),
        0xD428 => (SuperIoKind.NuvotonBank, "Nuvoton NCT6798D"),
        0xD451 => (SuperIoKind.NuvotonBank, "Nuvoton NCT6797D"),
        0xD802 => (SuperIoKind.NuvotonBank, "Nuvoton NCT6799D"),
        _ => (SuperIoKind.None, "")
    };

    private static bool TryIte(IKernelDriver drv, ushort port, out SuperIo? result, out string status)
    {
        result = null; status = "";
        ushort value = (ushort)(port + 1);
        // ITE unlock: 0x87, 0x01, 0x55, then 0x55 on 0x2E or 0xAA on 0x4E.
        drv.WriteIoPortByte(port, 0x87);
        drv.WriteIoPortByte(port, 0x01);
        drv.WriteIoPortByte(port, 0x55);
        drv.WriteIoPortByte(port, port == 0x2E ? (byte)0x55 : (byte)0xAA);
        byte Read(byte reg) { drv.WriteIoPortByte(port, reg); return drv.ReadIoPortByte(value); }
        void Write(byte reg, byte v) { drv.WriteIoPortByte(port, reg); drv.WriteIoPortByte(value, v); }
        void Exit() { Write(0x02, 0x02); }

        ushort chip = (ushort)((Read(CHIP_ID_REGISTER) << 8) | Read(CHIP_REVISION_REGISTER));
        if ((chip & 0xFF00) != 0x8600 && (chip & 0xFF00) != 0x8700) { Exit(); return false; }

        Write(DEVICE_SELECT_REGISTER, ITE_HWM_LDN);
        ushort addr = (ushort)((Read(BASE_ADDRESS_REGISTER) << 8) | Read((byte)(BASE_ADDRESS_REGISTER + 1)));
        Thread.Sleep(1);
        ushort verify = (ushort)((Read(BASE_ADDRESS_REGISTER) << 8) | Read((byte)(BASE_ADDRESS_REGISTER + 1)));
        Exit();

        if (addr != verify || addr < 0x100 || addr == 0xFFFF)
        {
            status = $"ITE IT{chip:X4} at LPC 0x{port:X2} but its monitor base did not read back stable";
            return false;
        }
        result = new SuperIo(drv, port, addr, SuperIoKind.Ite, $"ITE IT{chip:X4}");
        status = $"ITE IT{chip:X4} at LPC 0x{port:X2}, hardware monitor at 0x{addr:X4}";
        return true;
    }

    // ---------------------------------------------------------------- register access
    private byte ReadByteEc(ushort address)
    {
        byte page = (byte)(address >> 8), index = (byte)(address & 0xFF);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (_drv.ReadIoPortByte((ushort)(_base + EC_PAGE)) != EC_PAGE_FREE && sw.ElapsedMilliseconds < 200)
            Thread.Sleep(1);
        _drv.WriteIoPortByte((ushort)(_base + EC_PAGE), EC_PAGE_FREE);
        _drv.WriteIoPortByte((ushort)(_base + EC_PAGE), page);
        _drv.WriteIoPortByte((ushort)(_base + EC_INDEX), index);
        byte result = _drv.ReadIoPortByte((ushort)(_base + EC_DATA));
        _drv.WriteIoPortByte((ushort)(_base + EC_PAGE), EC_PAGE_FREE);
        return result;
    }

    private byte ReadByteBank(ushort address)
    {
        byte bank = (byte)(address >> 8), reg = (byte)(address & 0xFF);
        _drv.WriteIoPortByte((ushort)(_base + IDX), BANK_SELECT);
        _drv.WriteIoPortByte((ushort)(_base + DAT), bank);
        _drv.WriteIoPortByte((ushort)(_base + IDX), reg);
        return _drv.ReadIoPortByte((ushort)(_base + DAT));
    }

    private byte ReadByteIte(ushort address)
    {
        _drv.WriteIoPortByte((ushort)(_base + IDX), (byte)(address & 0xFF));
        return _drv.ReadIoPortByte((ushort)(_base + DAT));
    }

    private byte ReadByteRaw(ushort address) => Kind switch
    {
        SuperIoKind.NuvotonEc => ReadByteEc(address),
        SuperIoKind.NuvotonBank => ReadByteBank(address),
        SuperIoKind.Ite => ReadByteIte(address),
        _ => 0
    };

    private T WithLock<T>(Func<T> body, T fallback)
    {
        lock (_lock)
        {
            bool held = false;
            try { held = _isaMutex?.WaitOne(300) ?? true; } catch (AbandonedMutexException) { held = true; }
            if (!held) return fallback;
            try { return body(); }
            catch { return fallback; }
            finally { try { _isaMutex?.ReleaseMutex(); } catch { } }
        }
    }

    // ---------------------------------------------------------------- voltages
    /// <summary>
    /// MSI's channel wiring on the EC parts, verified against HWiNFO and the vendor tool:
    /// VIN0 x12, VIN1 x5, VIN4 (VDD2) and VIN6 (AUX) x2, everything else direct.
    /// </summary>
    private static double EcDivider(int index) => index switch { 0 => 12.0, 1 => 5.0, 4 => 2.0, 6 => 2.0, _ => 1.0 };

    private static readonly ushort[] EcVoltageRegisters =
        { 0x120, 0x122, 0x124, 0x126, 0x128, 0x12A, 0x12C, 0x12E, 0x130, 0x13A, 0x13E, 0x136, 0x138, 0x13C };

    public int ChannelCount => Kind switch
    {
        SuperIoKind.NuvotonEc => EcVoltageRegisters.Length,
        SuperIoKind.NuvotonBank => 16,
        SuperIoKind.Ite => 9,
        _ => 0
    };

    public double? ReadVoltage(int index)
    {
        if (index < 0 || index >= ChannelCount) return null;
        return WithLock<double?>(() => Kind switch
        {
            // 16-bit reading at reg / reg+1, 1 mV per LSB before the input divider.
            SuperIoKind.NuvotonEc => 0.001 * ((16 * ReadByteRaw(EcVoltageRegisters[index]))
                                              + (ReadByteRaw((ushort)(EcVoltageRegisters[index] + 1)) >> 4)) * EcDivider(index),
            // Banked Nuvoton: 8 mV per LSB, Vcore on VIN0.
            SuperIoKind.NuvotonBank => 0.008 * ReadByteRaw((ushort)(0x480 + index)),
            // ITE: 12 mV per LSB, Vcore on VIN0.
            SuperIoKind.Ite => 0.012 * ReadByteRaw((ushort)(0x20 + index)),
            _ => null
        }, null);
    }

    public double? ReadVcore() => ReadVoltage(VcoreIndex);

    /// <summary>The rails whose wiring is known on this board; empty on families where it is not.</summary>
    public List<BoardRail> ReadRails()
    {
        var list = new List<BoardRail>();
        foreach (var (name, index) in NamedRails)
            if (ReadVoltage(index) is double v && v > 0.05) list.Add(new BoardRail(name, index, v));
        return list;
    }

    /// <summary>Every channel, for diagnostics.</summary>
    public List<(int index, double volts)> ReadAll()
    {
        var list = new List<(int, double)>();
        for (int i = 0; i < ChannelCount; i++)
            if (ReadVoltage(i) is double v) list.Add((i, v));
        return list;
    }

    public void Dispose() => _isaMutex?.Dispose();
}
