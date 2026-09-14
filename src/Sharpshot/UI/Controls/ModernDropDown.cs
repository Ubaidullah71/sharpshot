namespace Sharpshot.UI.Controls;

internal sealed class ModernDropDown : ThemedControl
{
    private readonly List<string> _items = [];
    private ContextMenuStrip? _menu;
    private int _selectedIndex = -1;
    private bool _hover;

    public ModernDropDown()
    {
        TabStop = true;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.Selectable, true);
    }

    public event EventHandler? SelectedIndexChanged;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var index = _items.Count == 0 ? -1 : Math.Clamp(value, 0, _items.Count - 1);
            if (index == _selectedIndex)
            {
                return;
            }

            _selectedIndex = index;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetItems(IEnumerable<string> items)
    {
        _items.Clear();
        _items.AddRange(items);
        DisposeMenu(); // rebuilt with the new items when next opened
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var widest = _items.Count == 0 ? Dp(80) : _items.Max(i => MeasureText(i, Font).Width);
        return new Size(widest + Dp(11) + Dp(40), Dp(34));
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new DropDownAccessibleObject(this);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Surface);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var radius = Dpf(4);
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        Theme.FillRounded(g, !Enabled ? P.ControlPressed : _hover ? P.ControlHover : P.Control, bounds, radius);
        Theme.DrawRounded(g, P.ControlBorder, bounds, radius);

        var textColor = Enabled ? P.Text : P.DisabledText;
        var chevron = Dp(34);
        if (_selectedIndex >= 0)
        {
            DrawText(g, _items[_selectedIndex], Font, new Rectangle(Dp(11), 0, Width - Dp(11) - chevron, Height), textColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }

        DrawText(g, Glyphs.ChevronDown, IconFont(0.8f), new Rectangle(Width - chevron, 0, chevron - Dp(6), Height),
            Enabled ? P.SecondaryText : P.DisabledText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
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
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            Focus();
            OpenMenu();
        }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        DisposeMenu(); // its spacing is in device pixels
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Down when e.Alt:
            case Keys.Enter:
            case Keys.Space:
            case Keys.F4:
                OpenMenu();
                e.Handled = true;
                break;
            case Keys.Up:
                SelectedIndex = Math.Max(0, _selectedIndex - 1);
                e.Handled = true;
                break;
            case Keys.Down:
                SelectedIndex = Math.Min(_items.Count - 1, _selectedIndex + 1);
                e.Handled = true;
                break;
        }
    }

    private void OpenMenu()
    {
        if (_items.Count == 0)
        {
            return;
        }

        if (_menu is null)
        {
            _menu = new ContextMenuStrip();
            MenuRenderer.Style(_menu, this, checkMargin: true);
            for (var i = 0; i < _items.Count; i++)
            {
                var index = i;
                var item = new ToolStripMenuItem(_items[i]) { Padding = new Padding(0, Dp(6), Dp(10), Dp(6)) };
                item.Click += (_, _) => SelectedIndex = index;
                _menu.Items.Add(item);
            }
        }

        for (var i = 0; i < _menu.Items.Count; i++)
        {
            ((ToolStripMenuItem)_menu.Items[i]).Checked = i == _selectedIndex;
        }

        _menu.MinimumSize = new Size(Width, 0);
        _menu.Show(this, new Point(0, Height + Dp(2)));
    }

    private void DisposeMenu()
    {
        _menu?.Dispose(); // also disposes its items
        _menu = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeMenu();
        }

        base.Dispose(disposing);
    }

    /// <summary>Screen readers get the label as the name and the chosen option as the value.</summary>
    private sealed class DropDownAccessibleObject(ModernDropDown owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.ComboBox;

        public override string? Value
        {
            get => owner._selectedIndex >= 0 ? owner._items[owner._selectedIndex] : null;
            set
            {
                var index = value is null ? -1 : owner._items.IndexOf(value);
                if (index >= 0)
                {
                    owner.SelectedIndex = index;
                }
            }
        }

        public override string? DefaultAction => "Open";

        public override void DoDefaultAction() => owner.OpenMenu();
    }
}