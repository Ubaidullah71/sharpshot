using System.Drawing.Drawing2D;
using Sharpshot.Links;
using Sharpshot.Settings;

namespace Sharpshot.UI.Controls;

/// <summary>A mock-up of how a shared link looks as a Discord embed.</summary>
internal sealed class EmbedPreview : ThemedControl
{
    private static Bitmap? _sample;

    private string _color = EmbedSettings.DefaultColor;
    private string _siteName = "";
    private string _title = "";
    private string _description = "";

    public EmbedPreview()
    {
        BackColor = P.Card;
    }

    public void ShowEmbed(string color, string siteName, string title, string description)
    {
        _color = color;
        _siteName = Expand(siteName);
        _title = Expand(title);
        _description = Expand(description);
        Invalidate();
    }

    private Color EmbedBackground => P.IsDark ? Color.FromArgb(43, 45, 49) : Color.FromArgb(242, 243, 245);

    private Color SiteText => P.IsDark ? Color.FromArgb(219, 222, 225) : Color.FromArgb(78, 80, 88);

    private Color TitleText => P.IsDark ? Color.FromArgb(0, 168, 252) : Color.FromArgb(0, 103, 224);

    private Color BodyText => P.IsDark ? Color.FromArgb(219, 222, 225) : Color.FromArgb(49, 51, 56);

    private Font SiteFont => DerivedFont(UiFonts.Text, 0.8f);

    private Font TitleFont => DerivedFont(UiFonts.TextSemibold, 0.95f);

    private Font BodyFont => DerivedFont(UiFonts.Text, 0.9f);

    public override Size GetPreferredSize(Size proposedSize)
    {
        var width = proposedSize.Width > 0 ? proposedSize.Width : Dp(250);
        return new Size(width, Arrange(width).Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var layout = Arrange(Width);
        var bounds = new RectangleF(0, 0, Width, layout.Height);
        using var shape = Theme.RoundedRectangle(bounds, Dpf(4));
        using (var fill = new SolidBrush(EmbedBackground))
        {
            g.FillPath(fill, shape);
        }

        g.SetClip(shape);
        using (var bar = new SolidBrush(EmbedPage.IsValidColor(_color) ? ColorTranslator.FromHtml(EmbedPage.NormalizeColor(_color)) : Theme.Accent))
        {
            g.FillRectangle(bar, 0, 0, Dp(4), layout.Height);
        }

        g.ResetClip();

        var flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        if (_siteName.Length > 0)
        {
            DrawText(g, _siteName, SiteFont, layout.Site, SiteText, flags);
        }

        if (_title.Length > 0)
        {
            DrawText(g, _title, TitleFont, layout.Title, TitleText, flags);
        }

        if (_description.Length > 0)
        {
            DrawText(g, _description, BodyFont, layout.Description, BodyText, flags);
        }

        using var imageShape = Theme.RoundedRectangle(layout.Image, Dpf(4));
        g.SetClip(imageShape);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(Sample, layout.Image);
        g.ResetClip();
        FadeIfDisabled(g);
    }

    private (int Height, Rectangle Site, Rectangle Title, Rectangle Description, Rectangle Image) Arrange(int width)
    {
        var left = Dp(4) + Dp(12);
        var inner = Math.Max(Dp(40), width - left - Dp(14));
        var y = Dp(10);

        Rectangle Line(string text, Font font, int gapAfter)
        {
            if (text.Length == 0)
            {
                return Rectangle.Empty;
            }

            var rect = new Rectangle(left, y, inner, font.Height);
            y += font.Height + gapAfter;
            return rect;
        }

        var site = Line(_siteName, SiteFont, Dp(4));
        var title = Line(_title, TitleFont, Dp(4));
        var description = Line(_description, BodyFont, Dp(4));
        y += Dp(6);
        var image = new Rectangle(left, y, inner, inner * 9 / 16);
        return (image.Bottom + Dp(14), site, title, description, image);
    }

    private static string Expand(string template) =>
        EmbedPage.Expand(template, 1920, 1080, 245_000, DateTimeOffset.Now);

    private static Bitmap Sample => _sample ??= CreateSample();

    private static Bitmap CreateSample()
    {
        var bitmap = new Bitmap(480, 270);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var background = new LinearGradientBrush(new Point(0, 0), new Point(480, 270), Color.FromArgb(59, 72, 196), Color.FromArgb(214, 76, 160)))
        {
            g.FillRectangle(background, 0, 0, 480, 270);
        }

        using var window = new SolidBrush(Color.FromArgb(235, 250, 250, 252));
        using var bar = new SolidBrush(Color.FromArgb(255, 226, 228, 236));
        using var line = new SolidBrush(Color.FromArgb(255, 200, 204, 216));
        using var accent = new SolidBrush(Color.FromArgb(255, 91, 108, 255));
        using (var path = Theme.RoundedRectangle(new RectangleF(70, 46, 340, 196), 10))
        {
            g.FillPath(window, path);
        }

        g.FillRectangle(bar, 70, 56, 340, 22);
        for (var i = 0; i < 5; i++)
        {
            g.FillRectangle(line, 96, 100 + i * 24, i % 2 == 0 ? 220 : 170, 10);
        }

        g.FillEllipse(accent, 330, 170, 52, 52);
        return bitmap;
    }
}

