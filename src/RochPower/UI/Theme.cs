using System.Runtime.InteropServices;

namespace RochPower.UI;

/// <summary>
/// The Roch Studio look shared with Roch GPU and Roch Viewer: black, grey, red, white.
/// No OS chrome; a drawn title bar; flat controls with a red accent on hover.
/// </summary>
public static class Theme
{
    private static bool _preview;
    public static bool IsDark { get; private set; } = LoadDark();
    private static string PreferencePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roch CPU", "theme.txt");
    /// <summary>Light unless the user has chosen dark.</summary>
    private static bool LoadDark() { try { return File.ReadAllText(PreferencePath).Trim() == "dark"; } catch { return false; } }
    private static Color Tone(string dark, string light) => ColorTranslator.FromHtml(IsDark ? dark : light);
    // Pure surfaces and shared semantic roles from the Roch family UI contract.
    public static Color Bg => Tone("#000000", "#FFFFFF");
    public static Color Panel => Bg;
    public static Color PanelAlt => Bg;
    public static Color Header => Bg;
    /// <summary>Title bar: the same colour as the body, so the window reads as one surface.</summary>
    public static Color TitleBar => Bg;
    public static Color Highlight => PanelAlt;
    public static Color Border => Tone("#383838", "#D0D0D0");
    public static Color Text => Tone("#FFFFFF", "#000000");
    public static Color Muted => Tone("#A0A0A0", "#62666D");
    public static Color TitleHover => Tone("#222222", "#E6E6E6");
    public static Color CloseHover => ColorTranslator.FromHtml("#C42B1C");
    public static Color Accent => Tone("#FF5A5F", "#B91C1C");
    public static Color Selected => ColorTranslator.FromHtml("#D0343A");
    public static Color Hover => ColorTranslator.FromHtml("#E0383E");
    public static Color AccentDim => Selected;
    public static Color Hairline => Accent;
    public static Color Warn => Accent;
    public static Color Danger => Accent;
    public static Color Ok => Text;

    private static Color[] Palette() => new[] { Bg, Panel, PanelAlt, Header, Highlight, Border, Text, Muted, Accent, AccentDim, Hairline, Warn, TitleHover };

