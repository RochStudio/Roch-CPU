using RochPower.Core;
using RochPower.Hardware;

namespace RochPower.UI;

public sealed class MainForm : Form
{
    public const string AppName = "Roch CPU";
    public const string AppVersion = "1.0.6";
    private const int ResizeBorder = 6;

    private readonly bool _uiPreview;
    private readonly bool _previewScrollBottom;
    private readonly bool _previewSplitDimms;
    private readonly bool _previewCoreReadFailed;
    private readonly string? _previewVoltageReadings;
    private readonly HardwareModel? _runtimeHardware;
    // A preview never constructs HardwareModel (even its SMBIOS initializer probes the machine).
    private HardwareModel _hw => _runtimeHardware ?? throw new InvalidOperationException("Hardware is unavailable in UI preview.");
    private readonly IReadOnlyList<Setting> _previewSettings;
    private readonly LogForm _logForm = new();

    // Hardware-read identity shown above the tuning controls.
    private readonly Label _lblCpu = Theme.Label("", Theme.Big);
    private readonly Label _lblCores = Theme.Muted_("");
    private readonly Label _lblMicrocode = Theme.Muted_("");
    private readonly Label _lblBoard = Theme.Muted_("");
    private readonly Label _lblAgesa = Theme.Muted_("");
    private readonly Label _lblBios = Theme.Muted_("");
    private readonly Label _lblLive = Theme.Muted_("");
    private readonly ToolTip _resultTip = new() { AutoPopDelay = 30000 };
    private readonly Label _lblWarn = Theme.Label("", Theme.Small, Theme.Warn);
    private readonly Button _btnLog = Theme.Button("Log");
    private readonly Button _btnTheme = Theme.TitleButton(Theme.GlyphTheme);
    private readonly Button _btnPerCore = Theme.Button("Per-Core Ratio Table");