internal sealed class ColorSwatches : ThemedControl
{
    private static readonly string[] Presets = ["#5B6CFF", "#5865F2", "#3BA55D", "#1ABC9C", "#FAA61A", "#ED4245", "#EB459E", "#FFFFFF"];

    private string _color = EmbedSettings.DefaultColor;
    private int _hover = -1;
    private bool _syncing;

    public ColorSwatches()
    {
        BackColor = P.Card;
        HexBox.Inner.MaxLength = 7;
        Controls.Add(HexBox);
        HexBox.TextChanged += (_, _) =>
        {
            if (_syncing)
            {
                return;
            }

            var normalized = EmbedPage.NormalizeColor(HexBox.Text);
            HexBox.HasError = !EmbedPage.IsValidColor(normalized);
            if (!HexBox.HasError)
            {
                SetColor(normalized, updateBox: false);
            }
        };
    }

    public event EventHandler? ColorChanged;

    public ModernTextBox HexBox { get; } = new(placeholder: "#RRGGBB");

    /// <summary>What the user typed, which may not be valid yet.</summary>
    public string Color
    {
        get => EmbedPage.IsValidColor(EmbedPage.NormalizeColor(HexBox.Text)) ? EmbedPage.NormalizeColor(HexBox.Text) : HexBox.Text.Trim();
        set => SetColor(EmbedPage.NormalizeColor(value), updateBox: true);
    }

    private int SwatchSize => Dp(24);

    private int SwatchGap => Dp(10);

