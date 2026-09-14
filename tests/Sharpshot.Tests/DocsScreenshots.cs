using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Sharpshot.Capture;
using Sharpshot.Links;
using Sharpshot.Settings;
using Sharpshot.UI;
using Sharpshot.UI.Controls;
using Sharpshot.Uploads;

namespace Sharpshot.Tests;

/// <summary>
/// Regenerates the screenshots in docs/images from the real UI, filled with sample data only.
/// Run with:  $env:SHARPSHOT_DOCS = "1"; dotnet test --filter DocsScreenshots
/// </summary>
public class DocsScreenshots
{
    [OptInFact("SHARPSHOT_DOCS")]
    public void GenerateReadmeImages()
    {
        var output = Path.Combine(FindRepositoryRoot(), "docs", "images");
        Directory.CreateDirectory(output);

        Sta.Run(() =>
        {
            Application.SetColorMode(SystemColorMode.Dark);
            Theme.Refresh();
            var samples = Enumerable.Range(0, 12).Select(SampleScreenshot).ToArray();
            try
            {
                using var http = new HttpClient();
                using var form = new SettingsForm(SampleSettings(), startWithWindows: true, firstRun: false, http, (_, _) => null)
                {
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-20000, -20000),
                    ShowInTaskbar = false,
                };
                form.Show();

                var shots = Enumerable.Range(0, 318)
                    .Select(i => new StoredShot($"{i}.png", i % 3 == 0 ? $"{i}" : null, 120_000 + (i * 7_919 % 900_000), DateTimeOffset.Now.AddHours(-i * 3.7),
                        i % 4 == 0 ? 2560 : 1920, i % 4 == 0 ? 1440 : 1080, null))
                    .ToList();
                var thumbnails = (Func<StoredShot, Bitmap?>)(shot => samples[int.Parse(Path.GetFileNameWithoutExtension(shot.ImageKey), System.Globalization.CultureInfo.InvariantCulture) % samples.Length]);

                form.Uploads.ShowContents(new BucketContents(shots, 1_240_000_000), thumbnails: thumbnails);
                Save(Window(form, SettingsForm.UploadsPage), output, "uploads.png");

                form.Uploads.ShowContents(new BucketContents(shots, 1_240_000_000), ["1.png", "2.png", "5.png"], confirming: true, thumbnails: thumbnails);
                Save(Window(form, SettingsForm.UploadsPage), output, "uploads-delete.png");

                form.ShowTestResult(StatusKind.Success, "Everything works, including embeds. Screenshots upload to \"screenshots\" and cdn.example.com serves them at full quality.");
                Save(Window(form, SettingsForm.StoragePage), output, "settings-r2.png");
                Save(Window(form, SettingsForm.LinksPage), output, "settings-links.png");
                Save(Window(form, SettingsForm.EmbedsPage), output, "settings-embeds.png");
                Save(Window(form, SettingsForm.CapturePage), output, "settings-capture.png");
                Save(Window(form, SettingsForm.GeneralPage), output, "settings-general.png");
                form.Close();

                using (var menu = PopupMenu.CreateForPreview(
                [
                    new MenuEntry("Capture region", Hint: "Ctrl+PrtScn"),
                    new MenuEntry("Capture full screen", Hint: "Ctrl+Shift+PrtScn"),
                    new MenuEntry("Recent uploads", Children: () => []),
                    new MenuEntry("Manage uploads"),
                    new MenuEntry("Open screenshots folder"),
                    new MenuEntry("Settings"),
                    new MenuEntry("Quit Sharpshot"),
                ], hoverIndex: 1))
                {
                    menu.CreateControl();
                    using var bitmap = new Bitmap(menu.Width, menu.Height);
                    menu.DrawToBitmap(bitmap, new Rectangle(Point.Empty, menu.Size));
                    Save(bitmap, output, "tray-menu.png", radius: 8);
                }
            }
            finally
            {
                foreach (var sample in samples)
                {
                    sample.Dispose();
                }
            }
        });

