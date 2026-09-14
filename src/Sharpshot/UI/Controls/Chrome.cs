namespace Sharpshot.UI.Controls;

internal sealed class NavItem : ThemedControl
{
    private bool _selected;
    private bool _hover;

    public NavItem(string glyph, string text)
    {
        Glyph = glyph;
        Text = text;
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PageTab;
        AccessibleName = text;
        SetStyle(ControlStyles.Selectable, true);
    }

    public string Glyph { get; }

    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            AccessibleDescription = value ? "Selected" : null;
            Invalidate();
        }
    }

    public override Size GetPreferredSize(Size proposedSize) => new(proposedSize.Width, Dp(38));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Surface);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var bounds = new RectangleF(0, Dpf(1), Width, Height - Dpf(2));
        if (_selected || _hover)
        {
            Theme.FillRounded(g, _selected ? P.NavSelected : P.NavHover, bounds, Dpf(4));
        }

        if (_selected)
        {
            var pill = new RectangleF(0, (Height - Dpf(16)) / 2f, Dpf(3), Dpf(16));
            Theme.FillRounded(g, P.Accent, pill, Dpf(1.5f));
        }

        DrawText(g, Glyph, IconFont(1.15f), new Rectangle(Dp(14), 0, Dp(18), Height), P.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        DrawText(g, Text, Font, new Rectangle(Dp(44), 0, Width - Dp(48), Height), P.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        DrawFocusRing(g, ClientRectangle, Dpf(4));
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
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            OnClick(EventArgs.Empty);
        }
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
        }
    }
}

internal sealed class Sidebar : ThemedControl
{
    private readonly List<NavItem> _items = [];

    public Sidebar()
    {
        BackColor = P.Window;
    }

    public NavItem AddItem(string glyph, string text)
    {
        var item = new NavItem(glyph, text);
        _items.Add(item);
        Controls.Add(item);
        return item;
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var y = Dp(12);
        foreach (var item in _items)
        {
            var height = item.GetPreferredSize(Size.Empty).Height;
            item.SetBounds(Dp(8), y, Width - Dp(16), height);
            y += height + Dp(2);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        DrawText(g, $"Version {AppInfo.Version}", CaptionFont, new Rectangle(Dp(24), Height - Dp(40), Width - Dp(32), Dp(20)), P.SecondaryText,
            TextFormatFlags.SingleLine);
    }
}

/// <summary>Empty areas report themselves as caption to Windows, so dragging, Alt+Space and the system menu still work.</summary>
internal sealed class TitleBar : ThemedControl
{
    private enum Part
    {
        None,
        Minimize,
        Close,
    }

    private readonly ScaledAppIcon _icon = new();
    private Part _hover;
    private Part _pressed;
    private bool _active = true;

    public TitleBar()
    {
        BackColor = P.Window;
    }

    public event EventHandler? MinimizeClicked;

    public event EventHandler? CloseClicked;

    /// <summary>Text and icons dim while the window is inactive, as in Windows.</summary>
    public bool Active
    {
        get => _active;
        set
        {
            _active = value;
            Invalidate();
        }
    }

    private Rectangle CloseBounds => new(Width - Dp(46), 0, Dp(46), Height);

    private Rectangle MinimizeBounds => new(Width - Dp(92), 0, Dp(46), Height);

    /// <param name="point">In this control's coordinates.</param>
    public bool IsOverButton(Point point) => HitTest(point) != Part.None;

    protected override void WndProc(ref Message m)
    {
        // Let everything except the buttons fall through to the form, which reports it as caption (draggable).
        if (m.Msg == Native.NativeMethods.WM_NCHITTEST)
        {
            var screen = new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16)));
            m.Result = IsOverButton(PointToClient(screen)) ? Native.NativeMethods.HTCLIENT : Native.NativeMethods.HTTRANSPARENT;
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var foreground = _active ? P.Text : P.SecondaryText;

        var iconSize = Dp(20);
        if (_icon.Get(iconSize) is { } icon)
        {
            g.DrawIcon(icon, new Rectangle(Dp(20), (Height - iconSize) / 2, iconSize, iconSize));
        }

        DrawText(g, AppInfo.Name, DerivedFont(UiFonts.TextSemibold, 1f), Rectangle.FromLTRB(Dp(20) + iconSize + Dp(12), 0, MinimizeBounds.Left, Height),
            foreground, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

        PaintButton(g, Part.Minimize, MinimizeBounds, Glyphs.ChromeMinimize, foreground);
        PaintButton(g, Part.Close, CloseBounds, Glyphs.ChromeClose, foreground);
    }