    // rows
    private readonly TableLayoutPanel _rows = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, BackColor = Theme.Bg };
    private readonly Panel _rowsViewport = new() { Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0) };
    private readonly Dictionary<Setting, TextBox> _boxes = new();
    private readonly Dictionary<Setting, Label> _statusLabels = new();
    private readonly Dictionary<Setting, Label> _rangeLabels = new();
    /// <summary>When each row last showed an apply result in its range label, so the live reading leaves it up for a while.</summary>
    private readonly Dictionary<Setting, DateTime> _rowResults = new();
    private MemoryVoltageLink? _memoryLink;
    private RadioButton? _splitDimms, _syncDimms;
    private readonly Dictionary<string, Label> _memoryGroupLabels = new();
    private readonly Dictionary<string, TextBox> _sharedMemoryBoxes = new();
    private readonly Dictionary<string, Control> _sharedMemoryRows = new();
    private readonly Dictionary<Setting, Control> _memoryPerDimmRows = new();
    private readonly Dictionary<string, string> _settingDrafts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sharedDraftTexts = new();
    private readonly HashSet<string> _pendingSharedMemoryEdits = new();
    private readonly HashSet<Setting> _pendingMemoryEdits = new();
    private bool _stagingMemory;
    private bool _projectingShared;

    private readonly Button _btnApply = Theme.Button("Apply", primary: true);
    private readonly Button _btnReset = Theme.Button("Reset to Default");
    private readonly Label _lblStatus = Theme.Label("", Theme.Small, Theme.Text);

    private readonly System.Windows.Forms.Timer _slowRefresh = new() { Interval = 3000 };
    private bool _bclkBusy;
    /// <summary>Set while an apply is running on a worker, so nothing else touches the hardware.</summary>
    private bool _applying;

    public MainForm() : this(uiPreview: false) { }

    public static MainForm CreateUiPreview(bool scrollBottom = false, bool verifiedCore = false, bool splitDimms = false, bool coreReadFailed = false, string? voltageReadings = null) => new(uiPreview: true, previewScrollBottom: scrollBottom, previewVerifiedCore: verifiedCore, previewSplitDimms: splitDimms, previewCoreReadFailed: coreReadFailed, previewVoltageReadings: voltageReadings);

    private MainForm(bool uiPreview, bool previewScrollBottom = false, bool previewVerifiedCore = false, bool previewSplitDimms = false, bool previewCoreReadFailed = false, string? previewVoltageReadings = null)
    {
        _uiPreview = uiPreview;
        _previewScrollBottom = uiPreview && previewScrollBottom;
        _previewSplitDimms = uiPreview && previewSplitDimms;
        _previewCoreReadFailed = uiPreview && (previewCoreReadFailed || previewVoltageReadings == "error");
        _previewVoltageReadings = uiPreview ? previewVoltageReadings : null;
        _previewSettings = uiPreview ? UiPreviewSettings.Create(previewVerifiedCore, _previewCoreReadFailed) : Array.Empty<Setting>();
        if (uiPreview) ConfigureVoltageReadingsFixture();
        _runtimeHardware = uiPreview ? null : new HardwareModel();
        Text = AppName;
        Icon = Theme.LoadAppIcon();
        Font = Theme.Base;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(480, 400);
        Size = new Size(500, 760);
        KeyPreview = true;
        DoubleBuffered = true;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildLayout();
        if (_runtimeHardware != null) _runtimeHardware.Log += AppendLog;
        Load += uiPreview ? OnPreviewLoad : OnLoad;
        FormClosing += OnClosing;
        KeyDown += OnKeyDown;
    }

    // ------------------------------------------------------------------ layout
    private TableLayoutPanel _body = null!;

    private void BuildLayout()
    {
        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Bg, Margin = new Padding(0), Padding = new Padding(1) };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        Controls.Add(outer);

        // ---- title bar ----
        var title = new Panel { Dock = DockStyle.Fill, BackColor = Theme.TitleBar, Margin = new Padding(0) };
        var brand = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Location = new Point(8, 0), Height = 30, BackColor = Color.Transparent };
        var mark = Theme.LoadMark(24);
        if (mark != null) brand.Controls.Add(new PictureBox { Image = mark, Size = new Size(24, 24), Margin = new Padding(0, 3, 6, 0), BackColor = Color.Transparent });
        var appTitle = Theme.Label($"{AppName} {AppVersion}", Theme.Brand, Theme.Text); appTitle.Margin = new Padding(0, 7, 0, 0);
        brand.Controls.Add(appTitle);
        title.Controls.Add(brand);
        var btnClose = Theme.TitleButton(Theme.GlyphClose, close: true);
        var btnMin = Theme.TitleButton(Theme.GlyphMinimise);
        btnClose.Click += (_, _) => Close();
        btnMin.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _btnTheme.Click += (_, _) =>
        {
            try { Theme.Toggle(this, _logForm); }
            catch (Exception ex) { AppendLog("Theme preference could not be saved: " + ex.Message); }
            _btnTheme.Text = Theme.GlyphTheme;
            _resultTip.SetToolTip(_btnTheme, ThemeTip());
        };
        _resultTip.SetToolTip(_btnTheme, ThemeTip());
        _btnTheme.Width = 36;
        var tb = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Right, Width = 124, BackColor = Color.Transparent, Margin = new Padding(0) };
        tb.Controls.Add(btnClose); tb.Controls.Add(btnMin); tb.Controls.Add(_btnTheme);
        title.Controls.Add(tb);
        foreach (Control c in new Control[] { title, brand, appTitle }) Theme.EnableDrag(c, this);
        foreach (Control c in brand.Controls) Theme.EnableDrag(c, this);
        outer.Controls.Add(title, 0, 0);

        // ---- body ----
        _body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Theme.Bg, Padding = new Padding(10, 2, 10, 6), Margin = new Padding(0) };
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _body.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // header
        _body.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // tools
        _body.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // rows
        _body.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // apply
        outer.Controls.Add(_body, 0, 1);

        // Header: CPU, topology, microcode, board and BIOS. Log button top right.
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 2) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _btnLog.Font = Theme.Small; _btnLog.AutoSize = false; _btnLog.Width = 48; _btnLog.Height = 24; _btnLog.Padding = new Padding(0); _btnLog.Margin = new Padding(0, 2, 0, 0);
        _btnLog.Click += (_, _) => ToggleLog();
        header.Controls.Add(_lblCpu, 0, 0);
        var headerButtons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0), BackColor = Theme.Bg };
        _btnLog.Width = 64;
        headerButtons.Controls.Add(_btnLog);
        header.Controls.Add(headerButtons, 1, 0);
        header.SetRowSpan(headerButtons, 2);
        _lblCores.Margin = new Padding(0, 1, 0, 0);
        _lblMicrocode.Margin = new Padding(0, 1, 0, 0);
        _lblBoard.Margin = new Padding(0, 1, 0, 0);
        _lblAgesa.Margin = new Padding(0, 1, 0, 0);
        _lblBios.Margin = new Padding(0, 1, 0, 0);
        _lblWarn.Margin = new Padding(0, 4, 0, 0);
        header.Controls.Add(_lblCores, 0, 1);
        header.Controls.Add(_lblMicrocode, 0, 2);
        header.Controls.Add(_lblBoard, 0, 3);
        header.Controls.Add(_lblAgesa, 0, 4);
        header.Controls.Add(_lblBios, 0, 5);
        header.Controls.Add(_lblLive, 0, 6);
        header.SetColumnSpan(_lblLive, 2);
        header.Controls.Add(_lblWarn, 0, 7);
        _lblWarn.MaximumSize = new Size(460, 0);
        header.SetColumnSpan(_lblWarn, 2);
        _body.Controls.Add(header, 0, 0);

        // Manual per-core controls remain; automatic ratio stepping has been removed.
        var toolRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = false, Height = 36, ColumnCount = 1, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0, 4, 0, 2) };
        toolRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        _btnPerCore.Dock = DockStyle.Top; _btnPerCore.AutoSize = false; _btnPerCore.Height = 30; _btnPerCore.Font = Theme.Bold; _btnPerCore.Margin = new Padding(0, 0, 0, 6);
        _btnPerCore.Click += (_, _) => OpenPerCore();
        toolRow.Controls.Add(_btnPerCore, 0, 0);
        _body.Controls.Add(toolRow, 0, 1);

        // Only the setting rows scroll; header, actions and footer remain reachable on smaller displays.
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _rows.Margin = new Padding(0, 2, 0, 0);
        _rowsViewport.BackColor = Theme.Bg;
        _rowsViewport.Controls.Add(_rows);
        _body.Controls.Add(_rowsViewport, 0, 2);

        // Apply / reset. Refresh-from-hardware remains automatic for live read-only rows.
        var applyRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, BackColor = Theme.Bg, Margin = new Padding(0, 4, 0, 0) };
        applyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        applyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _btnApply.Dock = DockStyle.Fill; _btnApply.AutoSize = false; _btnApply.Height = 32; _btnApply.Margin = new Padding(0, 0, 6, 0);
        _btnReset.Height = 32; _btnReset.AutoSize = false; _btnReset.Width = 72; _btnReset.Padding = new Padding(0); _btnReset.Margin = new Padding(0);
        _btnReset.Dock = DockStyle.Fill;
        _btnApply.Height = _btnReset.Height = 32;
        _btnApply.Click += (_, _) => ApplyAll();
        _btnReset.Click += (_, _) => RestoreDefaults();
        _resultTip.SetToolTip(_btnReset, "Reset changed available writable settings using their recorded startup values or per-control Auto/default actions; discard staged drafts. AMD power limits use CPU stock defaults and Curve Optimizer returns to zero. This does not reset factory BIOS settings.");
        applyRow.Controls.Add(_btnApply, 0, 0);
        applyRow.Controls.Add(_btnReset, 1, 0);
        _body.Controls.Add(applyRow, 0, 3);

        // Viewer-style footer: social links left, existing status right.
        var status = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(10, 0, 10, 0),
            BackColor = Theme.Bg, Margin = new Padding(0)
        };
        status.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _lblStatus.AutoSize = false;
        _lblStatus.Dock = DockStyle.Fill;
        _lblStatus.TextAlign = ContentAlignment.MiddleRight;
        _lblStatus.AutoEllipsis = true;
        _lblStatus.Margin = new Padding(0);
        // Roch Viewer's footer handle: brand red, bold, no underline, and colour is the whole
        // affordance - there is no button edge, so it lifts a shade under the pointer.
        var author = new LinkLabel
        {
            Text = "YouTube | X | Discord", ForeColor = Theme.Text, LinkColor = Theme.Selected, ActiveLinkColor = Theme.Hover, VisitedLinkColor = Theme.Selected,
            LinkBehavior = LinkBehavior.NeverUnderline, Font = Theme.Bold, AutoSize = true,
            BackColor = Color.Transparent, Cursor = _uiPreview ? Cursors.Default : Cursors.Hand, Margin = new Padding(0, 5, 6, 0)
        };
        author.Links.Clear();
        author.Links.Add(0, 7, "https://www.youtube.com/@MateoPcTech");
        author.Links.Add(10, 1, Theme.AuthorUrl);
        author.Links.Add(14, 7, "https://discord.gg/KfzExpKQHB");
        author.LinkClicked += (_, e) => { if (_uiPreview) return; try { if (e.Link?.LinkData is string url) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { } };
        author.MouseEnter += (_, _) => author.LinkColor = Theme.Hover;
        author.MouseLeave += (_, _) => author.LinkColor = Theme.Selected;
        status.Controls.Add(author, 0, 0);
        status.Controls.Add(_lblStatus, 1, 0);
        outer.Controls.Add(status, 0, 2);

        Resize += (_, _) =>
        {
            foreach (var l in new[] { _lblCpu, _lblCores, _lblMicrocode, _lblBoard, _lblAgesa, _lblBios, _lblWarn }) l.MaximumSize = new Size(ClientSize.Width - 80, 0);
        };

    }

    /// <summary>One compact line per available setting; every row remains accessible through the viewport.</summary>
    private void BuildRows()
    {
        bool synced = _memoryLink?.IsSynced ?? !_previewSplitDimms;
        var pendingIds = _pendingMemoryEdits.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _rows.SuspendLayout();
        foreach (Control old in _rows.Controls.Cast<Control>().ToArray()) old.Dispose();
        _rows.Controls.Clear();
        _rows.RowStyles.Clear();
        _boxes.Clear(); _statusLabels.Clear(); _rangeLabels.Clear(); _rowResults.Clear();
        _pendingMemoryEdits.Clear(); _memoryGroupLabels.Clear();
        _sharedMemoryBoxes.Clear(); _sharedMemoryRows.Clear(); _memoryPerDimmRows.Clear();
        var settings = (_uiPreview ? _previewSettings : _hw.Settings).ToArray();
        _memoryLink = CreateMemoryLink(settings);
        _memoryLink.SetSynced(synced);
        SettingGroup? last = null;
        TableLayoutPanel? section = null;
        int sectionRow = 0;
        foreach (var s in settings)
        {
            // Keep the core target visible when its existing verification gate refuses access.
            // Its target is not the measured Vcore or VID, and no value is read through that gate.
            bool unavailableCore = s.Id == "core_v" && !s.Available;
            if (!s.Available && !unavailableCore) continue;

            if (s.Group != last)
            {
                last = s.Group;
                sectionRow = 0;
                section = new TableLayoutPanel
                {
                    AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    ColumnCount = 1, Dock = DockStyle.Top, BackColor = Theme.Panel,
                    Padding = new Padding(10, 3, 10, 4), Margin = new Padding(0, 0, 0, 6)
                };
                section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                section.Paint += (_, e) =>
                {
                    var panel = (Control)_!;
                    using var pen = new Pen(Theme.Border);
                    e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
                };
                var heading = Theme.SectionTitle(s.Group switch
                {
                    SettingGroup.Clocks => "Clocks", SettingGroup.Voltages => "Voltages",
                    SettingGroup.Power => "Power",
                    SettingGroup.Pbo => "Precision Boost",
                    SettingGroup.Memory => "Memory",
                    SettingGroup.Board => "Board VRM rails", _ => ""
                });
                heading.Margin = new Padding(0, 0, 0, 2);
                section.Controls.Add(heading);
                section.Controls.Add(Theme.Rule());
                if (s.Group == SettingGroup.Memory) AddMemoryLinkControls(section);
                if (!_uiPreview && s.Group == SettingGroup.Power && _hw.IsAmd && !PawnIo.IsInstalled) section.Controls.Add(PawnIoNote());
                _rows.Controls.Add(section);
            }

            // Alternating row shading, the way Roch Viewer's tables read.
            var rowBack = sectionRow++ % 2 == 0 ? Theme.Panel : Theme.PanelAlt;
            var row = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, Dock = DockStyle.Top, BackColor = rowBack, Margin = new Padding(0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var displayName = s.Name.Replace(" (Package Power Tracking)", "").Replace(" (Thermal Design Current)", "")
                .Replace(" (Electrical Design Current)", "").Replace(" (Tctl max)", "")
                .Replace(" (all cores)", "").Replace("DRAM ", "").Replace(" Voltage", "");
            if (s.Id == "core_v") displayName = "CPU Core Voltage";
            var name = Theme.Label(displayName, Theme.Row, Theme.Text);
            if (s.Group == SettingGroup.Memory && !displayName.Contains("VDDQ") && !displayName.Contains("VPP")) name.Text += " VDD";
            name.MaximumSize = new Size(175, 0);
            name.Margin = new Padding(6, 4, 0, 3);
            var range = Theme.Label(unavailableCore ? "unavailable" : IdleText(s), Theme.Small, s.ReadOnly || unavailableCore ? Theme.Muted : Theme.Text);
            range.Margin = new Padding(8, 5, 8, 0);
            range.TextAlign = ContentAlignment.MiddleRight;
            var frame = Theme.ValueBox(out var box, 76);
            frame.Margin = new Padding(0, 1, 0, 1);
            box.Text = unavailableCore ? "Not read" : _settingDrafts.GetValueOrDefault(s.Id, CoreDisplayText(s));
            box.Enabled = s.Available && !s.ReadOnly;
            box.ReadOnly = _uiPreview;
            box.Tag = s;
            var unit = Theme.Muted_(s.Unit); unit.Margin = new Padding(4, 5, 0, 0); unit.Width = 34; unit.AutoSize = false;

            row.Controls.Add(name, 0, 0);
            row.Controls.Add(frame, 1, 0);
            row.Controls.Add(unit, 2, 0);
            range.Dock = DockStyle.Fill; range.AutoSize = false; range.AutoEllipsis = true;
            row.Controls.Add(range, 3, 0);
            if (RowTip(s) is { } tipText) { var tip = new ToolTip { AutoPopDelay = 20000 }; tip.SetToolTip(name, tipText); tip.SetToolTip(box, tipText); tip.SetToolTip(range, tipText); }
            section!.Controls.Add(row);
            if (unavailableCore)
            {
                string detail = _uiPreview ? "Fixture: automatic startup core-page check failed; no voltage or mode target changed." :
                    _hw.VoltageAccessRestriction ?? (_hw.CoreVoltageStatus != "not probed" ? _hw.CoreVoltageStatus : s.Note ?? "Core target is unavailable; see Log for the existing access checks.");
                var reason = Theme.Label("Core voltage unavailable - see Log", Theme.Small, Theme.Warn);
                reason.Margin = new Padding(6, 0, 6, 4);
                reason.MaximumSize = new Size(420, 0);
                _resultTip.SetToolTip(reason, detail);
                _resultTip.SetToolTip(box, detail);
                _resultTip.SetToolTip(name, detail);
                _resultTip.SetToolTip(range, detail);
                section.Controls.Add(reason);
            }
            _boxes[s] = box; _statusLabels[s] = range; _rangeLabels[s] = range;
            _settingDrafts[s.Id] = box.Text;
            box.TextChanged += (_, _) => _settingDrafts[s.Id] = box.Text;
            if (MemoryVoltageLink.TryGetRail(s, out _))
            {
                _memoryPerDimmRows[s] = row;
                if (pendingIds.Contains(s.Id)) _pendingMemoryEdits.Add(s);
                box.TextChanged += (_, _) =>
                {
                    if (!_stagingMemory && !_applying && box.Focused && _memoryLink?.IsSynced == true)
                        _pendingMemoryEdits.Add(s);
                    UpdateMemoryGroupLabels();
                };
                box.Leave += (_, _) =>
                {
                    if (!_applying && _memoryLink?.IsSynced == true && _pendingMemoryEdits.Contains(s))
                        StageMemoryDraft(s);
                };
            }
        }
        UpdateMemoryGroupLabels();
        _rows.ResumeLayout();
    }

    private MemoryVoltageLink CreateMemoryLink(IReadOnlyList<Setting> settings)
    {
        var grids = new Dictionary<string, MemoryVoltageGrid>(StringComparer.OrdinalIgnoreCase);
        if (_uiPreview)
        {
            foreach (var s in settings)
                if (MemoryVoltageLink.TryGetRail(s, out string rail))
                {
                    int step = _previewVoltageReadings == "error" && s.Id == "dimmb1_vddq" ? 0 : rail == "VPP" ? 5 : 10;
                    grids[s.Id] = new(rail == "VPP" ? 1500 : 800, step);
                }
        }
        else
        {
            // These are the cached results of existing capability detection, not new reads.
            foreach (var dimm in _hw.Dimms)
            {
                string id = dimm.SlotName.ToLowerInvariant();
                grids[id + "_vdd"] = new(800, dimm.VddStepMv);
                grids[id + "_vddq"] = new(800, dimm.VddqStepMv);
                grids[id + "_vpp"] = new(1500, dimm.VppVerified ? 5 : 0);
            }
        }
        return new(settings, grids);
    }

    private void AddMemoryLinkControls(TableLayoutPanel section)
    {
        var modes = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = false, BackColor = Theme.Panel, Margin = new Padding(6, 2, 0, 2) };
        bool synced = _memoryLink!.IsSynced;
        _splitDimms = new RadioButton { Text = "Split DIMMs", AutoSize = true, Checked = !synced, FlatStyle = FlatStyle.Flat, Font = Theme.Base, ForeColor = synced ? Theme.Text : Theme.Selected, BackColor = Theme.Panel, Margin = new Padding(0, 0, 18, 0) };
        _syncDimms = new RadioButton { Text = "Sync DIMMs", AutoSize = true, Checked = synced, FlatStyle = FlatStyle.Flat, Font = Theme.Base, ForeColor = synced ? Theme.Selected : Theme.Text, BackColor = Theme.Panel, Margin = new Padding(0) };
        _splitDimms.CheckedChanged += (_, _) => _splitDimms.ForeColor = _splitDimms.Checked ? Theme.Selected : Theme.Text;
        _syncDimms.CheckedChanged += (_, _) => _syncDimms.ForeColor = _syncDimms.Checked ? Theme.Selected : Theme.Text;
        modes.Controls.Add(_splitDimms); modes.Controls.Add(_syncDimms);
        _syncDimms.CheckedChanged += (_, _) =>
        {
            _memoryLink!.SetSynced(_syncDimms.Checked);
            // Changing mode only changes the projection; drafts and pending shared text survive.
            UpdateMemoryGroupLabels();
        };
        section.Controls.Add(modes);
        var hint = Theme.Label("PMIC targets; same rail only; staged until Apply.", Theme.Small, Theme.Text);
        hint.Margin = new Padding(6, 0, 0, 3);
        section.Controls.Add(hint);
        foreach (string rail in new[] { "VDD", "VDDQ", "VPP" })
        {
            var row = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, Dock = DockStyle.Top, BackColor = Theme.Panel, Margin = new Padding(0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var name = Theme.Label(rail, Theme.Row, Theme.Text); name.Margin = new Padding(6, 4, 0, 3);
            var frame = Theme.ValueBox(out var box, 76); frame.Margin = new Padding(0, 1, 0, 1);
            box.ReadOnly = _uiPreview;
            var unit = Theme.Muted_("V"); unit.Margin = new Padding(4, 5, 0, 0);
            row.Controls.Add(name, 0, 0); row.Controls.Add(frame, 1, 0); row.Controls.Add(unit, 2, 0);
            var summary = Theme.Label("", Theme.Small, Theme.Text);
            summary.MaximumSize = new Size(420, 0); summary.Margin = new Padding(6, 0, 0, 2);
            _memoryGroupLabels[rail] = summary;
            row.Controls.Add(summary, 0, 1); row.SetColumnSpan(summary, 4);
            _sharedMemoryBoxes[rail] = box; _sharedMemoryRows[rail] = row;
            box.TextChanged += (_, _) =>
            {
                if (_projectingShared || _applying || !box.Focused) return;
                _sharedDraftTexts[rail] = box.Text;
                _pendingSharedMemoryEdits.Add(rail);
            };
            box.Leave += (_, _) =>
            {
                if (!_applying && _memoryLink?.IsSynced == true && _pendingSharedMemoryEdits.Contains(rail))
                    StageSharedMemoryDraft(rail);
            };
            section.Controls.Add(row);
        }
    }

    private void UpdateMemoryGroupLabels()
    {
        if (_memoryLink == null) return;
        foreach (var row in _memoryPerDimmRows.Values) row.Visible = !_memoryLink.IsSynced;
        foreach (var row in _sharedMemoryRows.Values) row.Visible = _memoryLink.IsSynced;
        foreach (var (rail, label) in _memoryGroupLabels)
        {
            var source = _boxes.Keys.FirstOrDefault(s => MemoryVoltageLink.TryGetRail(s, out string r) && r == rail);
            var group = source == null ? null : _memoryLink.GetGroup(source);
            var box = _sharedMemoryBoxes[rail];
            if (group == null) { label.Visible = true; label.Text = "No detected rail"; label.ForeColor = Theme.Muted; box.Enabled = false; continue; }
            var display = _memoryLink.GetDisplay(group, DraftTargets());
            _projectingShared = true;
            try
            {
                box.Enabled = display.CanEdit;
                box.Text = _sharedDraftTexts.GetValueOrDefault(rail, display.Text);
                box.ForeColor = display.IsMixed && !_sharedDraftTexts.ContainsKey(rail) ? Theme.Muted : box.Enabled ? Theme.Accent : Theme.Muted;
            }
            finally { _projectingShared = false; }
            if (!group.CanSync)
            {
                label.Visible = true;
                label.Text = "Sync unavailable - use Split DIMMs";
                label.ForeColor = Theme.Warn;
                _resultTip.SetToolTip(label, group.Reason);
                _resultTip.SetToolTip(box, group.Reason);
                continue;
            }
            // Hide the allowed range/step hint and its layout space; validation still uses
            // the exact same capability intersection and PMIC grids before staging/Apply.
            label.Text = "";
            label.Visible = false;
            label.ForeColor = Theme.Text;
            _resultTip.SetToolTip(label, "Supported targets across detected DIMMs. Mixed targets remain unchanged until you edit this rail. Auto/0 requires Split DIMMs.");
            _resultTip.SetToolTip(box, "A shared staged target for this rail. Mixed means the DIMM setpoints or drafts differ; no value is chosen automatically.");
        }
    }

    /// <summary>Inert target-state fixtures retained for UI QA; no sensor objects or callbacks are used.</summary>
    private void ConfigureVoltageReadingsFixture()
    {
        if (_previewVoltageReadings == "auto-mixed")
            foreach (var setting in _previewSettings.Where(s => s.Id == "core_v" || s.Group == SettingGroup.Memory))
                setting.Current = null;
    }

    private bool StageSharedMemoryDraft(string rail)
    {
        if (_memoryLink?.IsSynced != true || !_sharedMemoryBoxes.TryGetValue(rail, out var box)) return true;
        var source = _boxes.Keys.FirstOrDefault(s => MemoryVoltageLink.TryGetRail(s, out string r) && r == rail);
        if (source == null) return false;
        if (!_memoryLink.TryStage(source, box.Text, out var updates, out string? error))
        {
            _sharedDraftTexts[rail] = box.Text;
            _pendingSharedMemoryEdits.Add(rail);
            SetStatus(error ?? "DIMM targets could not be linked.", true);
            return false;
        }
        _stagingMemory = true;
        try
        {
            foreach (var (setting, text) in updates)
            {
                _settingDrafts[setting.Id] = text;
                if (_boxes.TryGetValue(setting, out var peer)) peer.Text = text;
                _pendingMemoryEdits.Remove(setting);
            }
            _sharedDraftTexts.Remove(rail); _pendingSharedMemoryEdits.Remove(rail);
        }
        finally { _stagingMemory = false; }
        UpdateMemoryGroupLabels();
        return true;
    }

    private bool StageMemoryDraft(Setting source)
    {
        if (_memoryLink?.IsSynced != true || !_boxes.TryGetValue(source, out var box)) return true;
        if (!_memoryLink.TryStage(source, box.Text, out var updates, out string? error))
        {
            SetRowStatus(source, "invalid Sync target", Theme.Danger);
            SetStatus(error ?? "DIMM targets could not be linked.", true);
            return false;
        }
        _stagingMemory = true;
        try
        {
            foreach (var (setting, text) in updates)
            {
                if (_boxes.TryGetValue(setting, out var peer)) peer.Text = text;
                _pendingMemoryEdits.Remove(setting);
                SetRowStatus(setting, "staged", Theme.Text);
            }
        }
        finally { _stagingMemory = false; }
        UpdateMemoryGroupLabels();
        return true;
    }

    private bool PrepareMemoryForApply(out string? error)
    {
        error = null;
        if (_memoryLink?.IsSynced != true) return true;
        foreach (string rail in _pendingSharedMemoryEdits.ToArray())
            if (!StageSharedMemoryDraft(rail)) { error = _lblStatus.Text; return false; }
        // Enter can request Apply while the edited textbox still has focus.
        var focused = _boxes.FirstOrDefault(pair => pair.Value.Focused && _pendingMemoryEdits.Contains(pair.Key));
        if (focused.Key != null && !StageMemoryDraft(focused.Key)) { error = _lblStatus.Text; return false; }
        return _memoryLink.ValidateForApply(DraftTargets(), out error);
    }

    private string DraftText(Setting setting) => _settingDrafts.GetValueOrDefault(setting.Id,
        _boxes.TryGetValue(setting, out var box) ? box.Text : setting.CurrentText);

    private IReadOnlyDictionary<Setting, string> DraftTargets() => _boxes.Keys.ToDictionary(setting => setting, DraftText);

    /// <summary>The right-hand column when there is no live reading or apply result to show.</summary>
    private static string IdleText(Setting s) => s.ReadOnly ? "read-only" : "";

    /// <summary>Hover text for a row: its allowed range first, then what the setting does.</summary>
    private static string? RowTip(Setting s)
    {
        string? range = s.ReadOnly ? null : "Range: " + s.RangeText.Replace("Min:", "").Replace(", Max:", " to ");
        return range == null ? s.Note : s.Note == null ? range : range + "\n\n" + s.Note;
    }

    private static string ThemeTip() => Theme.IsDark ? "Switch to light mode" : "Switch to dark mode";

    /// <summary>Without PawnIO the SMU power table cannot be read, so the AMD limit rows start as Auto.</summary>
    private static LinkLabel PawnIoNote()
    {
        const string site = "pawnio.eu";
        var note = new LinkLabel
        {
            Text = $"PPT, TDC, EDC and Thermal Limit show Auto because PawnIO is not installed. Install it from {site}, then restart Roch CPU to see the values set in the BIOS.",
            Font = Theme.Small, ForeColor = Theme.Warn, LinkColor = Theme.Accent, ActiveLinkColor = Theme.Warn, VisitedLinkColor = Theme.Accent,
            BackColor = Color.Transparent, AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(6, 2, 6, 6)
        };
        note.Links.Clear();
        note.Links.Add(note.Text.IndexOf(site, StringComparison.Ordinal), site.Length, "https://pawnio.eu");
        note.LinkClicked += (_, e) => { try { if (e.Link?.LinkData is string url) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { } };
        return note;
    }

    /// <summary>Use natural content height within the work area; overflow stays in the settings viewport.</summary>
    private void FitToContent()
    {
        _body.PerformLayout();
        int needed = _rows.PreferredSize.Height + _body.Padding.Vertical + _rows.Margin.Vertical
            + _body.Controls.Cast<Control>().Where(c => c != _rowsViewport).Sum(c => c.PreferredSize.Height + c.Margin.Vertical)
            + (int)Math.Ceiling(54 * DeviceDpi / 96.0) + 2;
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
    private void OnPreviewLoad(object? sender, EventArgs e)
    {
        Text = AppName + " - UI preview";
        _lblCpu.Text = "CPU: Fixture - Intel Core i5-14600KF";
        _lblCores.Text = "Cores / Threads: 6 / 12";
        _lblMicrocode.Text = "Microcode: 0x11F (fixture)";
        _lblBoard.Text = "Motherboard: Z790MPOWER (fixture)";
        _lblAgesa.Visible = false;
        _lblBios.Text = "BIOS: P.A0 (fixture)";
        _lblLive.Text = "Fixture: P x54 E x? Ring x50 | VID 1.270 V | " + (_previewVoltageReadings == "error" ? "Vcore unavailable" : "Vcore 1.276 V");
        _lblWarn.Text = "UI PREVIEW - fixed fixture values; no hardware access or changes.";
        _lblWarn.Visible = true;
        _btnApply.Enabled = _btnReset.Enabled = _btnPerCore.Enabled = false;
        BuildRows();
        SetStatus("UI preview - no hardware access", false);
        AppendLog("UI preview: hardware model, drivers and refresh timers are not initialized.");
        FitToContent();
        BeginInvoke(() =>
        {
            ActiveControl = null;
            // Fixture-only layout aid: expose the final memory row without changing values,
            // starting refresh timers, invoking hardware callbacks or saving preferences.
            if (_previewScrollBottom)
            {
                Control? last = _memoryLink?.IsSynced == true ? _sharedMemoryRows.GetValueOrDefault("VPP") : _memoryPerDimmRows.GetValueOrDefault(_previewSettings[^1]);
                if (last != null) _rowsViewport.ScrollControlIntoView(last);
            }
        });
    }

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
            _lblCpu.Text = $"CPU: {_hw.CpuName}";
            _lblCores.Text = $"Cores / Threads: {_hw.CoreSummary}";
            _lblMicrocode.Text = _hw.MicrocodeRevision > 0
                ? $"Microcode: 0x{_hw.MicrocodeRevision:X}"
                : "Microcode: Unknown";
        }
        else
        {
            _lblCpu.Text = "CPU: No CPU access";
            _lblCores.Text = $"Driver: {_hw.DriverStatus}";
            _lblMicrocode.Text = "Microcode: Unknown";
        }
        _lblBoard.Text = $"Motherboard: {(string.IsNullOrWhiteSpace(sm.BoardProduct) ? sm.SystemProduct : sm.BoardProduct)}";
        _lblAgesa.Text = $"AGESA: {sm.Agesa}";
        _lblAgesa.Visible = sm.Agesa != "";
        _lblBios.Text = $"BIOS: {sm.BiosVersion}";

        var warns = new List<string>();
        if (_hw.Driver == null) warns.Add("kernel driver not loaded, nothing can be read or written (see Log)");
        if (_hw.OcLocked) warns.Add("BIOS OC Lock set: ratio and voltage writes will be rejected");
        if (_hw.VoltageAccessRestriction != null) warns.Add("Windows Hyper-V blocked voltage access (see Log)");
        else
        {
            if (_hw.Cpu != null && !_hw.MailboxAvailable) warns.Add("OC mailbox not responding: voltage rows disabled");
            if (_hw.Cpu is { HypervisorPresent: true }) warns.Add("Hypervisor active: read-back values may not reach hardware (see Log)");
        }
        if (_hw.Cpu is { IsLga1700Family: false }) warns.Add("not a known LGA1700 CPU");
        if (_hw.Amd is { IsSupported: false }) warns.Add("unknown Zen generation: SMU message numbers assumed");
        if (_hw.Amd != null && !_hw.SmuAvailable) warns.Add("SMU not responding: PBO rows disabled (see Log)");
        if (_hw.Amd != null && _hw.SmuAvailable && !_hw.PboAllowed) warns.Add("SMU reports PBO unavailable: enable Precision Boost Overdrive in the BIOS or writes will be rejected");
        _lblWarn.Text = string.Join("  ·  ", warns);
        _lblWarn.Visible = warns.Count > 0;

        BuildRows();
        if (_hw.IsAmd)
        {
            _btnPerCore.Text = "Curve Optimizer (per core)";
            _btnPerCore.Enabled = _hw.SmuAvailable && _hw.Amd!.Smu.Messages.HasCurveOptimizer;
            // The live line reports Intel P/E/ring ratios and VID; on AMD it would only hold an empty row.
            _lblLive.Visible = false;
        }
        else _btnPerCore.Enabled = _hw.Cpu != null;
        SetStatus($"Ready  ·  {_hw.DriverStatus}", false);

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
        // Disposing the driver mid-apply could leave the base clock on an intermediate value with
        // nothing left to put it back. An apply is short; ask again in a moment.
        if (_applying)
        {
            e.Cancel = true;
            SetStatus("Still applying - closing once it finishes.", false);
            return;
        }
        _slowRefresh.Stop();
        _logForm.AllowClose = true;
        _logForm.Close();
        _runtimeHardware?.Dispose();
    }

    private void AppendLog(string msg)
    {
        void Do() => _logForm.Append(msg);
        if (InvokeRequired) BeginInvoke(Do); else Do();
    }

    private void SetStatus(string text, bool error)
    {
        _lblStatus.Text = text;
        _lblStatus.ForeColor = error ? Theme.Danger : Theme.Text;
        _resultTip.SetToolTip(_lblStatus, text);
    }

    private void ToggleLog()
    {
        if (_logForm.Visible) _logForm.Hide();
        else { _logForm.Show(this); _logForm.Location = new Point(Right + 6, Top); }
    }

    private void SlowTick()
    {
        if (_uiPreview) return;
        if (_applying) return;
        if (_hw.IsAmd) { AmdTick(); return; }
        if (_hw.Cpu == null) return;
        var live = _hw.ReadLive();
        _lblLive.Text = $"Live: P x{live.CoreRatio?.ToString() ?? "?"} E x{live.ECoreRatio?.ToString() ?? "?"} Ring x{live.RingRatio?.ToString() ?? "?"} | VID {live.CoreVid?.ToString("0.000") ?? "?"} V | " +
            (live.VcoreVrm is double rail ? $"Vcore {rail:0.000} V" : "Vcore unavailable");
        RefreshBclk();
        // Existing rail reads continue at the same rate; a staged writable request stays intact.
        foreach (var s in _hw.Settings.Where(s => s.Group == SettingGroup.Board && s.Available))
            if (_boxes.TryGetValue(s, out var box))
            {
                bool untouched = !box.Focused && box.Text == s.CurrentText;
                _hw.Refresh(s);
                if (s.ReadOnly || untouched) box.Text = s.CurrentText;
            }

        // The mailbox voltage rows were read once at start-up and then never again, so a single
        // read that came back wrong - one was seen reporting a domain as Auto that was in fact
        // holding an override - stayed on screen for the life of the process. Re-read them, but
        // only where the box still shows what was last read: anything the user has typed is
        // theirs until they apply or revert it.
        foreach (var s in _hw.Settings.Where(s => s.Group == SettingGroup.Voltages && s.Available))
        {
            if (!_boxes.TryGetValue(s, out var box) || box.Focused) continue;
            string was = s.CurrentText;
            if (box.Text != was && !IsUntouchedCorePlaceholder(s, box.Text)) continue;
            _hw.Refresh(s);
            string display = CoreDisplayText(s);
            if (box.Text != display) box.Text = display;
        }
    }

    /// <summary>Starts a base-clock measurement on a worker and shows the last finished one.</summary>
    private void RefreshBclk()
    {
        if (_uiPreview) return;
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
    }

    /// <summary>AMD: measured base clock, and what the CPU is drawing against each power limit.</summary>
    private void AmdTick()
    {
        if (_uiPreview) return;
        RefreshBclk();
        foreach (var (s, label) in _rangeLabels)
        {
            // An apply result stays readable for ten seconds before the live reading takes the label back.
            if (s.Live == null || (_rowResults.TryGetValue(s, out var shown) && DateTime.UtcNow - shown < TimeSpan.FromSeconds(10))) continue;
            label.Text = s.Live() is double now ? $"Live {s.Format(now)} {s.Unit}" : IdleText(s);
            label.ForeColor = s.ReadOnly ? Theme.Muted : Theme.Text;
        }
    }

    // ------------------------------------------------------------------ apply
    private void RefreshRows(string? message = null)
    {
        if (_uiPreview) return;
        _rowResults.Clear();
        _hw.RefreshAll();
        ShowReadbackTexts();
        foreach (var (s, l) in _rangeLabels) { l.Text = !s.Available ? "unavailable" : IdleText(s); l.ForeColor = s.ReadOnly || !s.Available ? Theme.Muted : Theme.Text; }
        if (message != null) { AppendLog(message); SetStatus(message, false); }
    }

    /// <summary>
    /// Runs hardware work on a worker thread so the window stays live, and puts the controls back
    /// when it finishes. Anything that writes hardware from the buttons goes through here.
    ///
    /// A base-clock or VDD2 change measures the result after every step, so it takes a second or
    /// more; doing that inline froze the window for the whole operation. The slow refresh is
    /// stopped for the duration, and not only so its repaint does not fight the row updates: it
    /// measures the base clock on its own worker, and two overlapping measurements share the same
    /// fixed counters, which would make a step look like it had missed and trigger a restore that
    /// was never needed.
    /// </summary>
    private void RunOnHardware(string busyText, Func<(string Status, bool Error)> work)
    {
        if (_uiPreview) return;
        if (_applying) return;
        _applying = true;
        _slowRefresh.Stop();
        SetControlsEnabled(false);
        SetStatus(busyText, false);

        Task.Run(() =>
        {
            // A base-clock measurement started by the last refresh may still be in flight, and it
            // owns the same fixed counters this is about to measure with. Let it finish.
            for (int i = 0; i < 40 && _bclkBusy; i++) Thread.Sleep(25);

            (string Status, bool Error) result;
            try { result = work(); }
            catch (Exception ex) { result = (ex.Message, true); }

            BeginInvoke(() =>
            {
                SetStatus(result.Status, result.Error);
                SetControlsEnabled(true);
                _applying = false;
                _slowRefresh.Start();
            });
        });
    }

    private void ApplyAll()
    {
        if (_uiPreview) return;
        if (_applying) return;
        if (!PrepareMemoryForApply(out string? memoryError))
        {
            SetStatus(memoryError ?? "DIMM targets are invalid.", true);
            AppendLog("Apply refused: " + memoryError);
            return;
        }

        // Parsing and the confirmation dialog stay on the UI thread, where they belong.
        var work = new List<(Setting Setting, TextBox Box, double Value)>();
        int rejected = 0;
        var linkedRails = new HashSet<string>();
        foreach (var (s, box) in _boxes)
        {
            if (!s.Available || s.ReadOnly) continue;
            if (_memoryLink?.IsSynced == true && MemoryVoltageLink.TryGetRail(s, out string rail))
            {
                if (!linkedRails.Add(rail)) continue;
                var group = _memoryLink.GetGroup(s)!;
                if (!group.Members.Any(member => IsMemoryTargetEdited(member, DraftText(member)))) continue;
                if (!group.CanSync) { rejected++; continue; } // Defensive: preflight must already have rejected this group.
                var linkedWork = new List<(Setting Setting, TextBox Box, double Value)>();
                bool confirmed = true;
                foreach (var member in group.Members)
                {
                    if (!_boxes.TryGetValue(member, out var peer)) continue;
                    string target = DraftText(member).Trim();
                    if (!IsMemoryTargetEdited(member, target)) continue;
                    // The complete group was validated before any work was queued.
                    if (!member.TryParse(target, out double linkedValue)) { confirmed = false; rejected++; break; }
                    if (!ConfirmDangerous(member, linkedValue)) { confirmed = false; break; }
                    linkedWork.Add((member, peer, linkedValue));
                }
                if (confirmed) work.AddRange(linkedWork);
                else foreach (var member in group.Members) SetRowStatus(member, "group skipped", Theme.Muted);
                continue;
            }
            string text = box.Text.Trim();
            if (text.Length == 0) continue;
            if (IsUntouchedCorePlaceholder(s, text)) continue;
            if (text.Equals(s.CurrentText, StringComparison.OrdinalIgnoreCase) && s.LastError == null) continue;
            if (!s.TryParse(text, out double value)) { AppendLog($"{s.Name}: '{text}' is not a number."); rejected++; SetRowStatus(s, "invalid", Theme.Danger); continue; }
            if (value != 0 && !ConfirmDangerous(s, value)) { SetRowStatus(s, "skipped", Theme.Muted); continue; }
            work.Add((s, box, value));
        }

        if (work.Count == 0)
        {
            if (rejected == 0) SetStatus("No edits to apply; displayed values are register readings.", false);
            else SetStatus($"Applied at {DateTime.Now:HH:mm:ss}  ·  0 ok, {rejected} failed", true);
            return;
        }

        int preFailed = rejected;
        RunOnHardware("Applying...", () =>
        {
            int applied = 0, failed = preFailed;
            foreach (var (s, box, value) in work)
            {
                bool ok = _hw.Apply(s, value);
                bool ignored = ok && s.Id is "core_v" or "core_off" && _hw.CoreVoltageIgnored;
                if (ok && !ignored) applied++; else failed++;
                BeginInvoke(() =>
                {
                    SetRowStatus(s, ignored ? "board ignored it" : ok ? s.LastResult ?? "applied" : "failed - see Log", ok && !ignored ? Theme.Ok : Theme.Danger);
                    _stagingMemory = true;
                    try { box.Text = CoreDisplayText(s); _pendingMemoryEdits.Remove(s); }
                    finally { _stagingMemory = false; }
                    UpdateMemoryGroupLabels();
                });
            }
            AppendLog($"Apply: {applied} applied, {failed} failed.");
            return ($"Applied at {DateTime.Now:HH:mm:ss}  ·  {applied} ok, {failed} failed", failed > 0);
        });
    }

    private void SetControlsEnabled(bool on)
    {
        _btnApply.Enabled = on; _btnReset.Enabled = on;
        _btnApply.Text = on ? "Apply" : "Working...";
        if (_splitDimms != null) _splitDimms.Enabled = on;
        if (_syncDimms != null) _syncDimms.Enabled = on;
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
            // No ceiling on base clock, but it takes the memory controller and the ring with it,
            // so a figure this far out is worth a second look before it is walked to.
            "bclk" => value > BclkController.ConfirmAboveMHz,
            _ => false
        };
        if (!risky) return true;
        return MessageBox.Show(this, $"{s.Name} = {s.Format(value)} {s.Unit} is above the usual safe range for daily use and can damage hardware.\n\nApply it anyway?",
            AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    private void SetRowStatus(Setting s, string text, Color color)
    {
        if (_statusLabels.TryGetValue(s, out var l)) { l.Text = text; l.ForeColor = color; _resultTip.SetToolTip(l, s.LastError ?? RowTip(s)); _rowResults[s] = DateTime.UtcNow; }
    }

    private void RestoreDefaults()
    {
        if (_uiPreview) return;
        if (_applying) return;
        if (MessageBox.Show(this, "Reset changed writable settings to their existing defaults? Voltage overrides may return to Auto.", AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        // On a worker for the same reason as Apply: putting the base clock and VDD2 back walks
        // them a step at a time, measuring each, which is seconds of work.
        RunOnHardware("Restoring...", () =>
        {
            int failures = _hw.RestoreAllDefaults();
            _hw.RefreshAll();
            BeginInvoke(() =>
            {
                _rowResults.Clear();
                ShowReadbackTexts();
                foreach (var (s, l) in _rangeLabels) { l.Text = !s.Available ? "unavailable" : IdleText(s); l.ForeColor = s.ReadOnly || !s.Available ? Theme.Muted : Theme.Text; }
            });
            string result = failures == 0 ? "Reset to Default: defaults/Auto restored." : $"Reset to Default: {failures} setting(s) failed; see Log.";
            AppendLog(result);
            return (result, failures > 0);
        });
    }

    private void ShowReadbackTexts()
    {
        _stagingMemory = true;
        try
        {
            foreach (var (setting, box) in _boxes)
                box.Text = CoreDisplayText(setting);
            _pendingMemoryEdits.Clear();
            _pendingSharedMemoryEdits.Clear(); _sharedDraftTexts.Clear();
        }
        finally { _stagingMemory = false; }
        UpdateMemoryGroupLabels();
    }

    private bool CoreTargetReadFailed => _uiPreview ? _previewCoreReadFailed : _hw.CoreVoltageTargetReadFailed;

    private string CoreDisplayText(Setting setting) => setting.Id == "core_v" && (!setting.Available || CoreTargetReadFailed)
        ? "Not read" : setting.CurrentText;

    private bool IsUntouchedCorePlaceholder(Setting setting, string text) => setting.Id == "core_v" &&
        text.Equals("Not read", StringComparison.OrdinalIgnoreCase) && (!setting.Available || CoreTargetReadFailed);

    private static bool IsMemoryTargetEdited(Setting setting, string text) => (setting.LastError != null && setting.Available && !setting.ReadOnly) ||
        !setting.TryParse(text, out double target) || !setting.TryParse(setting.CurrentText, out double current) ||
        Math.Abs(target - current) >= 0.0000001;

    // ------------------------------------------------------------------ manual per core
    private void OpenPerCore()
    {
        if (_uiPreview) return;
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

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_uiPreview) return;
        switch (e.KeyCode)
        {
            case Keys.Enter when ActiveControl is not Button: ApplyAll(); e.Handled = true; e.SuppressKeyPress = true; break;
        }
    }
}
