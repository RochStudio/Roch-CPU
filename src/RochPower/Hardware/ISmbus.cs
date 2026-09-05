namespace RochPower.Hardware;

/// <summary>
/// SMBus host controller: byte and word transfers to a 7-bit address. Intel's PCH controller
/// and AMD's FCH controller have different registers but the same job, and the DDR5 SPD hub
/// and PMIC code only needs this much.
/// </summary>
public interface ISmbus : IDisposable
{
    string Description { get; }
    bool ReadByte(byte address, byte command, out byte value);
    bool WriteByte(byte address, byte command, byte value);
    bool ReadWord(byte address, byte command, out ushort value);
}
