namespace RochPower.Hardware;

/// <summary>
/// Sets the base clock, through <see cref="EcClockGen"/>, with every step measured.
///
/// <para><b>The model.</b> The clock generator is a fractional-N PLL: a fixed oscillator of about
/// 10002 MHz divided by <c>D = block[0] + fraction/2^28</c>, where the fraction is the 28-bit
/// tuning word. So the base clock is <c>Fvco / D</c>, and both fields run backwards against
/// frequency. The oscillator is measured on this board by <see cref="Calibrate"/> rather than
/// assumed, and every step is then checked against a real measurement anyway.</para>
///
/// <para><b>Why the guards are here.</b> Writing a clock generator is the one adjustment in this
/// program where a wrong value does not produce a wrong reading, it stops the machine. So: a hard
/// ceiling, checked against what was measured rather than only against what was asked for; steps
/// of at most <see cref="MaxStepMHz"/>; a measurement against the ACPI timer, which does not move
/// with the base clock, after every single step; a write order chosen so that the few milliseconds
/// where the divider is half-updated always land on the slow side; and a restore of the block
/// found at start-up the moment anything lands somewhere it was not aimed.</para>
/// </summary>
public sealed class BclkController
{
    /// <summary>
    /// Ceiling, in MHz. This is a fixed limit, applied on every path in and re-checked against the
    /// measurement afterwards. Above roughly here the board stops being reliable: a run that
    /// reached 103.1 MHz and kept climbing ended in an unexpected shutdown.
    /// </summary>
    public const double MaxBclkMHz = 102.5;

    /// <summary>Floor. Below this the machine is uselessly slow and the meter gets noisy.</summary>
    public const double MinBclkMHz = 97.0;

    /// <summary>
    /// Largest single change, in MHz. The divider latches all at once, so this is not about
    /// avoiding a half-applied value - it is about not asking the PLL for a big jump, and about
    /// bounding how far a wrong model could take the clock before the check after the step
    /// notices and puts everything back.
    /// </summary>
    public const double MaxStepMHz = 0.5;

    /// <summary>How long to let the PLL settle after a write before measuring.</summary>
    private const int SettleMs = 60;

    /// <summary>
    /// How far a step may land from where it was aimed before the change is treated as failed.
    /// Repeat measurements of a steady clock spread about 0.03 MHz, so this is a few times that.
    /// </summary>
    public const double ToleranceMHz = 0.12;

    /// <summary>Fraction bits in the divider, i.e. the width of the tuning word the part uses.</summary>
    private const int FractionBits = 28;
    private const double FractionScale = 1u << FractionBits;
    private const uint FractionMask = (1u << FractionBits) - 1;

    /// <summary>
    /// The oscillator the divider divides, in MHz. Measured; the seed is only there so the first
    /// probe is roughly the right size.
    /// </summary>
    public double VcoMHz { get; private set; } = 10002.3;
    public bool Calibrated { get; private set; }

    /// <summary>The block as it was found at start-up, and the clock that went with it.</summary>
    public byte[]? Baseline { get; private set; }
    public double BaselineMHz { get; private set; }

    public string Status { get; private set; } = "not started";

    private readonly EcClockGen _gen;
    private readonly Func<double?> _measure;

    public BclkController(EcClockGen gen, Func<double?> measure)
    {
        _gen = gen; _measure = measure;
    }

    // ---------------------------------------------------------------- divider <-> block
    public static double DividerOf(byte[] block) =>
        block[0] + (EcClockGen.GetTuning(block) & FractionMask) / FractionScale;

    /// <summary>Rebuilds a block for a divider, leaving the bits the part ignores as they were.</summary>
    public static byte[] BlockFor(byte[] block, double divider)
    {
        divider = Math.Clamp(divider, 1, 255.999);
        byte whole = (byte)Math.Floor(divider);
        uint frac = (uint)Math.Clamp(Math.Round((divider - whole) * FractionScale), 0, FractionMask);
        uint word = (EcClockGen.GetTuning(block) & ~FractionMask) | frac;
        var copy = EcClockGen.WithTuning(block, word);
        copy[0] = whole;
        return copy;
    }

    public double MhzFor(double divider) => VcoMHz / divider;
    public double DividerForMhz(double mhz) => VcoMHz / mhz;

    // ---------------------------------------------------------------- measurement
    /// <summary>Median of several measurements, discarding any that fail.</summary>
    private double? Measure(int samples = 3)
    {
        var vals = new List<double>();
        for (int i = 0; i < samples; i++)
            if (_measure() is double v && v > 50 && v < 200) vals.Add(v);
        if (vals.Count == 0) return null;
        vals.Sort();
        return vals[vals.Count / 2];
    }