    /// <summary>The explicit fixture mode changes colors only in memory, without saving user preferences.</summary>
    internal static void ConfigurePreview(bool dark)
    {
        _preview = true;
        IsDark = dark;
    }
    public static void Toggle(params Form[] windows)
    {
        var previous = Palette();
        IsDark = !IsDark;
        var current = Palette();
        Color Map(Color color)
        {
            int index = Array.FindIndex(previous, p => p.ToArgb() == color.ToArgb());
            return index < 0 ? color : current[index];
        }
        void Repaint(Control control)
        {
            bool primary = control is Button && control.BackColor.ToArgb() == Selected.ToArgb();
            control.BackColor = Map(control.BackColor);
            control.ForeColor = primary && control.Enabled ? Color.White : Map(control.ForeColor);
            if (control is Button button)
            {
                button.FlatAppearance.BorderColor = Map(button.FlatAppearance.BorderColor);
                button.FlatAppearance.MouseOverBackColor = Map(button.FlatAppearance.MouseOverBackColor);
                button.FlatAppearance.MouseDownBackColor = Map(button.FlatAppearance.MouseDownBackColor);
            }
            if (control is LinkLabel link)
            {
                bool brand = link.VisitedLinkColor.ToArgb() == Selected.ToArgb();
                link.LinkColor = brand ? Selected : Accent;
                link.VisitedLinkColor = brand ? Selected : Accent;
                link.ActiveLinkColor = brand ? Hover : Warn;
            }
            foreach (Control child in control.Controls) Repaint(child);
            control.Invalidate();
        }
        foreach (var window in windows.Concat(Application.OpenForms.Cast<Form>()).Distinct().ToArray())
        {
            if (window.IsDisposed) continue;
            window.SuspendLayout();
            Repaint(window);
            if (window.IsHandleCreated) SetTitleTheme(window);
            window.ResumeLayout(true);
        }
        if (!_preview)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(PreferencePath, IsDark ? "dark" : "light");
        }
    }

    private static void SetTitleTheme(Form form)
    {
        try
        {
            int on = IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, 19, ref on, sizeof(int));
        }
        catch { }
    }

    // Viewer uses 12 logical pixels. WinForms font sizes are points: 9 pt = 12 px at 96 DPI.
    private const string Family = "Consolas";
    private const float Size = 9f;
    public static readonly Font Base = new(Family, Size);
    public static readonly Font Bold = new(Family, Size, FontStyle.Bold);
    public static readonly Font Small = new(Family, Size);
    public static readonly Font SmallBold = new(Family, Size, FontStyle.Bold);
    public static readonly Font Row = new(Family, Size);
    public static readonly Font Value = new(Family, Size);
    public static readonly Font Big = new(Family, Size, FontStyle.Bold);
    public static readonly Font Brand = new(Family, Size, FontStyle.Bold);
    public static readonly Font Glyph = new("Segoe UI Symbol", 13.5f); // 18 px at 96 DPI
    public static readonly Font CloseGlyph = new("Segoe UI Symbol", 15f); // 20 px at 96 DPI
    public static readonly Font Mono = new(Family, Size);

    public const string Author = "@MateoPCTech";
    public const string AuthorUrl = "https://x.com/MateoPCTech";

    // ------------------------------------------------------------------ controls
    public static Label Label(string text, Font? font = null, Color? color = null, bool autoSize = true) => new()
    {
        Text = text, Font = font ?? Base, ForeColor = color ?? Text, BackColor = Color.Transparent,
        AutoSize = autoSize, Margin = new Padding(0)
    };

    // Secondary information uses primary contrast; gray is reserved for disabled/inactive states.
    public static Label Muted_(string text, Font? font = null) => Label(text, font ?? Small, Text);

    public static Label SectionTitle(string text)
    {
        var l = Label(text.ToUpperInvariant(), SmallBold, Text);
        l.Margin = new Padding(0, 12, 0, 4);
        return l;
    }

    /// <summary>Flat button: dark plate, grey border, red border on hover. Primary = solid red.</summary>
    public static Button Button(string text, bool primary = false, bool danger = false)
    {
        var b = new ThemedButton
        {
            Text = text, FlatStyle = FlatStyle.Flat, Font = primary ? Bold : Base, Cursor = Cursors.Hand,
            BackColor = primary ? Selected : PanelAlt, ForeColor = primary ? Color.White : Text,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 3, 10, 3),
            Margin = new Padding(0, 0, 4, 0), UseVisualStyleBackColor = false, TabStop = false
        };
        b.FlatAppearance.BorderColor = primary ? Selected : danger ? Danger : Border;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.MouseOverBackColor = primary ? Hover : PanelAlt;
        b.FlatAppearance.MouseDownBackColor = primary ? AccentDim : Panel;
        if (!primary)
        {
            b.MouseEnter += (_, _) => { if (b.Enabled) b.FlatAppearance.BorderColor = Accent; };
            b.MouseLeave += (_, _) => b.FlatAppearance.BorderColor = b.Enabled && danger ? Danger : Border;
        }
        b.EnabledChanged += (_, _) =>
        {
            b.BackColor = b.Enabled && primary ? Selected : PanelAlt;
            b.ForeColor = b.Enabled ? (primary ? Color.White : Text) : Muted;
            b.FlatAppearance.BorderColor = b.Enabled ? (primary ? Selected : danger ? Danger : Border) : Border;
        };
        return b;
    }

    // WinForms' flat button renderer uses a system disabled-text color, which becomes
    // nearly black on our dark plates. Only disabled painting needs a palette override.
    private sealed class ThemedButton : Button
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Enabled) { base.OnPaint(e); return; }

            using var background = new SolidBrush(BackColor);
            e.Graphics.FillRectangle(background, ClientRectangle);
            if (FlatAppearance.BorderSize > 0)
                ControlPaint.DrawBorder(e.Graphics, ClientRectangle, FlatAppearance.BorderColor, ButtonBorderStyle.Solid);

            var textBounds = Rectangle.FromLTRB(Padding.Left, Padding.Top,
                Math.Max(Padding.Left, ClientSize.Width - Padding.Right),
                Math.Max(Padding.Top, ClientSize.Height - Padding.Bottom));
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsClipping;
            if (!UseMnemonic) flags |= TextFormatFlags.NoPrefix;
            else if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
            TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, Muted, BackColor, flags);
        }
    }

    // Same visible caption characters and symbol font as Roch Viewer.
    public const string GlyphMinimise = "\u2212";
    public const string GlyphClose = "\u00D7";
    public const string GlyphSun = "\u2600";
    public const string GlyphMoon = "\u263E";
    /// <summary>The theme button shows where it goes: a moon in light mode, a sun in dark mode.</summary>
    public static string GlyphTheme => IsDark ? GlyphSun : GlyphMoon;

    /// <summary>
    /// Title-bar glyph button (minimise / close): sits flat on the title bar until hovered, then
    /// lifts to grey, or to red for close, the way Windows does it.
    /// </summary>
    public static Button TitleButton(string glyph, bool close = false)
    {
        var b = new CaptionButton
        {
            Text = glyph, Font = close ? CloseGlyph : Glyph, FlatStyle = FlatStyle.Flat, Width = 44, Height = 30, Margin = new Padding(0),
            Padding = new Padding(0), BackColor = TitleBar, ForeColor = Text, TabStop = false, Cursor = Cursors.Default, UseVisualStyleBackColor = false
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = close ? CloseHover : TitleHover;
        b.FlatAppearance.MouseDownBackColor = close ? CloseHover : TitleHover;
        b.MouseEnter += (_, _) => b.ForeColor = close ? Color.White : Text;
        b.MouseLeave += (_, _) => b.ForeColor = Text;
        return b;
    }

    private sealed class CaptionButton : Button
    {
        private bool _hovered;
        protected override void OnMouseEnter(EventArgs e) { _hovered = true; base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { _hovered = false; base.OnMouseLeave(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Text != GlyphMoon) { base.OnPaint(e); return; }
            // Draw the common filled crescent instead of the font's outlined moon glyph.
            // The sun, minimize and close retain their normal symbol-font rendering.
            using var background = new SolidBrush(_hovered && Enabled ? FlatAppearance.MouseOverBackColor : BackColor);
            e.Graphics.FillRectangle(background, ClientRectangle);
            SharedMoonGeometry.Draw(e.Graphics, ClientRectangle, DeviceDpi, Enabled ? Theme.Text : Muted);
        }
    }

    /// <summary>A typeable value in red on a dark plate with a 1 px grey border (the Roch GPU "ValueBox").</summary>
    public static Panel ValueBox(out TextBox box, int width = 72)
    {
        var frame = new Panel { BackColor = Border, Padding = new Padding(1), Width = width, Height = 24, Margin = new Padding(6, 0, 0, 0) };
        box = new TextBox
        {
            BorderStyle = BorderStyle.None, BackColor = PanelAlt, ForeColor = Accent, Font = Value,
            TextAlign = HorizontalAlignment.Right, Dock = DockStyle.Fill, Margin = new Padding(0)
        };
        var inner = new Panel { BackColor = PanelAlt, Dock = DockStyle.Fill, Padding = new Padding(4, 3, 4, 0) };
        inner.Controls.Add(box);
        frame.Controls.Add(inner);
        var tb = box;
        tb.Enter += (_, _) => { frame.BackColor = Accent; tb.SelectAll(); };
        tb.Leave += (_, _) => frame.BackColor = Border;
        tb.EnabledChanged += (_, _) => tb.ForeColor = tb.Enabled ? Accent : Muted;
        return frame;
    }

    public static CheckBox CheckBox(string text) => new()
    {
        Text = text, Font = Base, ForeColor = Text, BackColor = Color.Transparent, AutoSize = true,
        FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Margin = new Padding(0)
    };

    public static Panel Rule() => new() { Height = 1, BackColor = Border, Dock = DockStyle.Top, Margin = new Padding(0) };

    // ------------------------------------------------------------------ window helpers
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;

    /// <summary>Dark palette on a dialog that keeps the OS title bar; asks DWM to paint that bar dark too.</summary>
    public static void ApplyDark(Form f)
    {
        f.BackColor = Bg; f.ForeColor = Text; f.Font = Base;
        f.HandleCreated += (_, _) =>
        {
            try
            {
                int on = IsDark ? 1 : 0;
                if (DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, sizeof(int));
            }
            catch { }
        };
    }

    /// <summary>Lets a control act as the drag handle of a borderless form.</summary>
    public static void EnableDrag(Control handle, Form form)
    {
        handle.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(form.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        };
    }

    public static Icon? LoadAppIcon()
    {
        try
        {
            string p = Path.Combine(AppContext.BaseDirectory, "RochCPU.ico");
            if (File.Exists(p)) return new Icon(p);
        }
        catch { }
        return null;
    }

    public static Image? LoadMark(int size)
    {
        try
        {
            string p = Path.Combine(AppContext.BaseDirectory, "mark.png");
            if (!File.Exists(p)) return null;
            using var src = Image.FromFile(p);
            var bmp = new Bitmap(size, size);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, size, size);
            return bmp;
        }
        catch { return null; }
    }
}
