namespace RochPower.Hardware;

/// <summary>
/// CPU VDD2 - the memory controller's supply - set through the regulator on the EC's I2C bus.
///
/// <para>The path is one byte: register 0x01 of the device at 0x20, captured from the vendor tool
/// changing that rail. Register 0x05 reads as a status byte first, which the vendor tool does and
/// this follows. The value is a step count, measured at <see cref="StepVolts"/> per count against
/// the Super I/O's own reading of the rail.</para>
///
/// <para><b>This is a regulator, so the value is clamped in volts and everything is relative.</b>
/// The raw byte reaches well past 3 V at the top of its range, which would destroy the memory
/// controller, so nothing here computes an absolute code from an assumed zero point. The current
/// code is read, the current rail is measured, and the target is reached by stepping the code and
/// checking the rail after each step - the same discipline as the base clock, and with the same
/// consequence for a step that does not land where it was aimed: put the start-up value back and
/// stop. On top of that the request is clamped to <see cref="MinVolts"/>..<see cref="MaxVolts"/>
/// and the code may never move more than <see cref="MaxStepsFromBaseline"/> from where it started,
/// so even a wrong scale cannot walk the rail away.</para>
/// </summary>
public sealed class Vdd2Rail
{
    /// <summary>The regulator's I2C address in the 8-bit form the mailbox wants (7-bit 0x10).</summary>
    public const byte Address = 0x20;
    private const byte R_STATUS = 0x05, R_VOLTAGE = 0x01;

    /// <summary>
    /// Volts per code step, measured: the vendor tool moving this rail by two counts moved the
    /// measured rail by 20 mV. <see cref="Calibrate"/> re-measures it here rather than trusting it.
    /// </summary>
    public const double StepVolts = 0.010;

    /// <summary>
    /// Clamp. Stock on the board this was measured on is about 1.37 V. DDR5 memory-controller
    /// supplies live in a narrow band and there is no reason to go outside it from here.
    /// </summary>
    public const double MinVolts = 1.100, MaxVolts = 1.450;

    /// <summary>Hard limit on how far the code may move from where it was found at start-up.</summary>
    public const int MaxStepsFromBaseline = 20;

    /// <summary>
    /// How far the rail may land from where it was aimed before a step counts as failed. The
    /// Super I/O reads this rail through a divide-by-two input, so its quantum is a few mV, and
    /// the regulator's own tolerance adds to that.
    /// </summary>
    public const double ToleranceVolts = 0.012;

    /// <summary>
    /// How long to wait after a write before the rail reading means anything. The Super I/O
    /// samples its voltage channels on its own schedule, and it lags a change on this rail by
    /// several hundred milliseconds: measuring at 150 ms read the old value, and the *next*
    /// reading then showed the previous step, which looks exactly like a step that did nothing.
    /// </summary>
    private const int SettleMs = 700;

    private readonly EcMailbox _mb;
    private readonly Func<double?> _measure;

    /// <summary>The code found at start-up, and the rail that went with it.</summary>
    public byte? BaselineCode { get; private set; }
    public double BaselineVolts { get; private set; }
    public double MeasuredStepVolts { get; private set; } = StepVolts;
    public bool Calibrated { get; private set; }
    public string Status { get; private set; } = "not started";

    public Vdd2Rail(EcMailbox mailbox, Func<double?> measureRail)
    {
        _mb = mailbox; _measure = measureRail;
    }

    public static bool IsSupported(SuperIo? sio) => EcMailbox.IsSupported(sio);

    // ---------------------------------------------------------------- access
    public byte? ReadCode()
    {
        _mb.ReadByte(Address, R_STATUS);   // the vendor tool reads this first; harmless, and kept
        return _mb.ReadByte(Address, R_VOLTAGE);
    }

    private bool WriteCode(byte code) => _mb.WriteByte(Address, R_VOLTAGE, code);

    /// <summary>Median of several rail readings; the Super I/O value wanders by a millivolt or two.</summary>
    private double? MeasureRail(int samples = 5)
    {
        var v = new List<double>();
        for (int i = 0; i < samples; i++)
        {
            if (_measure() is double r && r > 0.5 && r < 3.0) v.Add(r);
            Thread.Sleep(40);
        }
        if (v.Count == 0) return null;
        v.Sort();
        return v[v.Count / 2];
    }

    public bool CaptureBaseline()
    {
        if (ReadCode() is not byte code) { Status = "the VDD2 regulator did not answer"; return false; }
        if (MeasureRail(3) is not double v) { Status = "the VDD2 rail could not be measured"; return false; }
        BaselineCode = code; BaselineVolts = v;
        Status = $"code 0x{code:X2}, rail {v:0.000} V";
        return true;
    }

