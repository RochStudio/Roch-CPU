namespace RochPower.Hardware;

/// <summary>
/// CPU-core override control used by MSI's Intel 600/700-series boards.
///
/// The CPU's OC mailbox carries the requested VID, but on these boards the Renesas regulator also
/// has to be put into override mode and given the same target. MSI Dragon Power performs this
/// transaction first, through the EC-owned I2C bus, then writes OC mailbox domain 0. Writing only
/// the mailbox changes its readback while leaving the physical rail under the board's old target.
/// </summary>
public sealed class MsiCoreVoltage : IDisposable
{
    // Eight-bit I2C address as required by the EC mailbox (7-bit address 0x63).
    private const byte Address = 0xC6;
    private const byte R_PAGE = 0x00;
    private const byte R_VOUT_COMMAND = 0x21;
    private const byte R_VOUT_SECONDARY = 0x23;
    private const byte R_MODE = 0xF0;
    private const ushort MODE_MASK = 0x0030;
    private const ushort MODE_OVERRIDE = 0x0030;

    private readonly EcMailbox _mailbox;
    private readonly Mutex? _sequenceMutex;
    private readonly object _lock = new();

    public readonly record struct State(byte Page, ushort ModeRegister, ushort TargetMv, ushort SecondaryTargetMv)
    {
        public int Mode => (ModeRegister >> 4) & 3;
        public bool OverrideEnabled => Mode == 3 && TargetMv != 0;
        public double? TargetVolts => OverrideEnabled ? TargetMv / 1000.0 : null;
        public override string ToString() =>
            $"page {Page}, mode {Mode}, target {(TargetMv / 1000.0):0.000} V, secondary 0x{SecondaryTargetMv:X4}";
    }

    private MsiCoreVoltage(EcMailbox mailbox)
    {
        _mailbox = mailbox;
        try { _sequenceMutex = new Mutex(false, @"Global\Access_SMBUS.Renesas.HTP.Method"); }
        catch { _sequenceMutex = null; }
    }

    /// <summary>Read-only detection. It deliberately refuses to change the regulator's active page.</summary>
    public static MsiCoreVoltage? TryCreate(EcMailbox mailbox, out string status)
    {
        var control = new MsiCoreVoltage(mailbox);
        bool keep = false;
        try
        {
            if (!control.Acquire()) { status = "timed out waiting for the Renesas regulator bus"; return null; }
            try
            {
                byte? page = mailbox.ReadByte(Address, R_PAGE);
                if (page is null)
                {
                    status = "Renesas core regulator at EC-I2C address 0xC6 did not answer";
                    return null;
                }
                if (page.Value != 0)
                {
                    status = $"Renesas core regulator answered on page {page.Value}, not core page 0; control left disabled";
                    return null;
                }
                var state = control.ReadStateCore(page.Value);
                status = "Renesas core regulator: " + state;
                keep = true;
                return control;
            }
            finally { control.Release(); }
        }
        catch (Exception ex)
        {
            status = "Renesas core regulator unavailable: " + ex.Message;
            return null;
        }
        finally
        {
            if (!keep) control.Dispose();
        }
    }

    private bool Acquire()
    {
        try { return _sequenceMutex?.WaitOne(1000) ?? true; }
        catch (AbandonedMutexException) { return true; }
    }

    private void Release() { try { _sequenceMutex?.ReleaseMutex(); } catch { } }

    private byte ReadByte(byte register) =>
        _mailbox.ReadByte(Address, register) ?? throw new IOException($"regulator read 0x{register:X2} failed");

    private ushort ReadWord(byte register) =>
        _mailbox.ReadWord(Address, register) ?? throw new IOException($"regulator read 0x{register:X2} failed");

    private void WriteByte(byte register, byte value)
    {
        if (!_mailbox.WriteByte(Address, register, value) || ReadByte(register) != value)
            throw new IOException($"regulator write 0x{register:X2}=0x{value:X2} did not read back");
    }

    private void WriteWord(byte register, ushort value)
    {
        if (!_mailbox.WriteWord(Address, register, value) || ReadWord(register) != value)
            throw new IOException($"regulator write 0x{register:X2}=0x{value:X4} did not read back");
    }

