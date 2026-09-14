using System.Drawing.Imaging;
using Sharpshot.Capture;
using Sharpshot.Settings;
using Sharpshot.UI;
using Xunit.Abstractions;

namespace Sharpshot.Tests;

/// <summary>
/// Renders the real windows off-screen to PNGs in %TEMP%\sharpshot-ui, so layout problems can be checked without
/// running the app.
/// </summary>
public class UiRenderTests(ITestOutputHelper output)
{
    private static readonly string OutputFolder = Path.Combine(Path.GetTempPath(), "sharpshot-ui");

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void SettingsWindowBuildsAndEveryPageRenders(bool firstRun, bool dark)
    {
        Directory.CreateDirectory(OutputFolder);
        Sta.Run(() =>
        {
            Application.SetColorMode(dark ? SystemColorMode.Dark : SystemColorMode.Classic);
            Theme.Refresh();
            var settings = firstRun ? new AppSettings() : SettingsTests.ValidSettings();
            if (dark)
            {
                settings.Links.Style = Sharpshot.Links.LinkStyle.Braille;
                settings.Embed.Enabled = true;
                settings.Embed.SiteName = "example.com";
                settings.Embed.Title = "Look at this screenshot";
                settings.Embed.Color = "#EB459E";
            }

            using var http = new HttpClient();
            // Saving "fails" so the window stays open and the footer error can be rendered.
            using var form = new SettingsForm(settings, startWithWindows: true, firstRun, http, (_, _) => "Couldn't save settings: the file is in use by another program.")
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-20000, -20000),
                ShowInTaskbar = false,
                Opacity = 0,
            };
            form.Show();

            void Render(string page, string suffix = "")
            {
                form.ShowPage(page);
                form.PerformLayout();
                Application.DoEvents();
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                var name = $"settings-{(firstRun ? "first-run" : "configured")}-{(dark ? "dark" : "light")}-{page.Replace(' ', '-').ToLowerInvariant()}{suffix}.png";
                var file = Path.Combine(OutputFolder, name);
                bitmap.Save(file, ImageFormat.Png);
                output.WriteLine(file);
                AssertNoControlOverflows(form);
                foreach (var stack in form.Pages.Where(p => p.Visible))
                {
                    var available = stack.Parent!.ClientSize.Height;
                    Assert.True(stack.ContentHeight <= available, $"Page '{page}{suffix}' needs {stack.ContentHeight}px but has {available}px");
                }
            }

            foreach (var page in new[] { SettingsForm.StoragePage, SettingsForm.UploadsPage, SettingsForm.LinksPage, SettingsForm.EmbedsPage, SettingsForm.CapturePage, SettingsForm.GeneralPage })
            {
                Render(page);
            }

            // The Uploads page with a bucket full of screenshots: normal, with a selection, and asking to confirm.
            var now = DateTimeOffset.Now;
            var shots = Enumerable.Range(0, 14)
                .Select(i => new Sharpshot.Uploads.StoredShot($"shot{i}.png", i % 3 == 0 ? $"shot{i}" : null, 180_000 + i * 37_000, now.AddHours(-i * 5), i % 2 == 0 ? 1920 : 0, 1080, null))
                .ToList();
            form.Uploads.ShowContents(new Sharpshot.Uploads.BucketContents(shots, 1_240_000_000));
            Render(SettingsForm.UploadsPage, "-ready");
            form.Uploads.ShowContents(new Sharpshot.Uploads.BucketContents(shots, 1_240_000_000), ["shot1.png", "shot2.png"]);
            Render(SettingsForm.UploadsPage, "-selected");
            form.Uploads.ShowContents(new Sharpshot.Uploads.BucketContents(shots, 1_240_000_000), ["shot1.png", "shot2.png"], confirming: true);
            Render(SettingsForm.UploadsPage, "-confirm");

            // The Test connection button changes text while testing; it must never end up narrower than its text.
            form.ShowPage(SettingsForm.StoragePage);
            var button = form.TestButton;
            button.Text = "Testing…";
            button.Glyph = null;
            button.Text = "Test connection";
            button.Glyph = Glyphs.Sync;
            form.PerformLayout();
            Assert.True(button.Width >= button.GetPreferredSize(Size.Empty).Width, $"Test button is {button.Width}px, needs {button.GetPreferredSize(Size.Empty).Width}px");

            // Worst cases for space: every field invalid, and the longest connection-test message.
            form.ClickSave();
            form.ShowTestResult(Sharpshot.UI.Controls.StatusKind.Error,
                "Uploading works, but cdn.example.com blocked the download (HTTP 403). Turn off Bot Fight Mode, Hotlink Protection or WAF rules for this domain, otherwise Discord can't show previews.");
            Render(SettingsForm.StoragePage, "-errors");

            form.Close();
        });
    }

    /// <summary>
    /// DrawToBitmap paints a classic caption over the window, so the custom title bar is captured the way
    /// Windows composes it instead (PrintWindow with PW_RENDERFULLCONTENT).
    /// </summary>
    [Fact]
    public void CustomTitleBarReplacesTheSystemCaption()
    {
        Directory.CreateDirectory(OutputFolder);
        Sta.Run(() =>
        {
            Application.SetColorMode(SystemColorMode.Dark);
            Theme.Refresh();
            var settings = SettingsTests.ValidSettings();
            settings.Links.Style = Sharpshot.Links.LinkStyle.Braille;
            using var http = new HttpClient();
            using var form = new SettingsForm(settings, startWithWindows: true, firstRun: false, http, (_, _) => null)
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-20000, -20000),
                ShowInTaskbar = false,
            };
            form.Show();
            Application.DoEvents();

            // The client area now starts at the very top of the window (no system caption above it).
            var clientTop = form.PointToScreen(Point.Empty).Y - form.Bounds.Top;
            Assert.InRange(clientTop, 0, 2);

            using (var window = Capture(form))
            {
                window.Save(Path.Combine(OutputFolder, "settings-window-titlebar-dark.png"), ImageFormat.Png);
            }

            form.Close();

            static Bitmap Capture(Form window)
            {
                var bitmap = new Bitmap(window.Width, window.Height);
                using var g = Graphics.FromImage(bitmap);
                var hdc = g.GetHdc();
                PrintWindow(window.Handle, hdc, 2);
                g.ReleaseHdc(hdc);
                return bitmap;
            }
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    private static Rectangle Bounds(IEnumerable<Rectangle> areas) => areas.Aggregate(Rectangle.Union);

    [Fact]
    public void OpeningASubmenuKeepsTheMenuOpen()
    {
        Sta.Run(() =>
        {
            MenuEntry[] entries =
            [
                new("Capture region"),
                new("Recent uploads", Children: () => [new("Sep 14, 16:58", Hint: "1920 × 1080"), new("Sep 14, 17:10", Hint: "1836 × 919")]),
                new("Quit Sharpshot"),
            ];

            var menu = PopupMenu.Open(entries, new Point(200, 200));
            try
            {
                Application.DoEvents();
                var submenu = menu.OpenSubmenuNow(1);
                for (var i = 0; i < 10; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(20);
                }

                Assert.True(menu.IsOpen, "The main menu closed when its submenu opened");
                Assert.NotNull(submenu);
                Assert.True(submenu!.IsOpen && submenu.Visible, "The submenu didn't stay open");
                Assert.True(submenu.Left >= menu.Right - 10 || submenu.Right <= menu.Left + 10, "The submenu should open beside the menu");
            }
            finally
            {
                menu.CloseAll();
            }
        });
    }

    [Fact]
    public void ThemedMenuRendersWithSelectionPill()
    {
        Directory.CreateDirectory(OutputFolder);
        Sta.Run(() =>
        {
            Application.SetColorMode(SystemColorMode.Dark);
            Theme.Refresh();
            using var dropdown = new ContextMenuStrip();
            using var owner = new Control { Font = new Font(UiFonts.Text, 10f) };
            MenuRenderer.Style(dropdown, owner, checkMargin: true);
            foreach (var (text, selected) in new[] { ("Plain", false), ("Braille", true), ("Blocks", false), ("Emoji", false) })
            {
                dropdown.Items.Add(new ToolStripMenuItem(text) { Checked = selected, Padding = new Padding(0, 6, 10, 6) });
            }

            Save(dropdown, "dropdown-menu-dark.png", selectIndex: 2);

            MenuEntry[] trayEntries =
            [
                new("Capture region", Hint: "Ctrl+["),
                new("Capture full screen", Hint: "Ctrl+Shift+PrtScn"),
                new("Recent uploads", Children: () => [new("Sep 14, 16:58", Hint: "1920 × 1080")]),
                new("Open screenshots folder"),
                new("Settings"),
                new("Quit Sharpshot"),
            ];
            using (var tray = PopupMenu.CreateForPreview(trayEntries, hoverIndex: 3))
            {
                tray.CreateControl();
                using var bitmap = new Bitmap(tray.Width, tray.Height);
                tray.DrawToBitmap(bitmap, new Rectangle(Point.Empty, tray.Size));
                SaveWithZoom(bitmap, "tray-menu-dark.png");
            }

            void SaveWithZoom(Bitmap bitmap, string name)
            {
                bitmap.Save(Path.Combine(OutputFolder, name), ImageFormat.Png);
                using var zoom = new Bitmap(bitmap.Width * 3, bitmap.Height * 3);
                using (var g = Graphics.FromImage(zoom))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    g.DrawImage(bitmap, 0, 0, zoom.Width, zoom.Height);
                }

                zoom.Save(Path.Combine(OutputFolder, Path.GetFileNameWithoutExtension(name) + "-zoom.png"), ImageFormat.Png);
            }

            void Save(ContextMenuStrip menu, string name, int selectIndex)
            {
                menu.Show(new Point(-20000, -20000));
                menu.Items[selectIndex].Select();
                // Menus are layered windows that PrintWindow can't capture; DrawToBitmap draws the items
                // faithfully but shifts them up by the menu's top padding.
                using var bitmap = new Bitmap(menu.Width, menu.Height);
                menu.DrawToBitmap(bitmap, new Rectangle(Point.Empty, menu.Size));
                bitmap.Save(Path.Combine(OutputFolder, name), ImageFormat.Png);

                // A 3x enlargement makes pixel-level alignment easy to inspect.
                using var zoom = new Bitmap(bitmap.Width * 3, bitmap.Height * 3);
                using (var g = Graphics.FromImage(zoom))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    g.DrawImage(bitmap, 0, 0, zoom.Width, zoom.Height);
                }

                zoom.Save(Path.Combine(OutputFolder, Path.GetFileNameWithoutExtension(name) + "-zoom.png"), ImageFormat.Png);
                menu.Close();
            }
        });
    }

    [Fact]
    public void RegionOverlayRendersSelectionLabelAndHint()
    {
        Directory.CreateDirectory(OutputFolder);
        using var screen = PngEncoderTests.ScreenshotLikeBitmap(1600, 900);
        using var renderer = new RegionOverlayRenderer(screen, scale: 1.25f, hintArea: new Rectangle(0, 0, 1600, 900))
        {
            Selection = new Rectangle(420, 180, 640, 360),
        };
        using var canvas = new Bitmap(1600, 900, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(canvas))
        {
            renderer.Paint(g, new Rectangle(0, 0, 1600, 900));
        }

        var file = Path.Combine(OutputFolder, "region-overlay.png");
        canvas.Save(file, ImageFormat.Png);
        output.WriteLine(file);

        // Inside the selection the screen is untouched; outside it is dimmed.
        Assert.Equal(screen.GetPixel(700, 400).ToArgb(), canvas.GetPixel(700, 400).ToArgb());
        Assert.True(canvas.GetPixel(1500, 850).GetBrightness() < screen.GetPixel(1500, 850).GetBrightness());

        var dirty = Bounds(renderer.ChangedAreas(Rectangle.Empty, renderer.Selection));
        Assert.True(dirty.Contains(renderer.Selection));
        Assert.Empty(renderer.ChangedAreas(Rectangle.Empty, Rectangle.Empty));
    }

    [Fact]
    public void DraggingRepaintsOnlyWhatChanged()
    {
        using var screen = new Bitmap(3840, 2160, PixelFormat.Format32bppRgb);
        using var renderer = new RegionOverlayRenderer(screen, 1.5f, new Rectangle(0, 0, 3840, 2160));
        var before = new Rectangle(400, 300, 2400, 1400);
        var after = before with { Width = before.Width + 6, Height = before.Height + 4 };

        var areas = renderer.ChangedAreas(before, after).ToList();

        // Everything that changes is covered...
        Assert.All(new[] { new Point(before.Right + 2, 900), new Point(1200, before.Bottom + 1), new Point(before.Right, before.Top - 1) },
            point => Assert.Contains(areas, area => area.Contains(point)));
        // ...but the untouched middle of the selection isn't repainted.
        Assert.DoesNotContain(areas, area => area.Contains(1600, 1000));
        Assert.True(areas.Sum(a => (long)a.Width * a.Height) < (long)before.Width * before.Height / 20);
    }

    [Fact]
    public void SelectionLabelMovesInsideWhenThereIsNoRoomOutside()
    {
        using var screen = new Bitmap(800, 600, PixelFormat.Format32bppRgb);
        using var renderer = new RegionOverlayRenderer(screen, 1f, new Rectangle(0, 0, 800, 600));

        var fullScreen = new Rectangle(0, 0, 800, 600);
        var dirty = Bounds(renderer.ChangedAreas(Rectangle.Empty, fullScreen));

        Assert.True(dirty.Top >= -4 && dirty.Bottom <= 604, $"Label escaped the screen: {dirty}");
    }

    [Fact]
    public void SelectionLabelStaysOnItsOwnMonitorWhenMonitorsDiffer()
    {
        // A 1080p monitor next to a taller 1440p one: below y=1080 on the left is a gap with no screen.
        var monitors = new[] { new Rectangle(0, 0, 1920, 1080), new Rectangle(1920, 0, 2560, 1440) };
        using var screen = new Bitmap(4480, 1440, PixelFormat.Format32bppRgb);
        using var renderer = new RegionOverlayRenderer(screen, 1f, monitors[0], monitors);

        var nearBottom = new Rectangle(100, 900, 500, 170);
        var dirty = Bounds(renderer.ChangedAreas(Rectangle.Empty, nearBottom));

        Assert.True(dirty.Bottom <= nearBottom.Bottom + 4, $"Label fell into the gap below the monitor: {dirty}");
        Assert.True(dirty.Top < nearBottom.Top, "Label should move above the selection");
    }

    [Fact]
    public void TrayIconsLoadFromResources()
    {
        using var idle = AppIcons.Load(AppIcons.Idle, new Size(16, 16));
        using var busy = AppIcons.Load(AppIcons.Busy, new Size(32, 32));
        Assert.Equal(16, idle.Width);
        Assert.Equal(32, busy.Width);
    }

    private static void AssertNoControlOverflows(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child.Visible && root is not ScrollableControl { AutoScroll: true } && root.ClientSize.Width > 0)
            {
                Assert.True(child.Right <= root.ClientSize.Width + 1,
                    $"{child.GetType().Name} '{child.Text}' overflows {root.GetType().Name} ({child.Right} > {root.ClientSize.Width})");
            }

            AssertNoControlOverflows(child);
        }
    }
}