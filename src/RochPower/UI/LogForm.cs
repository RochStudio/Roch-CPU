namespace RochPower.UI;

/// <summary>The log in its own window, like the Roch GPU "Log" button. Hidden rather than closed.</summary>
public sealed class LogForm : Form
{
    private readonly TextBox _text = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        BackColor = Theme.Panel, ForeColor = Theme.Text, Font = Theme.Mono, BorderStyle = BorderStyle.None
    };

    public bool AllowClose { get; set; }

    public LogForm()
    {
        Text = MainForm.AppName + " - Log";
        Icon = Theme.LoadAppIcon();
        Theme.ApplyDark(this);
        StartPosition = FormStartPosition.Manual;
        Size = new Size(620, 420);
        ShowInTaskbar = false;
        Padding = new Padding(8);
        var copy = Theme.Button("Copy");
        copy.Dock = DockStyle.Bottom; copy.AutoSize = false; copy.Height = 28; copy.Margin = new Padding(0);
        copy.Click += (_, _) => { try { Clipboard.SetText(_text.Text); } catch { } };
        Controls.Add(_text);
        Controls.Add(copy);
        FormClosing += (_, e) => { if (!AllowClose) { e.Cancel = true; Hide(); } };
    }

    public void Append(string message)
    {
        _text.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }
}
