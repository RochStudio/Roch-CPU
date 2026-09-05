namespace RochPower.Hardware;

/// <summary>
/// Intel PCH SMBus host controller (the i801 family: bus 0, device 31, function 4)
/// driven through I/O ports. Used to reach the DDR5 SPD hubs and PMICs on the DIMMs.
/// </summary>
public sealed class SmbusI801 : ISmbus
{
    private const byte Bus = 0, Device = 31, Function = 4;

    // Register offsets from the I/O base
    private const int HST_STS = 0, HST_CNT = 2, HST_CMD = 3, XMIT_SLVA = 4, HST_D0 = 5, HST_D1 = 6, AUX_CTL = 0xD;

    // HST_STS bits
    private const byte STS_BUSY = 0x01, STS_INTR = 0x02, STS_DEV_ERR = 0x04, STS_BUS_ERR = 0x08, STS_FAILED = 0x10,
        STS_INUSE = 0x40;
    private const byte STS_ERROR_FLAGS = STS_DEV_ERR | STS_BUS_ERR | STS_FAILED;
    private const byte STS_ALL_FLAGS = STS_BUSY | STS_INTR | STS_ERROR_FLAGS;

    // HST_CNT bits
    private const byte CNT_START = 0x40, CNT_KILL = 0x02;
    private const byte PROTO_BYTE_DATA = 2 << 2, PROTO_WORD_DATA = 3 << 2;

    private readonly IKernelDriver _drv;
    private readonly ushort _base;
    private readonly Mutex? _globalMutex;
    private readonly object _lock = new();

    public ushort BaseAddress => _base;
    public uint PciId { get; }
    public string Description => $"PCH SMBus 0x{PciId >> 16:X4} at I/O 0x{_base:X4}";

    private SmbusI801(IKernelDriver drv, ushort baseAddr, uint pciId)
    {
        _drv = drv; _base = baseAddr; PciId = pciId;
        try { _globalMutex = new Mutex(false, @"Global\Access_SMBUS.HTP.Method"); } catch { _globalMutex = null; }
    }

    /// <summary>Returns null if the PCH SMBus controller is absent, hidden by the BIOS, or has no I/O window.</summary>
    public static SmbusI801? TryCreate(IKernelDriver drv, out string status)
    {
        if (!drv.ReadPciConfig(Bus, Device, Function, 0, out uint id) || (id & 0xFFFF) != 0x8086)
        {
            status = "PCH SMBus controller (B0:D31:F4) not visible - hidden by BIOS or unsupported chipset.";
            return null;
        }
        drv.ReadPciConfig(Bus, Device, Function, 0x20, out uint bar);
        ushort baseAddr = (ushort)(bar & 0xFFFE);
        if ((bar & 1) == 0 || baseAddr == 0 || baseAddr == 0xFFFE)
        {
            status = "PCH SMBus controller has no I/O base (SMB_BASE).";
            return null;
        }
        drv.ReadPciConfig(Bus, Device, Function, 0x40, out uint hstcfg);
        if ((hstcfg & 1) == 0)
        {
            status = "PCH SMBus host interface is disabled (HST_EN=0).";
            return null;
        }
        status = $"PCH SMBus 0x{id >> 16:X4} at I/O 0x{baseAddr:X4}";
        return new SmbusI801(drv, baseAddr, id);
    }

    private byte In(int off) => _drv.ReadIoPortByte((ushort)(_base + off));
    private void Out(int off, byte v) => _drv.WriteIoPortByte((ushort)(_base + off), v);

    private bool Acquire()
    {
        try { return _globalMutex?.WaitOne(500) ?? true; }
        catch (AbandonedMutexException) { return true; }
    }

    private void Release() { try { _globalMutex?.ReleaseMutex(); } catch { } }

    private bool Transaction(byte address, byte command, byte protocol, bool read, ushort data, out ushort result)
    {
        result = 0;
        lock (_lock)
        {
            if (!Acquire()) return false;
            try
            {
                byte sts = In(HST_STS);
                if ((sts & STS_BUSY) != 0)
                {
                    Out(HST_CNT, CNT_KILL);
                    Thread.Sleep(1);
                    Out(HST_CNT, 0);
                    sts = In(HST_STS);
                    if ((sts & STS_BUSY) != 0) return false;
                }
                if ((sts & STS_ALL_FLAGS) != 0) Out(HST_STS, (byte)(sts & STS_ALL_FLAGS));

                Out(XMIT_SLVA, (byte)((address << 1) | (read ? 1 : 0)));
                Out(HST_CMD, command);
                if (!read)
                {
                    Out(HST_D0, (byte)(data & 0xFF));
                    if (protocol == PROTO_WORD_DATA) Out(HST_D1, (byte)(data >> 8));
                }
                Out(AUX_CTL, 0); // no PEC, no 32-byte buffer
                Out(HST_CNT, (byte)(protocol | CNT_START));

                var sw = System.Diagnostics.Stopwatch.StartNew();
                do
                {
                    sts = In(HST_STS);
                    if ((sts & STS_BUSY) == 0 && (sts & (STS_INTR | STS_ERROR_FLAGS)) != 0) break;
                    Thread.SpinWait(50);
                } while (sw.ElapsedMilliseconds < 100);

                bool ok = (sts & STS_ERROR_FLAGS) == 0 && (sts & STS_INTR) != 0;
                if (ok && read)
                {
                    result = In(HST_D0);
                    if (protocol == PROTO_WORD_DATA) result |= (ushort)(In(HST_D1) << 8);
                }
                Out(HST_STS, (byte)(sts & STS_ALL_FLAGS)); // clear status
                Out(HST_STS, STS_INUSE);                    // release the in-use semaphore
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
