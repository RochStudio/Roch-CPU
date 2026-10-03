using RochPower.Hardware;

namespace RochPower.Core;

public sealed partial class HardwareModel
{
    private object? _coreVoltageInitializationLock;
    private delegate MsiCoreVoltage? CoreVoltageStartupFactory(EcMailbox mailbox,
        out CorePageVerificationResult result);
    public CorePageVerificationResult? CoreVoltageInitializationResult { get; private set; }

    public string CoreVoltageTargetReadStatus => CoreVoltage?.TargetReadStatus ?? CoreVoltageStatus;
    public bool CoreVoltageTargetReadFailed => CoreVoltage?.TargetReadFailed ?? false;

    /// <summary>
    /// One bounded startup attempt. Reuses the existing identified EC/driver; no discovery,
    /// driver lifecycle, Intel-mailbox command, voltage or mode write occurs here.
    /// A failed snapshot or PAGE restoration leaves the control disabled. Later calls return
    /// the same recorded result without accessing hardware; refresh never invokes this method.
    /// </summary>
    public CorePageVerificationResult InitializeCoreVoltage() =>
        InitializeCoreVoltageCore(MsiCoreVoltage.TryCreateForStartup);

    // The private seam permits inert tests to substitute an already-isolated fake controller.
    // Production always uses the fixed factory above; capability gates and once caching are shared.
    private CorePageVerificationResult InitializeCoreVoltageCore(CoreVoltageStartupFactory startupFactory)
    {
        lock (LazyInitializer.EnsureInitialized(ref _coreVoltageInitializationLock))
        {
            if (CoreVoltageInitializationResult is { } recorded) return recorded;
            string? restriction = Driver is not { IsOpen: true } ? "Hardware access is unavailable."
                : Cpu == null ? "An Intel CPU is required."
                : VoltageAccessRestriction != null ? VoltageAccessRestriction
                : !MailboxAvailable ? "The Intel OC mailbox is unavailable."
                : SuperIo is not { Kind: SuperIoKind.NuvotonEc } ? "A supported MSI embedded controller is unavailable."
                : !(Smbios.BoardManufacturer.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase)
                    || Smbios.SystemManufacturer.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase))
                    ? "The MSI board identity is not confirmed." : null;

            CorePageVerificationResult result;
            if (restriction != null)
                result = new(false, null, null, null, restriction, false, false);
            else
                CoreVoltage = startupFactory(new EcMailbox(SuperIo!), out result);

            CoreVoltageInitializationResult = result;
            CoreVoltageStatus = result.Succeeded ? "Core voltage ready." : "Core voltage unavailable: " + result.Status;
            Emit("CPU core regulator startup: " + result.Status + (result.Succeeded ? " Editable target enabled." : " Control left disabled."));
            foreach (string line in result.DiagnosticLines)
            {
                try { Emit("CPU core startup I/O: " + line); }
                catch { } // Logging must not affect the completed hardware operation.
            }
            return result;
        }
    }

    private void WriteMsiCoreOverride(double volts)
    {
        var regulator = CoreVoltage ?? throw new IOException("Verified MSI core regulator is unavailable.");
        var mailbox = Cpu?.Mailbox ?? throw new IOException("Intel OC mailbox is unavailable.");
        CoreVoltagePairTransaction.Execute(regulator,
            () => mailbox.ReadDomain(OcMailbox.DOMAIN_CORE),
            () => regulator.SetOverride(volts),
            () => mailbox.SetOverride(OcMailbox.DOMAIN_CORE, volts),
            () =>
            {
                var after = mailbox.ReadDomain(OcMailbox.DOMAIN_CORE);
                if (!after.OverrideMode || Math.Abs(after.TargetVolts - volts) > 0.0011)
                    throw new IOException($"Intel mailbox did not retain {volts:0.000} V (read back {after.TargetVolts:0.000} V)");
            },
            before => mailbox.WriteDomain(OcMailbox.DOMAIN_CORE, before));
    }

    private void ClearMsiCoreOverride()
    {
        var regulator = CoreVoltage ?? throw new IOException("Verified MSI core regulator is unavailable.");
        var mailbox = Cpu?.Mailbox ?? throw new IOException("Intel OC mailbox is unavailable.");
        CoreVoltagePairTransaction.Execute(regulator,
            () => mailbox.ReadDomain(OcMailbox.DOMAIN_CORE), regulator.DisableOverride,
            () => mailbox.SetOverride(OcMailbox.DOMAIN_CORE, 0),
            () =>
            {
                if (mailbox.ReadDomain(OcMailbox.DOMAIN_CORE).OverrideMode)
                    throw new IOException("Intel mailbox remained in override mode");
            },
            before => mailbox.WriteDomain(OcMailbox.DOMAIN_CORE, before));
    }
}
