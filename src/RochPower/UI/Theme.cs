using System.Runtime.InteropServices;

namespace RochPower.UI;

/// <summary>
/// The Roch Studio look shared with Roch GPU and Roch Viewer: black, grey, red, white.
/// No OS chrome; a drawn title bar; flat controls with a red accent on hover.
/// </summary>
public static class Theme
{
    // Palette shared with Roch Viewer's dark theme, taken from its own constants so the two
    // tools read as one family rather than merely similar.
    public static readonly Color Bg = ColorTranslator.FromHtml("#101010");        // BG_COLOR
    public static readonly Color Panel = ColorTranslator.FromHtml("#161616");     // BG_COLOR2 / SECTION_COLOR
    public static readonly Color PanelAlt = ColorTranslator.FromHtml("#1A1A1A");  // ROW_COLOR
    public static readonly Color Header = ColorTranslator.FromHtml("#1C1C1C");    // HEADER_COLOR
    public static readonly Color Highlight = ColorTranslator.FromHtml("#171717"); // HIGHLIGHT_COLOR
    public static readonly Color Border = ColorTranslator.FromHtml("#2A2A2A");
    public static readonly Color Text = ColorTranslator.FromHtml("#FFFFFF");      // TEXT_COLOR
    public static readonly Color Muted = ColorTranslator.FromHtml("#B0B0B0");     // SUBTITLE_COLOR
    public static readonly Color Accent = ColorTranslator.FromHtml("#FF4D4D");    // BRAND_COLOR / VALUE_COLOR
    public static readonly Color AccentDim = ColorTranslator.FromHtml("#5D1A1A");
    public static readonly Color Hairline = ColorTranslator.FromHtml("#C53F3F");  // HAIRLINE_COLOR
    public static readonly Color Warn = ColorTranslator.FromHtml("#FF8080");      // BRAND_HOVER_COLOR
    public static readonly Color Danger = ColorTranslator.FromHtml("#FF4D4D");
    public static readonly Color Ok = ColorTranslator.FromHtml("#B0B0B0");

    // Roch Viewer is monospace throughout (Consolas). It uses size 12 for body text; 10 here
    // keeps the same character while letting every row stay on screen without scrolling.
    private const string Family = "Consolas";
    private const float Size = 10f;
    public static readonly Font Base = new(Family, Size);
    public static readonly Font Bold = new(Family, Size, FontStyle.Bold);
    public static readonly Font Small = new(Family, Size - 1f);
    public static readonly Font SmallBold = new(Family, Size - 1f, FontStyle.Bold);
    public static readonly Font Row = new(Family, Size);
    public static readonly Font Value = new(Family, Size, FontStyle.Bold);
    public static readonly Font Big = new(Family, Size + 2f, FontStyle.Bold);
    public static readonly Font Brand = new(Family, Size + 1f, FontStyle.Bold);
    public static readonly Font Glyph = new("Segoe MDL2 Assets", 8f);
    public static readonly Font Mono = new(Family, Size - 1f);

    public const string Author = "@MateoPCTech";
    public const string AuthorUrl = "https://x.com/MateoPCTech";

    // ------------------------------------------------------------------ controls
    public static Label Label(string text, Font? font = null, Color? color = null, bool autoSize = true) => new()
    {
        Text = text, Font = font ?? Base, ForeColor = color ?? Text, BackColor = Color.Transparent,
        AutoSize = autoSize, Margin = new Padding(0)
    };

    public static Label Muted_(string text, Font? font = null) => Label(text, font ?? Small, Muted);

    public static Label SectionTitle(string text)
    {
        var l = Label(text.ToUpperInvariant(), SmallBold, Muted);
        l.Margin = new Padding(0, 12, 0, 4);
        return l;
    }

    /// <summary>Flat button: dark plate, grey border, red border on hover. Primary = solid red.</summary>
    public static Button Button(string text, bool primary = false, bool danger = false)
    {
        var b = new Button
        {
            Text = text, FlatStyle = FlatStyle.Flat, Font = primary ? Bold : Base, Cursor = Cursors.Hand,
            BackColor = primary ? Accent : PanelAlt, ForeColor = primary ? Color.White : Text,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 3, 10, 3),
            Margin = new Padding(0, 0, 4, 0), UseVisualStyleBackColor = false, TabStop = false
        };
        b.FlatAppearance.BorderColor = primary ? Accent : danger ? Danger : Border;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.MouseOverBackColor = primary ? Danger : PanelAlt;
        b.FlatAppearance.MouseDownBackColor = primary ? AccentDim : Panel;
        if (!primary)
        {
            b.MouseEnter += (_, _) => b.FlatAppearance.BorderColor = Accent;
            b.MouseLeave += (_, _) => b.FlatAppearance.BorderColor = danger ? Danger : Border;
        }
        b.EnabledChanged += (_, _) => b.ForeColor = b.Enabled ? (primary ? Color.White : Text) : Muted;
        return b;
    }

    /// <summary>Title-bar glyph button (minimise / close): transparent until hovered.</summary>
    public static Button TitleButton(string glyph, bool close = false)
    {
        var b = new Button
        {
            Text = glyph, Font = Glyph, FlatStyle = FlatStyle.Flat, Width = 44, Height = 30, Margin = new Padding(0),
            BackColor = Bg, ForeColor = Muted, TabStop = false, Cursor = Cursors.Default, UseVisualStyleBackColor = false
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = close ? Danger : PanelAlt;
        b.FlatAppearance.MouseDownBackColor = close ? AccentDim : Panel;
        b.MouseEnter += (_, _) => b.ForeColor = close ? Color.White : Text;
        b.MouseLeave += (_, _) => b.ForeColor = Muted;
        return b;
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
                int on = 1;
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
