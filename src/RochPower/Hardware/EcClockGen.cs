namespace RochPower.Hardware;

/// <summary>
/// The external base-clock generator, reached through the mailbox in MSI's embedded controller.
///
/// The BCLK on a Raptor Lake board does not come from the CPU: it comes from a small synthesiser
/// on the board, an I2C device at the CK505-style address 0xD2 (7-bit 0x69). That device does not
/// sit on the PCH SMBus, so no amount of work on the SMBus controller can reach it. The EC is its
/// bus master, and the EC exposes a mailbox in its own address space that will run a transaction
/// on that bus for you. That mailbox is the only route to the clock generator from software, and
/// it is why base clock could be measured but never set.
///
/// The register roles and the handshake below were captured from the vendor tool, by forwarding
/// its kernel driver's exports through a logging proxy and recording a real base-clock change;
/// they are not guesses. The exact sequence is replayed here rather than reconstructed, which is
/// what makes writing to a clock generator defensible at all: a wrong value stops the machine
/// dead, so nothing is invented.
///
/// The one deliberate difference from the vendor tool is that every write is verified against a
/// real measurement of the resulting clock and rolled back if it does not match, and the whole
/// path is clamped (see <see cref="BclkController"/>).
/// </summary>
public sealed class EcClockGen
{
    // Mailbox registers in EC space.
    private const ushort R_COMMAND = 0x463;   // 0x02/0x03 = write byte/word, 0x82/0x83 = read byte/word
    private const ushort R_ADDRESS = 0x465;   // I2C address, 8-bit form (0xD2 -> 7-bit 0x69)
    private const ushort R_REGISTER = 0x466;  // register index inside the target device
    private const ushort R_WDATA_LO = 0x470, R_WDATA_HI = 0x471;
    private const ushort R_RDATA_LO = 0x4B0, R_RDATA_HI = 0x4B1;
    private const ushort R_DOORBELL = 0x460;

    private const byte CMD_WRITE_WORD = 0x03, CMD_READ_WORD = 0x83;

    // The doorbell holds 0x80 at rest. The caller sets a go bit and the EC clears it when the
    // transaction on the I2C bus has finished. A read needs two phases - 0x88 runs the transfer,
    // 0xC0 latches the result into 0x4B0/0x4B1 - while a write needs only the 0xC0 phase, because
    // its data is already staged in 0x470/0x471.
    private const byte GO_TRANSFER = 0x88, GO_LATCH = 0xC0, DOORBELL_IDLE = 0x80;
    private const int DoorbellTimeoutMs = 250;

    /// <summary>The clock generator's I2C address in the 8-bit form the mailbox wants.</summary>
    public const byte ClockGenAddress = 0xD2;

    /// <summary>
    /// Written as a word with the value 1 before each burst. The vendor tool does this before
    /// every read group and again before every write group, so it is treated as required rather
    /// than incidental; it is most likely the register-window enable.
    /// </summary>
    private const byte R_CLKGEN_UNLOCK = 0xFD;

    /// <summary>The clock generator's frequency block: eight bytes at 0xE0.</summary>
    public const byte BlockBase = 0xE0;
    public const int BlockLength = 8;

    private readonly SuperIo _sio;

    public EcClockGen(SuperIo sio) => _sio = sio;

    /// <summary>Only MSI's EC-space Nuvoton parts carry this mailbox.</summary>
    public static bool IsSupported(SuperIo? sio) => sio is { Kind: SuperIoKind.NuvotonEc };