    public override Size GetPreferredSize(Size proposedSize) => new(proposedSize.Width, Dp(34));

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var left = Presets.Length * (SwatchSize + SwatchGap) + Dp(6);
        HexBox.SetBounds(left, 0, Math.Min(Dp(110), Math.Max(Dp(60), Width - left)), Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        for (var i = 0; i < Presets.Length; i++)
        {
            var rect = SwatchBounds(i);
            using (var brush = new SolidBrush(ColorTranslator.FromHtml(Presets[i])))
            {
                g.FillEllipse(brush, rect);
            }

            using (var outline = new Pen(P.ControlBorder))
            {
                g.DrawEllipse(outline, rect);
            }

            var selected = string.Equals(Presets[i], _color, StringComparison.OrdinalIgnoreCase);
            if (selected || i == _hover)
            {
                using var ring = new Pen(selected ? P.Text : P.SecondaryText, Dpf(2));
                g.DrawEllipse(ring, RectangleF.Inflate(rect, Dpf(3.5f), Dpf(3.5f)));
            }
        }

        FadeIfDisabled(g);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = HitTest(e.Location);
        if (index != _hover)
        {
            _hover = index;
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        var index = HitTest(e.Location);
        if (e.Button == MouseButtons.Left && index >= 0)
        {
            SetColor(Presets[index], updateBox: true);
        }
    }

    private RectangleF SwatchBounds(int index) =>
        new(Dpf(4) + index * (SwatchSize + SwatchGap), (Height - SwatchSize) / 2f, SwatchSize, SwatchSize);

    private int HitTest(Point point)
    {
        for (var i = 0; i < Presets.Length; i++)
        {
            if (RectangleF.Inflate(SwatchBounds(i), Dpf(4), Dpf(4)).Contains(point))
            {
                return i;
            }
        }

        return -1;
    }

    private void SetColor(string color, bool updateBox)
    {
        _color = color;
        if (updateBox)
        {
            _syncing = true;
            HexBox.Text = color;
            HexBox.HasError = !EmbedPage.IsValidColor(color);
            _syncing = false;
        }

        Invalidate();
        ColorChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class EmbedEditor : ThemedControl
{
    private readonly Field[] _fields;

    public EmbedEditor(Field siteName, Field title, Field description, ColorSwatches colors, EmbedPreview preview)
    {
        _fields = [siteName, title, description];
        description.Hint ??= "Use {date} {time} {size}";
        Colors = colors;
        Preview = preview;
        BackColor = P.Card;
        foreach (var field in _fields)
        {
            Controls.Add(field);
        }

        Controls.Add(colors);
        Controls.Add(preview);
    }

    public ColorSwatches Colors { get; }

    public EmbedPreview Preview { get; }

    private int Pad => Dp(16);

    private int PreviewWidth => Dp(250);

    private Font LabelFont => DerivedFont(UiFonts.TextSemibold, 0.95f);

    private int ColorLabelWidth => MeasureText("Colour", LabelFont).Width;

    public override Size GetPreferredSize(Size proposedSize) => new(proposedSize.Width, Arrange(proposedSize.Width, apply: false));

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        Arrange(Width, apply: true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        var label = Enabled ? P.Text : P.DisabledText;
        DrawText(g, "Colour", LabelFont, new Rectangle(Pad, Colors.Top, ColorLabelWidth, Colors.Height), label,
            TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
        DrawText(g, "Preview", LabelFont, new Rectangle(Preview.Left, Pad, Preview.Width, LabelFont.Height + Dp(2)), label,
            TextFormatFlags.SingleLine);
    }

    private int Arrange(int width, bool apply)
    {
        var leftWidth = Math.Max(Dp(120), width - Pad * 2 - PreviewWidth - Dp(24));
        var y = Pad;
        foreach (var field in _fields)
        {
            var height = field.GetPreferredSize(Size.Empty).Height;
            if (apply)
            {
                field.SetBounds(Pad, y, leftWidth, height);
            }

            y += height + Dp(12);
        }

        var leftBottom = y - Dp(12);
        var previewHeight = Preview.GetPreferredSize(new Size(PreviewWidth, 0)).Height;
        if (apply)
        {
            Preview.SetBounds(width - Pad - PreviewWidth, Pad + LabelFont.Height + Dp(6), PreviewWidth, previewHeight);
        }

        var top = Math.Max(leftBottom, Pad + LabelFont.Height + Dp(6) + previewHeight);
        var colorsTop = top + Dp(16);
        if (apply)
        {
            var left = Pad + ColorLabelWidth + Dp(12);
            Colors.SetBounds(left, colorsTop, width - left - Pad, Colors.GetPreferredSize(Size.Empty).Height);
        }

        return colorsTop + Colors.GetPreferredSize(Size.Empty).Height + Pad;
    }
}