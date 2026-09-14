using System.Runtime.InteropServices;
using Sharpshot.Links;
using Sharpshot.Native;
using Sharpshot.Settings;
using Sharpshot.Storage;
using Sharpshot.UI.Controls;

namespace Sharpshot.UI;

internal sealed class SettingsForm : Form
{
    /// <summary>Returns an error message to show, or null on success.</summary>
    public delegate string? ApplyHandler(AppSettings settings, bool startWithWindows);

    public const string StoragePage = "Cloudflare R2";
    public const string UploadsPage = "Uploads";
    public const string LinksPage = "Links";
    public const string EmbedsPage = "Embeds";
    public const string CapturePage = "Capture";
    public const string GeneralPage = "General";

    private const string ExampleBaseUrl = "https://cdn.example.com";

    private readonly AppSettings _original;
    private readonly ApplyHandler _apply;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _closing = new();
    private readonly List<(NavItem Item, StackPanel Page)> _pages = [];
    private readonly Dictionary<string, Field> _fields = [];

    private readonly ModernTextBox _accountId = new(placeholder: "32-character ID");
    private readonly ModernTextBox _accessKeyId = new(placeholder: "From your R2 API token");
    private readonly ModernTextBox _secret = new() { UseSystemPasswordChar = true };
    private readonly ModernTextBox _bucket = new(placeholder: "e.g. screenshots");
    private readonly ModernTextBox _publicUrl = new(placeholder: "https://cdn.example.com");
    private readonly ModernTextBox _prefix = new(placeholder: "Optional, e.g. shots/");
    private readonly ModernTextBox _endpoint = new(placeholder: "Leave empty unless your bucket is in the EU jurisdiction");
    private readonly ActionRow _testRow = new(new ModernButton("Test connection", glyph: Glyphs.Sync));

    private readonly ModernDropDown _style = new();
    private readonly ModernStepper _length = new();
    private readonly ModernDropDown _copy = new();
    private readonly ModernButton _refreshExample = new("", glyph: Glyphs.Refresh);
    private SettingRow _styleRow = null!;
    private SettingRow _lengthRow = null!;
    private SettingRow _exampleRow = null!;

    private readonly HotkeyBox _regionHotkey = new();
    private readonly HotkeyBox _fullscreenHotkey = new();
    private readonly ModernDropDown _fullscreenTarget = new();
    private readonly ToggleSwitch _saveLocal = new();
    private readonly FolderPicker _folder = new(new ModernTextBox(placeholder: "Folder for local copies"), new ModernButton("Browse", glyph: Glyphs.Folder));

    private readonly ToggleSwitch _embedToggle = new();
    private readonly ModernTextBox _embedSiteName = new(placeholder: "Optional, e.g. example.com");
    private readonly ModernTextBox _embedTitle = new(placeholder: "Optional");
    private readonly ModernTextBox _embedDescription = new(placeholder: "Optional, e.g. {date} at {time}");
    private readonly ColorSwatches _embedColors = new();
    private readonly EmbedPreview _embedPreview = new();
    private Card _embedCard = null!;

    private UploadsView _uploads = null!;

    private readonly ToggleSwitch _autostart = new();
    private readonly ToggleSwitch _notify = new();

    private readonly FooterBar _footer = new() { Dock = DockStyle.Bottom, Height = 68 };
    private readonly TitleBar _titleBar = new() { Dock = DockStyle.Top, Height = TitleBarHeight };
    private readonly ToolTip _toolTip = new();
    private readonly Icon? _icon;
    private Panel _content = null!;

    /// <summary>Client size in logical pixels, including the custom title bar.</summary>
    private static readonly Size WindowClientSize = new(820, 600 + TitleBarHeight);

    private const int TitleBarHeight = 48;

    private string _sampleId = "";
    private LinkStyle? _lengthStyle;

