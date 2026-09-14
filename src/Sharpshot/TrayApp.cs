using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Sharpshot.Capture;
using Sharpshot.Imaging;
using Sharpshot.Links;
using Sharpshot.Native;
using Sharpshot.Settings;
using Sharpshot.Storage;
using Sharpshot.UI;
using Sharpshot.Uploads;

namespace Sharpshot;

internal sealed class TrayApp : ApplicationContext
{
    private const int RegionHotkeyId = 1;
    private const int FullscreenHotkeyId = 2;
    private const int RecentCount = 10;

    private readonly SettingsStore _settingsStore;
    private readonly HttpClient _http;
    private readonly UploadHistory _history;
    private readonly UploadLibrary _library;
    private readonly UploadQueue _queue;
    private readonly HotkeyManager _hotkeys = new();
    private readonly Control _ui = new();
    private readonly Icon _idleIcon;
    private readonly Icon _busyIcon;
    private readonly NotifyIcon _tray;
    private readonly RegisteredWaitHandle _showSettingsWait;
    private PopupMenu? _trayMenu;

    private readonly HashSet<string> _copyWhenUploaded = [];

    private readonly HashSet<string> _copiedEarly = [];

    /// <summary>Waited for on exit so no screenshot is lost before it reaches the queue.</summary>
    private readonly List<Task> _processingTasks = [];

    private AppSettings _settings;
    private volatile IObjectStore? _store;
    private SettingsForm? _settingsForm;
    private bool _capturing;
    private int _processing;
    private bool _showingBusyIcon;
    private string? _notificationUrl;
    private bool _disposed;

