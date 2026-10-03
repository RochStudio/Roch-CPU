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
    private readonly Mutex? _ecSequenceMutex;
    private readonly object _lock = new();
    private State? _lastVerifiedState;
    public State? VerifiedBaseline { get; private set; }
    public bool TargetReadIsCached { get; private set; }
    public bool TargetReadFailed { get; private set; }
    public string TargetReadStatus { get; private set; } = "Core page has not been verified.";

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
        _mailbox = mailbox.WithStrictIo();
        try { _sequenceMutex = new Mutex(false, @"Global\Access_SMBUS.Renesas.HTP.Method"); }
        catch { _sequenceMutex = null; }
        try { _ecSequenceMutex = new Mutex(false, @"Global\Access_EC"); }
        catch { _ecSequenceMutex = null; }
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
                byte? page = control._mailbox.ReadByte(Address, R_PAGE);
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
                if (control.ReadByte(R_PAGE) != 0) throw new IOException("Regulator PAGE changed while probing the core state; control left disabled.");
                CorePageProbe.ValidateSnapshot(new(state.ModeRegister, state.TargetMv, state.SecondaryTargetMv));
                control.VerifiedBaseline = control._lastVerifiedState = state;
                control.TargetReadStatus = "Core PAGE 0 target read from the regulator.";
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

    /// <summary>
    /// One supported startup verification. Selects/reads core PAGE 0 and restores the original
    /// PAGE, whether initialization found PAGE 0 or PAGE 1. It never changes voltage/mode/mailbox
    /// targets and never initializes a driver. The model prevents repeated initialization attempts.
    /// </summary>
    public static MsiCoreVoltage? TryCreateForStartup(EcMailbox mailbox,
        out CorePageVerificationResult result)
    {
        var control = new MsiCoreVoltage(mailbox);
        return CompleteStartupVerification(control, out result);
    }

    // Common verification after construction, kept private so production cannot supply a fake
    // transport or synchronization. Inert tests replace the fake controller's locks first.
    private static MsiCoreVoltage? CompleteStartupVerification(MsiCoreVoltage control,
        out CorePageVerificationResult result)
    {
        var diagnostics = StartupIoDiagnostics.Begin();
        try
        {
            result = CorePageProbe.Verify(new ProbeTransport(control._mailbox), control.Acquire, control.Release);
            StartupIoDiagnostics.ObserveProbeResult(result);
        }
        finally { diagnostics?.Dispose(); }
        try { if (diagnostics != null) result = result with { DiagnosticLines = diagnostics.Snapshot() }; }
        catch { } // Diagnostics cannot change activation, failure or hardware flow.
        if (!result.Succeeded || result.Snapshot is not { } snapshot)
        {
            control.Dispose();
            return null;
        }
        control.VerifiedBaseline = control._lastVerifiedState = new State(0, snapshot.ModeRegister,
            snapshot.TargetMv, snapshot.SecondaryTargetMv);
        control.TargetReadIsCached = result.RestoredPage != 0;
        control.TargetReadStatus = control.TargetReadIsCached
            ? $"Core target last verified on PAGE 0; regulator restored to PAGE {result.RestoredPage}."
            : "Core PAGE 0 target read from the regulator.";
        return control;
    }

    private bool Acquire()
    {
        if (_sequenceMutex == null || _ecSequenceMutex == null) return false;
        bool held;
        try { held = _sequenceMutex.WaitOne(1000); }
        catch (AbandonedMutexException) { held = true; }
        if (!held) return false;
        bool ecHeld = false;
        try
        {
            try { ecHeld = _ecSequenceMutex.WaitOne(1000); }
            catch (AbandonedMutexException) { ecHeld = true; }
            return ecHeld;
        }
        finally { if (!ecHeld) _sequenceMutex.ReleaseMutex(); }
    }

    private void Release()
    {
        try { _ecSequenceMutex?.ReleaseMutex(); }
        finally { _sequenceMutex?.ReleaseMutex(); }
    }

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
        if (ReadByte(R_PAGE) != 0) throw new IOException("Core PAGE 0 is no longer selected; target write refused.");
        if (!_mailbox.WriteWord(Address, register, value) || ReadWord(register) != value)
            throw new IOException($"regulator write 0x{register:X2}=0x{value:X4} did not read back");
    }

    private void SelectCorePage()
    {
        if (ReadByte(R_PAGE) != 0) WriteByte(R_PAGE, 0);
    }

    private State ReadStateCore(byte page) =>
        new(page, ReadWord(R_MODE), ReadWord(R_VOUT_COMMAND), ReadWord(R_VOUT_SECONDARY));

    private T OnCorePage<T>(Func<State, T> work, bool validateCurrent = true)
    {
        lock (_lock)
        {
            if (!Acquire()) throw new IOException("timed out waiting for the regulator bus");
            byte? originalPage = null;
            Exception? failure = null;
            T result = default!;
            try
            {
                originalPage = ReadByte(R_PAGE);
                if (originalPage is not (0 or 1)) throw new IOException($"Regulator PAGE {originalPage} is not verified for core control.");
                SelectCorePage();
                var state = ReadStateCore(0);
                if (ReadByte(R_PAGE) != 0) throw new IOException("Regulator PAGE changed while reading the core state.");
                if (validateCurrent) CorePageProbe.ValidateSnapshot(new(state.ModeRegister, state.TargetMv, state.SecondaryTargetMv));
                result = work(state);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                try
                {
                    if (originalPage is 0 or 1)
                    {
                        // A failing select/read may still have switched PAGE. Restore even then.
                        byte? current = null;
                        try { current = ReadByte(R_PAGE); } catch { }
                        if (current != originalPage) WriteByte(R_PAGE, originalPage.Value);
                        if (ReadByte(R_PAGE) != originalPage) throw new IOException("Original regulator PAGE restoration did not read back.");
                        if (_lastVerifiedState.HasValue)
                        {
                            if (failure == null) TargetReadFailed = false;
                            TargetReadIsCached = originalPage != 0;
                            TargetReadStatus = TargetReadIsCached
                                ? "Core target last verified on PAGE 0; regulator restored to PAGE 1."
                                : "Core PAGE 0 target read from the regulator.";
                        }
                    }
                }
                catch (Exception ex) { failure = failure == null ? ex : new AggregateException(failure, ex); }
                finally { Release(); }
            }
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return result;
        }
    }

    /// <summary>Explicit apply/rollback state read; preserves the regulator's original PAGE.</summary>
    public State ReadState() => OnCorePage(state => { _lastVerifiedState = state; return state; });

    /// <summary>Explicit Apply only: holds the shared bus locks and core PAGE across both targets.</summary>
    public void WithCorePageForApply(Action<State> work) => OnCorePage(state => { work(state); return true; });

    /// <summary>
    /// Refresh never selects a regulator PAGE. On a different page it returns the last verified
    /// core target and labels it cached; it never reads another page as Vcore or substitutes VID.
    /// </summary>
    public double? ReadTargetVolts()
    {
        lock (_lock)
        {
            try
            {
                if (!Acquire()) throw new IOException("timed out waiting for the regulator bus");
                try
                {
                    byte page = ReadByte(R_PAGE);
                    if (page == 0)
                    {
                        var state = ReadStateCore(0);
                        if (ReadByte(R_PAGE) != 0) throw new IOException("Regulator PAGE changed while reading the core target.");
                        CorePageProbe.ValidateSnapshot(new(state.ModeRegister, state.TargetMv, state.SecondaryTargetMv));
                        _lastVerifiedState = state;
                        TargetReadIsCached = false;
                        TargetReadStatus = "Core PAGE 0 target read from the regulator.";
                    }
                    else if (page == 1 && _lastVerifiedState.HasValue)
                    {
                        TargetReadIsCached = true;
                        TargetReadStatus = "Core target last verified on PAGE 0; regulator currently on PAGE 1.";
                    }
                    else throw new IOException($"Core target unavailable on regulator PAGE {page}; no page change attempted.");
                    TargetReadFailed = false;
                    return _lastVerifiedState?.TargetVolts;
                }
                finally { Release(); }
            }
            catch (Exception ex)
            {
                TargetReadFailed = true;
                TargetReadStatus = "Core target not read: " + ex.Message;
                throw;
            }
        }
    }

    /// <summary>
    /// Set the board regulator to the requested millivolt target. This is the precise ordering used
    /// by MSI Dragon Power: disable another active target, program VOUT_COMMAND, then select mode 3.
    /// </summary>
    public void SetOverride(double volts)
    {
        ushort targetMv = checked((ushort)Math.Round(volts * 1000.0));
        if (targetMv == 0) { DisableOverride(); return; }

        OnCorePage(before =>
        {
            try
            {
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
                _lastVerifiedState = after;
            }
            catch
            {
                TryRestoreCore(before);
                throw;
            }
            return true;
        });
    }

    public void DisableOverride()
    {
        OnCorePage(before =>
        {
            try
            {
                ushort mode = (ushort)(before.ModeRegister & ~MODE_MASK);
                if (mode != before.ModeRegister) WriteWord(R_MODE, mode);
                if (before.TargetMv != 0) WriteWord(R_VOUT_COMMAND, 0);
                if (before.SecondaryTargetMv != 0) WriteWord(R_VOUT_SECONDARY, 0);
                _lastVerifiedState = ReadStateCore(0);
            }
            catch
            {
                TryRestoreCore(before);
                throw;
            }
            return true;
        });
    }

    /// <summary>Restores a captured regulator state; used when the following CPU-mailbox write fails.</summary>
    public void Restore(State state)
    {
        CorePageProbe.ValidateSnapshot(new(state.ModeRegister, state.TargetMv, state.SecondaryTargetMv));
        OnCorePage(_ =>
        {
            RestoreCore(state with { Page = 0 });
            _lastVerifiedState = ReadStateCore(0);
            return true;
        }, validateCurrent: false);
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

    private sealed class ProbeTransport(EcMailbox mailbox) : ICorePageProbeTransport
    {
        public byte? ReadPage() => mailbox.ReadByte(Address, R_PAGE);
        public bool WritePage(byte page)
        {
            if (page is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(page));
            return mailbox.WriteByte(Address, R_PAGE, page);
        }
        public ushort? ReadMode() => mailbox.ReadWord(Address, R_MODE);
        public ushort? ReadTarget() => mailbox.ReadWord(Address, R_VOUT_COMMAND);
        public ushort? ReadSecondaryTarget() => mailbox.ReadWord(Address, R_VOUT_SECONDARY);
    }

    public void Dispose() { _ecSequenceMutex?.Dispose(); _sequenceMutex?.Dispose(); }
}
