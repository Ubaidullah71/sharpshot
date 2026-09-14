using System.Drawing.Drawing2D;
using Sharpshot.Settings;
using Sharpshot.Uploads;

namespace Sharpshot.UI.Controls;

/// <summary>Only changes R2, never local copies. Nothing is fetched until the page is opened.</summary>
internal sealed class UploadsView : ThemedControl
{
    private enum Mode
    {
        NotLoaded,
        NotConfigured,
        Loading,
        Error,
        Ready,
        Confirming,
        Deleting,
    }

    private const int ThumbnailCacheLimit = 160;

    private static readonly Size ThumbnailSize = new(320, 200);

    private readonly Func<AppSettings> _settings;
    private readonly UploadLibrary? _library;
    private readonly ThumbnailGrid _grid = new();
    private readonly ModernButton _refresh = new("", glyph: Glyphs.Refresh) { AccessibleName = "Refresh" };
    private readonly ToolTip _toolTip = new();
    private readonly ModernButton _delete = new("Delete") { Danger = true };
    private readonly ModernButton _cancel = new("Cancel");
    private readonly ModernButton _retry = new("Try again");
    private readonly System.Windows.Forms.Timer _spinner = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _noticeTimer = new() { Interval = 4000 };
    /// <summary>A null bitmap couldn't be loaded. LastUsed drives least-recently-used eviction.</summary>
    private readonly Dictionary<string, (Bitmap? Bitmap, long LastUsed)> _thumbnails = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loadingThumbnails = new(StringComparer.Ordinal);
    private long _thumbnailClock;
    private readonly SemaphoreSlim _thumbnailSlots = new(2);
    private readonly CancellationTokenSource _disposing = new();

    /// <summary>Right to left. Control.Visible can't be used: it's false while the page is hidden.</summary>
    private ModernButton[] _toolbarButtons = [];

    private Mode _mode = Mode.NotLoaded;
    private BucketContents? _contents;
    private string? _message;
    private string? _notice;
    private float _angle;
    private int _deletingDone;
    private int _deletingTotal;

    public UploadsView(Func<AppSettings> settings, UploadLibrary? library)
    {
        _settings = settings;
        _library = library;
        BackColor = P.Window;

        _grid.Thumbnails = GetThumbnail;
        _grid.SelectionChanged += (_, _) => OnSelectionChanged();
        _grid.DeleteRequested += (_, _) => AskToDelete();
        _grid.OpenRequested += shot => Shell.OpenUrl(RequestUrl(shot));
        _grid.ContextMenuRequested += ShowContextMenu;
        _refresh.Click += (_, _) => Reload();
        _toolTip.SetToolTip(_refresh, "Refresh");
        _retry.Click += (_, _) =>
        {
            if (_mode == Mode.NotConfigured)
            {
                SetupRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Reload();
            }
        };
        _delete.Click += (_, _) =>
        {
            if (_mode == Mode.Confirming)
            {
                _ = DeleteSelectedAsync();
            }
            else
            {
                AskToDelete();
            }
        };
        _cancel.Click += (_, _) =>
        {
            if (_mode == Mode.Confirming)
            {
                SetMode(Mode.Ready);
            }
            else
            {
                _grid.ClearSelection();
            }
        };
        _spinner.Tick += (_, _) =>
        {
            _angle = (_angle + 6f) % 360f;
            Invalidate(ToolbarBounds);
        };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            _notice = null;
            Invalidate();
        };