    public SettingsForm(AppSettings settings, bool startWithWindows, bool firstRun, HttpClient http, ApplyHandler apply, Uploads.UploadLibrary? library = null)
    {
        _original = settings;
        _apply = apply;
        _http = http;
        var p = Theme.Current;

        SuspendLayout();
        Text = $"{AppInfo.Name} Settings";
        Font = new Font(UiFonts.Text, 10f);
        BackColor = p.Window;
        ForeColor = p.Text;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        try
        {
            Icon = _icon = AppIcons.Load(AppIcons.Idle, new Size(32, 32)); // taskbar and Alt+Tab
        }
        catch (InvalidOperationException)
        {
            // Icon is cosmetic.
        }

        var sidebar = new Sidebar { Dock = DockStyle.Left, Width = 224 };
        var content = _content = new Panel { Dock = DockStyle.Fill, BackColor = p.Window };
        content.Layout += (_, _) => ArrangePages();

        AddPage(sidebar, content, Glyphs.Cloud, StoragePage, BuildStoragePage(firstRun));
        AddPage(sidebar, content, Glyphs.Library, UploadsPage, BuildUploadsPage(library));
        AddPage(sidebar, content, Glyphs.Link, LinksPage, BuildLinksPage());
        AddPage(sidebar, content, Glyphs.Picture, EmbedsPage, BuildEmbedsPage());
        AddPage(sidebar, content, Glyphs.Camera, CapturePage, BuildCapturePage());
        AddPage(sidebar, content, Glyphs.Settings, GeneralPage, BuildGeneralPage());

        // Docking happens in reverse order of adding: title bar (full width), sidebar, footer, then content.
        Controls.Add(content);
        Controls.Add(_footer);
        Controls.Add(sidebar);
        Controls.Add(_titleBar);
        _titleBar.MinimizeClicked += (_, _) => WindowState = FormWindowState.Minimized;
        _titleBar.CloseClicked += (_, _) => Close();

        _footer.Save.Click += OnSaveClicked;
        _footer.Cancel.Click += (_, _) => Close();
        AcceptButton = _footer.Save;
        CancelButton = _footer.Cancel;

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = WindowClientSize;

        LoadValues(settings, startWithWindows || firstRun);
        SelectPage(0);
        ActiveControl = _pages[0].Item; // don't open with a text field focused and its text selected
        ResumeLayout(false);
        PerformLayout();
    }

    /// <param name="title">One of the *Page constants.</param>
    public void ShowPage(string title)
    {
        var index = _pages.FindIndex(page => page.Item.Text == title);
        if (index >= 0)
        {
            SelectPage(index);
        }
    }

    /// <summary>True while a hotkey box has focus, so capture hotkeys can pause while one is recorded.</summary>
    public event EventHandler<bool>? HotkeyRecordingChanged;

    internal IEnumerable<StackPanel> Pages => _pages.Select(page => page.Page);

    internal void ClickSave() => OnSaveClicked(this, EventArgs.Empty);

    internal void ShowTestResult(StatusKind kind, string message) => _testRow.SetStatus(kind, message);

    internal ModernButton TestButton => _testRow.Button;

