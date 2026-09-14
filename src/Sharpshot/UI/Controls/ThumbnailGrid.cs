using System.Drawing.Drawing2D;
using System.Globalization;
using Sharpshot.Uploads;

namespace Sharpshot.UI.Controls;

/// <summary>Only visible tiles ask for thumbnails, so thousands of uploads stay cheap.</summary>
internal sealed class ThumbnailGrid : ThemedControl
{
    private const int Columns = 4;

    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private IReadOnlyList<StoredShot> _items = [];
    private int _scroll;
    private int _hover = -1;
    private int _focus;
    private bool _scrollbarHover;
    private int? _dragOffset;
    private int _wheelRemainder;

    public ThumbnailGrid()
    {
        TabStop = true;
        SetStyle(ControlStyles.Selectable, true);
        AccessibleName = "Screenshots";
        AccessibleDescription = "Arrow keys move, Space selects, Enter opens, Delete deletes the selection";
        AccessibleRole = AccessibleRole.List;
    }

    public event EventHandler? SelectionChanged;

    public event Action<StoredShot, Point>? ContextMenuRequested;

    public event Action<StoredShot>? OpenRequested;

    public event EventHandler? DeleteRequested;

    /// <summary>Returns the thumbnail if it's ready, and starts loading it if not.</summary>
    public Func<StoredShot, Bitmap?>? Thumbnails { get; set; }

    public IReadOnlyList<StoredShot> Selected => _items.Where(i => _selected.Contains(i.ImageKey)).ToList();

