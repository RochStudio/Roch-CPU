namespace RochPower.Hardware;

/// <summary>
/// AMD FCH SMBus host controller (the PIIX4-compatible block at bus 0, device 20, function 0,
/// vendor 0x1022 device 0x790B on every Zen platform), driven through I/O ports. Its register
/// window sits at I/O 0xB00 on AM4 and AM5 boards; the FCH has no BAR for it, the base lives
/// in the PM MMIO block, and every BIOS seen so far leaves the default. The window is checked
/// before use: a status register that reads 0xFF means nothing is there.
///
/// The FCH can route this controller to one of several physical SMBus ports through a mux in
/// the PM MMIO block. Changing it needs an MMIO write the WinRing0 driver cannot do, so this
/// class talks to whichever port the BIOS left selected; the DIMMs are on it on the boards
/// tried so far, and if they are not the probe simply finds no SPD hubs and says so.
/// </summary>
public sealed class SmbusPiix4 : ISmbus
{
    private const byte Bus = 0, Device = 0x14, Function = 0;
    private const ushort VendorAmd = 0x1022, DeviceKernCz = 0x790B;
    private const ushort DefaultBase = 0x0B00;

    private const int HST_STS = 0x00, HST_CNT = 0x02, HST_CMD = 0x03, HST_ADD = 0x04, HST_D0 = 0x05, HST_D1 = 0x06;
    private const byte STS_BUSY = 0x01, STS_INTR = 0x02, STS_DEV_ERR = 0x04, STS_BUS_ERR = 0x08, STS_FAILED = 0x10;
    private const byte STS_ERROR_FLAGS = STS_DEV_ERR | STS_BUS_ERR | STS_FAILED;
    private const byte CNT_START = 0x40;
    private const byte PROTO_BYTE_DATA = 0x08, PROTO_WORD_DATA = 0x0C;

    private readonly IKernelDriver _drv;
    private readonly ushort _base;
    private readonly Mutex? _globalMutex;
    private readonly object _lock = new();

    public ushort BaseAddress => _base;
    public string Description { get; }

    private SmbusPiix4(IKernelDriver drv, ushort baseAddr, byte revision)
    {
        _drv = drv; _base = baseAddr;
        Description = $"AMD FCH SMBus (0x{DeviceKernCz:X4} rev 0x{revision:X2}) at I/O 0x{baseAddr:X4}";
        // The same mutex HWiNFO, LibreHardwareMonitor and ZenStates take around SMBus transactions.
        try { _globalMutex = new Mutex(false, @"Global\Access_SMBUS.HTP.Method"); } catch { _globalMutex = null; }
    }

    /// <summary>Returns null when the FCH SMBus controller is absent or its I/O window does not answer.</summary>
    public static SmbusPiix4? TryCreate(IKernelDriver drv, out string status)
    {
        if (!drv.ReadPciConfig(Bus, Device, Function, 0, out uint id) || (id & 0xFFFF) != VendorAmd)
        {
            status = "AMD FCH SMBus controller (B0:D20:F0) not visible.";
            return null;
        }
        if ((id >> 16) != DeviceKernCz)
        {
            status = $"B0:D20:F0 is AMD device 0x{id >> 16:X4}, not the FCH SMBus controller.";
            return null;
        }
        drv.ReadPciConfig(Bus, Device, Function, 0x08, out uint rev);
        // The controller's I/O decode must be on; the BIOS enables it for its own SPD reads.
        drv.ReadPciConfig(Bus, Device, Function, 0x04, out uint cmd);
        if ((cmd & 1) == 0)
        {
            status = "AMD FCH SMBus controller has I/O decoding disabled (PCI command bit 0).";
            return null;
        }
        byte sts = drv.ReadIoPortByte(DefaultBase + HST_STS);
        if (sts == 0xFF)
        {
            status = $"AMD FCH SMBus window at I/O 0x{DefaultBase:X4} does not answer (status 0xFF); the BIOS relocated it.";
            return null;
        }
        var bus = new SmbusPiix4(drv, DefaultBase, (byte)(rev & 0xFF));
        status = bus.Description;
        return bus;
    }

    private byte In(int off) => _drv.ReadIoPortByte((ushort)(_base + off));
    private void Out(int off, byte v) => _drv.WriteIoPortByte((ushort)(_base + off), v);

    private bool Acquire()
    {
        try { return _globalMutex?.WaitOne(500) ?? true; }
        catch (AbandonedMutexException) { return true; }
    }

    private void Release() { try { _globalMutex?.ReleaseMutex(); } catch { } }

    /// <summary>One transaction. The controller latches its status bits; they are written back to clear them.</summary>
    private bool Transaction(byte address, byte command, byte protocol, bool read, ushort data, out ushort result)
    {
        result = 0;
        lock (_lock)
        {
            if (!Acquire()) return false;
            try
            {
                byte sts = In(HST_STS);
                if (sts != 0)
                {
                    Out(HST_STS, sts);
                    sts = In(HST_STS);
                    if (sts != 0) return false; // stuck busy or an error that will not clear: leave it alone
                }

                Out(HST_ADD, (byte)((address << 1) | (read ? 1 : 0)));
                Out(HST_CMD, command);
                if (!read)
                {
                    Out(HST_D0, (byte)(data & 0xFF));
                    if (protocol == PROTO_WORD_DATA) Out(HST_D1, (byte)(data >> 8));
                }
                Out(HST_CNT, protocol);
                Out(HST_CNT, (byte)(protocol | CNT_START));

                var sw = System.Diagnostics.Stopwatch.StartNew();
                do
                {
                    sts = In(HST_STS);
                    if ((sts & STS_BUSY) == 0) break;
                    Thread.SpinWait(100);
                } while (sw.ElapsedMilliseconds < 64);

                bool ok = (sts & (STS_BUSY | STS_ERROR_FLAGS)) == 0 && (sts & STS_INTR) != 0;
                if (ok && read)
                {
                    result = In(HST_D0);
                    if (protocol == PROTO_WORD_DATA) result |= (ushort)(In(HST_D1) << 8);
                }
                if (sts != 0) Out(HST_STS, sts);
                return ok;
            }
            finally { Release(); }
        }
    }

    public bool ReadByte(byte address, byte command, out byte value)
    {
        bool ok = Transaction(address, command, PROTO_BYTE_DATA, true, 0, out ushort r);
        value = (byte)r;
        return ok;
    }

    public bool WriteByte(byte address, byte command, byte value) =>
        Transaction(address, command, PROTO_BYTE_DATA, false, value, out _);

    public bool ReadWord(byte address, byte command, out ushort value) =>
        Transaction(address, command, PROTO_WORD_DATA, true, 0, out value);

    public void Dispose() => _globalMutex?.Dispose();
}
