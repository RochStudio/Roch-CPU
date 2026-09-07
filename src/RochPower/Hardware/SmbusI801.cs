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

    private const byte PROTO_BLOCK_DATA = 5 << 2;
    private const byte STS_BYTE_DONE = 0x80;
    private const int BLOCK_DB = 0x07;

    // NOTE: a QuickCommand() scan lived here. Driving a bare address phase across every
    // address hung this board's SMBus - afterwards no device answered at all, including the DDR5
    // SPD hubs, and only a power cycle brought it back. Detect devices with a byte read instead.

    /// <summary>SMBus block read. Returns the payload, or null when the device does not answer.</summary>
    public byte[]? BlockRead(byte address, byte command)
    {
        lock (_lock)
        {
            if (!Acquire()) return null;
            try
            {
                byte sts = In(HST_STS);
                if ((sts & STS_BUSY) != 0) { Out(HST_CNT, CNT_KILL); Thread.Sleep(1); Out(HST_CNT, 0); if ((In(HST_STS) & STS_BUSY) != 0) return null; }
                if ((sts & STS_ALL_FLAGS) != 0) Out(HST_STS, (byte)(sts & STS_ALL_FLAGS));

                Out(XMIT_SLVA, (byte)((address << 1) | 1));
                Out(HST_CMD, command);
                Out(AUX_CTL, 0);                 // byte-at-a-time, no 32-byte buffer
                Out(HST_CNT, PROTO_BLOCK_DATA | CNT_START);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool Wait(byte flag)
                {
                    while (sw.ElapsedMilliseconds < 100)
                    {
                        byte s = In(HST_STS);
                        if ((s & STS_ERROR_FLAGS) != 0) return false;
                        if ((s & flag) != 0) return true;
                        Thread.SpinWait(50);
                    }
                    return false;
                }

                if (!Wait(STS_BYTE_DONE)) { Abort(); return null; }
                int count = In(HST_D0);
                if (count is < 1 or > 32) { Abort(); return null; }
                var data = new byte[count];
                for (int i = 0; i < count; i++)
                {
                    data[i] = In(BLOCK_DB);
                    Out(HST_STS, STS_BYTE_DONE);  // advance to the next byte
                    if (i < count - 1 && !Wait(STS_BYTE_DONE)) { Abort(); return null; }
                }
                sts = In(HST_STS);
                Out(HST_STS, (byte)(sts & STS_ALL_FLAGS));
                Out(HST_STS, STS_INUSE);
                return data;
            }
            finally { Release(); }
        }
    }

    /// <summary>
    /// Bring the host controller back to idle. A transaction that was killed part way, or an
    /// address phase a device never completed, leaves BUSY or the in-use semaphore set and every
    /// later transfer then fails. This is the recovery path for that.
    /// </summary>
    public string Reset()
    {
        lock (_lock)
        {
            var before = In(HST_STS);
            for (int i = 0; i < 3; i++)
            {
                Out(HST_CNT, CNT_KILL);
                Thread.Sleep(2);
                Out(HST_CNT, 0);
                Thread.Sleep(2);
                Out(HST_STS, STS_ALL_FLAGS | STS_BYTE_DONE); // clear every latched flag
                Out(HST_STS, STS_INUSE);                     // hand the in-use semaphore back
                Thread.Sleep(2);
                if ((In(HST_STS) & STS_BUSY) == 0) break;
            }
            var after = In(HST_STS);
            return $"HST_STS 0x{before:X2} -> 0x{after:X2}";
        }
    }

    /// <summary>
    /// Soft-reset the host controller through HSTCFG (PCI 0:31:4 offset 0x40, bit 3 = SSRESET).
    /// Needed when HOST_BUSY latches high and a KILL will not clear it, which is what a killed
    /// or never-completed address phase leaves behind.
    /// </summary>
    public string HardReset()
    {
        lock (_lock)
        {
            if (!_drv.ReadPciConfig(Bus, Device, Function, 0x40, out uint cfg))
                return "could not read HSTCFG";
            uint before = cfg;
            _drv.WritePciConfig(Bus, Device, Function, 0x40, cfg | 0x08);   // SSRESET
            Thread.Sleep(10);
            _drv.WritePciConfig(Bus, Device, Function, 0x40, cfg & ~0x08u); // release, HST_EN preserved
            Thread.Sleep(10);
            Out(HST_STS, STS_ALL_FLAGS | STS_BYTE_DONE);
            Out(HST_STS, STS_INUSE);
            Thread.Sleep(10);
            _drv.ReadPciConfig(Bus, Device, Function, 0x40, out uint after);
            return $"HSTCFG 0x{before:X2} -> 0x{after:X2}, HST_STS now 0x{In(HST_STS):X2}";
        }
    }

    private void Abort()
    {
        try
        {
            Out(HST_CNT, CNT_KILL);
            Thread.Sleep(1);
            Out(HST_CNT, 0);
            Out(HST_STS, STS_ALL_FLAGS);
            Out(HST_STS, STS_INUSE);
        }
        catch { }
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