    public bool Restore()
    {
        if (BaselineCode is not byte code) return false;
        bool ok = WriteCode(code);
        Status = ok ? $"restored to {BaselineVolts:0.000} V" : "restore FAILED";
        return ok;
    }

    /// <summary>
    /// Measures volts per code step by moving one count down and watching the rail. Downwards is
    /// the safe direction, and a single count is the smallest move the regulator can make.
    /// </summary>
    public bool Calibrate(Action<string>? log = null)
    {
        if (BaselineCode is null && !CaptureBaseline()) return false;
        byte start = BaselineCode!.Value;
        if (start == 0) { Status = "VDD2 code is already at zero"; return false; }
        if (MeasureRail() is not double v0) { Status = "the VDD2 rail could not be measured"; return false; }
        log?.Invoke($"  baseline code 0x{start:X2}, rail {v0:0.000} V");

        try
        {
            if (!WriteCode((byte)(start - 1))) { Status = "probe write failed"; return false; }
            Thread.Sleep(SettleMs);
            if (MeasureRail() is not double v1) { Status = "the probe could not be measured"; return false; }
            double delta = v0 - v1;
            log?.Invoke($"  one count down -> {v1:0.000} V (a step of {delta * 1000:0.0} mV)");

            if (delta < 0.004 || delta > 0.030)
            { Status = $"a one-count step moved the rail {delta * 1000:0.0} mV, which is not the expected scale - refusing to set VDD2"; return false; }

            MeasuredStepVolts = delta;
            Calibrated = true;
            Status = $"{delta * 1000:0.0} mV per step, range {MinVolts:0.000}-{MaxVolts:0.000} V";
            return true;
        }
        finally
        {
            WriteCode(start);
            Thread.Sleep(SettleMs);
            log?.Invoke($"  restored: {MeasureRail(3):0.000} V");
        }
    }

    public static double Clamp(double volts) => Math.Clamp(volts, MinVolts, MaxVolts);

    /// <summary>
    /// Walks the rail to <paramref name="targetVolts"/> one code step at a time, measuring after
    /// every step. Anything that lands off target puts the start-up code back and stops.
    /// </summary>
    public bool SetVolts(double targetVolts, Action<string>? log = null)
    {
        if (!Calibrated && !Calibrate(log)) return false;
        if (BaselineCode is not byte baseline) return false;

        double want = Clamp(targetVolts);
        if (Math.Abs(want - targetVolts) > 0.0005)
            log?.Invoke($"  {targetVolts:0.000} V is outside {MinVolts:0.000}-{MaxVolts:0.000}; using {want:0.000}");

        if (ReadCode() is not byte code) { Status = "the VDD2 regulator did not answer"; return false; }
        if (MeasureRail() is not double volts) { Status = "the VDD2 rail could not be measured"; return false; }

        int guard = 0;
        while (Math.Abs(want - volts) > MeasuredStepVolts * 0.6 && guard++ < 2 * MaxStepsFromBaseline)
        {
            int dir = want > volts ? 1 : -1;
            int next = code + dir;
            if (next < 0 || next > 0xFF) { Status = "the VDD2 code is at the end of its range"; break; }
            if (Math.Abs(next - baseline) > MaxStepsFromBaseline)
            { Status = $"stopped {MaxStepsFromBaseline} steps from where VDD2 started, which is as far as this will go"; break; }

            double aim = volts + dir * MeasuredStepVolts;
            if (!WriteCode((byte)next)) { Status = "write failed"; Restore(); return false; }
            Thread.Sleep(SettleMs);

            if (MeasureRail() is not double got)
            { Status = "could not measure the rail after the write"; Restore(); return false; }
            log?.Invoke($"  code 0x{next:X2}: aimed {aim:0.000} -> measured {got:0.000} V");

            // Checked against the measurement, not only the request, so a wrong scale cannot walk
            // the rail past the clamp unnoticed.
            if (got > MaxVolts + ToleranceVolts || got < MinVolts - ToleranceVolts)
            { Status = $"a step took the rail to {got:0.000} V, outside the safe window; restored"; Restore(); return false; }
            if (Math.Abs(got - aim) > ToleranceVolts)
            { Status = $"a step aimed at {aim:0.000} V landed at {got:0.000}; restored"; Restore(); return false; }

            code = (byte)next; volts = got;
        }

        Status = $"CPU VDD2 {volts:0.000} V (code 0x{code:X2})";
        return true;
    }
}
