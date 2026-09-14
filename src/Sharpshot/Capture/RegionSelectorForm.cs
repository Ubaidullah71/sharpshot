using Sharpshot.Native;

namespace Sharpshot.Capture;

internal sealed class RegionSelectorForm : Form
{
    private const int MinimumSelection = 4;

    /// <summary>More pieces than this and one repaint of their bounding box is cheaper.</summary>
    private const int MaxPaintAreas = 40;

    private readonly RegionOverlayRenderer _renderer;
    private readonly BufferedGraphicsContext _buffers = new();
    private readonly Rectangle _virtualBounds;
    private Rectangle[]? _paintAreas;
    private Point? _anchor;
    private bool _wasActivated;

    /// <param name="screenshot">Capture of <paramref name="virtualBounds"/>. Not owned.</param>
    public RegionSelectorForm(Bitmap screenshot, Rectangle virtualBounds)
    {
        _virtualBounds = virtualBounds;

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        BackColor = Color.Black;
        Text = $"{AppInfo.Name} capture";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
        SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
        Bounds = virtualBounds;

        Rectangle ToOverlay(Rectangle r) => r with { X = r.X - virtualBounds.X, Y = r.Y - virtualBounds.Y };
        var cursor = Cursor.Position;
        _renderer = new RegionOverlayRenderer(
            screenshot,
            NativeMethods.ScaleAt(cursor),
            ToOverlay(Screen.FromPoint(cursor).Bounds),
            Screen.AllScreens.Select(s => ToOverlay(s.Bounds)).ToArray());
        _buffers.MaximumBuffer = virtualBounds.Size;
    }

    /// <summary>Relative to the screenshot. Set when the dialog returns OK.</summary>
    public Rectangle? Selection { get; private set; }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW; // keep out of Alt+Tab
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.SetDwmInt(Handle, NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED, 1);
        NativeMethods.SetDwmInt(Handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, NativeMethods.DWMWCP_DONOTROUND);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Re-apply the exact physical bounds in case Windows adjusted them for DPI while creating the window.
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, _virtualBounds.X, _virtualBounds.Y,
            _virtualBounds.Width, _virtualBounds.Height, NativeMethods.SWP_NOACTIVATE);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        NativeMethods.SetForegroundWindow(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        // The overlay spans monitors with different scales; never let Windows resize it.
        if (m.Msg == NativeMethods.WM_DPICHANGED)
        {
            m.Result = IntPtr.Zero;
            return;
        }

        if (m.Msg == NativeMethods.WM_PAINT)
        {
            // OnPaint only learns the bounding box of what needs repainting; read the exact pieces first.
            _paintAreas = NativeMethods.GetUpdateRectangles(Handle);
            base.WndProc(ref m);
            _paintAreas = null;
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Everything is painted in OnPaint.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var clip = e.ClipRectangle;
        if (RegionOverlayRenderer.IsEmpty(clip))
        {
            return;
        }

        // Dragging on a big screen changes only thin strips, so paint those rather than their bounding box.
        var areas = _paintAreas is { Length: > 0 and <= MaxPaintAreas } ? _paintAreas : [clip];
        foreach (var area in areas)
        {
            var part = Rectangle.Intersect(area, clip);
            if (RegionOverlayRenderer.IsEmpty(part))
            {
                continue;
            }

            using var buffered = _buffers.Allocate(e.Graphics, part);
            _renderer.Paint(buffered.Graphics, part);
            buffered.Render(e.Graphics);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _anchor = e.Location;
            if (_renderer.ShowHint)
            {
                _renderer.ShowHint = false;
                Invalidate(Rectangle.Inflate(_renderer.HintBounds, 2, 2));
            }

            UpdateSelection(Rectangle.Empty);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_anchor is { } anchor && e.Button.HasFlag(MouseButtons.Left))
        {
            UpdateSelection(FromPoints(anchor, e.Location));
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        // Right-click acts on release: closing on press would hand the release to the window underneath,
        // which would then pop up its own context menu.
        if (e.Button == MouseButtons.Right)
        {
            if (_anchor is not null || !RegionOverlayRenderer.IsEmpty(_renderer.Selection))
            {
                _anchor = null;
                UpdateSelection(Rectangle.Empty);
            }
            else
            {
                DialogResult = DialogResult.Cancel;
            }

            return;
        }

        if (e.Button != MouseButtons.Left || _anchor is not { } anchor)
        {
            return;
        }

        _anchor = null;
        var selection = FromPoints(anchor, e.Location);
        if (selection.Width >= MinimumSelection && selection.Height >= MinimumSelection)
        {
            Selection = selection;
            DialogResult = DialogResult.OK;
        }
        else
        {
            UpdateSelection(Rectangle.Empty);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
        }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _wasActivated = true;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // Switching away (Alt+Tab, Win key) cancels, so the overlay can never get stuck on screen.
        if (_wasActivated && DialogResult == DialogResult.None)
        {
            DialogResult = DialogResult.Cancel;
        }
    }

    private void UpdateSelection(Rectangle next)
    {
        var previous = _renderer.Selection;
        if (previous == next)
        {
            return;
        }

        _renderer.Selection = next;
        foreach (var area in _renderer.ChangedAreas(previous, next))
        {
            if (!RegionOverlayRenderer.IsEmpty(area))
            {
                Invalidate(area);
            }
        }
    }

    /// <summary>Rectangle covering both points (inclusive), clamped to the overlay.</summary>
    private Rectangle FromPoints(Point a, Point b)
    {
        var maxX = ClientSize.Width - 1;
        var maxY = ClientSize.Height - 1;
        var ax = Math.Clamp(a.X, 0, maxX);
        var ay = Math.Clamp(a.Y, 0, maxY);
        var bx = Math.Clamp(b.X, 0, maxX);
        var by = Math.Clamp(b.Y, 0, maxY);
        return Rectangle.FromLTRB(Math.Min(ax, bx), Math.Min(ay, by), Math.Max(ax, bx) + 1, Math.Max(ay, by) + 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _renderer.Dispose();
            _buffers.Dispose();
        }

        base.Dispose(disposing);
    }
}