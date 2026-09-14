namespace Sharpshot.UI.Controls;

internal enum TextStyle
{
    Body,
    Caption,
    Title,
}

internal sealed class TextBlock : ThemedControl
{
    private readonly TextStyle _style;
    private Rectangle _linkBounds;
    private bool _linkHover;

    public TextBlock(string text, TextStyle style = TextStyle.Body, string? linkText = null)
    {
        _style = style;
        Text = text;
        LinkText = linkText;
    }

    public event EventHandler? LinkClicked;

    public string? LinkText { get; }

    public Color? ColorOverride { get; set; }

    private Font TextFont => StyledFont(FontStyle.Regular);

    private Font StyledFont(FontStyle fontStyle) => _style switch
    {
        TextStyle.Title => DerivedFont(UiFonts.Display, 2f, fontStyle),
        TextStyle.Caption => DerivedFont(UiFonts.Text, 0.9f, fontStyle),
        _ => fontStyle == FontStyle.Regular ? Font : DerivedFont(Font.Name, 1f, fontStyle),
    };

    private Color TextColor => ColorOverride ?? (_style == TextStyle.Caption ? P.SecondaryText : P.Text);

    public override Size GetPreferredSize(Size proposedSize)
    {
        var width = proposedSize.Width > 0 ? proposedSize.Width : int.MaxValue;
        var text = string.IsNullOrEmpty(Text) ? new Size(0, TextFont.Height) : MeasureText(Text, TextFont, width, TextFormatFlags.WordBreak);
        var (_, linkRect) = Arrange(width, text);
        var height = Math.Max(text.Height, linkRect.IsEmpty ? 0 : linkRect.Bottom);
        return new Size(proposedSize.Width > 0 ? proposedSize.Width : Math.Max(text.Width, linkRect.Right), height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Surface);
        var text = MeasureText(Text, TextFont, Width, TextFormatFlags.WordBreak);
        DrawText(e.Graphics, Text, TextFont, new Rectangle(0, 0, Width, text.Height + Dp(2)), TextColor, TextFormatFlags.WordBreak);

        (_, _linkBounds) = Arrange(Width, text);
        if (LinkText is not null)
        {
            DrawText(e.Graphics, LinkText, StyledFont(_linkHover ? FontStyle.Underline : FontStyle.Regular), _linkBounds, P.LinkText, TextFormatFlags.Default);
        }
    }

    /// <summary>The link sits after single-line text if it fits, otherwise on its own line.</summary>
    private (bool Inline, Rectangle Link) Arrange(int width, Size text)
    {
        if (LinkText is null)
        {
            return (false, Rectangle.Empty);
        }

        var link = MeasureText(LinkText, TextFont);
        var singleLine = text.Height <= TextFont.Height + Dp(2);
        var gap = MeasureText(" ", TextFont).Width + Dp(2);
        if (singleLine && text.Width + gap + link.Width <= width)
        {
            return (true, new Rectangle(text.Width + gap, 0, link.Width, link.Height));
        }

        return (false, new Rectangle(0, text.Height + Dp(2), link.Width, link.Height));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hover = LinkText is not null && _linkBounds.Contains(e.Location);
        if (hover != _linkHover)
        {
            _linkHover = hover;
            Cursor = hover ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_linkHover)
        {
            _linkHover = false;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && _linkHover)
        {
            LinkClicked?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
        Parent?.PerformLayout();
    }
}