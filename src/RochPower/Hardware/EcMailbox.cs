namespace RochPower.Hardware;

/// <summary>
/// The I2C mailbox in MSI's embedded controller.
///
/// Several things worth setting on these boards - the base clock generator, the regulator that
/// feeds VDD2 - sit on an I2C bus the EC owns, not on the PCH SMBus, so no amount of work on the
/// SMBus controller reaches them. The EC exposes a mailbox in its own address space that will run
/// one transaction on that bus and hand back the result, and that mailbox is the only route to
/// those devices from software.
///
/// The register roles and the handshake were captured from the vendor tool, by forwarding its
/// kernel driver's exported entry points through a logging proxy and recording real changes. They
/// are observations of an interface, not guesses, and they are replayed here rather than
/// reconstructed.
/// </summary>
public sealed class EcMailbox
{
    // Mailbox registers in EC space.
    private const ushort R_COMMAND = 0x463;   // see the CMD_ constants
    private const ushort R_ADDRESS = 0x465;   // I2C address, 8-bit form (0xD2 -> 7-bit 0x69)
    private const ushort R_REGISTER = 0x466;  // register index inside the target device
    private const ushort R_WDATA_LO = 0x470, R_WDATA_HI = 0x471;
    private const ushort R_RDATA_LO = 0x4B0, R_RDATA_HI = 0x4B1;
    private const ushort R_DOORBELL = 0x460;

    private const byte CMD_WRITE_BYTE = 0x02, CMD_WRITE_WORD = 0x03;
    private const byte CMD_READ_BYTE = 0x82, CMD_READ_WORD = 0x83;

    // The doorbell holds 0x80 at rest. The caller sets a go bit and the EC clears it when the
    // transaction on the I2C bus has finished. A read needs two phases - 0x88 runs the transfer,
    // 0xC0 latches the result into 0x4B0/0x4B1 - while a write needs only the 0xC0 phase, because
    // its data is already staged in 0x470/0x471.
    private const byte GO_TRANSFER = 0x88, GO_LATCH = 0xC0, DOORBELL_IDLE = 0x80;
    private const int DoorbellTimeoutMs = 250;

    private readonly SuperIo _sio;
    private readonly object _lock = new();

    public EcMailbox(SuperIo sio) => _sio = sio;

    /// <summary>Only MSI's EC-space Nuvoton parts carry this mailbox.</summary>
    public static bool IsSupported(SuperIo? sio) => sio is { Kind: SuperIoKind.NuvotonEc };

    // ---------------------------------------------------------------- handshake
    private bool WaitDoorbell()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            if (_sio.ReadRaw(R_DOORBELL) == DOORBELL_IDLE) return true;
            Thread.Sleep(1);
        } while (sw.ElapsedMilliseconds < DoorbellTimeoutMs);
        return false;
    }

    private bool Ring(byte go) => _sio.WriteRaw(R_DOORBELL, go) && WaitDoorbell();

    private bool Stage(byte command, byte address, byte register)
    {
        // The vendor tool reads each of these back after writing it. Keeping that check means a
        // mailbox busy with someone else's transaction is noticed before the doorbell is rung.
        if (!_sio.WriteRaw(R_COMMAND, command) || _sio.ReadRaw(R_COMMAND) != command) return false;
        if (!_sio.WriteRaw(R_ADDRESS, address) || _sio.ReadRaw(R_ADDRESS) != address) return false;
        if (!_sio.WriteRaw(R_REGISTER, register) || _sio.ReadRaw(R_REGISTER) != register) return false;
        return true;
    }

    private bool Transact(byte command, byte address, byte register, ushort? write, out ushort read)
    {
        read = 0;
        lock (_lock)
        {
            if (!WaitDoorbell()) return false;
            if (!Stage(command, address, register)) return false;

            if (write is ushort w)
            {
                byte lo = (byte)w;
                if (!_sio.WriteRaw(R_WDATA_LO, lo) || _sio.ReadRaw(R_WDATA_LO) != lo) return false;
                if (command == CMD_WRITE_WORD)
                {
                    byte hi = (byte)(w >> 8);
                    if (!_sio.WriteRaw(R_WDATA_HI, hi) || _sio.ReadRaw(R_WDATA_HI) != hi) return false;
                }
                return Ring(GO_LATCH);
            }

            if (!Ring(GO_TRANSFER)) return false;
            if (!Ring(GO_LATCH)) return false;
            read = _sio.ReadRaw(R_RDATA_LO);
            if (command == CMD_READ_WORD) read |= (ushort)(_sio.ReadRaw(R_RDATA_HI) << 8);
            return true;
        }
    }

    // ---------------------------------------------------------------- transfers
    /// <summary>Reads one byte from a device on the EC's I2C bus.</summary>
    public byte? ReadByte(byte address, byte register) =>
        Transact(CMD_READ_BYTE, address, register, null, out ushort v) ? (byte)v : null;

    /// <summary>Reads one 16-bit register from a device on the EC's I2C bus.</summary>
    public ushort? ReadWord(byte address, byte register) =>
        Transact(CMD_READ_WORD, address, register, null, out ushort v) ? v : null;

    /// <summary>Writes one byte to a device on the EC's I2C bus.</summary>
    public bool WriteByte(byte address, byte register, byte value) =>
        Transact(CMD_WRITE_BYTE, address, register, value, out _);

    /// <summary>Writes one 16-bit register to a device on the EC's I2C bus.</summary>
    public bool WriteWord(byte address, byte register, ushort value) =>
        Transact(CMD_WRITE_WORD, address, register, value, out _);
}
