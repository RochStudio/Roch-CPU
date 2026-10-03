namespace RochPower.Hardware;

/// <summary>
/// A historical PMIC ADC observation, captured only during existing calibration/verification.
/// It is never a voltage target and reading this record performs no hardware access.
/// A failed latest attempt retains the previous successful observation for diagnostic context.
/// </summary>
public sealed record PmicVoltageMeasurement(
    double? LastSuccessfulVolts,
    DateTimeOffset? SampledAtUtc,
    DateTimeOffset? LastAttemptAtUtc,
    string? LastError,
    string Source)
{
    public bool LatestAttemptSucceeded => LastError == null && LastSuccessfulVolts.HasValue && SampledAtUtc.HasValue;
    public static PmicVoltageMeasurement NotRead(string source) =>
        new(null, null, null, "No PMIC ADC sample has been recorded.", source);
}
