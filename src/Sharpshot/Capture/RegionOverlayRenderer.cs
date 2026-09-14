using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Sharpshot.UI;

namespace Sharpshot.Capture;

/// <summary>Separate from the form so the overlay can be rendered off-screen in tests.</summary>
internal sealed class RegionOverlayRenderer : IDisposable
{
    private const string HintText = "Drag to capture  ·  Esc or right-click to cancel";

    private readonly Bitmap _screenshot;
    private readonly Bitmap _dimmed;
    private readonly Font _font;
    private readonly Bitmap _measureSurface = new(1, 1);
    private readonly Graphics _measure;
    private readonly float _scale;
    private readonly Rectangle _bounds;
    private readonly Rectangle _hintArea;
    private readonly IReadOnlyList<Rectangle> _monitors;

    /// <param name="screenshot">Not owned.</param>
    /// <param name="scale">1.0 = 100%.</param>
    /// <param name="hintArea">The monitor, in overlay coordinates, where the hint is shown.</param>
    /// <param name="monitors">In overlay coordinates, so labels never land in gaps between monitors.</param>
    public RegionOverlayRenderer(Bitmap screenshot, float scale, Rectangle hintArea, IReadOnlyList<Rectangle>? monitors = null)
    {
        _screenshot = screenshot;
        _scale = Math.Max(1f, scale);
        _bounds = new Rectangle(Point.Empty, screenshot.Size);
        _hintArea = hintArea;
        _monitors = monitors is { Count: > 0 } ? monitors : [_bounds];
        _font = new Font("Segoe UI", 13f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
        _measure = Graphics.FromImage(_measureSurface);
        _measure.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        _dimmed = new Bitmap(_bounds.Width, _bounds.Height, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(_dimmed);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(screenshot, _bounds, _bounds, GraphicsUnit.Pixel);
        g.CompositingMode = CompositingMode.SourceOver;
        using var shade = new SolidBrush(Color.FromArgb(125, 0, 0, 0));
        g.FillRectangle(shade, _bounds);
    }

    /// <summary>In overlay coordinates; empty when nothing is selected.</summary>
    public Rectangle Selection { get; set; }

    public bool ShowHint { get; set; } = true;

    private int BorderWidth => Math.Max(1, (int)Math.Round(_scale * 1.5f));

    public void Paint(Graphics g, Rectangle clip)
    {
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_dimmed, clip, clip, GraphicsUnit.Pixel);

        var selection = Selection;
        if (!IsEmpty(selection))
        {
            var bright = Rectangle.Intersect(selection, clip);
            if (!IsEmpty(bright))
            {
                g.DrawImage(_screenshot, bright, bright, GraphicsUnit.Pixel);
            }
        }

        g.CompositingMode = CompositingMode.SourceOver;
        g.PixelOffsetMode = PixelOffsetMode.None;

        if (!IsEmpty(selection))
        {
            DrawBorder(g, selection);
            DrawPill(g, $"{selection.Width} × {selection.Height}", LabelBounds(selection));
        }

        if (ShowHint)
        {
            DrawPill(g, HintText, HintBounds);
        }
    }

    /// <summary>Where the screen turns bright or dim, plus both borders and both size labels.</summary>
    public IEnumerable<Rectangle> ChangedAreas(Rectangle previous, Rectangle next)
    {
        foreach (var area in Subtract(previous, next).Concat(Subtract(next, previous)))
        {
            yield return area;
        }

        foreach (var selection in new[] { previous, next })
        {
            if (IsEmpty(selection))
            {
                continue;
            }

            var outside = BorderWidth + 2;
            yield return Rectangle.FromLTRB(selection.Left - outside, selection.Top - outside, selection.Right + outside, selection.Top + 1);
            yield return Rectangle.FromLTRB(selection.Left - outside, selection.Bottom - 1, selection.Right + outside, selection.Bottom + outside);
            yield return Rectangle.FromLTRB(selection.Left - outside, selection.Top, selection.Left + 1, selection.Bottom);
            yield return Rectangle.FromLTRB(selection.Right - 1, selection.Top, selection.Right + outside, selection.Bottom);
            yield return Rectangle.Inflate(LabelBounds(selection), 2, 2);
        }
    }

    /// <summary>The parts of <paramref name="a"/> outside <paramref name="b"/>, as up to four rectangles.</summary>
    private static IEnumerable<Rectangle> Subtract(Rectangle a, Rectangle b)
    {
        if (IsEmpty(a))
        {
            yield break;
        }

        var overlap = Rectangle.Intersect(a, b);
        if (IsEmpty(overlap))
        {
            yield return a;
            yield break;
        }

        var pieces = new[]
        {
            Rectangle.FromLTRB(a.Left, a.Top, a.Right, overlap.Top),
            Rectangle.FromLTRB(a.Left, overlap.Bottom, a.Right, a.Bottom),
            Rectangle.FromLTRB(a.Left, overlap.Top, overlap.Left, overlap.Bottom),
            Rectangle.FromLTRB(overlap.Right, overlap.Top, a.Right, overlap.Bottom),
        };
        foreach (var piece in pieces.Where(p => !IsEmpty(p)))
        {
            yield return piece;
        }
    }

    public Rectangle HintBounds
    {
        get
        {
            var size = PillSize(HintText);
            var x = _hintArea.Left + (_hintArea.Width - size.Width) / 2;
            var y = _hintArea.Top + (int)(28 * _scale);
            return new Rectangle(new Point(x, y), size);
        }
    }

    internal static bool IsEmpty(Rectangle r) => r.Width <= 0 || r.Height <= 0;

    private Rectangle LabelBounds(Rectangle selection)
    {
        var size = PillSize($"{selection.Width} × {selection.Height}");
        var gap = (int)(8 * _scale);
        var area = MonitorContaining(selection);

        var y = selection.Bottom + BorderWidth + gap;
        if (y + size.Height > area.Bottom)
        {
            y = selection.Top - BorderWidth - gap - size.Height;
        }

        if (y < area.Top)
        {
            y = selection.Top + gap; // no room outside: tuck it inside the selection
        }

        var x = Math.Clamp(selection.Left, area.Left, Math.Max(area.Left, area.Right - size.Width));
        return new Rectangle(new Point(x, y), size);
    }

    /// <summary>The monitor holding the selection's bottom-left corner, otherwise the one showing most of it.</summary>
    private Rectangle MonitorContaining(Rectangle selection)
    {
        var corner = new Point(selection.Left, selection.Bottom - 1);
        foreach (var monitor in _monitors)
        {
            if (monitor.Contains(corner))
            {
                return monitor;
            }
        }

        var best = _bounds;
        var bestArea = -1L;
        foreach (var monitor in _monitors)
        {
            var overlap = Rectangle.Intersect(monitor, selection);
            var area = (long)Math.Max(0, overlap.Width) * Math.Max(0, overlap.Height);
            if (area > bestArea)
            {
                best = monitor;
                bestArea = area;
            }
        }

        return best;
    }

    private Size PillSize(string text)
    {
        var textSize = _measure.MeasureString(text, _font, PointF.Empty, StringFormat.GenericTypographic);
        return new Size(
            (int)Math.Ceiling(textSize.Width + 20 * _scale),
            (int)Math.Ceiling(textSize.Height + 10 * _scale));
    }

    private void DrawBorder(Graphics g, Rectangle s)
    {
        var t = BorderWidth;
        using var brush = new SolidBrush(Theme.Accent);
        g.SmoothingMode = SmoothingMode.None;
        g.FillRectangle(brush, s.Left - t, s.Top - t, s.Width + t * 2, t);
        g.FillRectangle(brush, s.Left - t, s.Bottom, s.Width + t * 2, t);
        g.FillRectangle(brush, s.Left - t, s.Top, t, s.Height);
        g.FillRectangle(brush, s.Right, s.Top, t, s.Height);
    }

    private void DrawPill(Graphics g, string text, Rectangle bounds)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.RoundedRectangle(bounds, 6 * _scale))
        using (var background = new SolidBrush(Color.FromArgb(235, 24, 24, 28)))
        {
            g.FillPath(background, path);
        }

        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString(text, _font, Brushes.White, bounds, format);
    }

    public void Dispose()
    {
        _dimmed.Dispose();
        _font.Dispose();
        _measure.Dispose();
        _measureSurface.Dispose();
    }
}