    private void PaintButton(Graphics g, Part part, Rectangle bounds, string glyph, Color foreground)
    {
        var glyphColor = foreground;
        if (_hover == part)
        {
            var fill = part == Part.Close ? (_pressed == part ? P.DangerPressed : P.Danger) : _pressed == part ? P.ControlPressed : P.NavSelected;
            using var brush = new SolidBrush(fill);
            g.FillRectangle(brush, bounds);
            if (part == Part.Close)
            {
                glyphColor = Color.White;
            }
        }

        DrawText(g, glyph, IconFont(0.72f), bounds, glyphColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var part = HitTest(e.Location);
        if (part != _hover)
        {
            _hover = part;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = Part.None;
        _pressed = Part.None;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _pressed = HitTest(e.Location);
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var part = HitTest(e.Location);
        var pressed = _pressed;
        _pressed = Part.None;
        Invalidate();
        if (e.Button != MouseButtons.Left || part != pressed)
        {
            return;
        }

        if (part == Part.Minimize)
        {
            MinimizeClicked?.Invoke(this, EventArgs.Empty);
        }
        else if (part == Part.Close)
        {
            CloseClicked?.Invoke(this, EventArgs.Empty);
        }
    }

    private Part HitTest(Point point) =>
        CloseBounds.Contains(point) ? Part.Close : MinimizeBounds.Contains(point) ? Part.Minimize : Part.None;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal sealed class AboutHeader : ThemedControl
{
    private readonly ScaledAppIcon _icon = new();

    public AboutHeader(ModernButton action)
    {
        Action = action;
        BackColor = P.Card;
        Controls.Add(action);
    }

    public ModernButton Action { get; }

    public override Size GetPreferredSize(Size proposedSize) => new(proposedSize.Width, Dp(84));

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var size = Action.GetPreferredSize(Size.Empty);
        Action.SetBounds(Width - Dp(16) - size.Width, (Height - size.Height) / 2, size.Width, size.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        var iconSize = Dp(40);
        if (_icon.Get(iconSize) is { } icon)
        {
            g.DrawIcon(icon, new Rectangle(Dp(16), (Height - iconSize) / 2, iconSize, iconSize));
        }

        var nameFont = DerivedFont(UiFonts.TextSemibold, 1.3f);
        var left = Dp(16) + iconSize + Dp(16);
        var textWidth = Math.Max(0, Action.Left - Dp(16) - left);
        var blockHeight = nameFont.Height + Dp(2) + CaptionFont.Height;
        var top = (Height - blockHeight) / 2;
        DrawText(g, AppInfo.Name, nameFont, new Rectangle(left, top, textWidth, nameFont.Height), P.Text, TextFormatFlags.SingleLine);
        DrawText(g, $"Version {AppInfo.Version}  ·  Free and open source (MIT)", CaptionFont,
            new Rectangle(left, top + nameFont.Height + Dp(2), textWidth, CaptionFont.Height + Dp(2)), P.SecondaryText,
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal sealed class FooterBar : ThemedControl
{
    private string? _message;

    public FooterBar()
    {
        BackColor = P.Window;
        Save = new ModernButton("Save", primary: true);
        Cancel = new ModernButton("Cancel");
        Controls.Add(Save);
        Controls.Add(Cancel);
    }

    public ModernButton Save { get; }

    public ModernButton Cancel { get; }

    public void SetError(string? message)
    {
        _message = message;
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var pad = Dp(32);
        var cancel = Cancel.GetPreferredSize(Size.Empty);
        var save = Save.GetPreferredSize(Size.Empty);
        var y = (Height - cancel.Height) / 2;
        Cancel.SetBounds(Width - pad - cancel.Width, y, cancel.Width, cancel.Height);
        Save.SetBounds(Cancel.Left - Dp(8) - save.Width, y, save.Width, save.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (string.IsNullOrEmpty(_message))
        {
            return;
        }

        var left = Dp(32);
        var right = Save.Left - Dp(16);
        DrawText(g, Glyphs.ErrorBadge, IconFont(1.1f), new Rectangle(left, 0, Dp(18), Height), P.Error, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        DrawText(g, _message, Font, Rectangle.FromLTRB(left + Dp(26), 0, right, Height), P.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
    }
}