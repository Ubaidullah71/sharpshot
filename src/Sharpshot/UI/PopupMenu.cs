using System.Drawing.Drawing2D;
using Sharpshot.Native;

namespace Sharpshot.UI;

/// <param name="Hint">Dim text on the right, such as a hotkey or an image size.</param>
/// <param name="Children">Builds a submenu when the row is opened.</param>
internal sealed record MenuEntry(string Text, Action? Invoke = null, string? Hint = null, Func<IReadOnlyList<MenuEntry>>? Children = null, bool Enabled = true);

/// <summary>Custom-drawn menu for the tray, where WinForms' ToolStrip menus can't be laid out precisely.</summary>
internal sealed class PopupMenu : Form
{
    private readonly IReadOnlyList<MenuEntry> _entries;
    private readonly PopupMenu? _parent;
    private readonly float _scale;
    private readonly Font _font;
    private readonly Font _hintFont;
    private readonly Font _iconFont;
    private readonly System.Windows.Forms.Timer _submenuTimer = new() { Interval = 250 };
    private System.Windows.Forms.Timer? _outsideClickWatch;
    private PopupMenu? _submenu;
    private int _submenuIndex = -1;
    private int _hover = -1;
    private bool _closing;

    private PopupMenu(IReadOnlyList<MenuEntry> entries, PopupMenu? parent, float scale)
    {
        _entries = entries;
        _parent = parent;
        _scale = scale;
        _font = new Font(UiFonts.Text, 14f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        _hintFont = new Font(UiFonts.Text, 13f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        _iconFont = new Font(UiFonts.Icons, 10f * scale, FontStyle.Regular, GraphicsUnit.Pixel);

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        KeyPreview = true;
        BackColor = Theme.Current.Menu;
        Text = AppInfo.Name;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Size = MeasureSize();
        _submenuTimer.Tick += (_, _) => UpdateSubmenu();
    }

    /// <summary>Kept inside the working area of the screen containing <paramref name="anchor"/>.</summary>
    public static PopupMenu Open(IReadOnlyList<MenuEntry> entries, Point anchor)
    {
        var menu = new PopupMenu(entries, null, NativeMethods.ScaleAt(anchor));
        var area = Screen.FromPoint(anchor).WorkingArea;
        var x = anchor.X + menu.Width <= area.Right ? anchor.X : anchor.X - menu.Width;
        var y = anchor.Y - menu.Height >= area.Top ? anchor.Y - menu.Height : anchor.Y;
        menu.Location = Clamp(new Rectangle(new Point(x, y), menu.Size), area);
        menu.Show();
        menu.Activate();
        NativeMethods.SetForegroundWindow(menu.Handle); // so clicking anywhere else closes it
        if (NativeMethods.GetForegroundWindow() != menu.Handle)
        {
            menu.WatchForOutsideClicks(); // Windows refused: closing on deactivation won't happen
        }

        return menu;
    }

    /// <summary>Closes on outside clicks when the menu couldn't become the active window.</summary>
    private void WatchForOutsideClicks()
    {
        // A quick click can fall between two checks, but it usually moves focus to another window, which is caught too.
        var foreground = NativeMethods.GetForegroundWindow();
        _outsideClickWatch = new System.Windows.Forms.Timer { Interval = 50 };
        _outsideClickWatch.Tick += (_, _) =>
        {
            var now = NativeMethods.GetForegroundWindow();
            if (now == Handle)
            {
                _outsideClickWatch.Stop(); // the menu is active after all, so deactivation closes it as usual
                return;
            }

            var cursor = Cursor.Position;
            var overMenu = Bounds.Contains(cursor) || (_submenu is { IsOpen: true } submenu && submenu.Bounds.Contains(cursor));
            var focusMoved = now != foreground;
            if ((MouseButtons != MouseButtons.None && !overMenu) || focusMoved)
            {
                CloseAll();
            }
        };
        _outsideClickWatch.Start();
    }

    /// <summary>Tests use this instead of hovering.</summary>
    internal PopupMenu? OpenSubmenuNow(int index)
    {
        OpenSubmenu(index);
        return _submenu;
    }

    internal static PopupMenu CreateForPreview(IReadOnlyList<MenuEntry> entries, int hoverIndex, float scale = 1f) =>
        new(entries, null, scale) { _hover = hoverIndex };

    public bool IsOpen => !IsDisposed && !_closing;

    private int Px(float logical) => (int)Math.Round(logical * _scale);

    private int PadY => Px(6);

    private int RowHeight => Px(38);

    private int TextLeft => Px(18);

    private int PadRight => Px(18);

    protected override bool ShowWithoutActivation => _parent is not null;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style = NativeMethods.WS_POPUP;
            // WS_EX_TOPMOST rather than TopMost, which would activate the window.
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;
            if (_parent is not null)
            {
                cp.ExStyle |= NativeMethods.WS_EX_NOACTIVATE; // a submenu must not steal activation from its parent
            }

            cp.ClassStyle |= NativeMethods.CS_DROPSHADOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.SetDwmInt(Handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, NativeMethods.DWMWCP_ROUND);
        NativeMethods.SetDwmInt(Handle, NativeMethods.DWMWA_BORDER_COLOR, NativeMethods.ToColorRef(Theme.Current.MenuBorder));
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_MOUSEACTIVATE && _parent is not null)
        {
            m.Result = NativeMethods.MA_NOACTIVATE;
            return;
        }

        if (m.Msg == NativeMethods.WM_DPICHANGED)
        {
            return; // sized for its monitor already
        }

        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(p.Menu);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            var row = RowBounds(i);
            if (i == _hover && entry.Enabled)
            {
                Theme.FillRounded(g, p.MenuHover, new RectangleF(Px(5), row.Top + Px(1), Width - Px(10), row.Height - Px(2)), Px(5));
            }

            var color = entry.Enabled ? p.Text : p.DisabledText;
            TextRenderer.DrawText(g, entry.Text, _font, Rectangle.FromLTRB(TextLeft, row.Top, Width - PadRight, row.Bottom), color,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

            var right = Width - PadRight;
            if (entry.Children is not null)
            {
                TextRenderer.DrawText(g, Glyphs.ChevronRight, _iconFont, Rectangle.FromLTRB(right - Px(12), row.Top, right, row.Bottom), p.SecondaryText,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPadding);
                right -= Px(12) + Px(14);
            }

            if (entry.Hint is not null)
            {
                TextRenderer.DrawText(g, entry.Hint, _hintFont, Rectangle.FromLTRB(TextLeft, row.Top, right, row.Bottom), p.SecondaryText,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
        }

        if (!NativeMethods.SystemRoundsCorners)
        {
            using var border = new Pen(p.MenuBorder);
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _parent?.KeepSubmenuOpen();
        SetHover(HitTest(e.Location), fromMouse: true);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_submenu is null)
        {
            SetHover(-1, fromMouse: true);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
        {
            Activate(HitTest(e.Location));
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Only the root menu is active; it forwards keys to an open submenu.
        var target = _submenu is { IsOpen: true } && _submenu._hover >= 0 ? _submenu : this;
        switch (keyData)
        {
            case Keys.Down:
                target.MoveHover(+1);
                return true;
            case Keys.Up:
                target.MoveHover(-1);
                return true;
            case Keys.Enter or Keys.Space:
                target.Activate(target._hover);
                return true;
            case Keys.Right when target == this && _hover >= 0 && _entries[_hover].Children is not null:
                OpenSubmenu(_hover);
                _submenu?.MoveHover(+1);
                return true;
            case Keys.Left or Keys.Escape when target != this:
                CloseSubmenu();
                return true;
            case Keys.Escape:
                CloseAll();
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (_parent is not null || _closing)
        {
            return;
        }

        // Only close when activation really went to another app. Creating a submenu window can briefly take it,
        // and closing right then would destroy the submenu while it's still being created.
        BeginInvoke(() =>
        {
            if (_closing || IsDisposed)
            {
                return;
            }

            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground == Handle)
            {
                return;
            }

            if (_submenu is { IsDisposed: false, IsHandleCreated: true } submenu && foreground == submenu.Handle)
            {
                Activate(); // keep keyboard input on the main menu
                return;
            }

            CloseAll();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _submenuTimer.Dispose();
            _outsideClickWatch?.Dispose();
            _submenu?.Dispose();
            _font.Dispose();
            _hintFont.Dispose();
            _iconFont.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Closes this menu and everything it belongs to.</summary>
    public void CloseAll()
    {
        if (_parent is not null)
        {
            _parent.CloseAll();
            return;
        }

        if (_closing || IsDisposed)
        {
            return;
        }

        _closing = true;
        CloseSubmenu();
        Close();
    }

    private Size MeasureSize()
    {
        var widest = Px(160);
        foreach (var entry in _entries)
        {
            var width = TextRenderer.MeasureText(entry.Text, _font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width;
            if (entry.Hint is not null)
            {
                width += Px(40) + TextRenderer.MeasureText(entry.Hint, _hintFont, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width;
            }

            if (entry.Children is not null)
            {
                width += Px(40);
            }

            widest = Math.Max(widest, width);
        }

        return new Size(TextLeft + widest + PadRight, PadY * 2 + RowHeight * _entries.Count);
    }

    private Rectangle RowBounds(int index) => new(0, PadY + index * RowHeight, Width, RowHeight);

    private int HitTest(Point point)
    {
        if (point.X < 0 || point.X >= Width || point.Y < PadY)
        {
            return -1;
        }

        var index = (point.Y - PadY) / RowHeight;
        return index < _entries.Count ? index : -1;
    }

    private void SetHover(int index, bool fromMouse)
    {
        if (index == _hover)
        {
            return;
        }

        _hover = index;
        Invalidate();
        if (fromMouse)
        {
            _submenuTimer.Stop();
            _submenuTimer.Start(); // open or close submenus after a short pause, like Windows does
        }
    }

    private void MoveHover(int step)
    {
        if (_entries.Count == 0)
        {
            return;
        }

        var index = _hover;
        for (var i = 0; i < _entries.Count; i++)
        {
            index = (index + step + _entries.Count) % _entries.Count;
            if (_entries[index].Enabled)
            {
                SetHover(index, fromMouse: false);
                return;
            }
        }
    }

    private void Activate(int index)
    {
        if (index < 0 || index >= _entries.Count || !_entries[index].Enabled)
        {
            return;
        }

        var entry = _entries[index];
        if (entry.Children is not null)
        {
            OpenSubmenu(index);
            return;
        }

        CloseAll();
        entry.Invoke?.Invoke();
    }

    private void UpdateSubmenu()
    {
        _submenuTimer.Stop();
        if (_hover == _submenuIndex && _submenu is { IsOpen: true })
        {
            return;
        }

        CloseSubmenu();
        if (_hover >= 0 && _entries[_hover].Children is not null && _entries[_hover].Enabled)
        {
            OpenSubmenu(_hover);
        }
    }

    private void KeepSubmenuOpen()
    {
        _submenuTimer.Stop();
        if (_hover != _submenuIndex)
        {
            _hover = _submenuIndex;
            Invalidate();
        }
    }

    private void OpenSubmenu(int index)
    {
        if (_submenuIndex == index && _submenu is { IsOpen: true })
        {
            return;
        }

        CloseSubmenu();
        var children = _entries[index].Children?.Invoke() ?? [];
        if (children.Count == 0)
        {
            return;
        }

        var submenu = new PopupMenu(children, this, _scale) { Owner = this };
        var row = RectangleToScreen(RowBounds(index));
        var area = Screen.FromRectangle(Bounds).WorkingArea;
        var x = Right - Px(4) + submenu.Width <= area.Right ? Right - Px(4) : Left - submenu.Width + Px(4);
        submenu.Location = Clamp(new Rectangle(new Point(x, row.Top - PadY), submenu.Size), area);
        _submenu = submenu;
        _submenuIndex = index;
        submenu.Show();
    }

    private void CloseSubmenu()
    {
        if (_submenu is not null)
        {
            _submenu._closing = true;
            _submenu.Close();
            _submenu = null;
        }

        _submenuIndex = -1;
    }

    private static Point Clamp(Rectangle bounds, Rectangle area) => new(
        Math.Clamp(bounds.X, area.Left, Math.Max(area.Left, area.Right - bounds.Width)),
        Math.Clamp(bounds.Y, area.Top, Math.Max(area.Top, area.Bottom - bounds.Height)));
}