    private void SelectCorePage()
    {
        if (ReadByte(R_PAGE) != 0) WriteByte(R_PAGE, 0);
    }

    private State ReadStateCore(byte page) =>
        new(page, ReadWord(R_MODE), ReadWord(R_VOUT_COMMAND), ReadWord(R_VOUT_SECONDARY));

    public State ReadState()
    {
        lock (_lock)
        {
            if (!Acquire()) throw new IOException("timed out waiting for the regulator bus");
            try
            {
                SelectCorePage();
                return ReadStateCore(0);
            }
            finally { Release(); }
        }
    }

    public double? ReadTargetVolts() => ReadState().TargetVolts;

    /// <summary>
    /// Set the board regulator to the requested millivolt target. This is the precise ordering used
    /// by MSI Dragon Power: disable another active target, program VOUT_COMMAND, then select mode 3.
    /// </summary>
    public void SetOverride(double volts)
    {
        ushort targetMv = checked((ushort)Math.Round(volts * 1000.0));
        if (targetMv == 0) { DisableOverride(); return; }

        lock (_lock)
        {
            if (!Acquire()) throw new IOException("timed out waiting for the regulator bus");
            State before = default;
            bool captured = false;
            try
            {
                SelectCorePage();
                before = ReadStateCore(0);
                captured = true;
                ushort mode = before.ModeRegister;

                // The secondary target (0x23) and primary target (0x21) cannot own override mode
                // together. MSI clears 0x23 before enabling the primary CPU-core target.
                if (before.SecondaryTargetMv != 0 && (mode & MODE_MASK) != 0)
                {
                    mode = (ushort)(mode & ~MODE_MASK);
                    WriteWord(R_MODE, mode);
                    WriteWord(R_VOUT_SECONDARY, 0);
                }

                if (ReadWord(R_VOUT_COMMAND) != targetMv) WriteWord(R_VOUT_COMMAND, targetMv);
                if ((mode & MODE_MASK) != MODE_OVERRIDE)
                {
                    mode = (ushort)((mode & ~MODE_MASK) | MODE_OVERRIDE);
                    WriteWord(R_MODE, mode);
                }

                State after = ReadStateCore(0);
                if (!after.OverrideEnabled || after.TargetMv != targetMv)
                    throw new IOException($"regulator rejected {volts:0.000} V ({after})");
            }
            catch
            {
                if (captured) TryRestoreCore(before);
                throw;
            }
            finally { Release(); }
        }
    }

    public void DisableOverride()
    {
        lock (_lock)
        {
            if (!Acquire()) throw new IOException("timed out waiting for the regulator bus");
            State before = default;
            bool captured = false;
            try
            {
                SelectCorePage();
                before = ReadStateCore(0);
                captured = true;
                ushort mode = (ushort)(before.ModeRegister & ~MODE_MASK);
                if (mode != before.ModeRegister) WriteWord(R_MODE, mode);
                if (before.TargetMv != 0) WriteWord(R_VOUT_COMMAND, 0);
                if (before.SecondaryTargetMv != 0) WriteWord(R_VOUT_SECONDARY, 0);
            }
            catch
            {
                if (captured) TryRestoreCore(before);
                throw;
            }
            finally { Release(); }
        }
    }

    /// <summary>Restores a captured regulator state; used when the following CPU-mailbox write fails.</summary>
    public void Restore(State state)
    {
        lock (_lock)
        {
            if (!Acquire()) throw new IOException("timed out waiting for the regulator bus during rollback");
            try { RestoreCore(state); }
            finally { Release(); }
        }
    }

    private void RestoreCore(State state)
    {
        SelectCorePage();
        ushort disabled = (ushort)(ReadWord(R_MODE) & ~MODE_MASK);
        WriteWord(R_MODE, disabled);
        WriteWord(R_VOUT_COMMAND, state.TargetMv);
        WriteWord(R_VOUT_SECONDARY, state.SecondaryTargetMv);
        WriteWord(R_MODE, state.ModeRegister);
        if (state.Page != 0) WriteByte(R_PAGE, state.Page);
    }

    private void TryRestoreCore(State state)
    {
        try { RestoreCore(state); } catch { }
    }

    public void Dispose() => _sequenceMutex?.Dispose();
}
