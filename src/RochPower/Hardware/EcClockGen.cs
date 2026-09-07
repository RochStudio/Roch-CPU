namespace RochPower.Hardware;

/// <summary>
/// The external base-clock generator, reached over <see cref="EcMailbox"/>.
///
/// The BCLK on a Raptor Lake board does not come from the CPU: it comes from a small synthesiser
/// on the board, an I2C device at the CK505-style address 0xD2 (7-bit 0x69), on the bus the EC
/// owns. That is why base clock could be measured but never set until the mailbox was found.
/// </summary>
public sealed class EcClockGen
{
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

    private readonly EcMailbox _mb;

    public EcClockGen(EcMailbox mailbox) => _mb = mailbox;

    public static bool IsSupported(SuperIo? sio) => EcMailbox.IsSupported(sio);

    private bool Unlock() => _mb.WriteWord(ClockGenAddress, R_CLKGEN_UNLOCK, 1);

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
            if (_mb.ReadWord(ClockGenAddress, (byte)(BlockBase + i)) is not ushort w) return null;
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
            if (!_mb.WriteWord(ClockGenAddress, (byte)(BlockBase + i), (ushort)(block[i] | (next << 8)))) return false;
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