        using var desktop = SampleDesktop(1600, 900);
        using var renderer = new RegionOverlayRenderer(desktop, scale: 1.25f, hintArea: new Rectangle(0, 0, 1600, 900))
        {
            Selection = new Rectangle(560, 210, 720, 430),
        };
        renderer.ShowHint = false;
        using var overlay = new Bitmap(1600, 900, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(overlay))
        {
            renderer.Paint(g, new Rectangle(0, 0, 1600, 900));
        }

        using var scaled = new Bitmap(1200, 675);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(overlay, 0, 0, 1200, 675);
        }

        Save(scaled, output, "capture-region.png");
    }

    private static AppSettings SampleSettings()
    {
        var settings = new AppSettings();
        settings.R2.AccountId = "3f9c1e7a2b8d4c6e9f0a1b2c3d4e5f60";
        settings.R2.AccessKeyId = "8b1f2c3d4e5f60718293a4b5c6d7e8f9";
        settings.R2.SecretAccessKey = "sample-secret";
        settings.R2.Bucket = "screenshots";
        settings.R2.PublicUrl = "https://cdn.example.com";
        settings.Links.Style = LinkStyle.Braille;
        settings.Embed.Enabled = true;
        settings.Embed.SiteName = "example.com";
        settings.Embed.Title = "New screenshot";
        settings.Embed.Description = "{date} at {time}  ·  {dimensions}";
        settings.Embed.Color = "#EB459E";
        settings.Capture.LocalFolder = @"C:\Users\you\Pictures\Sharpshot";
        return settings;
    }

    /// <summary>Captured as Windows composes it, including the custom title bar.</summary>
    private static Bitmap Window(SettingsForm form, string page)
    {
        form.ShowPage(page);
        form.PerformLayout();
        for (var i = 0; i < 5; i++)
        {
            Application.DoEvents();
            Thread.Sleep(30);
        }

        using var window = new Bitmap(form.Width, form.Height);
        using (var g = Graphics.FromImage(window))
        {
            var hdc = g.GetHdc();
            PrintWindow(form.Handle, hdc, 2);
            g.ReleaseHdc(hdc);
        }

        var client = form.PointToScreen(Point.Empty);
        var crop = new Rectangle(client.X - form.Bounds.X, client.Y - form.Bounds.Y, form.ClientSize.Width, form.ClientSize.Height);
        return window.Clone(crop, PixelFormat.Format32bppArgb);
    }

    /// <summary>Adds rounded corners, a hairline edge and a soft shadow on a transparent background.</summary>
    private static void Save(Bitmap image, string folder, string name, int radius = 10)
    {
        const int shadow = 28;
        using var canvas = new Bitmap(image.Width + shadow * 2, image.Height + shadow * 2, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (var spread = shadow; spread > 0; spread -= 2)
            {
                var strength = 1f - spread / (float)shadow;
                using var brush = new SolidBrush(Color.FromArgb((int)(10 * strength * strength), 0, 0, 0));
                using var path = Theme.RoundedRectangle(new RectangleF(shadow - spread, shadow - spread + 8, image.Width + spread * 2, image.Height + spread * 2), radius + spread);
                g.FillPath(brush, path);
            }

            var bounds = new RectangleF(shadow, shadow, image.Width, image.Height);
            using (var content = Theme.RoundedRectangle(bounds, radius))
            using (var texture = new TextureBrush(image, WrapMode.Clamp))
            {
                texture.TranslateTransform(shadow, shadow);
                g.FillPath(texture, content);
            }

            using var edge = new Pen(Color.FromArgb(40, 255, 255, 255));
            using var edgePath = Theme.RoundedRectangle(RectangleF.Inflate(bounds, -0.5f, -0.5f), radius);
            g.DrawPath(edge, edgePath);
        }

        canvas.Save(Path.Combine(folder, name), ImageFormat.Png);
        image.Dispose();
    }

    private static Bitmap SampleScreenshot(int index)
    {
        Color[][] palettes =
        [
            [Color.FromArgb(30, 32, 44), Color.FromArgb(88, 101, 242), Color.FromArgb(235, 69, 158)],
            [Color.FromArgb(245, 246, 250), Color.FromArgb(59, 130, 246), Color.FromArgb(16, 185, 129)],
            [Color.FromArgb(24, 24, 27), Color.FromArgb(250, 204, 21), Color.FromArgb(244, 114, 182)],
            [Color.FromArgb(15, 39, 64), Color.FromArgb(56, 189, 248), Color.FromArgb(52, 211, 153)],
        ];
        var palette = palettes[index % palettes.Length];
        var bitmap = new Bitmap(320, 200);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(palette[0]);
        var light = palette[0].GetBrightness() > 0.5f;
        using var panel = new SolidBrush(light ? Color.FromArgb(255, 255, 255) : Color.FromArgb(40, 255, 255, 255));
        using var line = new SolidBrush(light ? Color.FromArgb(210, 214, 222) : Color.FromArgb(70, 255, 255, 255));
        using var accent = new SolidBrush(palette[1]);
        using var accent2 = new SolidBrush(palette[2]);

        switch (index % 3)
        {
            case 0: // chat
                g.FillRectangle(panel, 0, 0, 70, 200);
                for (var i = 0; i < 5; i++)
                {
                    g.FillEllipse(i == 1 ? accent : line, 12, 14 + i * 34, 22, 22);
                    using var bubble = Theme.RoundedRectangle(new RectangleF(92, 18 + i * 34, 120 + (i * 37 % 90), 22), 8);
                    g.FillPath(i % 2 == 0 ? line : accent, bubble);
                }

                break;
            case 1: // dashboard
                for (var i = 0; i < 6; i++)
                {
                    var height = 30 + (i * 53 % 110);
                    g.FillRectangle(i % 2 == 0 ? accent : accent2, 30 + i * 44, 170 - height, 28, height);
                }

                g.FillRectangle(line, 20, 20, 140, 10);
                g.FillRectangle(line, 20, 38, 90, 8);
                break;
            default: // editor
                g.FillRectangle(panel, 0, 0, 320, 24);
                for (var i = 0; i < 8; i++)
                {
                    g.FillRectangle(i % 3 == 0 ? accent : i % 3 == 1 ? accent2 : line, 24 + (i % 4) * 14, 40 + i * 19, 90 + (i * 41 % 140), 8);
                }

                break;
        }

        return bitmap;
    }

    private static Bitmap SampleDesktop(int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var wallpaper = new LinearGradientBrush(new Point(0, 0), new Point(width, height), Color.FromArgb(18, 32, 58), Color.FromArgb(64, 38, 88)))
        {
            g.FillRectangle(wallpaper, 0, 0, width, height);
        }

        using var sample = SampleScreenshot(0);
        using (var window = Theme.RoundedRectangle(new RectangleF(560, 210, 720, 430), 12))
        using (var texture = new TextureBrush(sample, WrapMode.Clamp))
        {
            // Map the sample onto the window: scale first, then move into place.
            texture.TranslateTransform(560, 210);
            texture.ScaleTransform(720f / sample.Width, 430f / sample.Height);
            g.FillPath(texture, window);
        }

        using var dim = new SolidBrush(Color.FromArgb(40, 255, 255, 255));
        using (var other = Theme.RoundedRectangle(new RectangleF(120, 140, 360, 520), 12))
        {
            g.FillPath(dim, other);
        }

        using var taskbar = new SolidBrush(Color.FromArgb(200, 20, 20, 24));
        g.FillRectangle(taskbar, 0, height - 48, width, 48);
        return bitmap;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sharpshot.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Couldn't find the repository root.");
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}