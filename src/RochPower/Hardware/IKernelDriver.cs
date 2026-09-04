namespace RochPower.Hardware;

/// <summary>
/// Minimal ring-0 access surface needed by an overclocking tool. Everything the
/// application does with hardware goes through this so a different driver
/// (PawnIO, a vendor driver, a simulator for UI work) can be dropped in later.
/// </summary>
public interface IKernelDriver : IDisposable
{
    string Name { get; }
    bool IsOpen { get; }

    /// <summary>Read an MSR on the logical CPU <paramref name="cpu"/> (-1 = whichever CPU we are on).</summary>
    bool ReadMsr(uint index, out ulong value, int cpu = -1);
    bool WriteMsr(uint index, ulong value, int cpu = -1);

    byte ReadIoPortByte(ushort port);
    ushort ReadIoPortWord(ushort port);
    uint ReadIoPortDword(ushort port);
    void WriteIoPortByte(ushort port, byte value);
    void WriteIoPortWord(ushort port, ushort value);
    void WriteIoPortDword(ushort port, uint value);

    /// <summary>PCI configuration space read (legacy 256-byte space). Returns false if the device is absent.</summary>
    bool ReadPciConfig(byte bus, byte device, byte function, ushort offset, out uint value);
    bool WritePciConfig(byte bus, byte device, byte function, ushort offset, uint value);
}