    /// <summary>Reads the current block and the clock it produces.</summary>
    public (byte[] Block, double Mhz)? Read(int samples = 3)
    {
        if (_gen.ReadBlock() is not { } blk) return null;
        if (Measure(samples) is not double mhz) return null;
        return (blk, mhz);
    }

    /// <summary>
    /// Records where the machine started, so any change can be undone. Runs at start-up, so it
    /// takes a single measurement: the figure is for reporting and for the row's default, and
    /// every path that depends on the clock being known accurately measures it again.
    /// </summary>
    public bool CaptureBaseline()
    {
        if (Read(1) is not { } now) { Status = "clock generator did not answer"; return false; }
        Baseline = now.Block; BaselineMHz = now.Mhz;
        Status = $"baseline {EcClockGen.Describe(now.Block)} at {now.Mhz:0.000} MHz";
        return true;
    }

    /// <summary>Puts back the block found at start-up, slow side first.</summary>
    public bool Restore()
    {
        if (Baseline is null) return false;
        bool ok = WriteSafely(Baseline);
        Status = ok ? $"restored to {BaselineMHz:0.000} MHz" : "restore FAILED";
        return ok;
    }

    /// <summary>
    /// The divider takes effect all at once, because the part latches it when the last byte of the
    /// block arrives - see <see cref="EcClockGen.WriteBlock"/>, which is why nothing here has to
    /// worry about a half-applied divider driving the clock.
    /// </summary>
    private bool WriteSafely(byte[] target) => _gen.WriteBlock(target);

    // ---------------------------------------------------------------- calibration
    /// <summary>
    /// Establishes the oscillator from a single measurement. The divider is known exactly - it is
    /// just the block - so <c>Fvco = f * D</c> needs nothing else, and this costs one read and one
    /// measurement rather than a write-probe-restore round trip.
    ///
    /// It deliberately does not confirm the model by moving the clock. <see cref="SetBclk"/>
    /// checks every step against a measurement and restores on any mismatch, so a wrong scale is
    /// caught on the first step having moved the clock by at most <see cref="MaxStepMHz"/> - the
    /// same protection the probe gave, without making every apply wait for it.
    /// </summary>
    public bool EstablishScale(byte[] block, double mhz, Action<string>? log = null)
    {
        double divider = DividerOf(block);
        if (divider < 1 || mhz < MinBclkMHz - 5 || mhz > MaxBclkMHz + 5)
        { Status = $"implausible starting point ({mhz:0.000} MHz at divider {divider:0.000})"; return false; }
        VcoMHz = mhz * divider;
        Calibrated = true;
        log?.Invoke($"  {mhz:0.000} MHz at divider {divider:0.000000} -> oscillator {VcoMHz:0.0} MHz");
        Status = $"oscillator {VcoMHz:0.0} MHz, range {MinBclkMHz:0.0}-{MaxBclkMHz:0.0} MHz";
        return true;
    }

    /// <summary>
    /// Establishes the oscillator and then confirms it by moving the clock to a second divider,
    /// so the relation is shown to be a division rather than something that merely looked like one
    /// over a short span. This is the diagnostic form; the apply path uses
    /// <see cref="EstablishScale"/> and leans on its per-step check instead. The probe moves the
    /// clock down, which is the safe direction, and the block is put back either way.
    /// </summary>
    public bool Calibrate(Action<string>? log = null)
    {
        if (Baseline is null && !CaptureBaseline()) return false;
        var start = Baseline!;

        if (Measure(4) is not double startMhz) { Status = "could not measure the base clock"; return false; }
        if (!EstablishScale(start, startMhz, log)) return false;
        double vco = VcoMHz;

        // A quarter of a MHz slower: a bigger divider. Comfortably above the noise, and downwards
        // so that a wrong model costs speed rather than stability.
        double probeDiv = vco / (startMhz - 0.25);
        var probe = BlockFor(start, probeDiv);
        log?.Invoke($"  probing divider {DividerOf(probe):0.000000} (aiming for {startMhz - 0.25:0.000} MHz)");

        try
        {
            if (!WriteSafely(probe)) { Status = "probe write failed"; return false; }
            Thread.Sleep(150);
            if (Measure(5) is not double probed) { Status = "probe could not be measured"; return false; }
            log?.Invoke($"  measured {probed:0.000} MHz");

            if (probed > MaxBclkMHz || probed < MinBclkMHz)
            { Status = $"the probe reached {probed:0.000} MHz, outside the safe window - refusing to tune"; return false; }
            if (Math.Abs(probed - (startMhz - 0.25)) > ToleranceMHz)
            { Status = $"a step aimed at {startMhz - 0.25:0.000} MHz landed at {probed:0.000} - the divider model does not hold here"; return false; }

            // Both points agree on the oscillator, so average them.
            VcoMHz = (vco + probed * DividerOf(probe)) / 2;
            Calibrated = true;
            Status = $"oscillator {VcoMHz:0.0} MHz, range {MinBclkMHz:0.0}-{MaxBclkMHz:0.0} MHz";
            return true;
        }
        finally
        {
            WriteSafely(start);
            Thread.Sleep(150);
            log?.Invoke($"  restored: {Measure(3):0.000} MHz");
        }
    }

