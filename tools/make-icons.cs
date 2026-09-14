#:property TargetFramework=net10.0-windows
#:property UseWindowsForms=true
#:property Nullable=enable
#:property PublishAot=false
#:property PublishTrimmed=false

// Generates assets/sharpshot.ico, assets/sharpshot-busy.ico and assets/sharpshot.png.
// Run from the repository root:  dotnet run tools/make-icons.cs

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

var assets = Path.Combine(Directory.GetCurrentDirectory(), "assets");
Directory.CreateDirectory(assets);

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 256];
WriteIco(Path.Combine(assets, "sharpshot.ico"), sizes, busy: false);
WriteIco(Path.Combine(assets, "sharpshot-busy.ico"), sizes, busy: true);
using (var preview = Render(512, busy: false))
{
    preview.Save(Path.Combine(assets, "sharpshot.png"), ImageFormat.Png);
}
Console.WriteLine($"Icons written to {assets}");

static Bitmap Render(int s, bool busy)
{
    var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.Clear(Color.Transparent);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;

    // The app's accent colour stands out on dark and light taskbars alike.
    var inset = s >= 32 ? s * 0.03f : 0f;
    var bg = new RectangleF(inset, inset, s - inset * 2, s - inset * 2);
    using (var path = RoundedRect(bg, s * 0.23f))
    using (var brush = new SolidBrush(Color.FromArgb(79, 91, 230)))
    {
        g.FillPath(brush, path);
    }

    // Viewfinder corners, snapped to whole pixels so they stay crisp at 16px.
    g.SmoothingMode = SmoothingMode.None;
    g.PixelOffsetMode = PixelOffsetMode.None;
    var stroke = Math.Max(2, (int)Math.Round(s * 0.075));
    var edge = (int)Math.Round(s * 0.23);
    var arm = Math.Max(stroke + 1, (int)Math.Round(s * 0.18));
    var far = s - edge;
    using (var white = new SolidBrush(Color.White))
    {
        g.FillRectangle(white, edge, edge, arm, stroke);
        g.FillRectangle(white, edge, edge, stroke, arm);
        g.FillRectangle(white, far - arm, edge, arm, stroke);
        g.FillRectangle(white, far - stroke, edge, stroke, arm);
        g.FillRectangle(white, edge, far - stroke, arm, stroke);
        g.FillRectangle(white, edge, far - arm, stroke, arm);
        g.FillRectangle(white, far - arm, far - stroke, arm, stroke);
        g.FillRectangle(white, far - stroke, far - arm, stroke, arm);
    }

    if (busy)
    {
        // "Uploading" badge with a transparent cut-out ring around it.
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var d = s * 0.46f;
        var gap = Math.Max(1f, s * 0.06f);
        var x = s - d;
        var y = s - d;
        g.CompositingMode = CompositingMode.SourceCopy;
        using (var clear = new SolidBrush(Color.Transparent))
        {
            g.FillEllipse(clear, x - gap, y - gap, d + gap * 2, d + gap * 2);
        }
        g.CompositingMode = CompositingMode.SourceOver;
        using (var badge = new SolidBrush(Color.White))
        {
            g.FillEllipse(badge, x, y, d, d);
        }
        if (s >= 32)
        {
            using var pen = new Pen(Color.FromArgb(79, 91, 230), Math.Max(1.5f, s * 0.05f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var cx = x + d / 2;
            g.DrawLine(pen, cx, y + d * 0.25f, cx, y + d * 0.75f);
            g.DrawLine(pen, cx - d * 0.2f, y + d * 0.45f, cx, y + d * 0.25f);
            g.DrawLine(pen, cx + d * 0.2f, y + d * 0.45f, cx, y + d * 0.25f);
        }
    }

    return bmp;
}

static GraphicsPath RoundedRect(RectangleF r, float radius)
{
    var d = radius * 2;
    var path = new GraphicsPath();
    path.AddArc(r.X, r.Y, d, d, 180, 90);
    path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    path.CloseFigure();
    return path;
}

// Writes a multi-size .ico. Sizes below 256 use classic 32-bit DIB entries (widest compatibility);
// the 256px entry is PNG-compressed, as Windows expects.
static void WriteIco(string path, int[] sizes, bool busy)
{
    var images = new List<byte[]>();
    foreach (var size in sizes)
    {
        using var bmp = Render(size, busy);
        images.Add(size >= 256 ? EncodePng(bmp) : EncodeDib(bmp));
    }

    using var file = File.Create(path);
    using var w = new BinaryWriter(file);
    w.Write((ushort)0); // reserved
    w.Write((ushort)1); // type: icon
    w.Write((ushort)sizes.Length);
    var offset = 6 + 16 * sizes.Length;
    for (var i = 0; i < sizes.Length; i++)
    {
        w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        w.Write((byte)0); // palette size
        w.Write((byte)0); // reserved
        w.Write((ushort)1); // planes
        w.Write((ushort)32); // bits per pixel
        w.Write(images[i].Length);
        w.Write(offset);
        offset += images[i].Length;
    }
    foreach (var image in images)
    {
        w.Write(image);
    }
}

static byte[] EncodePng(Bitmap bmp)
{
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

static byte[] EncodeDib(Bitmap bmp)
{
    int s = bmp.Width;
    var maskRow = (s + 31) / 32 * 4;
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write(40); // BITMAPINFOHEADER size
    w.Write(s);
    w.Write(s * 2); // XOR + AND mask height
    w.Write((ushort)1);
    w.Write((ushort)32);
    w.Write(0); // BI_RGB
    w.Write(s * s * 4 + maskRow * s);
    w.Write(0);
    w.Write(0);
    w.Write(0);
    w.Write(0);

    var data = bmp.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    try
    {
        var row = new byte[s * 4];
        var alpha = new bool[s, s];
        for (var y = s - 1; y >= 0; y--) // DIBs are stored bottom-up
        {
            System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
            w.Write(row);
            for (var x = 0; x < s; x++)
            {
                alpha[x, y] = row[x * 4 + 3] != 0;
            }
        }
        for (var y = s - 1; y >= 0; y--)
        {
            var mask = new byte[maskRow];
            for (var x = 0; x < s; x++)
            {
                if (!alpha[x, y])
                {
                    mask[x / 8] |= (byte)(0x80 >> (x % 8));
                }
            }
            w.Write(mask);
        }
    }
    finally
    {
        bmp.UnlockBits(data);
    }
    return ms.ToArray();
}