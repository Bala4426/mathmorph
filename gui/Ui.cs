using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace MathMorph.Gui;

/// <summary>Design system: colour tokens, type scale, spacing and small control factories (built-in Windows Forms only).</summary>
internal static class Ui
{
    // --- colour tokens (contrast of Text/Muted on Bg/Card >= 4.5:1)
    public static readonly Color Bg = Color.FromArgb(243, 245, 250);
    public static readonly Color Card = Color.White;
    public static readonly Color Border = Color.FromArgb(221, 226, 234);
    public static readonly Color BorderStrong = Color.FromArgb(196, 203, 214);
    public static readonly Color Text = Color.FromArgb(23, 27, 36);
    public static readonly Color Muted = Color.FromArgb(87, 96, 110);
    public static readonly Color Accent = Color.FromArgb(79, 70, 229);        // indigo 600
    public static readonly Color AccentHover = Color.FromArgb(67, 56, 202);   // indigo 700
    public static readonly Color AccentPressed = Color.FromArgb(55, 48, 163); // indigo 800
    public static readonly Color AccentSoft = Color.FromArgb(238, 237, 253);
    public static readonly Color AccentDisabled = Color.FromArgb(180, 177, 236);
    public static readonly Color HoverSoft = Color.FromArgb(241, 243, 248);
    public static readonly Color PressedSoft = Color.FromArgb(229, 233, 241);
    public static readonly Color Ok = Color.FromArgb(21, 128, 61);
    public static readonly Color OkSoft = Color.FromArgb(220, 246, 230);
    public static readonly Color Warn = Color.FromArgb(166, 77, 7);
    public static readonly Color WarnSoft = Color.FromArgb(254, 241, 219);
    public static readonly Color Bad = Color.FromArgb(185, 28, 28);
    public static readonly Color BadSoft = Color.FromArgb(254, 228, 228);
    public static readonly Color Info = Color.FromArgb(109, 40, 217);
    public static readonly Color InfoSoft = Color.FromArgb(243, 236, 254);
    public static readonly Color Neutral = Color.FromArgb(87, 96, 110);
    public static readonly Color NeutralSoft = Color.FromArgb(238, 241, 245);

    // --- spacing scale (logical px, scaled by AutoScaleMode.Dpi)
    public const int S1 = 4, S2 = 8, S3 = 12, S4 = 16, S5 = 24;
    public const int Radius = 10;