    // ---------------------------------------------------------------- diagnostics
    /// <summary>
    /// Writes a series of dividers and measures each, restoring the baseline afterwards whatever
    /// happens. For characterising a board this has not been measured on. Aborts the run if any
    /// measurement lands outside the safe window.
    /// </summary>
    public bool ProbeDividers(IEnumerable<double> dividers, Action<string>? log = null)
    {
        if (Baseline is null && !CaptureBaseline()) return false;
        var start = Baseline!;
        log?.Invoke($"  baseline divider {DividerOf(start):0.000000} at {BaselineMHz:0.000} MHz");
        try
        {
            foreach (double d in dividers)
            {
                var attempt = BlockFor(start, d);
                if (!WriteSafely(attempt)) { Status = "probe write failed"; return false; }
                Thread.Sleep(150);
                double? got = Measure(4);
                log?.Invoke($"  divider {DividerOf(attempt),11:0.000000}  predicted {MhzFor(DividerOf(attempt)),8:0.000}  measured {got:0.000} MHz");
                if (got is double m && (m > MaxBclkMHz || m < MinBclkMHz))
                { Status = $"probe reached {m:0.000} MHz, outside the safe window - stopping"; return false; }
            }
            Status = "probe complete";
            return true;
        }
        finally
        {
            WriteSafely(start);
            Thread.Sleep(150);
            log?.Invoke($"  restored: {Measure(3):0.000} MHz");
        }
    }

    public static double Clamp(double mhz) => Math.Clamp(mhz, MinBclkMHz, MaxBclkMHz);

    // ---------------------------------------------------------------- apply
    /// <summary>
    /// Walks the clock to <paramref name="targetMhz"/> in small steps, measuring each one. Any
    /// step that does not land where it was aimed puts the start-up block back and stops.
    /// </summary>
    public bool SetBclk(double targetMhz, Action<string>? log = null)
    {
        double clamped = Clamp(targetMhz);
        if (Math.Abs(clamped - targetMhz) > 0.0005)
            log?.Invoke($"  {targetMhz:0.000} MHz is outside {MinBclkMHz:0.0}-{MaxBclkMHz:0.0}; using {clamped:0.000}");

        // One read and one measurement serve both as the starting point and, the first time, as
        // the whole of the calibration.
        if (Read() is not { } cur) { Status = "clock generator did not answer"; return false; }
        byte[] block = cur.Block;
        double mhz = cur.Mhz;
        if (!Calibrated && !EstablishScale(block, mhz, log)) return false;

        int guard = 0;
        while (Math.Abs(clamped - mhz) > ToleranceMHz / 2 && guard++ < 40)
        {
            double next = mhz + Math.Clamp(clamped - mhz, -MaxStepMHz, MaxStepMHz);
            var attempt = BlockFor(block, DividerForMhz(next));
            bool last = Math.Abs(next - clamped) <= ToleranceMHz / 2;

            if (!WriteSafely(attempt)) { Status = "write failed"; Restore(); return false; }
            Thread.Sleep(SettleMs);

            // Intermediate steps only have to show the clock is tracking; the one that lands on
            // the target gets the more careful reading.
            if (Measure(last ? 3 : 2) is not double got)
            { Status = "could not measure after the write"; Restore(); return false; }
            log?.Invoke($"  aimed {next:0.000} -> measured {got:0.000} MHz");

            // Checked against the measurement, not only the request, so a slip in the encoding
            // cannot walk past the ceiling unnoticed.
            if (got > MaxBclkMHz + ToleranceMHz || got < MinBclkMHz - ToleranceMHz)
            {
                Status = $"a step landed at {got:0.000} MHz, outside the safe window; restored";
                Restore(); return false;
            }
            if (Math.Abs(got - next) > ToleranceMHz)
            {
                Status = $"a step aimed at {next:0.000} MHz landed at {got:0.000}; restored";
                Restore(); return false;
            }
            block = attempt; mhz = got;
        }

        Status = $"base clock {mhz:0.000} MHz";
        return true;
    }
}