        Controls.AddRange([_grid, _refresh, _delete, _cancel, _retry]);
        SetMode(Mode.NotLoaded);
    }

    public event EventHandler? SetupRequested;

    public void EnsureLoaded()
    {
        if (_mode == Mode.NotLoaded)
        {
            Reload();
        }
    }

    /// <summary>Doesn't touch the network; for previews and tests.</summary>
    internal void ShowContents(BucketContents contents, IEnumerable<string>? selectedKeys = null, bool confirming = false, Func<StoredShot, Bitmap?>? thumbnails = null)
    {
        if (thumbnails is not null)
        {
            _grid.Thumbnails = thumbnails;
        }

        _contents = contents;
        _grid.SetItems(contents.Shots);
        foreach (var shot in contents.Shots.Where(s => selectedKeys?.Contains(s.ImageKey) == true))
        {
            _grid.SetSelected(shot, true);
        }

        SetMode(confirming ? Mode.Confirming : Mode.Ready);
    }

    private int Pad => Dp(16);

    private Rectangle ToolbarBounds => new(0, 0, Width, Dp(64));

    public override Size GetPreferredSize(Size proposedSize) => new(proposedSize.Width, Dp(64) + Dp(12) + Dp(200));

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var toolbar = ToolbarBounds;
        var right = Width - Pad;
        foreach (var button in _toolbarButtons)
        {
            var size = button.GetPreferredSize(Size.Empty);
            button.SetBounds(right - size.Width, toolbar.Top + (toolbar.Height - size.Height) / 2, size.Width, size.Height);
            right -= size.Width + Dp(8);
        }

        _grid.SetBounds(0, toolbar.Bottom + Dp(12), Width, Math.Max(0, Height - toolbar.Bottom - Dp(12)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var toolbar = ToolbarBounds;
        var card = new RectangleF(0.5f, 0.5f, toolbar.Width - 1f, toolbar.Height - 1f);
        Theme.FillRounded(g, P.Card, card, Dpf(6));
        Theme.DrawRounded(g, P.CardBorder, card, Dpf(6));

        var textRight = _toolbarButtons.Select(b => b.Left).DefaultIfEmpty(Width - Pad).Min() - Dp(16);
        var textArea = Rectangle.FromLTRB(Pad, toolbar.Top, textRight, toolbar.Bottom);
        switch (_mode)
        {
            case Mode.Loading or Mode.Deleting:
                DrawProgressRing(g, new PointF(Pad + Dpf(9), toolbar.Top + toolbar.Height / 2f), 18, _angle);
                var working = _mode == Mode.Loading ? "Loading your uploads…" : $"Deleting {Math.Min(_deletingDone + 1, _deletingTotal)} of {_deletingTotal}…";
                DrawText(g, working, Font, Rectangle.FromLTRB(Pad + Dp(30), textArea.Top, textArea.Right, textArea.Bottom), P.SecondaryText,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                break;

            case Mode.Error or Mode.NotConfigured:
                DrawText(g, Glyphs.ErrorBadge, IconFont(1.1f), new Rectangle(Pad, toolbar.Top, Dp(18), toolbar.Height), _mode == Mode.Error ? P.Error : P.SecondaryText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                DrawText(g, _message ?? "", Font, Rectangle.FromLTRB(Pad + Dp(28), textArea.Top + Dp(4), textArea.Right, textArea.Bottom - Dp(4)), P.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
                break;

            case Mode.Confirming:
                var count = _grid.Selected.Count;
                DrawTwoLines(g, textArea, $"Delete {count} screenshot{(count == 1 ? "" : "s")} from Cloudflare R2?",
                    "Their links stop working. Local copies stay.", P.Text);
                break;

            case Mode.Ready:
                PaintSummary(g, textArea);
                break;
        }

        if (_mode == Mode.Ready && _contents is { Shots.Count: 0 })
        {
            var empty = _grid.Bounds;
            DrawText(g, Glyphs.Picture, IconFont(2.4f), new Rectangle(empty.Left, empty.Top + Dp(40), empty.Width, Dp(40)), P.DisabledText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            DrawText(g, "No screenshots in your bucket yet", Font, new Rectangle(empty.Left, empty.Top + Dp(88), empty.Width, Font.Height + Dp(4)), P.SecondaryText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine);
        }
    }

    private void PaintSummary(Graphics g, Rectangle area)
    {
        // A notice ("Link copied") shows briefly; changing the selection dismisses it.
        if (_notice is not null)
        {
            DrawText(g, Glyphs.Completed, IconFont(1.1f), new Rectangle(area.Left, area.Top, Dp(18), area.Height), P.Success,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            DrawText(g, _notice, Font, Rectangle.FromLTRB(area.Left + Dp(28), area.Top, area.Right, area.Bottom), P.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            return;
        }

        var selected = _grid.Selected;
        if (selected.Count > 0)
        {
            DrawTwoLines(g, area, $"{selected.Count} selected", $"{Sizes.Format(selected.Sum(s => s.SizeBytes))}  ·  Ctrl+A selects all, Esc clears", P.Text);
            return;
        }

        var contents = _contents!;

        var count = contents.Shots.Count;
        var title = $"{count} screenshot{(count == 1 ? "" : "s")}  ·  {Sizes.Format(contents.TotalBytes)} used of {Sizes.Format(UploadLibrary.FreeTierBytes)} free";
        var titleFont = DerivedFont(UiFonts.TextSemibold, 1f);
        var top = area.Top + (area.Height - (titleFont.Height + Dp(8) + Dp(4))) / 2;
        DrawText(g, title, titleFont, new Rectangle(area.Left, top, area.Width, titleFont.Height), P.Text,
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

        var bar = new RectangleF(area.Left, top + titleFont.Height + Dp(8), Math.Min(area.Width, Dp(260)), Dpf(4));
        Theme.FillRounded(g, P.ControlBorder, bar, bar.Height / 2f);
        var used = Math.Clamp((float)contents.TotalBytes / UploadLibrary.FreeTierBytes, 0f, 1f);
        if (used > 0)
        {
            var fill = bar with { Width = Math.Max(bar.Height, bar.Width * used) };
            Theme.FillRounded(g, used >= 0.9f ? P.Error : P.Accent, fill, bar.Height / 2f);
        }
    }

    private void DrawTwoLines(Graphics g, Rectangle area, string title, string subtitle, Color color)
    {
        var titleFont = DerivedFont(UiFonts.TextSemibold, 1f);
        var top = area.Top + (area.Height - (titleFont.Height + CaptionFont.Height + Dp(2))) / 2;
        DrawText(g, title, titleFont, new Rectangle(area.Left, top, area.Width, titleFont.Height), color, TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        DrawText(g, subtitle, CaptionFont, new Rectangle(area.Left, top + titleFont.Height + Dp(2), area.Width, CaptionFont.Height), P.SecondaryText,
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    private void SetMode(Mode mode)
    {
        _mode = mode;
        var selection = _grid.Selected.Count > 0;
        _toolbarButtons = mode switch
        {
            Mode.Confirming => [_cancel, _delete],
            Mode.Ready when selection => [_cancel, _delete],
            Mode.Ready => [_refresh],
            Mode.Error or Mode.NotConfigured => [_retry],
            _ => [],
        };
        foreach (var button in new[] { _cancel, _delete, _refresh, _retry })
        {
            button.Visible = _toolbarButtons.Contains(button);
        }

        _retry.Text = mode == Mode.NotConfigured ? "Set up" : "Try again";
        _delete.Text = mode == Mode.Confirming ? "Yes, delete" : "Delete";
        _grid.Visible = mode is Mode.Ready or Mode.Confirming or Mode.Deleting;
        _grid.Enabled = mode is Mode.Ready;
        _spinner.Enabled = mode is Mode.Loading or Mode.Deleting;
        PerformLayout();
        Invalidate();
    }

    private void OnSelectionChanged()
    {
        _notice = null;
        _noticeTimer.Stop();
        if (_mode is Mode.Ready or Mode.Confirming)
        {
            SetMode(Mode.Ready);
        }
    }

    /// <summary>Esc cancels the delete confirmation instead of closing the window.</summary>
    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape && _mode == Mode.Confirming)
        {
            SetMode(Mode.Ready);
            _grid.Focus();
            return true;
        }

        return base.ProcessDialogKey(keyData);
    }

    private async void Reload()
    {
        var settings = _settings();
        if (_library is null || !SettingsValidator.IsR2Ready(settings))
        {
            _message = "Connect your Cloudflare R2 bucket first, then your uploads show up here.";
            SetMode(Mode.NotConfigured);
            return;
        }

        SetMode(Mode.Loading);
        try
        {
            var contents = await _library.LoadAsync(settings, _disposing.Token);
            if (IsDisposed)
            {
                return;
            }

            _contents = contents;
            _grid.SetItems(contents.Shots);
            SetMode(Mode.Ready);
        }
        catch (OperationCanceledException) when (_disposing.IsCancellationRequested)
        {
            // Closing.
        }
        catch (Exception ex)
        {
            // Catch-all so the page never sticks on "Loading".
            Log.Warn($"Couldn't list uploads: {ex.Message}");
            if (!IsDisposed)
            {
                _message = ex is Storage.StorageException ? $"Couldn't load your uploads. {ex.Message}" : $"Couldn't load your uploads: {ex.Message}";
                SetMode(Mode.Error);
            }
        }
    }

    private void AskToDelete()
    {
        if (_mode == Mode.Ready && _grid.Selected.Count > 0)
        {
            SetMode(Mode.Confirming);
            _cancel.Focus();
        }
    }

    private async Task DeleteSelectedAsync()
    {
        var shots = _grid.Selected;
        if (_library is null || shots.Count == 0 || _contents is null)
        {
            return;
        }

        _deletingDone = 0;
        _deletingTotal = shots.Count;
        var hadFocus = ContainsFocus; // the buttons disappear while deleting, so give focus back afterwards
        SetMode(Mode.Deleting);
        DeleteResult result;
        try
        {
            var progress = new Progress<int>(done =>
            {
                _deletingDone = done;
                Invalidate(ToolbarBounds);
            });
            result = await _library.DeleteAsync(_settings(), shots, progress, _disposing.Token);
        }
        catch (OperationCanceledException) when (_disposing.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error("Deleting uploads failed unexpectedly", ex);
            if (!IsDisposed)
            {
                _message = $"Couldn't delete: {ex.Message} Refresh to see what's still stored.";
                SetMode(Mode.Error);
                RestoreFocus(hadFocus, _retry);
            }

            return;
        }

        if (IsDisposed)
        {
            return;
        }

        // Some may have been deleted before a failure; those leave the grid either way.
        var deletedKeys = result.Deleted.Select(s => s.ImageKey).ToHashSet(StringComparer.Ordinal);
        var failure = result.Error;
        foreach (var key in deletedKeys)
        {
            ForgetThumbnail(key);
        }

        var removedBytes = _contents.Shots.Where(s => deletedKeys.Contains(s.ImageKey)).Sum(s => s.SizeBytes);
        _contents = new BucketContents(_contents.Shots.Where(s => !deletedKeys.Contains(s.ImageKey)).ToList(), Math.Max(0, _contents.TotalBytes - removedBytes));
        _grid.SetItems(_contents.Shots);

        if (failure is not null)
        {
            _message = $"Deleted {deletedKeys.Count} of {shots.Count}, then hit a problem. {failure}";
            SetMode(Mode.Error);
            RestoreFocus(hadFocus, _retry);
            return;
        }

        _grid.ClearSelection();
        _notice = $"Deleted {deletedKeys.Count} screenshot{(deletedKeys.Count == 1 ? "" : "s")}  ·  freed {Sizes.Format(removedBytes)}";
        _noticeTimer.Stop();
        _noticeTimer.Start();
        SetMode(Mode.Ready);
        RestoreFocus(hadFocus, _grid);
    }

    private static void RestoreFocus(bool hadFocus, Control target)
    {
        if (hadFocus && target.CanFocus)
        {
            target.Focus();
        }
    }

    private void ShowContextMenu(StoredShot shot, Point screen)
    {
        if (_mode != Mode.Ready)
        {
            return;
        }

        PopupMenu.Open(
        [
            new MenuEntry("Copy link", () => CopyLink(shot)),
            new MenuEntry("Open in browser", () => Shell.OpenUrl(RequestUrl(shot))),
            new MenuEntry("Delete from R2", () =>
            {
                _grid.ClearSelection();
                _grid.SetSelected(shot, true);
                AskToDelete();
            }),
        ], screen);
    }

    private void CopyLink(StoredShot shot)
    {
        try
        {
            Clipboard.SetText(Links.ObjectNaming.BuildDisplayUrl(_settings().R2.PublicUrl, shot.PageKey ?? shot.ImageKey), TextDataFormat.UnicodeText);
            _notice = "Link copied";
            _noticeTimer.Stop();
            _noticeTimer.Start();
            Invalidate();
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Log.Warn($"Clipboard unavailable: {ex.Message}");
        }
    }

    private string RequestUrl(StoredShot shot) => Links.ObjectNaming.BuildRequestUrl(_settings().R2.PublicUrl, shot.PageKey ?? shot.ImageKey);

    private Bitmap? GetThumbnail(StoredShot shot)
    {
        if (_thumbnails.TryGetValue(shot.ImageKey, out var cached))
        {
            _thumbnails[shot.ImageKey] = cached with { LastUsed = ++_thumbnailClock };
            return cached.Bitmap;
        }

        if (_library is not null && _loadingThumbnails.Add(shot.ImageKey))
        {
            _ = LoadThumbnailAsync(shot);
        }

        return null;
    }

    /// <summary>Loads on a background thread, two at a time; continues on the UI thread.</summary>
    private async Task LoadThumbnailAsync(StoredShot shot)
    {
        var token = _disposing.Token;
        Bitmap? bitmap;
        try
        {
            await _thumbnailSlots.WaitAsync(token);
            try
            {
                // Scrolled out of view while queued; it's requested again if it comes back.
                if (!_grid.IsShowing(shot.ImageKey))
                {
                    _loadingThumbnails.Remove(shot.ImageKey);
                    return;
                }

                var settings = _settings();
                bitmap = await Task.Run(() => _library!.LoadThumbnailAsync(settings, shot, ThumbnailSize, token), token);
            }
            finally
            {
                _thumbnailSlots.Release();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return; // the window is closing
        }

        _loadingThumbnails.Remove(shot.ImageKey);
        if (IsDisposed)
        {
            bitmap?.Dispose();
            return;
        }

        _thumbnails[shot.ImageKey] = (bitmap, ++_thumbnailClock);
        if (_thumbnails.Count > ThumbnailCacheLimit)
        {
            ForgetThumbnail(_thumbnails.MinBy(entry => entry.Value.LastUsed).Key);
        }

        _grid.Invalidate();
    }

    private void ForgetThumbnail(string key)
    {
        if (_thumbnails.Remove(key, out var entry))
        {
            entry.Bitmap?.Dispose();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposing.Cancel();
            _spinner.Dispose();
            _noticeTimer.Dispose();
            _toolTip.Dispose();
            foreach (var entry in _thumbnails.Values)
            {
                entry.Bitmap?.Dispose();
            }

            _thumbnails.Clear();
            _disposing.Dispose();
        }

        base.Dispose(disposing);
    }
}