    // --- type scale
    private static readonly string Family = Installed("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
    private static readonly string DisplayFamily = Installed("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI";
    public static readonly Font Base = new(Family, 9.75f);
    public static readonly Font Small = new(Family, 8.75f);
    public static readonly Font Bold = new(Family, 9.75f, FontStyle.Bold);
    public static readonly Font SmallBold = new(Family, 8.75f, FontStyle.Bold);
    public static readonly Font CardTitle = new(DisplayFamily, 11.5f, FontStyle.Bold);
    public static readonly Font Title = new(DisplayFamily, 15f, FontStyle.Bold);
    public static readonly Font StatNumber = new(DisplayFamily, 20f, FontStyle.Bold);
    public static readonly Font EmptyTitle = new(DisplayFamily, 12.5f, FontStyle.Bold);
    public static readonly Font Mono = new("Consolas", 10.5f);

    private static bool Installed(string name)
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public static Button Primary(string text, int width = 0) => new FlatButton(text, FlatButton.Kind.Primary, width);
    public static Button Secondary(string text, int width = 0) => new FlatButton(text, FlatButton.Kind.Secondary, width);

    public static Label Label(string text, Font? font = null, Color? color = null) =>
        new() { Text = text, AutoSize = true, Font = font ?? Base, ForeColor = color ?? Text, Margin = new Padding(0, 0, 0, 4), BackColor = Color.Transparent };

    /// <summary>A white rounded card with a title; content goes into the returned body panel.</summary>
    public static (Panel Card, Panel Body) MakeCard(string title, string? subtitle = null)
    {
        var card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(S4 + 2, S3 + 2, S4 + 2, S4), Margin = new Padding(S2) };
        card.AccessibleName = title;
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Card };
        var head = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            BackColor = Card, Padding = new Padding(0, 0, 0, S2),
        };
        head.Controls.Add(new Label { Text = title, Font = CardTitle, ForeColor = Text, AutoSize = true, Margin = new Padding(0) });
        if (subtitle != null) head.Controls.Add(new Label { Text = subtitle, Font = Small, ForeColor = Muted, AutoSize = true, Margin = new Padding(1, 2, 0, 0) });
        card.Controls.Add(body);
        card.Controls.Add(head);
        return (card, body);
    }

    public static (Color Fore, Color Back) StatusColors(string status) => status switch
    {
        "CONVERTED" => (Ok, OkSoft),
        "CORRECTED" => (Info, InfoSoft),
        "NEEDS_REVIEW" => (Warn, WarnSoft),
        "UNSUPPORTED_FORMAT" or "NO_IMAGE" or "FAILED" or "GATE3_FAILED" => (Bad, BadSoft),
        _ => (Neutral, NeutralSoft),
    };

    public static string Friendly(string status) => status switch
    {
        "CONVERTED" => "Converted",
        "CORRECTED" => "Fixed – apply pending",
        "NEEDS_REVIEW" => "Needs review",
        "UNSUPPORTED_FORMAT" => "Unsupported format",
        "EXCLUDED" => "Not an equation",
        "NO_IMAGE" => "Image missing",
        _ => status,
    };

    public static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Draws a rounded status pill with a leading dot, vertically centred in <paramref name="cell"/>.</summary>
    public static void DrawPill(Graphics g, Rectangle cell, string text, Color fore, Color back, float dpiScale = 1f)
    {
        var size = TextRenderer.MeasureText(g, text, SmallBold, Size.Empty, TextFormatFlags.NoPadding);
        int h = (int)Math.Round(22 * dpiScale), dot = (int)Math.Round(6 * dpiScale), pad = (int)Math.Round(9 * dpiScale);
        int w = Math.Min(size.Width + pad * 2 + dot + pad / 2, cell.Width - (int)(10 * dpiScale));
        var r = new Rectangle(cell.X + (int)(6 * dpiScale), cell.Y + (cell.Height - h) / 2, w, h);
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(back)) using (var path = Rounded(r, h / 2f)) g.FillPath(b, path);
        using (var b = new SolidBrush(fore)) g.FillEllipse(b, r.X + pad, r.Y + (h - dot) / 2f, dot, dot);
        g.SmoothingMode = old;
        var tr = new Rectangle(r.X + pad + dot + pad / 2, r.Y, Math.Max(0, r.Right - (r.X + pad + dot + pad / 2) - pad / 2), h);
        TextRenderer.DrawText(g, text, SmallBold, tr, fore, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }

    /// <summary>Loads an image without locking the file.</summary>
    /// <summary>For a WMF/EMF picture (e.g. a MathType object's preview) writes a PNG copy beside it — white
    /// background, drawn 3× for sharpness — so it can be shown and scored like any other picture. Returns the PNG
    /// path, the input itself when it is not a metafile, or null when the file cannot be read.</summary>
    public static string? EnsurePng(string? path)
    {
        if (path == null || !File.Exists(path)) return null;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".wmf" or ".emf")) return path;
        var png = Path.ChangeExtension(path, ".png");
        if (File.Exists(png)) return png;
        // GDI (not GDI+) plays the metafile: GDI+ mis-places the positioned glyphs MathType writes, garbling the picture
        IntPtr emf = IntPtr.Zero;
        try
        {
            var d = File.ReadAllBytes(path);
            int w, h;
            if (ext == ".wmf")
            {
                int off = 0;
                w = h = 0;
                if (d.Length > 22 && BitConverter.ToUInt32(d, 0) == 0x9AC6CDD7)   // Aldus placeable header: bounds + units per inch
                {
                    short l = BitConverter.ToInt16(d, 6), t = BitConverter.ToInt16(d, 8), r = BitConverter.ToInt16(d, 10), b = BitConverter.ToInt16(d, 12);
                    ushort inch = BitConverter.ToUInt16(d, 14);
                    if (inch > 0) { w = (int)((r - l) * 96.0 / inch); h = (int)((b - t) * 96.0 / inch); }
                    off = 22;
                }
                var body = d.AsSpan(off).ToArray();
                // the picture size (0.01 mm) tells GDI how to scale the WMF; without it fonts come out the wrong width
                var mfp = new MetaFilePict { mm = 8 /* MM_ANISOTROPIC */, xExt = (int)(w / 96.0 * 2540), yExt = (int)(h / 96.0 * 2540) };
                emf = w > 0 && h > 0 ? SetWinMetaFileBits((uint)body.Length, body, IntPtr.Zero, ref mfp)
                                     : SetWinMetaFileBitsNoSize((uint)body.Length, body, IntPtr.Zero, IntPtr.Zero);
                if (w <= 0 || h <= 0) { using var mf = new System.Drawing.Imaging.Metafile(path); w = mf.Width; h = mf.Height; }
            }
            else
            {
                emf = GetEnhMetaFileW(path);
                using var mf = new System.Drawing.Imaging.Metafile(path); w = mf.Width; h = mf.Height;
            }
            if (emf == IntPtr.Zero) return null;
            const int scale = 3;
            w = Math.Clamp(Math.Abs(w) * scale, 8, 4000); h = Math.Clamp(Math.Abs(h) * scale, 8, 2000);
            using var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                var hdc = g.GetHdc();
                var rc = new GdiRect { L = 0, T = 0, R = w, B = h };
                PlayEnhMetaFile(hdc, emf, ref rc);
                g.ReleaseHdc(hdc);
            }
            bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            return png;
        }
        catch { return null; }
        finally { if (emf != IntPtr.Zero) DeleteEnhMetaFile(emf); }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct GdiRect { public int L, T, R, B; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MetaFilePict { public int mm, xExt, yExt; public IntPtr hMF; }
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr SetWinMetaFileBits(uint size, byte[] bits, IntPtr hdcRef, ref MetaFilePict mfp);
    [System.Runtime.InteropServices.DllImport("gdi32.dll", EntryPoint = "SetWinMetaFileBits")] private static extern IntPtr SetWinMetaFileBitsNoSize(uint size, byte[] bits, IntPtr hdcRef, IntPtr mfp);
    [System.Runtime.InteropServices.DllImport("gdi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern IntPtr GetEnhMetaFileW(string file);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool PlayEnhMetaFile(IntPtr hdc, IntPtr emf, ref GdiRect rect);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteEnhMetaFile(IntPtr emf);

    public static Image? LoadImage(string? path)
    {
        if (path == null || !File.Exists(path)) return null;
        try
        {
            using var ms = new MemoryStream(File.ReadAllBytes(path));
            using var img = Image.FromStream(ms);
            return new Bitmap(img);
        }
        catch { return null; }
    }
}

/// <summary>White panel with a soft rounded border, painted over the parent's background.</summary>
internal class CardPanel : Panel
{
    public Color Fill { get; set; } = Ui.Card;
    public Color Stroke { get; set; } = Ui.Border;

    public CardPanel()
    {
        BackColor = Ui.Card;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Ui.Bg);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float rad = Ui.Radius * DeviceDpi / 96f;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Ui.Rounded(r, rad);
        using var b = new SolidBrush(Fill);
        using var pen = new Pen(Stroke);
        e.Graphics.FillPath(b, path);
        e.Graphics.DrawPath(pen, path);
    }
}

/// <summary>Rounded flat button with hover / pressed / focus / disabled states.</summary>
internal sealed class FlatButton : Button
{
    public enum Kind { Primary, Secondary }
    private readonly Kind _kind;
    private bool _hover, _down;

    public FlatButton(string text, Kind kind, int width = 0)
    {
        _kind = kind;
        Text = text;
        AccessibleName = text.Replace("&&", "&").Replace("…", "").Replace("→", "").Replace("↓", "").Trim();
        Font = kind == Kind.Primary ? Ui.Bold : Ui.Base;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
        Height = 36;
        MinimumSize = new Size(0, 36);
        Padding = new Padding(kind == Kind.Primary ? 16 : 10, 0, kind == Kind.Primary ? 16 : 10, 0);
        AutoSize = width == 0;
        AutoSizeMode = AutoSizeMode.GrowOnly;
        if (width > 0) Width = width;
        Margin = new Padding(0, 0, Ui.S2, 0);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>Text width plus horizontal padding; height 36 logical px (the base Button adds extra slack).</summary>
    public override Size GetPreferredSize(Size proposedSize)
    {
        float s = DeviceDpi / 96f;
        var t = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine);
        return new Size(t.Width + Padding.Horizontal + (int)(4 * s), Math.Max((int)(36 * s), t.Height + (int)(12 * s)));
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Ui.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96f;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Color fill, stroke, fore;
        if (_kind == Kind.Primary)
        {
            fill = !Enabled ? Ui.AccentDisabled : _down ? Ui.AccentPressed : _hover ? Ui.AccentHover : Ui.Accent;
            stroke = fill;
            fore = Color.White;
        }
        else
        {
            fill = !Enabled ? Ui.Card : _down ? Ui.PressedSoft : _hover ? Ui.HoverSoft : Ui.Card;
            stroke = !Enabled ? Ui.Border : _hover ? Ui.BorderStrong : Ui.Border;
            fore = Enabled ? Ui.Text : Color.FromArgb(150, 157, 168);
        }
        using (var path = Ui.Rounded(r, 8 * scale))
        using (var b = new SolidBrush(fill))
        using (var pen = new Pen(stroke))
        {
            g.FillPath(b, path);
            g.DrawPath(pen, path);
        }
        if (Focused && ShowFocusCues)
        {
            var fr = RectangleF.Inflate(r, -2.5f * scale, -2.5f * scale);
            using var path = Ui.Rounded(fr, 6 * scale);
            using var pen = new Pen(_kind == Kind.Primary ? Color.White : Ui.Accent, 1.5f * scale) { DashStyle = _kind == Kind.Primary ? DashStyle.Dot : DashStyle.Solid };
            g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Header step indicator: numbered circles joined by lines; done steps get a tick, the current one is filled.</summary>
internal sealed class StepNav : Control
{
    private readonly string[] _steps;
    private readonly List<Rectangle> _hit = new();
    private int _current, _hover = -1;
    public event Action<int>? StepClicked;

    public StepNav(params string[] steps)
    {
        _steps = steps;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Font = Ui.Bold;
        BackColor = Color.White;
        TabStop = true;
        AccessibleRole = AccessibleRole.PageTabList;
        AccessibleName = "Steps";
        Size = GetPreferredSize(Size.Empty);
    }

    public int Current
    {
        get => _current;
        set { _current = value; AccessibleDescription = $"Step {value + 1} of {_steps.Length}: {_steps[value]}"; Invalidate(); }
    }

    private float Dpi => DeviceDpi / 96f;

    public override Size GetPreferredSize(Size proposedSize)
    {
        float s = Dpi;
        int w = 0;
        foreach (var t in _steps) w += (int)(28 * s) + (int)(8 * s) + TextRenderer.MeasureText(t, Font).Width;
        w += (int)(44 * s) * (_steps.Length - 1) + (int)(12 * s);
        return new Size(w, (int)(40 * s));
    }

    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Size = GetPreferredSize(Size.Empty); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Size = GetPreferredSize(Size.Empty); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Dpi;
        int d = (int)(28 * s), gap = (int)(8 * s), line = (int)(44 * s);
        int x = (int)(4 * s), cy = Height / 2;
        _hit.Clear();
        for (int i = 0; i < _steps.Length; i++)
        {
            bool done = i < _current, cur = i == _current;
            var tw = TextRenderer.MeasureText(_steps[i], Font).Width;
            var hitRect = new Rectangle(x - (int)(4 * s), 0, d + gap + tw + (int)(8 * s), Height);
            _hit.Add(hitRect);
            if (i == _hover && !cur)
                using (var hb = new SolidBrush(Ui.HoverSoft)) using (var hp = Ui.Rounded(hitRect, 8 * s)) g.FillPath(hb, hp);
            var circle = new RectangleF(x, cy - d / 2f, d, d);
            if (cur || done)
            {
                using var b = new SolidBrush(cur ? Ui.Accent : Ui.AccentSoft);
                g.FillEllipse(b, circle);
            }
            else
            {
                using var pen = new Pen(Ui.BorderStrong, 1.5f * s);
                g.DrawEllipse(pen, RectangleF.Inflate(circle, -0.75f * s, -0.75f * s));
            }
            if (done)
            {
                using var pen = new Pen(Ui.Accent, 2.2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLines(pen, new[]
                {
                    new PointF(circle.X + d * 0.29f, circle.Y + d * 0.52f), new PointF(circle.X + d * 0.44f, circle.Y + d * 0.66f),
                    new PointF(circle.X + d * 0.72f, circle.Y + d * 0.36f),
                });
            }
            else
                TextRenderer.DrawText(g, (i + 1).ToString(), Ui.Bold, Rectangle.Round(circle), cur ? Color.White : Ui.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x += d + gap;
            TextRenderer.DrawText(g, _steps[i], Font, new Rectangle(x, 0, tw + 2, Height), cur ? Ui.Text : done ? Ui.Accent : Ui.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            if (cur && Focused && ShowFocusCues)
            {
                using var fp = new Pen(Ui.Accent, 1f) { DashStyle = DashStyle.Dot };
                g.DrawRectangle(fp, x - 2, cy - (int)(11 * s), tw + 4, (int)(22 * s));
            }
            x += tw;
            if (i < _steps.Length - 1)
            {
                using var pen = new Pen(done ? Ui.Accent : Ui.Border, 2f * s);
                g.DrawLine(pen, x + 10 * s, cy, x + line - 10 * s, cy);
                x += line;
            }
        }
    }

    private int HitIndex(Point p) => _hit.FindIndex(r => r.Contains(p));
    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = HitIndex(e.Location);
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        if (h != _hover) { _hover = h; Invalidate(); }
        base.OnMouseMove(e);
    }
    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseClick(MouseEventArgs e) { int h = HitIndex(e.Location); if (h >= 0) StepClicked?.Invoke(h); base.OnMouseClick(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override bool IsInputKey(Keys k) => k is Keys.Left or Keys.Right || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Left && _current > 0) StepClicked?.Invoke(_current - 1);
        else if (e.KeyCode == Keys.Right && _current < _steps.Length - 1) StepClicked?.Invoke(_current + 1);
        base.OnKeyDown(e);
    }
}

/// <summary>Results tile: big number, label, coloured accent bar.</summary>
internal sealed class StatTile : CardPanel
{
    private readonly Label _value = new() { AutoSize = true, Font = Ui.StatNumber, Text = "0", Margin = new Padding(0), BackColor = Color.Transparent };
    private readonly Label _caption = new() { AutoSize = true, Font = Ui.Small, ForeColor = Ui.Muted, Margin = new Padding(1, 0, 0, 0), BackColor = Color.Transparent };
    private readonly Color _accent;

    public StatTile(string caption, Color accent)
    {
        _accent = accent;
        _caption.Text = caption;
        _value.ForeColor = accent == Ui.Muted ? Ui.Text : accent;
        Padding = new Padding(Ui.S4 + 4, Ui.S1 + 2, Ui.S3, Ui.S1);
        Margin = new Padding(Ui.S2, 0, Ui.S2, 0);
        Dock = DockStyle.Fill;
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Ui.Card };
        flow.Controls.Add(_value);
        flow.Controls.Add(_caption);
        Controls.Add(flow);
        AccessibleRole = AccessibleRole.StaticText;
        Value = 0;
    }

    public int Value
    {
        set { _value.Text = value.ToString("N0"); AccessibleName = $"{_caption.Text}: {value}"; }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        float s = DeviceDpi / 96f;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new SolidBrush(_accent);
        using var p = Ui.Rounded(new RectangleF(7 * s, 12 * s, 4 * s, Math.Max(4, Height - 24 * s)), 2 * s);
        e.Graphics.FillPath(b, p);
    }
}

/// <summary>Empty-state drop zone: dashed rounded frame, document icon, title and hint.</summary>
internal sealed class DropZone : Control
{
    private bool _dragOver;
    public string Title { get; set; } = "";
    public string Hint { get; set; } = "";

    public DropZone()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Ui.Card;
        AllowDrop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PushButton;
    }

    public bool Highlight { get => _dragOver; set { _dragOver = value; Invalidate(); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        var frame = new RectangleF(2 * s, 2 * s, Width - 4 * s - 1, Height - 4 * s - 1);
        using (var fill = new SolidBrush(_dragOver ? Ui.AccentSoft : Color.FromArgb(250, 251, 254)))
        using (var path = Ui.Rounded(frame, 12 * s))
        using (var pen = new Pen(_dragOver ? Ui.Accent : Ui.BorderStrong, 1.5f * s) { DashStyle = DashStyle.Dash })
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }
        // icon: document with a folded corner and a sigma
        float iw = 46 * s, ih = 58 * s, fold = 14 * s;
        int titleH = TextRenderer.MeasureText(g, Title, Ui.EmptyTitle).Height;
        int hintW = Math.Max(50, Width - (int)(40 * s));
        int hintH = TextRenderer.MeasureText(g, Hint, Ui.Base, new Size(hintW, 0), TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter).Height;
        float total = ih + 22 * s + titleH + 6 * s + hintH;
        float top = Math.Max(14 * s, (Height - total) / 2);
        float ix = (Width - iw) / 2;
        using (var halo = new SolidBrush(Ui.AccentSoft)) g.FillEllipse(halo, ix - 14 * s, top - 4 * s, iw + 28 * s, ih + 8 * s);
        using (var doc = new GraphicsPath())
        {
            doc.AddLines(new[]
            {
                new PointF(ix, top), new PointF(ix + iw - fold, top), new PointF(ix + iw, top + fold),
                new PointF(ix + iw, top + ih), new PointF(ix, top + ih),
            });
            doc.CloseFigure();
            using var b = new SolidBrush(Color.White);
            using var pen = new Pen(Ui.Accent, 2f * s) { LineJoin = LineJoin.Round };
            g.FillPath(b, doc);
            g.DrawPath(pen, doc);
            g.DrawLines(pen, new[] { new PointF(ix + iw - fold, top), new PointF(ix + iw - fold, top + fold), new PointF(ix + iw, top + fold) });
        }
        using (var sf = new Font("Segoe UI", 17f, FontStyle.Bold))
            TextRenderer.DrawText(g, "Σ", sf, new Rectangle((int)ix, (int)(top + fold * 0.5f), (int)iw, (int)(ih - fold * 0.5f)), Ui.Accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        float y = top + ih + 22 * s;
        TextRenderer.DrawText(g, Title, Ui.EmptyTitle, new Rectangle(0, (int)y, Width, titleH), Ui.Text, TextFormatFlags.HorizontalCenter);
        y += titleH + 6 * s;
        TextRenderer.DrawText(g, Hint, Ui.Base, new Rectangle((Width - hintW) / 2, (int)y, hintW, hintH + 4), Ui.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
    }
}
