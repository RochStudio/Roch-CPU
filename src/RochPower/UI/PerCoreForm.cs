using RochPower.Core;

namespace RochPower.UI;

/// <summary>
/// Turbo ratio table editor: the multiplier the CPU may use for each number of
/// active cores (MSR 0x1AD/0x1AE for P-cores, 0x650/0x651 for E-cores).
/// </summary>
public sealed class PerCoreForm : Form
{
    private readonly HardwareModel _hw;
    private readonly TextBox[] _p = new TextBox[8];
    private readonly TextBox[] _e = new TextBox[8];
    private readonly Label[] _pLabels = new Label[8];
    private readonly Label[] _eLabels = new Label[8];
    private readonly Label _status = Theme.Label("", Theme.Small, Theme.Muted);

    public PerCoreForm(HardwareModel hw)
    {
        _hw = hw;
        Text = "Per-core ratio table";
        Icon = Theme.LoadAppIcon();
        Theme.ApplyDark(this);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 420);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Theme.Bg, Padding = new Padding(14) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var intro = Theme.Muted_("The ratio the CPU may reach with N cores active. The last group is the all-core limit.");
        root.Controls.Add(intro, 0, 0); root.SetColumnSpan(intro, 2);
        root.Controls.Add(Theme.SectionTitle("P-cores"), 0, 1);
        root.Controls.Add(Theme.SectionTitle("E-cores"), 1, 1);
        root.Controls.Add(BuildTable(_p, _pLabels), 0, 2);
        root.Controls.Add(BuildTable(_e, _eLabels), 1, 2);
        _status.Margin = new Padding(0, 6, 0, 6);
        root.Controls.Add(_status, 0, 3); root.SetColumnSpan(_status, 2);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, BackColor = Color.Transparent };
        var apply = Theme.Button("Apply", primary: true); apply.Width = 90; apply.AutoSize = false; apply.Height = 30;
        var close = Theme.Button("Close"); close.Width = 80; close.AutoSize = false; close.Height = 30;
        var sync = Theme.Button("Same for all"); sync.Height = 30; sync.AutoSize = false; sync.Width = 100;
        apply.Click += (_, _) => Apply();
        close.Click += (_, _) => Close();
        sync.Click += (_, _) => { foreach (var t in _p.Skip(1)) t.Text = _p[0].Text; foreach (var t in _e.Skip(1)) t.Text = _e[0].Text; };
        buttons.Controls.Add(close); buttons.Controls.Add(apply); buttons.Controls.Add(sync);
        root.Controls.Add(buttons, 0, 4); root.SetColumnSpan(buttons, 2);
        Controls.Add(root);

        Load += (_, _) => Fill();
    }

    private static TableLayoutPanel BuildTable(TextBox[] boxes, Label[] labels)
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, BackColor = Theme.Bg };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int i = 0; i < 8; i++)
        {
            labels[i] = Theme.Label($"group {i + 1}", Theme.Row); labels[i].Margin = new Padding(0, 6, 0, 0);
            var frame = Theme.ValueBox(out boxes[i], 64); frame.Margin = new Padding(0, 2, 12, 2);
            t.Controls.Add(labels[i], 0, i);
            t.Controls.Add(frame, 1, i);
        }
        return t;
    }

    private void Fill()
    {
        var cpu = _hw.Cpu!;
        try
        {
            var (pr, pc) = cpu.ReadPCoreTurboTable();
            for (int i = 0; i < 8; i++) { _p[i].Text = pr[i].ToString(); _pLabels[i].Text = $"{pc[i]} P-core{(pc[i] == 1 ? "" : "s")} active"; }
            if (cpu.ECoreCount > 0)
            {
                var (er, ec) = cpu.ReadECoreTurboTable();
                for (int i = 0; i < 8; i++) { _e[i].Text = er[i].ToString(); _eLabels[i].Text = $"{ec[i]} E-core{(ec[i] == 1 ? "" : "s")} active"; }
            }
            else for (int i = 0; i < 8; i++) { _e[i].Text = "N/A"; _e[i].Enabled = false; _eLabels[i].ForeColor = Theme.Muted; }
            _status.Text = "Values read from the CPU.";
        }
        catch (Exception ex) { _status.Text = ex.Message; _status.ForeColor = Theme.Danger; }
    }

    private void Apply()
    {
        var cpu = _hw.Cpu!;
        try
        {
            int[] pr = _p.Select(b => int.TryParse(b.Text, out int v) ? Math.Clamp(v, 0, 255) : 0).ToArray();
            if (pr.Any(v => v == 0)) throw new InvalidOperationException("Every P-core group needs a ratio between 8 and 120.");
            cpu.WritePCoreTurboTable(pr);
            if (cpu.ECoreCount > 0)
            {
                int[] er = _e.Select(b => int.TryParse(b.Text, out int v) ? Math.Clamp(v, 0, 255) : 0).ToArray();
                if (er.Any(v => v == 0)) throw new InvalidOperationException("Every E-core group needs a ratio between 8 and 120.");
                cpu.WriteECoreTurboTable(er);
            }
            Fill();
            _status.Text = $"Applied at {DateTime.Now:HH:mm:ss}."; _status.ForeColor = Theme.Muted;
        }
        catch (Exception ex) { _status.Text = ex.Message; _status.ForeColor = Theme.Danger; }
    }
}