    internal UploadsView Uploads => _uploads;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Re-run WM_NCCALCSIZE (below) so the system caption is replaced by our own title bar, then size the
        // window so the client area is exactly what the layout was designed for.
        NativeMethods.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        FitToWorkingArea();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        FitToWorkingArea();
    }

    /// <summary>On a small screen with high scaling, keeps Save and Cancel on screen and lets tall pages scroll instead.</summary>
    private void FitToWorkingArea()
    {
        var size = LogicalToDeviceUnits(WindowClientSize) + (Size - ClientSize);
        var workingArea = Screen.FromHandle(Handle).WorkingArea;
        if (size.Height > workingArea.Height)
        {
            size.Height = workingArea.Height;
            _content.AutoScroll = true;
        }

        Size = size;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (StartPosition == FormStartPosition.CenterScreen)
        {
            CenterToScreen(); // the size changed after Windows first placed the window
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            // Keep Windows' borders but hand the caption area to the client, where TitleBar draws it.
            var top = Marshal.PtrToStructure<NativeMethods.NCCALCSIZE_PARAMS>(m.LParam).Rect0.Top;
            base.WndProc(ref m);
            var calculated = Marshal.PtrToStructure<NativeMethods.NCCALCSIZE_PARAMS>(m.LParam);
            calculated.Rect0.Top = top;
            Marshal.StructureToPtr(calculated, m.LParam, false);
            m.Result = IntPtr.Zero;
            return;
        }

        base.WndProc(ref m);

        if (m.Msg == NativeMethods.WM_NCHITTEST && m.Result == NativeMethods.HTCLIENT)
        {
            var point = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
            if (point.Y < _titleBar.Bottom && !_titleBar.IsOverButton(point))
            {
                m.Result = NativeMethods.HTCAPTION; // drag the window by the title bar
            }
        }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _titleBar.Active = true;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        _titleBar.Active = false;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closing.Cancel();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closing.Dispose();
            _toolTip.Dispose();
            _icon?.Dispose();
        }

        base.Dispose(disposing);
    }

    private StackPanel BuildStoragePage(bool firstRun)
    {
        var page = NewPage(
            StoragePage,
            firstRun ? "Welcome! Connect your R2 bucket to start sharing screenshots." : "Where your screenshots are uploaded and served from.",
            "Setup guide",
            AppInfo.SetupGuideUrl);

        var grid = new FieldGrid();
        grid.AddRow(NewField("Account ID", _accountId, nameof(R2Settings.AccountId)), NewField("Bucket", _bucket, nameof(R2Settings.Bucket)));
        grid.AddRow(NewField("Access Key ID", _accessKeyId, nameof(R2Settings.AccessKeyId)), NewField("Secret Access Key", _secret, nameof(R2Settings.SecretAccessKey)));
        grid.AddRow(NewField("Public URL", _publicUrl, nameof(R2Settings.PublicUrl)), NewField("Path prefix", _prefix, nameof(R2Settings.PathPrefix)));
        grid.AddRow(NewField("Custom endpoint", _endpoint, nameof(R2Settings.Endpoint)));

        _testRow.Button.Click += OnTestClicked;
        var card = page.Add(new Card(), spaceBefore: 20);
        card.Controls.Add(grid);
        card.Controls.Add(_testRow);
        return page;
    }

    private StackPanel BuildUploadsPage(Uploads.UploadLibrary? library)
    {
        var page = NewPage(UploadsPage, "Free up R2 space by deleting screenshots. Copies on this PC are kept.");
        // Saved settings, not unsaved edits.
        _uploads = page.Add(new UploadsView(() => _original, library), spaceBefore: 20);
        _uploads.SetupRequested += (_, _) => ShowPage(StoragePage);
        page.Fill = _uploads;
        return page;
    }

    private StackPanel BuildLinksPage()
    {
        var page = NewPage(LinksPage, "How the random file name at the end of each link looks.");

        _style.SetItems(LinkStyles.All.Select(s => s.Experimental ? $"{s.DisplayName} (experimental)" : s.DisplayName));
        _style.SelectedIndexChanged += (_, _) => OnStyleChanged();
        _length.ValueChanged += (_, _) => UpdateLinkPreview(newSample: true);
        _copy.SetItems(["Right away", "After uploading"]);

        _styleRow = new SettingRow(Glyphs.Link, "Link style", null, _style, editorWidth: 200);
        _lengthRow = new SettingRow(Glyphs.FontSize, "Length", null, _length);
        var copyRow = new SettingRow(Glyphs.Copy, "Copy link",
            "Right away is fastest; wait for the upload if Discord shows no preview", _copy, editorWidth: 200);

        var card = page.Add(new Card(), spaceBefore: 20);
        card.Controls.Add(_styleRow);
        card.Controls.Add(_lengthRow);
        card.Controls.Add(copyRow);

        _refreshExample.Click += (_, _) => UpdateLinkPreview(newSample: true);
        _refreshExample.AccessibleName = "Show another example";
        _toolTip.SetToolTip(_refreshExample, "Show another example");
        _exampleRow = new SettingRow(Glyphs.Globe, ExampleBaseUrl, "Example link", _refreshExample);
        var example = page.Add(new Card(), spaceBefore: 12);
        example.Controls.Add(_exampleRow);

        page.Add(new TextBlock("Tip: in Discord, send a link on its own and only the image is shown.", TextStyle.Caption), spaceBefore: 12);

        _publicUrl.TextChanged += (_, _) => UpdateLinkPreview(newSample: false);
        _prefix.TextChanged += (_, _) => UpdateLinkPreview(newSample: false);
        return page;
    }

    private StackPanel BuildEmbedsPage()
    {
        var page = NewPage(EmbedsPage, "Share links that show as a card with your image, colour and text.");

        _embedToggle.CheckedChanged += (_, _) =>
        {
            _embedCard.Enabled = _embedToggle.Checked;
            UpdateLinkPreview(newSample: false);
        };
        var toggle = page.Add(new Card(), spaceBefore: 20);
        toggle.Controls.Add(new SettingRow(Glyphs.Picture, "Share as embed",
            "Links show up as a rich card in Discord and other apps", _embedToggle));

        foreach (var box in new[] { _embedSiteName, _embedTitle, _embedDescription })
        {
            box.TextChanged += (_, _) => UpdateEmbedPreview();
        }

        _embedColors.ColorChanged += (_, _) => UpdateEmbedPreview();
        _embedCard = page.Add(new Card(), spaceBefore: 12);
        _embedCard.Controls.Add(new EmbedEditor(
            new Field("Site name", _embedSiteName),
            new Field("Title", _embedTitle),
            new Field("Description", _embedDescription),
            _embedColors,
            _embedPreview));
        return page;
    }

    private void UpdateEmbedPreview() =>
        _embedPreview.ShowEmbed(_embedColors.Color, _embedSiteName.Text, _embedTitle.Text, _embedDescription.Text);

    private StackPanel BuildCapturePage()
    {
        var page = NewPage(CapturePage, "Hotkeys, and where screenshots are kept on this PC.");

        _fullscreenTarget.SetItems(["Monitor under the mouse", "All monitors", "Main monitor"]);
        var hotkeys = page.Add(new Card(), spaceBefore: 20);
        hotkeys.Controls.Add(new SettingRow(Glyphs.Keyboard, "Region hotkey", "Drag to select part of the screen",
            new ModernTextBox(_regionHotkey), editorWidth: 250));
        hotkeys.Controls.Add(new SettingRow(Glyphs.FullScreen, "Full screen hotkey", "Capture a whole screen at once",
            new ModernTextBox(_fullscreenHotkey), editorWidth: 250));
        hotkeys.Controls.Add(new SettingRow(Glyphs.Monitor, "Full screen captures", null, _fullscreenTarget, editorWidth: 250));

        page.Add(new TextBlock("To change a hotkey, click its box and press the new keys. Backspace clears it.", TextStyle.Caption), spaceBefore: 8);

        foreach (var box in new[] { _regionHotkey, _fullscreenHotkey })
        {
            box.GotFocus += (_, _) => HotkeyRecordingChanged?.Invoke(this, true);
            box.LostFocus += (_, _) => HotkeyRecordingChanged?.Invoke(this, false);
        }

        _saveLocal.CheckedChanged += (_, _) => _folder.Enabled = _saveLocal.Checked;
        _folder.Browse.Click += OnBrowseClicked;
        var copies = page.Add(new Card(), spaceBefore: 16);
        copies.Controls.Add(new SettingRow(Glyphs.Folder, "Keep a copy on this PC", "Every screenshot is also saved here, sorted by month",
            _saveLocal, below: _folder));
        return page;
    }

    private StackPanel BuildGeneralPage()
    {
        var page = NewPage(GeneralPage, "Startup, notifications and information about Sharpshot.");

        var general = page.Add(new Card(), spaceBefore: 20);
        general.Controls.Add(new SettingRow(Glyphs.Power, "Start with Windows", "Starts in the tray when you sign in", _autostart));
        general.Controls.Add(new SettingRow(Glyphs.Ringer, "Upload notifications", "Show a notification when each upload finishes", _notify));

        var github = new ModernButton("GitHub", glyph: Glyphs.OpenInNew);
        github.Click += (_, _) => Shell.OpenUrl(AppInfo.RepositoryUrl);

        var about = page.Add(new Card(), spaceBefore: 16);
        about.Controls.Add(new AboutHeader(github));
        about.Controls.Add(new SettingRow(Glyphs.Document, "Setup guide", "How to connect your Cloudflare R2 bucket",
            () => Shell.OpenUrl(AppInfo.SetupGuideUrl)));
        about.Controls.Add(new SettingRow(Glyphs.FolderOpen, "Open log folder", "Useful when something isn't working",
            () => Shell.OpenFolder(AppPaths.Default.DataDirectory), Glyphs.ChevronRight));
        return page;
    }

    private void LoadValues(AppSettings s, bool startWithWindows)
    {
        _accountId.Text = s.R2.AccountId;
        _accessKeyId.Text = s.R2.AccessKeyId;
        _secret.PlaceholderText = string.IsNullOrEmpty(s.R2.SecretAccessKey) ? "Shown once when you create the token" : "Saved. Leave empty to keep it";
        _bucket.Text = s.R2.Bucket;
        _publicUrl.Text = s.R2.PublicUrl;
        _prefix.Text = s.R2.PathPrefix;
        _endpoint.Text = s.R2.Endpoint;

        _style.SelectedIndex = Math.Max(0, LinkStyles.All.ToList().FindIndex(x => x.Style == s.Links.Style));
        OnStyleChanged();
        _length.Value = s.Links.EffectiveLength;
        _copy.SelectedIndex = (int)s.Links.Copy;

        _regionHotkey.Hotkey = s.Capture.RegionHotkey;
        _fullscreenHotkey.Hotkey = s.Capture.FullscreenHotkey;
        _fullscreenTarget.SelectedIndex = (int)s.Capture.FullscreenTarget;
        _saveLocal.Checked = s.Capture.SaveLocalCopy;
        _folder.Enabled = s.Capture.SaveLocalCopy;
        _folder.Box.Text = s.Capture.LocalFolder;

        _embedToggle.Checked = s.Embed.Enabled;
        _embedSiteName.Text = s.Embed.SiteName;
        _embedTitle.Text = s.Embed.Title;
        _embedDescription.Text = s.Embed.Description;
        _embedColors.Color = s.Embed.Color;
        _embedCard.Enabled = s.Embed.Enabled;
        UpdateEmbedPreview();

        _autostart.Checked = startWithWindows;
        _notify.Checked = s.General.NotifyOnUpload;
        UpdateLinkPreview(newSample: true);
    }

    private AppSettings BuildSettings()
    {
        var s = _original.Clone();

        s.R2.AccountId = _accountId.Text.Trim();
        s.R2.AccessKeyId = _accessKeyId.Text.Trim();
        if (!string.IsNullOrWhiteSpace(_secret.Text))
        {
            s.R2.SecretAccessKey = _secret.Text.Trim();
        }

        s.R2.Bucket = _bucket.Text.Trim();
        s.R2.PublicUrl = ObjectNaming.NormalizeBaseUrl(_publicUrl.Text);
        s.R2.PathPrefix = ObjectNaming.NormalizePrefix(_prefix.Text);
        s.R2.Endpoint = _endpoint.Text.Trim();

        s.Links.Style = SelectedStyle.Style;
        s.Links.Length = _length.Value;
        s.Links.Copy = (CopyLinkMode)Math.Max(0, _copy.SelectedIndex);

        s.Capture.RegionHotkey = _regionHotkey.Hotkey;
        s.Capture.FullscreenHotkey = _fullscreenHotkey.Hotkey;
        s.Capture.FullscreenTarget = (FullscreenTarget)Math.Max(0, _fullscreenTarget.SelectedIndex);
        s.Capture.SaveLocalCopy = _saveLocal.Checked;
        s.Capture.LocalFolder = _folder.Box.Text.Trim();
        s.General.NotifyOnUpload = _notify.Checked;

        s.Embed.Enabled = _embedToggle.Checked;
        s.Embed.SiteName = _embedSiteName.Text.Trim();
        s.Embed.Title = _embedTitle.Text.Trim();
        s.Embed.Description = _embedDescription.Text.Trim();
        s.Embed.Color = _embedColors.Color;
        return s;
    }

    private LinkStyleInfo SelectedStyle => LinkStyles.All[Math.Clamp(_style.SelectedIndex, 0, LinkStyles.All.Count - 1)];

    private void OnStyleChanged()
    {
        var style = SelectedStyle;
        if (_lengthStyle != style.Style)
        {
            _length.Minimum = style.MinLength;
            _length.Maximum = LinkStyleInfo.MaxLength;
            _length.Value = style.DefaultLength;
            _lengthStyle = style.Style;
        }

        _styleRow.Description = style.Description;
        UpdateLinkPreview(newSample: true);
    }

    private void UpdateLinkPreview(bool newSample)
    {
        if (_exampleRow is null || _lengthRow is null)
        {
            return;
        }

        var style = SelectedStyle;
        if (newSample || _sampleId.Length == 0)
        {
            _sampleId = KeyGenerator.NewId(style, _length.Value);
        }

        var baseUrl = ObjectNaming.IsValidBaseUrl(_publicUrl.Text) ? _publicUrl.Text : ExampleBaseUrl;
        var prefix = ObjectNaming.IsValidPrefix(_prefix.Text) ? _prefix.Text : "";
        var key = _embedToggle.Checked ? ObjectNaming.BuildPageKey(prefix, _sampleId) : ObjectNaming.BuildKey(prefix, _sampleId);
        _exampleRow.Title = ObjectNaming.BuildDisplayUrl(baseUrl, key);
        _lengthRow.Description = $"{style.BitsFor(_length.Value):0} bits of randomness, so nobody can guess your links";
    }

    private void OnSaveClicked(object? sender, EventArgs e)
    {
        var next = BuildSettings();
        var fieldProblems = ShowFieldProblems(next.R2);
        var problems = SettingsValidator.Validate(next);
        if (problems.Count > 0)
        {
            if (fieldProblems > 0)
            {
                ShowPage(StoragePage);
                _footer.SetError(fieldProblems == 1 ? "Fix the highlighted field first." : $"Fix the {fieldProblems} highlighted fields first.");
            }
            else
            {
                ShowPage(next.Embed.Enabled && !EmbedPage.IsValidColor(next.Embed.Color) ? EmbedsPage : CapturePage);
                _footer.SetError(problems[0]);
            }

            return;
        }

        _footer.SetError(null);
        var error = _apply(next, _autostart.Checked);
        if (error is not null)
        {
            _footer.SetError(error);
            return;
        }

        Close();
    }

    private async void OnTestClicked(object? sender, EventArgs e)
    {
        var draft = BuildSettings();
        if (ShowFieldProblems(draft.R2) > 0)
        {
            _testRow.SetStatus(StatusKind.Error, "Fill in the highlighted fields first.");
            return;
        }

        _testRow.Button.Enabled = false;
        _testRow.Button.Text = "Testing…";
        _testRow.Button.Glyph = null;
        _testRow.SetStatus(StatusKind.Working, "Uploading a tiny test image and downloading it through your domain…");
        try
        {
            var result = await ConnectionTester.RunAsync(draft, _http, _closing.Token);
            if (!IsDisposed)
            {
                _testRow.SetStatus(result.Success ? StatusKind.Success : StatusKind.Error, result.Message);
            }
        }
        catch (OperationCanceledException)
        {
            // The window was closed mid-test.
        }
        catch (Exception ex)
        {
            Log.Error("Connection test failed unexpectedly", ex);
            if (!IsDisposed)
            {
                _testRow.SetStatus(StatusKind.Error, $"The test failed unexpectedly: {ex.Message}");
            }
        }
        finally
        {
            if (!IsDisposed)
            {
                _testRow.Button.Enabled = true;
                _testRow.Button.Text = "Test connection";
                _testRow.Button.Glyph = Glyphs.Sync;
            }
        }
    }

    /// <returns>The number of highlighted R2 fields.</returns>
    private int ShowFieldProblems(R2Settings r2)
    {
        var problems = SettingsValidator.FindR2Problems(r2).ToDictionary(problem => problem.Field);
        foreach (var (name, field) in _fields)
        {
            field.Error = problems.TryGetValue(name, out var problem) ? problem.ShortMessage : null;
        }

        return problems.Count;
    }

    private void OnBrowseClicked(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose where to keep copies of your screenshots",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = Directory.Exists(_folder.Box.Text) ? _folder.Box.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _folder.Box.Text = dialog.SelectedPath;
        }
    }

    private StackPanel NewPage(string title, string subtitle, string? linkText = null, string? linkUrl = null)
    {
        var page = new StackPanel { BackColor = Theme.Current.Window, Visible = false }; // placed by ArrangePages
        page.Add(new TextBlock(title, TextStyle.Title));
        var intro = page.Add(new TextBlock(subtitle, TextStyle.Body, linkText) { ColorOverride = Theme.Current.SecondaryText }, spaceBefore: 4);
        if (linkUrl is not null)
        {
            intro.LinkClicked += (_, _) => Shell.OpenUrl(linkUrl);
        }

        return page;
    }

    private Field NewField(string label, ModernTextBox box, string name)
    {
        var field = new Field(label, box);
        box.TextChanged += (_, _) => _footer.SetError(null);
        _fields[name] = field;
        return field;
    }

    private void AddPage(Sidebar sidebar, Panel content, string glyph, string title, StackPanel page)
    {
        var item = sidebar.AddItem(glyph, title);
        var index = _pages.Count;
        item.Click += (_, _) => SelectPage(index);
        page.Layout += (_, _) => OnPageLayout(page);
        content.Controls.Add(page);
        _pages.Add((item, page));
    }

    /// <summary>
    /// When the window had to be shorter than designed, a page that needs more height gets it and the area scrolls.
    /// The Uploads page always fits, as its grid scrolls by itself.
    /// </summary>
    private void ArrangePages()
    {
        var area = _content.ClientSize;
        var origin = _content.DisplayRectangle.Location; // offset by the scroll position
        foreach (var (_, page) in _pages)
        {
            page.SetBounds(origin.X, origin.Y, area.Width, area.Height);
            if (_content.AutoScroll && page.Fill is null && page.ContentHeight > area.Height)
            {
                // Leave room for the vertical scroll bar, or WinForms adds a horizontal one as well.
                page.Width = _content.VerticalScroll.Visible ? area.Width : area.Width - SystemInformation.VerticalScrollBarWidth;
                page.Height = Math.Max(area.Height, page.ContentHeight);
            }
        }
    }

    /// <summary>A page whose content grew (say, a long connection test message) gets re-arranged so it can be scrolled to.</summary>
    private void OnPageLayout(StackPanel page)
    {
        if (_content.AutoScroll && page.Visible && page.Fill is null && _content.IsHandleCreated
            && page.Height != Math.Max(_content.ClientSize.Height, page.ContentHeight))
        {
            _content.BeginInvoke(_content.PerformLayout);
        }
    }

    private void SelectPage(int index)
    {
        // Pause redraw so switching pages doesn't flicker.
        var pause = _content.IsHandleCreated;
        if (pause)
        {
            NativeMethods.SendMessageW(_content.Handle, NativeMethods.WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        }

        try
        {
            _content.AutoScrollPosition = Point.Empty;
            for (var i = 0; i < _pages.Count; i++)
            {
                _pages[i].Item.Selected = i == index;
                _pages[i].Page.Visible = i == index;
            }

            _content.PerformLayout();

            if (_pages[index].Item.Text == UploadsPage && IsHandleCreated)
            {
                _uploads.EnsureLoaded();
            }
        }
        finally
        {
            if (pause)
            {
                NativeMethods.SendMessageW(_content.Handle, NativeMethods.WM_SETREDRAW, 1, IntPtr.Zero);
                NativeMethods.RedrawWindow(_content.Handle, IntPtr.Zero, IntPtr.Zero,
                    NativeMethods.RDW_ERASE | NativeMethods.RDW_INVALIDATE | NativeMethods.RDW_ALLCHILDREN | NativeMethods.RDW_UPDATENOW);
            }
        }
    }
}