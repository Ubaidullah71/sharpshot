namespace Sharpshot.UI.Controls;

/// <summary>Fonts are derived from <see cref="Control.Font"/> so they follow WinForms' per-monitor DPI scaling.</summary>
internal abstract class ThemedControl : Control
{
    private readonly Dictionary<(string Family, float Ratio, FontStyle Style), Font> _fonts = [];

    protected ThemedControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
    }

    protected static Palette P => Theme.Current;

    /// <summary>What's painted behind this control.</summary>
    protected Color Surface => Parent?.BackColor ?? P.Window;

    protected int Dp(int logical) => LogicalToDeviceUnits(logical);

    protected float Dpf(float logical) => logical * DeviceDpi / 96f;

    protected Font DerivedFont(string family, float ratio, FontStyle style = FontStyle.Regular)
    {
        var key = (family, ratio, style);
        if (!_fonts.TryGetValue(key, out var font))
        {
            font = new Font(family, Font.Size * ratio, style, Font.Unit);
            _fonts[key] = font;
        }

        return font;
    }

    protected Font CaptionFont => DerivedFont(UiFonts.Text, 0.9f);

    protected Font IconFont(float ratio = 1.2f) => DerivedFont(UiFonts.Icons, ratio);

    protected static void DrawText(Graphics g, string text, Font font, Rectangle bounds, Color color, TextFormatFlags flags) =>
        TextRenderer.DrawText(g, text, font, bounds, color, flags | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

    protected static Size MeasureText(string text, Font font, int width = int.MaxValue, TextFormatFlags flags = TextFormatFlags.Default) =>
        TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue), flags | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

    /// <summary>Advance <paramref name="angle"/> on a timer to spin it.</summary>
    protected void DrawProgressRing(Graphics g, PointF center, float logicalSize, float angle)
    {
        var size = Dpf(logicalSize);
        var ring = new RectangleF(center.X - size / 2, center.Y - size / 2, size, size);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var track = new Pen(P.ControlBorder, Dpf(2));
        using var arc = new Pen(P.Accent, Dpf(2)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        g.DrawEllipse(track, ring);
        g.DrawArc(arc, ring, angle, 100f);
    }

    /// <summary>Washes out a custom-drawn control while it's disabled.</summary>
    protected void FadeIfDisabled(Graphics g)
    {
        if (!Enabled)
        {
            using var veil = new SolidBrush(Color.FromArgb(150, BackColor));
            g.FillRectangle(veil, ClientRectangle);
        }
    }

    protected void DrawFocusRing(Graphics g, Rectangle bounds, float radius)
    {
        if (Focused && ShowFocusCues)
        {
            var inset = Dpf(1f);
            Theme.DrawRounded(g, P.Text, RectangleF.Inflate(bounds, -inset, -inset), radius, Dpf(2f));
        }
    }

    protected override void OnFontChanged(EventArgs e)
    {
        ClearFonts();
        base.OnFontChanged(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClearFonts();
        }

        base.Dispose(disposing);
    }

    private void ClearFonts()
    {
        foreach (var font in _fonts.Values)
        {
            font.Dispose();
        }

        _fonts.Clear();
    }
}