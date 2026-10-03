namespace RochPower.Hardware;

/// <summary>Existing regulator-first / Intel-mailbox-second transaction, under one PAGE lease.</summary>
public static class CoreVoltagePairTransaction
{
    public static void Execute<TMailboxState>(MsiCoreVoltage regulator, Func<TMailboxState> readMailbox,
        Action writeRegulator, Action writeMailbox, Action verifyMailbox, Action<TMailboxState> restoreMailbox)
    {
        MsiCoreVoltage.State? before = null;
        TMailboxState mailboxBefore = default!;
        bool captured = false, regulatorAttempted = false, mailboxAttempted = false, rolledBack = false;
        void RollBack(Exception failure)
        {
            rolledBack = true;
            var failures = new List<Exception> { failure };
            if (captured)
            {
                if (mailboxAttempted)
                    try { restoreMailbox(mailboxBefore); } catch (Exception ex) { failures.Add(ex); }
                if (regulatorAttempted && before is { } state)
                    try { regulator.Restore(state); } catch (Exception ex) { failures.Add(ex); }
            }
            if (failures.Count > 1) throw new AggregateException("Core voltage transaction or rollback failed.", failures);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        try
        {
            regulator.WithCorePageForApply(state =>
            {
                mailboxBefore = readMailbox();
                before = state;
                captured = true;
                try
                {
                    regulatorAttempted = true;
                    writeRegulator();
                    mailboxAttempted = true;
                    writeMailbox();
                    verifyMailbox();
                }
                catch (Exception failure) { RollBack(failure); }
            });
        }
        catch (Exception failure)
        {
            // Includes failures restoring PAGE after the pair was otherwise successful.
            // An unread mailbox baseline never permits target writes or invented rollback.
            if (rolledBack) throw;
            RollBack(failure);
        }
    }
}
