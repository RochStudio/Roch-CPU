using RochPower.Core;
using RochPower.Hardware;

namespace RochPower.UI;

/// <summary>
/// Per-core Curve Optimizer editor: the voltage/frequency curve offset the SMU applies to each
/// core, in counts (one count is roughly 3 to 5 mV). Negative undervolts. The core numbering
/// follows the SMU's physical map (CCD, core-in-CCD), which is what the BIOS shows too.
/// </summary>
public sealed class CurveOptimizerForm : Form
{
    private readonly HardwareModel _hw;
    private readonly AmdCpu _cpu;
    private readonly List<(AmdCore core, TextBox box, Label status)> _rows = new();
    private readonly Label _status = Theme.Label("", Theme.Small, Theme.Muted);

    public CurveOptimizerForm(HardwareModel hw)
    {
        _hw = hw;
        _cpu = hw.Amd ?? throw new InvalidOperationException("Curve Optimizer needs an AMD CPU.");
        int range = _cpu.Smu.Messages.CoRange;
        Text = "Curve Optimizer";
        Icon = Theme.LoadAppIcon();
        Theme.ApplyDark(this);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        int cols = _cpu.Cores.Count > 8 ? 2 : 1;
        ClientSize = new Size(cols == 2 ? 620 : 400, 150 + 30 * (int)Math.Ceiling(_cpu.Cores.Count / (double)cols));

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = cols, BackColor = Theme.Bg, Padding = new Padding(14) };
        for (int c = 0; c < cols; c++) root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / cols));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var intro = Theme.Muted_($"Offset per core in counts, {-range} to +{range}. Negative lowers the voltage the core asks for at every frequency. " +
                                 (_cpu.Smu.Messages.HasCurveOptimizerReadback ? "Values are read back from the SMU." : "This SMU cannot report the current values; the fields show what was last applied here."));
        intro.MaximumSize = new Size(ClientSize.Width - 28, 0);
        root.Controls.Add(intro, 0, 0); root.SetColumnSpan(intro, cols);

        var perCol = (int)Math.Ceiling(_cpu.Cores.Count / (double)cols);
        for (int c = 0; c < cols; c++)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true, BackColor = Theme.Bg };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            foreach (var core in _cpu.Cores.Skip(c * perCol).Take(perCol))
            {
                int r = t.RowCount++;
                var name = Theme.Label(core.Label, Theme.Row); name.Margin = new Padding(0, 6, 8, 0);
                var loc = Theme.Muted_(core.Location); loc.Margin = new Padding(0, 7, 8, 0);
                var frame = Theme.ValueBox(out var box, 64); frame.Margin = new Padding(0, 2, 12, 2);
                t.Controls.Add(name, 0, r); t.Controls.Add(loc, 1, r); t.Controls.Add(frame, 2, r);
                _rows.Add((core, box, loc));
            }
            root.Controls.Add(t, c, 1);
        }

        _status.Margin = new Padding(0, 6, 0, 6);
        root.Controls.Add(_status, 0, 2); root.SetColumnSpan(_status, cols);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, BackColor = Color.Transparent };
        var apply = Theme.Button("Apply", primary: true); apply.Width = 90; apply.AutoSize = false; apply.Height = 30;
        var close = Theme.Button("Close"); close.Width = 80; close.AutoSize = false; close.Height = 30;
        var refresh = Theme.Button("Refresh"); refresh.Width = 80; refresh.AutoSize = false; refresh.Height = 30;
        var sync = Theme.Button("Same for all"); sync.Height = 30; sync.AutoSize = false; sync.Width = 100;
        apply.Click += (_, _) => Apply();
        close.Click += (_, _) => Close();
        refresh.Click += (_, _) => Fill();
        sync.Click += (_, _) => { foreach (var (_, b, _) in _rows.Skip(1)) b.Text = _rows[0].box.Text; };
        buttons.Controls.Add(close); buttons.Controls.Add(apply); buttons.Controls.Add(refresh); buttons.Controls.Add(sync);
        root.Controls.Add(buttons, 0, 3); root.SetColumnSpan(buttons, cols);
        Controls.Add(root);

        Load += (_, _) => Fill();
    }

    private void Fill()
    {
        int unread = 0;
        foreach (var (core, box, _) in _rows)
        {
            int? m = _hw.ReadCurveOptimizer(core);
            if (m is int v) box.Text = v.ToString();
            else { unread++; if (box.Text.Length == 0) box.Text = "0"; }
        }
        _status.ForeColor = Theme.Muted;
        _status.Text = unread == 0 ? "Values read from the SMU." : _cpu.Smu.Messages.HasCurveOptimizerReadback
            ? $"{unread} core(s) did not answer; those fields show the last value applied here."
            : "No read-back on this SMU; fields show the last value applied here (0 if none).";
    }

    private void Apply()
    {
        int range = _cpu.Smu.Messages.CoRange;
        int ok = 0, failed = 0;
        foreach (var (core, box, status) in _rows)
        {
            if (!int.TryParse(box.Text.Trim(), out int margin)) { status.Text = "not a number"; status.ForeColor = Theme.Danger; failed++; continue; }
            if (margin < -range || margin > range) { status.Text = $"outside {-range}..{range}"; status.ForeColor = Theme.Danger; failed++; continue; }
            try
            {
                _hw.ApplyCurveOptimizer(core, margin);
                status.Text = core.Location; status.ForeColor = Theme.Muted; ok++;
            }
            catch (Exception ex) { status.Text = ex.Message; status.ForeColor = Theme.Danger; failed++; }
        }
        _status.Text = failed == 0 ? $"Applied {ok} core(s) at {DateTime.Now:HH:mm:ss}." : $"{ok} applied, {failed} failed.";
        _status.ForeColor = failed == 0 ? Theme.Muted : Theme.Danger;
        if (_cpu.Smu.Messages.HasCurveOptimizerReadback) Fill();
    }
}