    public TrayApp(AppPaths paths, bool launchedAtStartup, WaitHandle showSettingsSignal)
    {
        _ = _ui.Handle; // a hidden window for marshalling background work onto the UI thread

        _settingsStore = new SettingsStore(paths.SettingsFile);
        _settings = _settingsStore.Load();
        _http = CreateHttpClient();
        RebuildStore();

        _history = new UploadHistory(paths.HistoryFile);
        _library = new UploadLibrary(_history, paths.ThumbnailDirectory, _http);
        _queue = new UploadQueue(paths.QueueDirectory, () => _store, _ => ObjectNaming.NewNames(_settings),
            pageBuilder: BuildEmbedPage, recordUpload: RecordUpload);
        _queue.Completed += job => Post(() => OnUploadCompleted(job));
        _queue.Failed += job => Post(() => OnUploadFailed(job));
        _queue.Delayed += job => Post(() => OnUploadDelayed(job));
        _queue.Renamed += (job, _) => Post(() => OnUploadRenamed(job));
        _queue.Changed += () => Post(UpdateTrayState);

        _idleIcon = AppIcons.Load(AppIcons.Idle, SystemInformation.SmallIconSize);
        _busyIcon = AppIcons.Load(AppIcons.Busy, SystemInformation.SmallIconSize);

        _tray = new NotifyIcon { Icon = _idleIcon, Text = AppInfo.Name, Visible = true };
        _tray.MouseClick += OnTrayClicked;
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_notificationUrl is { } url)
            {
                Shell.OpenUrl(url);
            }
        };

        _hotkeys.Pressed += id => Capture(fullscreen: id == FullscreenHotkeyId);
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _showSettingsWait = ThreadPool.RegisterWaitForSingleObject(showSettingsSignal, (_, _) => Post(() => ShowSettings()), null, Timeout.Infinite, executeOnlyOnce: false);

        AutoStart.RefreshPath();
        _queue.Start();

        var hotkeyProblem = RegisterHotkeys(_settings.Capture);
        UpdateTrayState();

        if (!SettingsValidator.IsR2Ready(_settings))
        {
            if (launchedAtStartup)
            {
                Notify("Sharpshot needs setting up", "Right-click the tray icon and open Settings to connect Cloudflare R2.", ToolTipIcon.Info);
            }
            else
            {
                Post(() => ShowSettings());
            }
        }
        else if (hotkeyProblem is not null)
        {
            Notify("A hotkey isn't available", hotkeyProblem, ToolTipIcon.Warning);
        }
        else if (!launchedAtStartup)
        {
            Notify("Sharpshot is running", $"Press {_settings.Capture.RegionHotkey.ToDisplayString()} to capture a region.", ToolTipIcon.Info);
        }
    }

    private void Capture(bool fullscreen)
    {
        if (_capturing)
        {
            return;
        }

        var settings = _settings;
        if (!SettingsValidator.IsR2Ready(settings))
        {
            Notify("Connect Cloudflare R2 first", "Sharpshot needs your bucket details before it can upload screenshots.", ToolTipIcon.Warning);
            ShowSettings();
            return;
        }

        _capturing = true;
        Bitmap? screenshot = null;
        try
        {
            Rectangle region;
            if (fullscreen)
            {
                var bounds = ScreenCapture.FullscreenBounds(settings.Capture.FullscreenTarget);
                screenshot = ScreenCapture.CaptureArea(bounds);
                region = new Rectangle(Point.Empty, bounds.Size);
            }
            else
            {
                var bounds = ScreenCapture.VirtualScreen;
                screenshot = ScreenCapture.CaptureArea(bounds);
                using var selector = new RegionSelectorForm(screenshot, bounds);
                if (selector.ShowDialog() != DialogResult.OK || selector.Selection is not { } selection)
                {
                    return;
                }

                region = selection;
            }

            var takenAt = DateTimeOffset.Now;
            var names = ObjectNaming.NewNames(settings);
            var jobId = UploadJob.NewId();

            if (settings.Links.Copy == CopyLinkMode.Immediately)
            {
                if (CopyLink(names.Link))
                {
                    _copiedEarly.Add(jobId);
                }
            }
            else
            {
                _copyWhenUploaded.Add(jobId);
            }

            var image = screenshot;
            screenshot = null; // the background task owns it now
            Interlocked.Increment(ref _processing);
            UpdateTrayState();
            var task = Task.Run(() => Process(jobId, image, region, names, takenAt, settings));
            lock (_processingTasks)
            {
                _processingTasks.RemoveAll(t => t.IsCompleted);
                _processingTasks.Add(task);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Capture failed", ex);
            Notify("Screenshot failed", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            screenshot?.Dispose();
            _capturing = false;
        }
    }

    /// <summary>Runs on a background thread.</summary>
    private void Process(string jobId, Bitmap image, Rectangle region, ShotNames names, DateTimeOffset takenAt, AppSettings settings)
    {
        try
        {
            byte[] png;
            using (image)
            {
                png = PngEncoder.Encode(image, region);
            }

            string? localPath = null;
            if (settings.Capture.SaveLocalCopy)
            {
                try
                {
                    localPath = LocalCopies.Save(settings.Capture.LocalFolder, takenAt, png);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    Log.Error("Couldn't save local copy", ex);
                    Post(() => Notify("Couldn't save a local copy", $"{ex.Message} The upload continues.", ToolTipIcon.Warning));
                }
            }

            _queue.Enqueue(new UploadJob
            {
                Id = jobId,
                CreatedAt = takenAt,
                Key = names.ImageKey,
                PageKey = names.PageKey,
                Url = names.Link,
                Width = region.Width,
                Height = region.Height,
                SizeBytes = png.Length,
                LocalCopyPath = localPath,
            }, png);
        }
        catch (Exception ex)
        {
            Log.Error("Processing a screenshot failed", ex);
            Post(() => Notify("Screenshot failed", $"{ex.Message} The copied link won't work.", ToolTipIcon.Error));
        }
        finally
        {
            Interlocked.Decrement(ref _processing);
            Post(UpdateTrayState);
            ReleaseMemory();
        }
    }

    /// <summary>Runs on the upload thread, with URLs from the current settings.</summary>
    private byte[] BuildEmbedPage(UploadJob job)
    {
        var settings = _settings;
        var pageKey = job.PageKey ?? job.Key;
        return EmbedPage.Build(settings.Embed, new EmbedImage(
            ObjectNaming.BuildRequestUrl(settings.R2.PublicUrl, pageKey),
            ObjectNaming.BuildRequestUrl(settings.R2.PublicUrl, job.Key),
            job.Width,
            job.Height,
            job.SizeBytes,
            job.CreatedAt));
    }

    /// <summary>Screenshots allocate large buffers; hand the memory back so the idle footprint stays small.</summary>
    private static void ReleaseMemory()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    /// <summary>Uses the current Public URL: if the domain was fixed while the upload waited, the old link never worked.</summary>
    private string FinalLink(UploadJob job) => ObjectNaming.BuildDisplayUrl(_settings.R2.PublicUrl, job.PageKey ?? job.Key);

    /// <summary>Runs on the upload thread.</summary>
    private void RecordUpload(UploadJob job)
    {
        try
        {
            _history.Append(new HistoryEntry(job.CreatedAt, FinalLink(job), job.Key, job.Width, job.Height, job.SizeBytes, job.LocalCopyPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Couldn't write upload history", ex);
        }
    }

    private void OnUploadCompleted(UploadJob job)
    {
        var url = FinalLink(job);
        var linkChanged = _copiedEarly.Remove(job.Id) && url != job.Url;
        var copied = (_copyWhenUploaded.Remove(job.Id) || linkChanged) && CopyLink(url);

        if (_settings.General.NotifyOnUpload || linkChanged)
        {
            Notify(
                linkChanged ? "Uploaded with a new link (copied)" : copied ? "Uploaded, link copied" : "Uploaded",
                $"{url}\n{job.Width} × {job.Height}  ·  {Sizes.Format(job.SizeBytes)}  ·  click to open",
                ToolTipIcon.None,
                url);
        }

        UpdateTrayState();
    }

    private void OnUploadFailed(UploadJob job)
    {
        var copiedLink = _copiedEarly.Contains(job.Id) ? " The copied link won't work until it uploads." : "";
        if (job.Discarded)
        {
            _copiedEarly.Remove(job.Id);
            _copyWhenUploaded.Remove(job.Id);
            Notify("Upload failed", $"{job.LastError}{copiedLink}", ToolTipIcon.Error);
        }
        else
        {
            Notify(
                "Upload failed",
                $"{job.LastError}{copiedLink} Fix it, then choose Retry failed uploads from the tray menu.",
                ToolTipIcon.Error);
        }

        UpdateTrayState();
    }

    private void OnUploadDelayed(UploadJob job)
    {
        var copiedLink = _copiedEarly.Contains(job.Id) ? " The copied link will work once it's uploaded." : "";
        Notify(
            "Upload is taking a while",
            $"{job.LastError} Sharpshot keeps retrying in the background.{copiedLink}",
            ToolTipIcon.Warning);
    }

    private void OnUploadRenamed(UploadJob job)
    {
        if (_copiedEarly.Contains(job.Id) && CopyLink(job.Url))
        {
            Notify("Link changed", "The file name was taken, so the screenshot got a new link. It's on your clipboard.", ToolTipIcon.Warning);
        }
    }

    /// <param name="page">Page to open, e.g. <see cref="SettingsForm.UploadsPage"/>.</param>
    private void ShowSettings(string? page = null)
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.WindowState = FormWindowState.Normal;
            if (page is not null)
            {
                _settingsForm.ShowPage(page);
            }

            _settingsForm.Activate();
            return;
        }

        bool startWithWindows;
        try
        {
            startWithWindows = AutoStart.IsEnabled();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            startWithWindows = false;
        }

        Theme.Refresh(); // follow a change to the Windows light/dark setting since the last window
        _settingsForm = new SettingsForm(_settings, startWithWindows, firstRun: !_settingsStore.Exists, _http, ApplySettings, _library);
        // Capture hotkeys keep working while Settings is open, except while a hotkey box is recording keys.
        _settingsForm.HotkeyRecordingChanged += (_, recording) =>
        {
            if (recording)
            {
                _hotkeys.UnregisterAll();
            }
            else
            {
                RegisterHotkeys(_settings.Capture);
            }
        };
        _settingsForm.FormClosed += (_, _) =>
        {
            _settingsForm = null; // a closed modeless form disposes itself
            _ = Task.Run(ReleaseMemory); // free the Uploads page's thumbnails
            var problem = RegisterHotkeys(_settings.Capture);
            if (problem is not null)
            {
                Notify("A hotkey isn't available", problem, ToolTipIcon.Warning);
            }
        };
        _settingsForm.Show();
        if (page is not null)
        {
            _settingsForm.ShowPage(page);
        }

        _settingsForm.Activate();
    }

    private string? ApplySettings(AppSettings next, bool startWithWindows)
    {
        // Hotkeys are registered when the window closes, so a taken hotkey never blocks saving the R2 details.
        try
        {
            _settingsStore.Save(next);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Couldn't save settings", ex);
            return $"Couldn't save settings: {ex.Message}";
        }

        try
        {
            AutoStart.Set(startWithWindows);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Error("Couldn't change the startup setting", ex);
        }

        _settings = next;
        RebuildStore();
        _queue.RetryFailed(); // new details may fix earlier failures
        Log.Info("Settings saved");
        return null;
    }

    private void RebuildStore() =>
        _store = SettingsValidator.IsR2Ready(_settings) ? new R2Client(R2Config.From(_settings.R2), _http) : null;

    /// <summary>Returns a message describing any hotkeys that are taken, or null.</summary>
    private string? RegisterHotkeys(CaptureSettings capture)
    {
        // Free both first, so swapping the two hotkeys doesn't collide with the old registration.
        _hotkeys.UnregisterAll();
        var taken = new List<Hotkey>();
        if (!_hotkeys.Register(RegionHotkeyId, capture.RegionHotkey))
        {
            taken.Add(capture.RegionHotkey);
        }

        if (!_hotkeys.Register(FullscreenHotkeyId, capture.FullscreenHotkey))
        {
            taken.Add(capture.FullscreenHotkey);
        }

        if (taken.Count == 0)
        {
            return null;
        }

        var names = string.Join(" and ", taken.Select(h => h.ToDisplayString()));
        var message = $"{names} {(taken.Count == 1 ? "is" : "are")} already used by another app. Choose a different hotkey in Settings.";
        if (taken.Any(h => h.Key == Keys.PrintScreen))
        {
            message += " For Print Screen, turn off \"Use the Print screen key to open screen capture\" in Windows Settings > Accessibility > Keyboard.";
        }

        return message;
    }

    /// <summary>Rebuilt each time so hotkeys, failed uploads and history are always current.</summary>
    internal IReadOnlyList<MenuEntry> BuildTrayMenu()
    {
        var capture = _settings.Capture;
        var entries = new List<MenuEntry>
        {
            new("Capture region", () => RunSoon(() => Capture(fullscreen: false)), Hint(capture.RegionHotkey)),
            new("Capture full screen", () => RunSoon(() => Capture(fullscreen: true)), Hint(capture.FullscreenHotkey)),
            new("Recent uploads", Children: RecentUploads),
        };

        var failed = _queue.FailedCount;
        if (failed > 0)
        {
            entries.Add(new($"Retry failed uploads ({failed})", _queue.RetryFailed));
        }

        entries.Add(new("Manage uploads", () => ShowSettings(SettingsForm.UploadsPage)));
        entries.Add(new("Open screenshots folder", OpenScreenshotsFolder));
        entries.Add(new("Settings", () => ShowSettings()));
        entries.Add(new($"Quit {AppInfo.Name}", ExitThread));
        return entries;

        static string? Hint(Hotkey hotkey) => hotkey.IsEmpty ? null : hotkey.ToShortcutString();
    }

    private IReadOnlyList<MenuEntry> RecentUploads()
    {
        IReadOnlyList<HistoryEntry> recent;
        try
        {
            recent = _history.ReadRecent(RecentCount);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Couldn't read upload history", ex);
            recent = [];
        }

        if (recent.Count == 0)
        {
            return [new("Nothing uploaded yet", Enabled: false)];
        }

        return recent.Select(entry => new MenuEntry(
            entry.CreatedAt.ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.CurrentCulture),
            () =>
            {
                if (CopyLink(entry.Url))
                {
                    Notify("Link copied", entry.Url, ToolTipIcon.None, entry.Url);
                }
            },
            $"{entry.Width} × {entry.Height}")).ToList();
    }

    private void OnTrayClicked(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            RunSoon(() => Capture(fullscreen: false));
        }
        else if (e.Button == MouseButtons.Right)
        {
            _trayMenu?.CloseAll();
            if (_settingsForm is null)
            {
                Theme.Refresh(); // not while Settings is open, so that window keeps one consistent look
            }

            _trayMenu = PopupMenu.Open(BuildTrayMenu(), Cursor.Position);
        }
    }

    private void UpdateTrayState()
    {
        if (_ui.IsDisposed)
        {
            return;
        }

        var busy = Volatile.Read(ref _processing) > 0 || _queue.PendingCount > 0;
        var failed = _queue.FailedCount;

        if (busy != _showingBusyIcon)
        {
            _tray.Icon = busy ? _busyIcon : _idleIcon;
            _showingBusyIcon = busy;
        }

        var hotkey = _settings.Capture.RegionHotkey;
        _tray.Text = busy ? $"{AppInfo.Name}: uploading…"
            : failed > 0 ? $"{AppInfo.Name}: {failed} upload{(failed == 1 ? "" : "s")} failed"
            : hotkey.IsEmpty ? AppInfo.Name
            : $"{AppInfo.Name}: press {hotkey.ToDisplayString()} or click to capture";
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable)
        {
            _queue.RetryNow();
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            _queue.RetryNow();
        }
    }

    private void OpenScreenshotsFolder()
    {
        var folder = _settings.Capture.LocalFolder;
        try
        {
            Directory.CreateDirectory(folder);
            Shell.OpenFolder(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Notify("Couldn't open the folder", ex.Message, ToolTipIcon.Warning);
        }
    }

    private bool CopyLink(string url)
    {
        try
        {
            Clipboard.SetText(url, TextDataFormat.UnicodeText);
            return true;
        }
        catch (ExternalException ex)
        {
            Log.Warn($"Clipboard unavailable: {ex.Message}");
            Notify("Couldn't copy the link", "Another app is holding the clipboard. Right-click the tray icon and choose Recent uploads to copy it.", ToolTipIcon.Warning);
            return false;
        }
    }

    private void Notify(string title, string text, ToolTipIcon icon, string? openUrl = null)
    {
        if (_ui.IsDisposed)
        {
            return;
        }

        _notificationUrl = openUrl;
        _tray.ShowBalloonTip(5000, Truncate(title, 60), Truncate(text, 250), icon);
    }

    private void Post(Action action)
    {
        try
        {
            if (!_ui.IsDisposed)
            {
                _ui.BeginInvoke(action);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    /// <summary>Runs after menus and the tray flyout have had time to disappear, so they're not in the screenshot.</summary>
    private static void RunSoon(Action action)
    {
        var timer = new System.Windows.Forms.Timer { Interval = 250 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            action();
        };
        timer.Start();
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.None,
            // R2 never redirects, and a redirect from the public domain would send requests somewhere unexpected.
            AllowAutoRedirect = false,
        };
        // Timeouts are per request, scaled to the upload size (see R2Client.TimeoutFor).
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
        return http;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    protected override void ExitThreadCore()
    {
        _tray.Visible = false; // don't leave a ghost icon behind
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        // WinForms disposes the context when the message loop ends, and Program's `using` does it again.
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (disposing)
        {
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _showSettingsWait.Unregister(null);

            // Let screenshots still being encoded reach the queue.
            Task[] processing;
            lock (_processingTasks)
            {
                processing = [.. _processingTasks];
            }

            if (processing.Length > 0 && !Task.WaitAll(processing, TimeSpan.FromSeconds(20)))
            {
                Log.Warn("Exited before a screenshot finished processing");
            }

            _settingsForm?.Dispose();
            _hotkeys.Dispose();
            _trayMenu?.CloseAll();
            _tray.Dispose();
            _queue.Dispose();
            _http.Dispose();
            _idleIcon.Dispose();
            _busyIcon.Dispose();
            _ui.Dispose();
        }

        base.Dispose(disposing);
    }
}