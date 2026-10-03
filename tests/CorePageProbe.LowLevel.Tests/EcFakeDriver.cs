using RochPower.Hardware;

sealed class EcFakeDriver : IKernelDriver
{
    private const ushort Base = 0x0A20;
    private readonly Dictionary<ushort, byte> _ec = new() { [0x460] = 0x80 };
    public byte Page = 1, EcSelectedPage = 0xFF;
    private byte _ecIndex;
    public bool FailPageResultOnce, FailModeResultOnce, FailTargetResultOnce, FailSecondaryResultOnce,
        FailCommandResultOnce, FailTargetWriteOnce, FailPageSelectOnce, FailPageRestoreOnce,
        IgnorePageSelectOnce, IgnorePageRestoreOnce, CorePageDriftOnSecondaryOnce;
    public int PageResultReads, FailPageResultAt = 0, IoReads, IoWrites;
    public readonly List<byte> RegulatorReads = new();
    public readonly List<byte> MailboxCommands = new();
    public bool DriverOpen = true, SimulateMailboxReads = false;
    private ulong _mailboxResponse;
    public readonly List<(byte reg, ushort value)> RegulatorWrites = new();
    public ushort Mode = 0x30, Target = 1280, Secondary;
    public string Name => "strict EC simulation";
    public bool IsOpen => DriverOpen;
    public byte ReadIoPortByte(ushort port)
    {
        IoReads++;
        if (port == Base + 4) return EcSelectedPage;
        if (port != Base + 6) throw new Exception("Unexpected port read");
        ushort address = (ushort)((EcSelectedPage << 8) | _ecIndex);
        if (address == 0x463 && FailCommandResultOnce) { FailCommandResultOnce = false; throw new IOException("Synthetic command result I/O failure"); }
        if (address == 0x4B0 && _ec.GetValueOrDefault((ushort)0x466) == 0)
        {
            PageResultReads++;
            if (FailPageResultOnce || PageResultReads == FailPageResultAt)
            { FailPageResultOnce = false; throw new IOException("Synthetic PAGE data I/O failure"); }
        }
        if (address == 0x4B0 && _ec.GetValueOrDefault((ushort)0x466) == 0xF0 && FailModeResultOnce)
        { FailModeResultOnce = false; throw new IOException("Synthetic mode data I/O failure"); }
        if (address == 0x4B0 && _ec.GetValueOrDefault((ushort)0x466) == 0x21 && FailTargetResultOnce)
        { FailTargetResultOnce = false; throw new IOException("Synthetic target data I/O failure"); }
        if (address == 0x4B0 && _ec.GetValueOrDefault((ushort)0x466) == 0x23 && FailSecondaryResultOnce)
        { FailSecondaryResultOnce = false; throw new IOException("Synthetic secondary data I/O failure"); }
        return _ec.GetValueOrDefault(address);
    }
    public void WriteIoPortByte(ushort port, byte value)
    {
        IoWrites++;
        if (port == Base + 4) { EcSelectedPage = value; return; }
        if (port == Base + 5) { _ecIndex = value; return; }
        if (port != Base + 6) throw new Exception("Unexpected port write");
        ushort address = (ushort)((EcSelectedPage << 8) | _ecIndex);
        _ec[address] = value;
        if (address != 0x460) return;
        byte command = _ec.GetValueOrDefault((ushort)0x463), register = _ec.GetValueOrDefault((ushort)0x466);
        if (_ec.GetValueOrDefault((ushort)0x465) != 0xC6) throw new Exception("Unexpected regulator address");
        if (command >= 0x80 && value == 0x88)
        {
            RegulatorReads.Add(register);
            ushort raw = register switch { 0 => Page, 0xF0 => Mode, 0x21 => Target, 0x23 => Secondary, _ => throw new Exception("Unexpected regulator read") };
            if (register != 0 && Page != 0) throw new Exception("Read another regulator page as core");
            _ec[0x4B0] = (byte)raw; _ec[0x4B1] = (byte)(raw >> 8);
            if (register == 0x23 && CorePageDriftOnSecondaryOnce) { CorePageDriftOnSecondaryOnce = false; Page = 1; }
        }
        if (command < 0x80 && value == 0xC0)
        {
            ushort raw = (ushort)(_ec.GetValueOrDefault((ushort)0x470) | (_ec.GetValueOrDefault((ushort)0x471) << 8));
            if (command == 2) raw = _ec.GetValueOrDefault((ushort)0x470);
            RegulatorWrites.Add((register, raw));
            if (register != 0 && Page != 0) throw new Exception("Attempted target/mode write on non-core PAGE");
            if (register == 0)
            {
                if (raw == 0 && IgnorePageSelectOnce) IgnorePageSelectOnce = false;
                else if (raw == 1 && IgnorePageRestoreOnce) IgnorePageRestoreOnce = false;
                else Page = (byte)raw;
            }
            else if (register == 0xF0) Mode = raw;
            else if (register == 0x21) Target = raw;
            else if (register == 0x23) Secondary = raw;
            else throw new Exception("Unexpected regulator write");
            _ec[0x460] = 0x80;
            if (register == 0x21 && FailTargetWriteOnce) { FailTargetWriteOnce = false; throw new IOException("Synthetic target-write I/O failure after physical change"); }
            if (register == 0 && raw == 0 && FailPageSelectOnce) { FailPageSelectOnce = false; throw new IOException("Synthetic PAGE selection failure after physical change"); }
            if (register == 0 && raw == 1 && FailPageRestoreOnce) { FailPageRestoreOnce = false; throw new IOException("Synthetic PAGE restoration failure after physical change"); }
        }
        _ec[0x460] = 0x80;
    }
    public ushort ReadIoPortWord(ushort p) => throw new Exception("Unexpected word port read");
    public uint ReadIoPortDword(ushort p) => throw new Exception("Unexpected dword port read");
    public void WriteIoPortWord(ushort p, ushort v) => throw new Exception("Unexpected word port write");
    public void WriteIoPortDword(ushort p, uint v) => throw new Exception("Unexpected dword port write");
    public bool ReadMsr(uint r, out ulong v, int cpu = -1)
    {
        if (SimulateMailboxReads && r == OcMailbox.MSR_OC_MAILBOX) { v = _mailboxResponse; return true; }
        v = 0; throw new Exception("Unexpected MSR read");
    }
    public bool WriteMsr(uint r, ulong v, int cpu = -1)
    {
        if (!SimulateMailboxReads || r != OcMailbox.MSR_OC_MAILBOX) throw new Exception("Unexpected MSR write");
        byte command = (byte)(v >> 32);
        MailboxCommands.Add(command);
        if (command != OcMailbox.CMD_READ_VF) throw new Exception("The model fixture refuses every target/mode mailbox command");
        // Deliberately disagree with the board's 1.280 V target. Startup/defaults must
        // come from the verified regulator snapshot rather than this mailbox value.
        _mailboxResponse = OcMailbox.Encode(new VfDomainSettings { MaxRatio = 50, TargetVolts = 0.900, OverrideMode = true, OffsetVolts = -0.010 });
        return true;
    }
    public bool ReadPciConfig(byte b, byte d, byte f, ushort o, out uint v) { v = 0; throw new Exception("Unexpected PCI read"); }
    public bool WritePciConfig(byte b, byte d, byte f, ushort o, uint v) => throw new Exception("Unexpected PCI write");
    public void Dispose() { }
}