    // ---------------------------------------------------------------- mailbox
    private bool WaitDoorbell(out byte last)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            last = _sio.ReadRaw(R_DOORBELL);
            if (last == DOORBELL_IDLE) return true;
            Thread.Sleep(1);
        } while (sw.ElapsedMilliseconds < DoorbellTimeoutMs);
        return false;
    }

    private bool Ring(byte go)
    {
        if (!_sio.WriteRaw(R_DOORBELL, go)) return false;
        return WaitDoorbell(out _);
    }

    private bool Stage(byte command, byte address, byte register)
    {
        // The vendor tool reads each of these back after writing it. Keeping that check means a
        // mailbox that is busy with someone else's transaction is noticed before the doorbell.
        if (!_sio.WriteRaw(R_COMMAND, command) || _sio.ReadRaw(R_COMMAND) != command) return false;
        if (!_sio.WriteRaw(R_ADDRESS, address) || _sio.ReadRaw(R_ADDRESS) != address) return false;
        if (!_sio.WriteRaw(R_REGISTER, register) || _sio.ReadRaw(R_REGISTER) != register) return false;
        return true;
    }

    /// <summary>Reads one 16-bit register from a device on the EC's I2C bus.</summary>
    public ushort? ReadWord(byte address, byte register)
    {
        if (!WaitDoorbell(out _)) return null;
        if (!Stage(CMD_READ_WORD, address, register)) return null;
        if (!Ring(GO_TRANSFER)) return null;
        if (!Ring(GO_LATCH)) return null;
        return (ushort)(_sio.ReadRaw(R_RDATA_LO) | (_sio.ReadRaw(R_RDATA_HI) << 8));
    }

    /// <summary>Writes one 16-bit register to a device on the EC's I2C bus.</summary>
    public bool WriteWord(byte address, byte register, ushort value)
    {
        if (!WaitDoorbell(out _)) return false;
        if (!Stage(CMD_WRITE_WORD, address, register)) return false;
        byte lo = (byte)value, hi = (byte)(value >> 8);
        if (!_sio.WriteRaw(R_WDATA_LO, lo) || _sio.ReadRaw(R_WDATA_LO) != lo) return false;
        if (!_sio.WriteRaw(R_WDATA_HI, hi) || _sio.ReadRaw(R_WDATA_HI) != hi) return false;
        return Ring(GO_LATCH);
    }

    private bool Unlock() => WriteWord(ClockGenAddress, R_CLKGEN_UNLOCK, 1);

    // ---------------------------------------------------------------- frequency block
    /// <summary>
    /// Reads the eight-byte frequency block. Each register is read as a 16-bit word covering
    /// itself and the byte after it, so consecutive reads overlap by one byte; the overlap is
    /// used as a consistency check, because a mailbox that returned stale data would break it.
    /// </summary>
    public byte[]? ReadBlock()
    {
        if (!Unlock()) return null;
        var bytes = new byte[BlockLength + 1];
        for (int i = 0; i < BlockLength; i++)
        {
            if (ReadWord(ClockGenAddress, (byte)(BlockBase + i)) is not ushort w) return null;
            byte lo = (byte)w, hi = (byte)(w >> 8);
            if (i > 0 && bytes[i] != lo) return null; // overlap disagreed - do not trust the read
            bytes[i] = lo;
            bytes[i + 1] = hi;
        }
        return bytes[..BlockLength];
    }

    /// <summary>
    /// Writes the eight-byte frequency block the way the vendor tool does: a 16-bit write at each
    /// of the eight register indices in ascending order, each carrying its own byte and the next
    /// one. The trailing byte past the block is written as zero, which is what the capture shows.
    ///
    /// <para><b>The order is load-bearing.</b> The bytes go into shadow registers and the part
    /// latches the divider from them when the last one arrives, so writing 0xE0 upwards makes the
    /// change take effect all at once - there is no moment where a half-old, half-new divider is
    /// driving the clock. Writing the fraction before the integer looks safer if you assume each
    /// byte takes effect as it lands, but it leaves the integer written and never latched, and the
    /// clock quietly runs a whole count off. Measured, twice. Do not reorder this loop.</para>
    /// </summary>
    public bool WriteBlock(byte[] block)
    {
        if (block.Length != BlockLength) throw new ArgumentException($"need {BlockLength} bytes", nameof(block));
        if (!Unlock()) return false;
        for (int i = 0; i < BlockLength; i++)
        {
            byte next = i + 1 < BlockLength ? block[i + 1] : (byte)0;
            if (!WriteWord(ClockGenAddress, (byte)(BlockBase + i), (ushort)(block[i] | (next << 8)))) return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- block layout
    /// <summary>
    /// The block holds one fractional-N divider, and the base clock is a fixed oscillator divided
    /// by it:
    /// <code>  f = Fvco / (block[0] + tuning / 2^28)      Fvco is about 10002 MHz</code>
    /// Byte 0 is the integer part and bytes 4..7 are a 28-bit fraction, little-endian; the top
    /// four bits of that word are ignored by the part, and bytes 1..3 read as zero. Both fields
    /// therefore run *backwards* against frequency - a smaller divider is a faster clock.
    ///
    /// Measured across eleven points spanning 100.0 to 103.1 MHz, that relation holds to a
    /// thousandth of a MHz. It is worth saying how far the capture alone would have got this
    /// wrong: read on its own, the vendor tool's recorded ramp steps the fraction down, which
    /// looks like an ordinary overclock ramp of a frequency field until it is anchored against a
    /// measurement. It is actually a divider, and the ramp was at 103.1 MHz and climbing.
    /// <see cref="BclkController"/> measures Fvco on the hardware rather than trusting a constant.
    /// </summary>
    public static uint GetTuning(byte[] block) => (uint)(block[4] | (block[5] << 8) | (block[6] << 16) | (block[7] << 24));

    public static byte[] WithTuning(byte[] block, uint tuning)
    {
        var copy = (byte[])block.Clone();
        copy[4] = (byte)tuning; copy[5] = (byte)(tuning >> 8);
        copy[6] = (byte)(tuning >> 16); copy[7] = (byte)(tuning >> 24);
        return copy;
    }

    public static string Describe(byte[] block) =>
        string.Join(" ", block.Select(b => b.ToString("X2"))) + $"   flags {block[0]:X2} tuning {GetTuning(block)} (0x{GetTuning(block):X8})";
}