    public void SetItems(IReadOnlyList<StoredShot> items)
    {
        _items = items;
        _selected.IntersectWith(items.Select(i => i.ImageKey));
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
        _hover = -1;
        _focus = Math.Clamp(_focus, 0, Math.Max(0, items.Count - 1));
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>True when the tile is at least partly on screen.</summary>
    public bool IsShowing(string imageKey)
    {
        var firstRow = Math.Max(0, _scroll / (TileHeight + Gap));
        var lastRow = Math.Min(Rows - 1, (_scroll + Height) / (TileHeight + Gap));
        for (var index = firstRow * Columns; index < Math.Min(_items.Count, (lastRow + 1) * Columns); index++)
        {
            if (_items[index].ImageKey == imageKey)
            {
                return Visible;
            }
        }

        return false;
    }

    public void SetSelected(StoredShot shot, bool selected)
    {
        if (selected ? _selected.Add(shot.ImageKey) : _selected.Remove(shot.ImageKey))
        {
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ClearSelection()
    {
        if (_selected.Count > 0)
        {
            _selected.Clear();
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private int Gap => Dp(12);

    private int ScrollbarSpace => Dp(12);

    private int TileWidth => Math.Max(Dp(60), (Width - ScrollbarSpace - Gap * (Columns - 1)) / Columns);

    private int ThumbHeight => TileWidth * 10 / 16;

    private int TileHeight => ThumbHeight + Dp(44);

    private int Rows => (_items.Count + Columns - 1) / Columns;

    private int ContentHeight => Rows == 0 ? 0 : Rows * TileHeight + (Rows - 1) * Gap;

    private int MaxScroll => Math.Max(0, ContentHeight - Height);

    public override Size GetPreferredSize(Size proposedSize) => new(proposedSize.Width, Dp(200));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Surface);
        if (_items.Count == 0)
        {
            return;
        }

        var firstRow = Math.Max(0, _scroll / (TileHeight + Gap));
        var lastRow = Math.Min(Rows - 1, (_scroll + Height) / (TileHeight + Gap));
        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = 0; column < Columns; column++)
            {
                var index = row * Columns + column;
                if (index < _items.Count)
                {
                    PaintTile(g, index, TileBounds(index));
                }
            }
        }

        PaintScrollbar(g);
    }

    private void PaintTile(Graphics g, int index, Rectangle tile)
    {
        var shot = _items[index];
        var selected = _selected.Contains(shot.ImageKey);
        var hover = index == _hover;
        var thumb = new Rectangle(tile.Left, tile.Top, tile.Width, ThumbHeight);
        var radius = Dpf(6);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.RoundedRectangle(thumb, radius))
        {
            using var placeholder = new SolidBrush(P.Card);
            g.FillPath(placeholder, path);
            if (Thumbnails?.Invoke(shot) is { } bitmap)
            {
                g.SetClip(path);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(bitmap, thumb);
                g.ResetClip();
            }
            else
            {
                DrawText(g, Glyphs.Picture, IconFont(1.6f), thumb, P.DisabledText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        if (selected)
        {
            Theme.DrawRounded(g, P.Accent, RectangleF.Inflate(thumb, -Dpf(1), -Dpf(1)), radius, Dpf(2.5f));
        }
        else if (hover)
        {
            Theme.DrawRounded(g, P.SecondaryText, RectangleF.Inflate(thumb, -Dpf(0.5f), -Dpf(0.5f)), radius, Dpf(1));
        }

        if (index == _focus && Focused && ShowFocusCues)
        {
            Theme.DrawRounded(g, P.Text, RectangleF.Inflate(thumb, Dpf(3), Dpf(3)), radius + Dpf(3), Dpf(2));
        }

        if (selected || hover)
        {
            var size = Dpf(20);
            var check = new RectangleF(thumb.Left + Dpf(8), thumb.Top + Dpf(8), size, size);
            using var fill = new SolidBrush(selected ? P.Accent : Color.FromArgb(150, 0, 0, 0));
            g.FillEllipse(fill, check);
            using var ring = new Pen(Color.White, Dpf(1.5f));
            g.DrawEllipse(ring, check);
            if (selected)
            {
                DrawText(g, Glyphs.CheckMark, IconFont(0.75f), Rectangle.Round(check), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        var text = new Rectangle(tile.Left + Dp(2), thumb.Bottom + Dp(8), tile.Width - Dp(4), Font.Height);
        DrawText(g, shot.UploadedAt.ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.CurrentCulture), CaptionFont, text, P.Text,
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        var details = shot.Width > 0 ? $"{Sizes.Format(shot.SizeBytes)}  ·  {shot.Width} × {shot.Height}" : Sizes.Format(shot.SizeBytes);
        DrawText(g, details, CaptionFont, new Rectangle(text.Left, text.Top + CaptionFont.Height + Dp(2), text.Width, CaptionFont.Height), P.SecondaryText,
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    private void PaintScrollbar(Graphics g)
    {
        if (MaxScroll == 0)
        {
            return;
        }

        var thumb = ScrollThumb();
        var wide = _scrollbarHover || _dragOffset is not null;
        var width = wide ? Dpf(6) : Dpf(3);
        var bar = new RectangleF(Width - Dpf(4) - width, thumb.Top, width, thumb.Height);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.FillRounded(g, Color.FromArgb(wide ? 170 : 110, P.SecondaryText), bar, width / 2f);
    }

    private Rectangle ScrollThumb()
    {
        var height = Math.Max(Dp(32), Height * Height / Math.Max(1, ContentHeight));
        var top = MaxScroll == 0 ? 0 : (int)((long)_scroll * (Height - height) / MaxScroll);
        return new Rectangle(Width - Dp(12), top, Dp(12), height);
    }

    private Rectangle TileBounds(int index)
    {
        var row = index / Columns;
        var column = index % Columns;
        return new Rectangle(column * (TileWidth + Gap), row * (TileHeight + Gap) - _scroll, TileWidth, TileHeight);
    }

    private int HitTest(Point point)
    {
        if (point.X >= Width - ScrollbarSpace)
        {
            return -1;
        }

        var column = point.X / (TileWidth + Gap);
        var row = (point.Y + _scroll) / (TileHeight + Gap);
        var index = row * Columns + column;
        return column < Columns && index >= 0 && index < _items.Count && TileBounds(index).Contains(point) ? index : -1;
    }

    private void ScrollTo(int value)
    {
        var clamped = Math.Clamp(value, 0, MaxScroll);
        if (clamped != _scroll)
        {
            _scroll = clamped;
            _hover = HitTest(PointToClient(MousePosition));
            Invalidate();
        }
    }

    private void InvalidateTile(int index)
    {
        if (index >= 0 && index < _items.Count)
        {
            var bounds = TileBounds(index);
            bounds.Inflate(Dp(4), Dp(4)); // the focus ring sits just outside the tile
            Invalidate(bounds);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        // One notch (120) scrolls half a tile; touchpads send many smaller steps, which add up to the same.
        var total = e.Delta * ((TileHeight + Gap) / 2) + _wheelRemainder;
        _wheelRemainder = total % 120;
        ScrollTo(_scroll - total / 120);
        if (e is HandledMouseEventArgs handled)
        {
            handled.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragOffset is { } offset)
        {
            var thumb = ScrollThumb();
            var track = Math.Max(1, Height - thumb.Height);
            ScrollTo((int)((long)(e.Y - offset) * MaxScroll / track));
            return;
        }

        var overScrollbar = MaxScroll > 0 && e.X >= Width - ScrollbarSpace;
        var hover = HitTest(e.Location);
        if (hover != _hover)
        {
            InvalidateTile(_hover);
            InvalidateTile(hover);
            _hover = hover;
            Cursor = hover >= 0 ? Cursors.Hand : Cursors.Default;
        }

        if (overScrollbar != _scrollbarHover)
        {
            _scrollbarHover = overScrollbar;
            Invalidate(new Rectangle(Width - ScrollbarSpace, 0, ScrollbarSpace, Height));
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        _scrollbarHover = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button == MouseButtons.Left && MaxScroll > 0 && e.X >= Width - ScrollbarSpace)
        {
            var thumb = ScrollThumb();
            if (thumb.Contains(e.Location))
            {
                _dragOffset = e.Y - thumb.Top;
            }
            else
            {
                ScrollTo(_scroll + (e.Y < thumb.Top ? -Height : Height)); // page up/down on the track
            }
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragOffset is not null)
        {
            _dragOffset = null;
            Invalidate();
            return;
        }

        var index = HitTest(e.Location);
        if (index < 0)
        {
            return;
        }

        var shot = _items[index];
        InvalidateTile(_focus);
        _focus = index;
        if (e.Button == MouseButtons.Left)
        {
            SetSelected(shot, !_selected.Contains(shot.ImageKey));
        }
        else if (e.Button == MouseButtons.Right)
        {
            ContextMenuRequested?.Invoke(shot, PointToScreen(e.Location));
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        var index = HitTest(e.Location);
        if (e.Button == MouseButtons.Left && index >= 0)
        {
            OpenRequested?.Invoke(_items[index]);
        }
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End
            or Keys.Delete or Keys.Space or Keys.Enter or Keys.Apps
        || (keyData == Keys.Escape && _selected.Count > 0)
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var rowsPerPage = Math.Max(1, Height / (TileHeight + Gap));
        switch (e.KeyCode)
        {
            case Keys.A when e.Control:
                _selected.UnionWith(_items.Select(i => i.ImageKey));
                Invalidate();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                break;
            case Keys.Escape:
                ClearSelection();
                break;
            case Keys.Delete when _selected.Count > 0:
                DeleteRequested?.Invoke(this, EventArgs.Empty);
                break;
            case Keys.Space when _focus < _items.Count:
                SetSelected(_items[_focus], !_selected.Contains(_items[_focus].ImageKey));
                break;
            case Keys.Enter when _focus < _items.Count:
                OpenRequested?.Invoke(_items[_focus]);
                break;
            case Keys.Apps or Keys.F10 when (e.KeyCode == Keys.Apps || e.Shift) && _focus < _items.Count:
                var tile = TileBounds(_focus);
                ContextMenuRequested?.Invoke(_items[_focus], PointToScreen(new Point(tile.Left + tile.Width / 2, Math.Max(0, tile.Top) + ThumbHeight / 2)));
                break;
            case Keys.Left: MoveFocus(-1); break;
            case Keys.Right: MoveFocus(1); break;
            case Keys.Up: MoveFocus(-Columns); break;
            case Keys.Down: MoveFocus(Columns); break;
            case Keys.PageUp: MoveFocus(-Columns * rowsPerPage); break;
            case Keys.PageDown: MoveFocus(Columns * rowsPerPage); break;
            case Keys.Home: MoveFocus(-_items.Count); break;
            case Keys.End: MoveFocus(_items.Count); break;
            default: return;
        }

        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void MoveFocus(int by)
    {
        if (_items.Count == 0)
        {
            return;
        }

        InvalidateTile(_focus);
        _focus = Math.Clamp(_focus + by, 0, _items.Count - 1);
        var tile = TileBounds(_focus);
        if (tile.Top < 0)
        {
            ScrollTo(_scroll + tile.Top);
        }
        else if (tile.Bottom > Height)
        {
            ScrollTo(_scroll + tile.Bottom - Height);
        }

        InvalidateTile(_focus);
    }
}