using Sharpshot.Capture;
using Sharpshot.Imaging;
using Sharpshot.Uploads;

namespace Sharpshot.Tests;

/// <summary>Takes real screenshots of this desktop.</summary>
public class ScreenCaptureTests
{
    [Fact]
    public void CapturesAPartOfTheRealScreenAndEncodesIt()
    {
        var screen = ScreenCapture.VirtualScreen;
        var area = new Rectangle(screen.X, screen.Y, Math.Min(200, screen.Width), Math.Min(120, screen.Height));

        using var bitmap = ScreenCapture.CaptureArea(area);
        var png = PngEncoder.Encode(bitmap);

        Assert.Equal(area.Size, bitmap.Size);
        using var decoded = new Bitmap(new MemoryStream(png));
        Assert.Equal(area.Size, decoded.Size);
    }

    [Fact]
    public void CapturesTheWholeVirtualScreen()
    {
        var screen = ScreenCapture.VirtualScreen;

        using var bitmap = ScreenCapture.CaptureArea(screen);

        Assert.Equal(screen.Size, bitmap.Size);
    }

    [Theory]
    [InlineData(Settings.FullscreenTarget.CursorMonitor)]
    [InlineData(Settings.FullscreenTarget.AllMonitors)]
    [InlineData(Settings.FullscreenTarget.PrimaryMonitor)]
    public void FullscreenTargetsResolveToRealScreenAreas(Settings.FullscreenTarget target)
    {
        var bounds = ScreenCapture.FullscreenBounds(target);

        Assert.True(bounds.Width > 0 && bounds.Height > 0);
        Assert.True(ScreenCapture.VirtualScreen.Contains(bounds));
    }

    [Fact]
    public void QueueCanBeDisposedTwice()
    {
        using var temp = new TempDirectory();
        var queue = new UploadQueue(temp.Path, () => null, job => new Sharpshot.Links.ShotNames(job.Key, null, job.Url));
        queue.Start();

        queue.Dispose();
        queue.Dispose();
    }
}