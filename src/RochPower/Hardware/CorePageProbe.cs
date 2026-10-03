namespace RochPower.Hardware;

/// <summary>The diagnostic exposes only PAGE changes and reads of the three core registers.</summary>
public interface ICorePageProbeTransport
{
    byte? ReadPage();
    bool WritePage(byte page);
    ushort? ReadMode();
    ushort? ReadTarget();
    ushort? ReadSecondaryTarget();
}

public readonly record struct CorePageSnapshot(ushort ModeRegister, ushort TargetMv, ushort SecondaryTargetMv)
{
    public int Mode => (ModeRegister >> 4) & 3;
    public double ProgrammedTargetVolts => TargetMv / 1000.0;
    public double? OverrideTargetVolts => Mode == 3 && TargetMv != 0 ? ProgrammedTargetVolts : null;
}

public sealed record CorePageVerificationResult(
    bool Succeeded, byte? OriginalPage, byte? RestoredPage, CorePageSnapshot? Snapshot,
    string Status, bool PageSelectionAttempted, bool PageRestorationAttempted)
{
    public IReadOnlyList<string> DiagnosticLines { get; init; } = Array.Empty<string>();
}

/// <summary>
/// A bounded diagnostic. The caller supplies the existing Renesas bus lock and an already
/// identified regulator transport. No voltage, mode, Intel mailbox, or driver-lifecycle API is exposed.
/// </summary>
public static class CorePageProbe
{
    public static CorePageVerificationResult Verify(ICorePageProbeTransport transport,
        Func<bool> acquireBus, Action releaseBus)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(acquireBus);
        ArgumentNullException.ThrowIfNull(releaseBus);
        byte? original = null, restored = null;
        CorePageSnapshot? snapshot = null;
        string? failure = null, restorationFailure = null;
        bool acquired = false, pageContext = false, selected = false, restoring = false;
        try
        {
            acquired = acquireBus();
            if (!acquired) throw new IOException("Timed out waiting for the Renesas regulator bus; no register access attempted.");
            original = transport.ReadPage() ?? throw new IOException("Regulator PAGE read failed; no page change attempted.");
            if (original is not (0 or 1))
                throw new IOException($"Regulator PAGE {original} is outside the verified 0/1 diagnostic scope; no page change attempted.");
            pageContext = true;
            if (original != 0)
            {
                selected = true; // A failed write may still have changed the physical page.
                if (!transport.WritePage(0)) throw new IOException("Selecting core PAGE 0 failed.");
            }
            if (transport.ReadPage() != 0) throw new IOException("Core PAGE 0 selection did not read back.");
            ushort mode = transport.ReadMode() ?? throw new IOException("Core register 0xF0 read failed.");
            ushort target = transport.ReadTarget() ?? throw new IOException("Core register 0x21 read failed.");
            ushort secondary = transport.ReadSecondaryTarget() ?? throw new IOException("Core register 0x23 read failed.");
            snapshot = new(mode, target, secondary);
            if (transport.ReadPage() != 0) throw new IOException("Regulator PAGE changed while reading core registers.");
            ValidateSnapshot(snapshot.Value);
        }
        catch (Exception ex) { failure = ex.Message; }
        finally
        {
            if (acquired)
            {
                try
                {
                    if (pageContext)
                    {
                        // Re-read before restoring. If the read fails, still attempt the saved page.
                        byte? current = null;
                        try { current = transport.ReadPage(); } catch { }
                        bool writeOk = true;
                        if (current != original)
                        {
                            restoring = true;
                            writeOk = transport.WritePage(original!.Value);
                        }
                        restored = transport.ReadPage();
                        if (!writeOk || restored != original)
                            restorationFailure = $"Original regulator PAGE {original} restoration could not be verified (read {restored?.ToString() ?? "failed"}).";
                    }
                }
                catch (Exception ex) { restorationFailure = "Original regulator PAGE restoration failed: " + ex.Message; }
                finally
                {
                    try { releaseBus(); }
                    catch (Exception ex) { restorationFailure = (restorationFailure == null ? "" : restorationFailure + " ") + "Regulator bus lock release failed: " + ex.Message; }
                }
            }
        }
        bool succeeded = failure == null && restorationFailure == null && snapshot.HasValue && restored == original;
        string status = succeeded
            ? $"Core PAGE 0 verified: mode {snapshot!.Value.Mode}, target {snapshot.Value.ProgrammedTargetVolts:0.000} V, secondary 0x{snapshot.Value.SecondaryTargetMv:X4}; original PAGE {original} restored. No voltage or mode changed."
            : string.Join(" ", new[] { failure, restorationFailure }.Where(s => s != null));
        return new(succeeded, original, restored, snapshot, status, selected, restoring);
    }

    public static void ValidateSnapshot(CorePageSnapshot snapshot)
    {
        if (snapshot.ModeRegister == ushort.MaxValue)
            throw new IOException("Core mode register returned 0xFFFF; controller identity/state is not verified.");
        if (snapshot.TargetMv != 0 && snapshot.TargetMv is < 600 or > 1720)
            throw new IOException($"Core target register {snapshot.TargetMv} mV is outside the existing 0.600–1.720 V control range; activation refused.");
        if (snapshot.SecondaryTargetMv != 0 && snapshot.SecondaryTargetMv is < 600 or > 1720)
            throw new IOException($"Core secondary target {snapshot.SecondaryTargetMv} mV is outside the verified range; activation refused.");
        if (snapshot.Mode == 3 && snapshot.TargetMv == 0)
            throw new IOException("Core override mode has no programmed target; activation refused.");
    }
}
