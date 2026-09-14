using System.Diagnostics.CodeAnalysis;

namespace Sharpshot.UI.Controls;

internal sealed class ModernTextBox : ThemedControl
{
    private bool _hover;
    private bool _hasError;

    /// <param name="placeholder">When null, the inner box keeps its own, e.g. a hotkey box's.</param>
    public ModernTextBox(TextBox? inner = null, string? placeholder = null)
    {
        Inner = inner ?? new TextBox();
        Inner.BorderStyle = BorderStyle.None;
        if (placeholder is not null)
        {
            Inner.PlaceholderText = placeholder;
        }

        Controls.Add(Inner);

        Cursor = Cursors.IBeam;
        Inner.GotFocus += (_, _) => RefreshState();
        Inner.LostFocus += (_, _) => RefreshState();
        Inner.MouseEnter += (_, _) => SetHover(true);
        Inner.MouseLeave += (_, _) => SetHover(ClientRectangle.Contains(PointToClient(MousePosition)));
        Inner.TextChanged += (_, _) => OnTextChanged(EventArgs.Empty);
        RefreshState();
    }

    public TextBox Inner { get; }

    [AllowNull]
    public override string Text
    {
        get => Inner.Text;
        set => Inner.Text = value;
    }

    public bool UseSystemPasswordChar
    {
        get => Inner.UseSystemPasswordChar;
        set => Inner.UseSystemPasswordChar = value;
    }

    public string PlaceholderText
    {
        get => Inner.PlaceholderText;
        set => Inner.PlaceholderText = value;
    }

    public bool HasError
    {
        get => _hasError;
        set
        {
            _hasError = value;
            Invalidate();
        }
    }

    private bool IsFocused => Inner.Focused;

    private Color Fill => !Enabled ? P.ControlPressed : IsFocused ? P.ControlFocused : _hover ? P.ControlHover : P.Control;

    public override Size GetPreferredSize(Size proposedSize) => new(proposedSize.Width > 0 ? proposedSize.Width : Dp(240), Dp(34));

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var pad = Dp(11);
        var height = Inner.PreferredHeight;
        Inner.SetBounds(pad, Math.Max(0, (Height - height) / 2), Math.Max(0, Width - pad * 2), height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Surface);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var radius = Dpf(4);
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        Theme.FillRounded(g, Fill, bounds, radius);
        Theme.DrawRounded(g, HasError ? P.Error : P.ControlBorder, bounds, radius);

        using var clip = Theme.RoundedRectangle(bounds, radius);
        g.SetClip(clip);
        var thick = IsFocused || HasError;
        var lineHeight = thick ? Dpf(2) : Dpf(1);
        using (var line = new SolidBrush(HasError ? P.Error : IsFocused ? P.Accent : P.ControlBottom))
        {
            g.FillRectangle(line, 0, Height - lineHeight, Width, lineHeight);
        }

        g.ResetClip();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        SetHover(true);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(ClientRectangle.Contains(PointToClient(MousePosition)));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Inner.Focus();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        RefreshState();
    }

    private void SetHover(bool hover)
    {
        if (hover != _hover)
        {
            _hover = hover;
            RefreshState();
        }
    }

    private void RefreshState()
    {
        Inner.BackColor = Fill;
        Inner.ForeColor = Enabled ? P.Text : P.DisabledText;
        Invalidate();
    }
}