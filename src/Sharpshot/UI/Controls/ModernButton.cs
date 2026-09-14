namespace Sharpshot.UI.Controls;

internal sealed class ModernButton : ThemedControl, IButtonControl
{
    private bool _hover;
    private bool _pressed;
    private string? _glyph;

    public ModernButton(string text, bool primary = false, string? glyph = null)
    {
        Text = text;
        Primary = primary;
        Glyph = glyph;
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PushButton;
        SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick, true);
    }

    public bool Primary { get; }

    public bool Danger { get; init; }

    public string? Glyph
    {
        get => _glyph;
        set
        {
            _glyph = value;
            Invalidate();
            Parent?.PerformLayout(); // the preferred width changed
        }
    }

    public DialogResult DialogResult { get; set; }

    /// <summary>Default buttons look the same as others, as in Windows 11.</summary>
    public void NotifyDefault(bool value)
    {
    }

    public void PerformClick()
    {
        if (Enabled && Visible)
        {
            OnClick(EventArgs.Empty);
        }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var width = string.IsNullOrEmpty(Text) ? 0 : MeasureText(Text, Font).Width;
        if (Glyph is not null)
        {
            width += Dp(16) + (width > 0 ? Dp(8) : 0);
        }

        var padding = string.IsNullOrEmpty(Text) ? Dp(9) : Dp(18);
        return new Size(Math.Max(width + padding * 2, string.IsNullOrEmpty(Text) ? Dp(34) : Dp(96)), Dp(34));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Surface);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var radius = Dpf(4);
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        Color fill, text;
        if (Danger)
        {
            fill = !Enabled ? P.ControlPressed : _pressed ? P.DangerPressed : _hover ? P.DangerHover : P.Danger;
            text = Enabled ? Color.White : P.DisabledText;
            Theme.FillRounded(g, fill, bounds, radius);
        }
        else if (Primary)
        {
            fill = !Enabled ? P.ControlPressed : _pressed ? P.AccentPressed : _hover ? P.AccentHover : P.Accent;
            text = Enabled ? P.OnAccent : P.DisabledText;
            Theme.FillRounded(g, fill, bounds, radius);
        }
        else
        {
            fill = !Enabled ? P.ControlPressed : _pressed ? P.ControlPressed : _hover ? P.ControlHover : P.Control;
            text = !Enabled ? P.DisabledText : _pressed ? P.SecondaryText : P.Text;
            Theme.FillRounded(g, fill, bounds, radius);
            Theme.DrawRounded(g, P.ControlBorder, bounds, radius);
        }

        var contentWidth = (string.IsNullOrEmpty(Text) ? 0 : MeasureText(Text, Font).Width)
            + (Glyph is null ? 0 : Dp(16) + (string.IsNullOrEmpty(Text) ? 0 : Dp(8)));
        var x = (Width - contentWidth) / 2;
        if (Glyph is not null)
        {
            DrawText(g, Glyph, IconFont(1f), new Rectangle(x, 0, Dp(16), Height), text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            x += Dp(16) + Dp(8);
        }

        if (!string.IsNullOrEmpty(Text))
        {
            DrawText(g, Text, Font, new Rectangle(x, 0, Width - x, Height), text, TextFormatFlags.VerticalCenter);
        }

        DrawFocusRing(g, ClientRectangle, radius);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Focus();
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressed = false;
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            e.Handled = true;
            PerformClick();
        }
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        AccessibleName = Text;
        Invalidate();
        Parent?.PerformLayout(); // the preferred width changed
    }
}