using System.Globalization;
using RochPower.Core;
using RochPower.Hardware;

namespace RochPower.UI;

public sealed class MainForm : Form
{
    public const string AppName = "Roch CPU";
    public const string AppVersion = "1.0.0";
    private const int ResizeBorder = 6;

    private readonly HardwareModel _hw = new();
    private readonly LogForm _logForm = new();

    // header: CPU, board, BIOS only
    private readonly Label _lblCpu = Theme.Label("", Theme.Big);
    private readonly Label _lblCores = Theme.Muted_("");
    private readonly Label _lblBoard = Theme.Muted_("");
    private readonly Label _lblBios = Theme.Muted_("");
    private readonly Label _lblWarn = Theme.Label("", Theme.Small, Theme.Warn);
    private readonly Button _btnLog = Theme.Button("Log");
    private readonly Button _btnPerCore = Theme.Button("Per-Core Ratio Table");
    private readonly Button _btnAuto = Theme.Button("Start");
    private TextBox _txtAutoStep = null!, _txtAutoInterval = null!;
    private TableLayoutPanel _toolRow = null!;
    private FlowLayoutPanel _autoPanel = null!;

    // rows
    private readonly TableLayoutPanel _rows = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, BackColor = Theme.Bg };
    private readonly Dictionary<Setting, TextBox> _boxes = new();
    private readonly Dictionary<Setting, Label> _statusLabels = new();
    private readonly Dictionary<Setting, Label> _rangeLabels = new();

    private readonly Button _btnApply = Theme.Button("Apply", primary: true);
    private readonly Button _btnRevert = Theme.Button("Revert");
    private readonly Button _btnReset = Theme.Button("Reset");
    private readonly Label _lblStatus = Theme.Label("", Theme.Small, Theme.Muted);

    private readonly System.Windows.Forms.Timer _autoTimer = new();
    private readonly System.Windows.Forms.Timer _slowRefresh = new() { Interval = 3000 };
    private bool _bclkBusy;
    private int _rowIndex;

    public MainForm()
    {
        Text = AppName;
        Icon = Theme.LoadAppIcon();
        Font = Theme.Base;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(600, 420);
        Size = new Size(620, 760);
        KeyPreview = true;
        DoubleBuffered = true;

        BuildLayout();
        _hw.Log += AppendLog;
        Load += OnLoad;
        FormClosing += OnClosing;
        KeyDown += OnKeyDown;
    }

    // ------------------------------------------------------------------ layout
    private TableLayoutPanel _body = null!;

    private void BuildLayout()
    {
        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Bg, Margin = new Padding(0), Padding = new Padding(1) };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(outer);

        // ---- title bar ----
        var title = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Margin = new Padding(0) };
        var brand = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Location = new Point(8, 0), Height = 30, BackColor = Color.Transparent };
        var mark = Theme.LoadMark(20);
        if (mark != null) brand.Controls.Add(new PictureBox { Image = mark, Size = new Size(20, 20), Margin = new Padding(0, 5, 6, 0), BackColor = Color.Transparent });
        var roch = Theme.Label("Roch", Theme.Brand, Theme.Accent); roch.Margin = new Padding(0, 6, 0, 0);
        var cpu = Theme.Label($"CPU {AppVersion}", Theme.Brand, Theme.Text); cpu.Margin = new Padding(4, 6, 0, 0);
        brand.Controls.Add(roch); brand.Controls.Add(cpu);
        title.Controls.Add(brand);
        var btnClose = Theme.TitleButton("", close: true);
        var btnMin = Theme.TitleButton("");
        btnClose.Click += (_, _) => Close();
        btnMin.Click += (_, _) => WindowState = FormWindowState.Minimized;
        var tb = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Right, Width = 88, BackColor = Color.Transparent, Margin = new Padding(0) };
        tb.Controls.Add(btnClose); tb.Controls.Add(btnMin);
        title.Controls.Add(tb);
        foreach (Control c in new Control[] { title, brand, roch, cpu }) Theme.EnableDrag(c, this);
        foreach (Control c in brand.Controls) Theme.EnableDrag(c, this);
        outer.Controls.Add(title, 0, 0);

        // ---- body ----
        _body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Theme.Bg, Padding = new Padding(12, 4, 12, 8), Margin = new Padding(0) };
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _body.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // header
        _body.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // tools
        _body.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // rows
        _body.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // apply
        _body.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // status
        outer.Controls.Add(_body, 0, 1);

        // header: CPU name + cores/threads, board, BIOS. Log button top right.
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 2) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _btnLog.Font = Theme.Small; _btnLog.AutoSize = false; _btnLog.Width = 48; _btnLog.Height = 24; _btnLog.Padding = new Padding(0); _btnLog.Margin = new Padding(0, 2, 0, 0);
        _btnLog.Click += (_, _) => ToggleLog();
        header.Controls.Add(_lblCpu, 0, 0);
        header.Controls.Add(_btnLog, 1, 0);
        header.SetRowSpan(_btnLog, 2);
        _lblCores.Margin = new Padding(0, 2, 0, 0);
        _lblBoard.Margin = new Padding(0, 2, 0, 0);
        _lblBios.Margin = new Padding(0, 2, 0, 0);
        _lblWarn.Margin = new Padding(0, 4, 0, 0);
        header.Controls.Add(_lblCores, 0, 1);
        header.Controls.Add(_lblBoard, 0, 2);
        header.Controls.Add(_lblBios, 0, 3);
        header.Controls.Add(_lblWarn, 0, 4);
        header.SetColumnSpan(_lblWarn, 2);
        _body.Controls.Add(header, 0, 0);

        // tools: per-core table + auto ratio stepper
        var toolRow = _toolRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = false, Height = 72, ColumnCount = 1, RowCount = 2, BackColor = Theme.Bg, Margin = new Padding(0, 8, 0, 4) };
        toolRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        toolRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        _btnPerCore.Dock = DockStyle.Top; _btnPerCore.AutoSize = false; _btnPerCore.Height = 30; _btnPerCore.Font = Theme.Bold; _btnPerCore.Margin = new Padding(0, 0, 0, 6);
        _btnPerCore.Click += (_, _) => OpenPerCore();
        toolRow.Controls.Add(_btnPerCore, 0, 0);
        var auto = _autoPanel = new FlowLayoutPanel { AutoSize = false, Height = 32, Dock = DockStyle.Top, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        var autoLbl = Theme.Label("Auto ratio step", Theme.Row); autoLbl.Margin = new Padding(0, 4, 8, 0);
        auto.Controls.Add(autoLbl);
        auto.Controls.Add(Theme.ValueBox(out _txtAutoStep, 44)); _txtAutoStep.Text = "1";
        var every = Theme.Muted_("every"); every.Margin = new Padding(6, 5, 0, 0); auto.Controls.Add(every);
        auto.Controls.Add(Theme.ValueBox(out _txtAutoInterval, 44)); _txtAutoInterval.Text = "5";
        var sec = Theme.Muted_("s"); sec.Margin = new Padding(4, 5, 8, 0); auto.Controls.Add(sec);
        _btnAuto.Margin = new Padding(0, 0, 0, 0); _btnAuto.AutoSize = false; _btnAuto.Padding = new Padding(0); _btnAuto.Width = 70; _btnAuto.Height = 26;
        _btnAuto.Click += (_, _) => ToggleAuto();
        auto.Controls.Add(_btnAuto);
        toolRow.Controls.Add(auto, 0, 1);
        _body.Controls.Add(toolRow, 0, 1);

        // rows: no scroller, the window is sized to hold them
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _rows.Margin = new Padding(0, 6, 0, 0);
        _body.Controls.Add(_rows, 0, 2);

        // apply / revert / reset
        var applyRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, BackColor = Theme.Bg, Margin = new Padding(0, 8, 0, 0) };
        applyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        applyRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        applyRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _btnApply.Dock = DockStyle.Fill; _btnApply.AutoSize = false; _btnApply.Height = 32; _btnApply.Margin = new Padding(0, 0, 6, 0);
        _btnRevert.Height = 32; _btnRevert.AutoSize = false; _btnRevert.Width = 80; _btnRevert.Padding = new Padding(0); _btnRevert.Margin = new Padding(0, 0, 6, 0);
        _btnReset.Height = 32; _btnReset.AutoSize = false; _btnReset.Width = 72; _btnReset.Padding = new Padding(0); _btnReset.Margin = new Padding(0);
        _btnApply.Click += (_, _) => ApplyAll();
        _btnRevert.Click += (_, _) => RefreshRows("Reverted the fields to what the hardware reports.");
        _btnReset.Click += (_, _) => RestoreDefaults();
        applyRow.Controls.Add(_btnApply, 0, 0);
        applyRow.Controls.Add(_btnRevert, 1, 0);
        applyRow.Controls.Add(_btnReset, 2, 0);
        _body.Controls.Add(applyRow, 0, 3);

        // status
        var status = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, BackColor = Theme.Bg, Margin = new Padding(0, 6, 0, 0) };
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _lblStatus.AutoSize = true;
        status.Controls.Add(_lblStatus, 0, 0);
        // Roch Viewer's footer handle: brand red, bold, no underline, and colour is the whole
        // affordance - there is no button edge, so it lifts a shade under the pointer.
        var author = new LinkLabel
        {
            Text = Theme.Author, LinkColor = Theme.Accent, ActiveLinkColor = Theme.Warn, VisitedLinkColor = Theme.Accent,
            LinkBehavior = LinkBehavior.NeverUnderline, Font = Theme.Bold, AutoSize = true,
            BackColor = Color.Transparent, Cursor = Cursors.Hand, Margin = new Padding(6, 0, 0, 0)
        };
        author.LinkClicked += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Theme.AuthorUrl) { UseShellExecute = true }); } catch { } };
        author.MouseEnter += (_, _) => author.LinkColor = Theme.Warn;
        author.MouseLeave += (_, _) => author.LinkColor = Theme.Accent;
        new ToolTip().SetToolTip(author, Theme.AuthorUrl);
        status.Controls.Add(author, 1, 0);
        _body.Controls.Add(status, 0, 4);

        Resize += (_, _) =>
        {
            int w = Math.Max(240, ClientSize.Width - 90);
            _lblStatus.MaximumSize = new Size(w, 0);
            foreach (var l in new[] { _lblCpu, _lblCores, _lblBoard, _lblBios, _lblWarn }) l.MaximumSize = new Size(ClientSize.Width - 80, 0);
        };
    }

    /// <summary>One compact line per setting: name, allowed range, value box. Nothing scrolls.</summary>
    private void BuildRows()
    {
        _rows.SuspendLayout();
        _rows.Controls.Clear();
        _rows.RowStyles.Clear();
        _boxes.Clear(); _statusLabels.Clear(); _rangeLabels.Clear();
        _rowIndex = 0;
        SettingGroup? last = null;
        foreach (var s in _hw.Settings)
        {
            // Unavailable rows are hidden rather than shown greyed out: it keeps everything on screen
            // without scrolling, and a control this system does not have is not worth a line.
            if (!s.Available) continue;

            if (s.Group != last)
            {
                last = s.Group;
                _rows.Controls.Add(Theme.SectionTitle(s.Group switch
                {
                    SettingGroup.Clocks => "Clocks", SettingGroup.Voltages => "Voltages (FIVR / OC mailbox)",
                    SettingGroup.Power => _hw.IsAmd ? "Power and current limits (SMU)" : "Power limits",
                    SettingGroup.Pbo => "Precision Boost Overdrive (SMU)",
                    SettingGroup.Memory => "DDR5 memory (PMIC)",
                    SettingGroup.Board => "Board VRM rails (measured, read-only)", _ => ""
                }));
            }

            // Alternating row shading, the way Roch Viewer's tables read.
            var rowBack = (_rowIndex++ % 2 == 0) ? Theme.Bg : Theme.PanelAlt;
            var row = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, Dock = DockStyle.Top, BackColor = rowBack, Margin = new Padding(0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var name = Theme.Label(s.Name, Theme.Row, Theme.Text);
            name.Margin = new Padding(6, 5, 0, 5);
            var range = Theme.Muted_(s.ReadOnly ? "read-only" : s.RangeText);
            range.Margin = new Padding(8, 6, 8, 0);
            range.TextAlign = ContentAlignment.MiddleRight;
            var frame = Theme.ValueBox(out var box, 76);
            frame.Margin = new Padding(0, 1, 0, 1);
            box.Text = s.CurrentText;
            box.Enabled = !s.ReadOnly;
            box.Tag = s;
            var unit = Theme.Muted_(s.Unit); unit.Margin = new Padding(4, 6, 0, 0); unit.Width = 34; unit.AutoSize = false;

            row.Controls.Add(name, 0, 0);
            row.Controls.Add(range, 1, 0);
            row.Controls.Add(frame, 2, 0);
            row.Controls.Add(unit, 3, 0);
            if (s.Note != null) { var tip = new ToolTip { AutoPopDelay = 20000 }; tip.SetToolTip(name, s.Note); tip.SetToolTip(box, s.Note); tip.SetToolTip(range, s.Note); }
            _rows.Controls.Add(row);
            _boxes[s] = box; _statusLabels[s] = range; _rangeLabels[s] = range;
        }
        _rows.ResumeLayout();
    }

    /// <summary>Grow the window to whatever the rows need, so there is never a scrollbar.</summary>
    private void FitToContent()
    {
        _body.PerformLayout();
        int needed = _body.PreferredSize.Height + 30 /* title bar */ + 12;
        var work = Screen.FromControl(this).WorkingArea;
        int height = Math.Min(needed, work.Height - 40);
        Height = Math.Max(MinimumSize.Height, height);
        if (Bottom > work.Bottom) Top = Math.Max(work.Top, work.Bottom - Height - 10);
    }

    // ------------------------------------------------------------------ borderless window
    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84, HTCLIENT = 1, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        base.WndProc(ref m);
        if (m.Msg == WM_NCHITTEST && (int)m.Result == HTCLIENT && WindowState == FormWindowState.Normal)
        {
            var p = PointToClient(new Point(m.LParam.ToInt32() & 0xFFFF, (m.LParam.ToInt32() >> 16) & 0xFFFF));
            bool l = p.X < ResizeBorder, r = p.X >= ClientSize.Width - ResizeBorder, t = p.Y < ResizeBorder, b = p.Y >= ClientSize.Height - ResizeBorder;
            int hit = (t && l) ? HTTOPLEFT : (t && r) ? HTTOPRIGHT : (b && l) ? HTBOTTOMLEFT : (b && r) ? HTBOTTOMRIGHT : l ? HTLEFT : r ? HTRIGHT : t ? HTTOP : b ? HTBOTTOM : HTCLIENT;
            if (hit != HTCLIENT) m.Result = (IntPtr)hit;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    // ------------------------------------------------------------------ lifecycle
    private void OnLoad(object? sender, EventArgs e)
    {
        AppendLog($"{AppName} {AppVersion} starting from {AppContext.BaseDirectory}");
        Cursor = Cursors.WaitCursor;
        try { _hw.Initialize(); }
        finally { Cursor = Cursors.Default; }

        var sm = _hw.Smbios;
        if (_hw.Cpu != null || _hw.Amd != null)
        {
            // Vendor-neutral summary from the model (Intel and AMD both feed it). The generation
            // string is deliberately not shown: it is still detected and still drives the
            // platform warning, it just does not earn a line in the header.
            _lblCpu.Text = _hw.CpuName;
            _lblCores.Text = _hw.CoreSummary;
        }
        else
        {
            _lblCpu.Text = "No CPU access";
            _lblCores.Text = _hw.DriverStatus;
        }
        _lblBoard.Text = sm.MainboardModel;
        _lblBios.Text = $"BIOS {sm.BiosVersion}";

        var warns = new List<string>();
        if (_hw.Driver == null) warns.Add("kernel driver not loaded, nothing can be read or written (see Log)");
        if (_hw.OcLocked) warns.Add("BIOS OC Lock set: ratio and voltage writes will be rejected");
        if (_hw.Cpu != null && !_hw.MailboxAvailable) warns.Add("OC mailbox not responding: voltage rows disabled");
        if (_hw.Cpu is { IsLga1700Family: false }) warns.Add("not a known LGA1700 CPU");
        if (_hw.Amd is { IsSupported: false }) warns.Add("unknown Zen generation: SMU message numbers assumed");
        if (_hw.Amd != null && !_hw.SmuAvailable) warns.Add("SMU not responding: PBO rows disabled (see Log)");
        if (_hw.Amd != null && _hw.SmuAvailable && !_hw.PboAllowed) warns.Add("SMU reports PBO unavailable: enable Precision Boost Overdrive in the BIOS or writes will be rejected");
        if (_hw.BiosCoreOverride) warns.Add("BIOS core voltage is in Override mode: on boards that pin the VRM there, CPU Core Voltage changes only the VID. Use Adaptive/Auto in BIOS.");
        _lblWarn.Text = string.Join("  ·  ", warns);
        _lblWarn.Visible = warns.Count > 0;

        BuildRows();
        if (_hw.IsAmd)
        {
            _btnPerCore.Text = "Curve Optimizer (per core)";
            _btnPerCore.Enabled = _hw.SmuAvailable && _hw.Amd!.Smu.Messages.HasCurveOptimizer;
            // The ratio stepper drives the Intel turbo table; nothing on the SMU side steps safely on a timer.
            _autoPanel.Visible = false;
            _toolRow.Height = 38;
        }
        else _btnPerCore.Enabled = _hw.Cpu != null;
        SetStatus($"Ready  ·  {_hw.DriverStatus}", false);

        _autoTimer.Tick += (_, _) => AutoTick();
        // Low rate on purpose: the BCLK row needs a measurement and the Vcore annotation a sample,
        // and nothing on this window justifies hammering the hardware.
        _slowRefresh.Tick += (_, _) => SlowTick();
        _slowRefresh.Start();
        SlowTick();

        FitToContent();
        BeginInvoke(() => ActiveControl = null);
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        _slowRefresh.Stop();
        _autoTimer.Stop();
        _logForm.AllowClose = true;
        _logForm.Close();
        _hw.Dispose();
    }

    private void AppendLog(string msg)
    {
        void Do() => _logForm.Append(msg);
        if (InvokeRequired) BeginInvoke(Do); else Do();
    }

    private void SetStatus(string text, bool error)
    {
        _lblStatus.Text = text;
        _lblStatus.ForeColor = error ? Theme.Danger : Theme.Muted;
    }

    private void ToggleLog()
    {
        if (_logForm.Visible) _logForm.Hide();
        else { _logForm.Show(this); _logForm.Location = new Point(Right + 6, Top); }
    }

    private void SlowTick()
    {
        if (_hw.Cpu == null) return;
        if (!_bclkBusy)
        {
            _bclkBusy = true;
            Task.Run(() => { try { _hw.MeasureBclk(); } finally { _bclkBusy = false; } });
        }
        var bclk = _hw.Settings.FirstOrDefault(s => s.Id == "bclk");
        if (bclk != null && _hw.LastBclk is double lb && _boxes.TryGetValue(bclk, out var bb) && !bb.Focused)
        {
            bclk.Current = lb; bb.Text = bclk.Format(lb);
        }
        // Board rails move on their own; keep the read-only rows current.
        foreach (var s in _hw.Settings.Where(s => s.Group == SettingGroup.Board && s.Available))
            if (_boxes.TryGetValue(s, out var box)) { _hw.Refresh(s); box.Text = s.CurrentText; }
    }

    // ------------------------------------------------------------------ apply
    private void RefreshRows(string? message = null)
    {
        _hw.RefreshAll();
        foreach (var (s, box) in _boxes) box.Text = s.CurrentText;
        foreach (var (s, l) in _rangeLabels) { l.Text = s.ReadOnly ? "read-only" : s.RangeText; l.ForeColor = Theme.Muted; }
        if (message != null) { AppendLog(message); SetStatus(message, false); }
    }

    private void ApplyAll()
    {
        int applied = 0, failed = 0;
        foreach (var (s, box) in _boxes)
        {
            if (s.ReadOnly) continue;
            string text = box.Text.Trim();
            if (text.Length == 0 || text.Equals(s.CurrentText, StringComparison.OrdinalIgnoreCase)) continue;
            if (!s.TryParse(text, out double value)) { AppendLog($"{s.Name}: '{text}' is not a number."); failed++; SetRowStatus(s, "invalid", Theme.Danger); continue; }
            if (value != 0 && !ConfirmDangerous(s, value)) { SetRowStatus(s, "skipped", Theme.Muted); continue; }
            bool ok = _hw.Apply(s, value);
            bool ignored = ok && s.Id is "core_v" or "core_off" && _hw.CoreVoltageIgnored;
            if (ok && !ignored) applied++; else failed++;
            SetRowStatus(s, ignored ? "board ignored it" : ok ? "applied" : "failed", ok && !ignored ? Theme.Ok : Theme.Danger);
            box.Text = s.CurrentText;
        }
        if (applied == 0 && failed == 0) { SetStatus("Nothing to apply: no field differs from the hardware.", false); return; }
        SetStatus($"Applied at {DateTime.Now:HH:mm:ss}  ·  {applied} ok, {failed} failed", failed > 0);
        AppendLog($"Apply: {applied} applied, {failed} failed.");
    }

    private bool ConfirmDangerous(Setting s, double value)
    {
        bool risky = s.Id switch
        {
            "core_v" => value > 1.45,
            "ecore_v" or "ring_v" or "sa_v" or "gt_v" => value > 1.35,
            "core_off" or "ecore_off" or "ring_off" or "sa_off" or "gt_off" => value > 150,
            _ when s.Id.EndsWith("_vdd") || s.Id.EndsWith("_vddq") => value > 1.40,
            _ when s.Id.EndsWith("_vpp") => value > 1.95,
            "ppt" => value > 500,
            "tdc" or "edc" => value > 800,
            "co_all" => value > 15,
            _ => false
        };
        if (!risky) return true;
        return MessageBox.Show(this, $"{s.Name} = {s.Format(value)} {s.Unit} is above the usual safe range for daily use and can damage hardware.\n\nApply it anyway?",
            AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    private void SetRowStatus(Setting s, string text, Color color)
    {
        if (_statusLabels.TryGetValue(s, out var l)) { l.Text = text; l.ForeColor = color; }
    }

    private void RestoreDefaults()
    {
        if (MessageBox.Show(this, $"Put every value back to what it was when {AppName} started?", AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _hw.RestoreAllDefaults();
        RefreshRows("Reset: start-up values restored.");
    }

    // ------------------------------------------------------------------ per core / auto
    private void OpenPerCore()
    {
        if (_hw.Amd != null)
        {
            using var co = new CurveOptimizerForm(_hw);
            co.ShowDialog(this);
            RefreshRows();
            return;
        }
        if (_hw.Cpu == null) return;
        using var f = new PerCoreForm(_hw);
        f.ShowDialog(this);
        RefreshRows();
    }

    private void ToggleAuto()
    {
        if (_autoTimer.Enabled) { _autoTimer.Stop(); _btnAuto.Text = "Start"; SetStatus("Auto stepping stopped.", false); return; }
        if (!double.TryParse(_txtAutoInterval.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double sec) || sec < 0.5) { SetStatus("Auto: interval must be at least 0.5 s.", true); return; }
        if (!double.TryParse(_txtAutoStep.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double step) || step == 0) { SetStatus("Auto: step must be non-zero.", true); return; }
        _autoTimer.Interval = (int)(sec * 1000);
        _autoTimer.Start();
        _btnAuto.Text = "Stop";
        SetStatus($"Auto: CPU ratio {(step > 0 ? "+" : "")}{step} every {sec} s until a write fails or you press Stop.", false);
    }

    private void AutoTick()
    {
        var s = _hw.Settings.FirstOrDefault(x => x.Id == "cpu_ratio");
        if (s == null || !s.Available || s.ReadOnly || s.Current is not double cur) { ToggleAuto(); return; }
        double.TryParse(_txtAutoStep.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double step);
        double next = cur + step;
        if (next > s.Max || next < s.Min || !_hw.Apply(s, next)) { ToggleAuto(); SetStatus($"Auto stopped at x{s.CurrentText}: the next step was rejected.", true); return; }
        if (_boxes.TryGetValue(s, out var box)) box.Text = s.CurrentText;
        SetStatus($"Auto: CPU ratio now x{s.CurrentText}.", false);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Enter when ActiveControl is not Button: ApplyAll(); e.Handled = true; e.SuppressKeyPress = true; break;
            case Keys.F6: ToggleAuto(); e.Handled = true; break;
        }
    }
}
