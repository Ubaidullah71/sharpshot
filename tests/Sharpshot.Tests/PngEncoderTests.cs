using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Sharpshot.Imaging;
using Xunit.Abstractions;

namespace Sharpshot.Tests;

public class PngEncoderTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 7)]
    [InlineData(64, 48)]
    [InlineData(257, 129)]
    public void RandomImagesRoundTripExactly(int width, int height)
    {
        using var source = RandomBitmap(width, height, seed: width * 31 + height);
        var png = PngEncoder.Encode(source);

        AssertValidChunks(png, expectedColorType: width * height > 256 ? (byte)2 : null);
        AssertSamePixels(source, new Rectangle(0, 0, width, height), png);
    }

    [Fact]
    public void FewColoursBecomeAPaletteImageAndRoundTripExactly()
    {
        using var source = new Bitmap(300, 200, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(source))
        {
            g.Clear(Color.FromArgb(30, 30, 30));
            g.FillRectangle(Brushes.OrangeRed, 20, 20, 120, 60);
            g.FillRectangle(Brushes.SteelBlue, 150, 90, 100, 80);
        }

        var png = PngEncoder.Encode(source);

        AssertValidChunks(png, expectedColorType: 3);
        AssertSamePixels(source, new Rectangle(0, 0, 300, 200), png);
    }

    [Fact]
    public void EncodesJustTheRequestedRegion()
    {
        using var source = RandomBitmap(200, 150, seed: 7);
        var region = new Rectangle(37, 21, 90, 64);

        var png = PngEncoder.Encode(source, region);

        AssertSamePixels(source, region, png);
    }

    [Fact]
    public void EmptyRegionIsRejected()
    {
        using var source = RandomBitmap(10, 10, seed: 1);
        Assert.Throws<ArgumentException>(() => PngEncoder.Encode(source, new Rectangle(20, 20, 5, 5)));
    }

    [Fact]
    public void ScreenshotLikeImageIsSmallerThanGdiPlusAndFastEnough()
    {
        using var source = ScreenshotLikeBitmap(1920, 1080);

        var stopwatch = Stopwatch.StartNew();
        var ours = PngEncoder.Encode(source);
        stopwatch.Stop();

        using var gdi = new MemoryStream();
        source.Save(gdi, ImageFormat.Png);

        output.WriteLine($"Sharpshot: {ours.Length:N0} bytes in {stopwatch.ElapsedMilliseconds} ms; GDI+: {gdi.Length:N0} bytes");
        AssertSamePixels(source, new Rectangle(0, 0, 1920, 1080), ours);
        Assert.True(ours.Length < gdi.Length, $"Expected smaller than GDI+ ({ours.Length:N0} vs {gdi.Length:N0})");
        Assert.True(stopwatch.ElapsedMilliseconds < 3000, $"Encoding took {stopwatch.ElapsedMilliseconds} ms");
    }

    private static Bitmap RandomBitmap(int width, int height, int seed)
    {
        var random = new Random(seed);
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
            }
        }

        return bitmap;
    }

    /// <summary>Gradients, anti-aliased text and flat panels: roughly what a desktop screenshot contains.</summary>
    internal static Bitmap ScreenshotLikeBitmap(int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(bitmap);
        using (var background = new LinearGradientBrush(new Point(0, 0), new Point(width, height), Color.FromArgb(24, 26, 32), Color.FromArgb(52, 40, 90)))
        {
            g.FillRectangle(background, 0, 0, width, height);
        }

        g.FillRectangle(new SolidBrush(Color.FromArgb(40, 42, 48)), 0, 0, 280, height);
        g.FillRectangle(Brushes.WhiteSmoke, 320, 80, width - 400, height - 160);
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using var font = new Font("Segoe UI", 11f);
        for (var line = 0; line < 40; line++)
        {
            g.DrawString($"Line {line}: the quick brown fox jumps over the lazy dog 0123456789", font, Brushes.Black, 340, 100 + line * 22);
            g.DrawString($"Channel {line}", font, Brushes.Gainsboro, 20, 20 + line * 26);
        }

        return bitmap;
    }

    private static void AssertSamePixels(Bitmap source, Rectangle region, byte[] png)
    {
        using var decoded = new Bitmap(new MemoryStream(png));
        Assert.Equal(region.Size, decoded.Size);

        var expected = ReadPixels(source, region);
        var actual = ReadPixels(decoded, new Rectangle(Point.Empty, decoded.Size));
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
            {
                var pixel = i / 4;
                Assert.Fail($"Pixel ({pixel % region.Width}, {pixel / region.Width}) differs");
            }
        }
    }

    /// <summary>BGRX bytes with the unused alpha byte cleared.</summary>
    private static byte[] ReadPixels(Bitmap bitmap, Rectangle region)
    {
        var data = bitmap.LockBits(region, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            var bytes = new byte[region.Width * region.Height * 4];
            for (var y = 0; y < region.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), bytes, y * region.Width * 4, region.Width * 4);
            }

            for (var i = 3; i < bytes.Length; i += 4)
            {
                bytes[i] = 0;
            }

            return bytes;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>Walks every chunk and checks its CRC, so a decoder that ignores CRCs can't hide a bug.</summary>
    private static void AssertValidChunks(byte[] png, byte? expectedColorType)
    {
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        var offset = 8;
        var types = new List<string>();
        while (offset < png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset));
            var type = png.AsSpan(offset + 4, 4);
            var data = png.AsSpan(offset + 8, length);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length));
            Assert.Equal(Crc32.Compute(type, data), crc);
            types.Add(System.Text.Encoding.ASCII.GetString(type));
            if (types.Count == 1 && expectedColorType is { } colorType)
            {
                Assert.Equal(colorType, data[9]);
            }

            offset += 12 + length;
        }

        Assert.Equal("IHDR", types[0]);
        Assert.Equal("IEND", types[^1]);
        Assert.Contains("IDAT", types);
    }
}