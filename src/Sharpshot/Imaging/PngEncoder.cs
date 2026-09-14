using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Sharpshot.Imaging;

/// <summary>
/// Smaller files than GDI+'s encoder: images with up to 256 colours become palette PNGs, the rest get per-row
/// adaptive filtering. Alpha is ignored, since screen captures are always opaque.
/// </summary>
internal static class PngEncoder
{
    private const int MaxPaletteColors = 256;

    internal static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] Encode(Bitmap bitmap) => Encode(bitmap, new Rectangle(Point.Empty, bitmap.Size));

    public static byte[] Encode(Bitmap bitmap, Rectangle region)
    {
        region.Intersect(new Rectangle(Point.Empty, bitmap.Size));
        if (region.Width <= 0 || region.Height <= 0)
        {
            throw new ArgumentException("The region to encode is empty.", nameof(region));
        }

        var data = bitmap.LockBits(region, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            var source = new PixelSource(data.Scan0, data.Stride, region.Width, region.Height);
            using var output = new MemoryStream();
            output.Write(Signature);

            var palette = TryBuildPalette(source);
            WriteHeader(output, source.Width, source.Height, colorType: palette is null ? (byte)2 : (byte)3);

            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                if (palette is null)
                {
                    WriteRgbRows(zlib, source);
                }
                else
                {
                    WritePaletteChunk(output, palette.Colors);
                    WriteIndexedRows(zlib, source, palette);
                }
            }

            WriteChunk(output, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
            WriteChunk(output, "IEND"u8, []);
            return output.ToArray();
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private readonly record struct PixelSource(IntPtr Scan0, int Stride, int Width, int Height)
    {
        /// <summary>Pixels are BGRX.</summary>
        public void ReadRow(int y, byte[] row) => Marshal.Copy(IntPtr.Add(Scan0, y * Stride), row, 0, Width * 4);
    }

    private sealed class Palette
    {
        public List<int> Colors { get; } = new(MaxPaletteColors);

        public Dictionary<int, byte> Index { get; } = new(MaxPaletteColors + 1);
    }

    private static Palette? TryBuildPalette(PixelSource source)
    {
        var palette = new Palette();
        var row = new byte[source.Width * 4];
        var lastColor = -1;
        for (var y = 0; y < source.Height; y++)
        {
            source.ReadRow(y, row);
            for (var x = 0; x < row.Length; x += 4)
            {
                var color = (row[x + 2] << 16) | (row[x + 1] << 8) | row[x];
                if (color == lastColor || palette.Index.ContainsKey(color))
                {
                    lastColor = color;
                    continue;
                }

                if (palette.Colors.Count == MaxPaletteColors)
                {
                    return null;
                }

                palette.Index[color] = (byte)palette.Colors.Count;
                palette.Colors.Add(color);
                lastColor = color;
            }
        }

        return palette;
    }

    private static void WriteIndexedRows(Stream zlib, PixelSource source, Palette palette)
    {
        var row = new byte[source.Width * 4];
        var line = new byte[source.Width + 1]; // line[0] = filter type 0 (None), recommended for palette images
        for (var y = 0; y < source.Height; y++)
        {
            source.ReadRow(y, row);
            for (int x = 0, i = 0; x < source.Width; x++, i += 4)
            {
                line[x + 1] = palette.Index[(row[i + 2] << 16) | (row[i + 1] << 8) | row[i]];
            }

            zlib.Write(line);
        }
    }

    private static void WriteRgbRows(Stream zlib, PixelSource source)
    {
        const int bpp = 3;
        var width = source.Width;
        var length = width * bpp;
        var bgrx = new byte[width * 4];
        var current = new byte[length];
        var previous = new byte[length]; // the row above the first row counts as all zeros
        var candidates = new byte[5][];
        for (var f = 0; f < candidates.Length; f++)
        {
            candidates[f] = new byte[length + 1];
            candidates[f][0] = (byte)f;
        }

        for (var y = 0; y < source.Height; y++)
        {
            source.ReadRow(y, bgrx);
            for (int x = 0, s = 0, d = 0; x < width; x++, s += 4, d += 3)
            {
                current[d] = bgrx[s + 2];
                current[d + 1] = bgrx[s + 1];
                current[d + 2] = bgrx[s];
            }

            var best = 0;
            var bestCost = long.MaxValue;
            for (var f = 0; f < candidates.Length; f++)
            {
                var cost = ApplyFilter(f, current, previous, candidates[f].AsSpan(1), bpp);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = f;
                }
            }

            zlib.Write(candidates[best]);
            (previous, current) = (current, previous);
        }
    }

    /// <summary>Returns the "minimum sum of absolute differences" cost of the filtered row.</summary>
    private static long ApplyFilter(int filter, byte[] cur, byte[] prev, Span<byte> output, int bpp)
    {
        long cost = 0;
        for (var i = 0; i < cur.Length; i++)
        {
            var a = i >= bpp ? cur[i - bpp] : 0;
            var b = prev[i];
            var c = i >= bpp ? prev[i - bpp] : 0;
            var predictor = filter switch
            {
                0 => 0,
                1 => a,
                2 => b,
                3 => (a + b) >> 1,
                _ => Paeth(a, b, c),
            };

            var value = (byte)(cur[i] - predictor);
            output[i] = value;
            cost += value < 128 ? value : 256 - value;
        }

        return cost;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteHeader(Stream output, int width, int height, byte colorType)
    {
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)height);
        header[8] = 8;          // bit depth
        header[9] = colorType;  // 2 = RGB, 3 = palette
        header[10] = 0;         // deflate
        header[11] = 0;         // adaptive filtering
        header[12] = 0;         // no interlace
        WriteChunk(output, "IHDR"u8, header);
    }

    private static void WritePaletteChunk(Stream output, List<int> colors)
    {
        var plte = new byte[colors.Count * 3];
        for (var i = 0; i < colors.Count; i++)
        {
            plte[i * 3] = (byte)(colors[i] >> 16);
            plte[i * 3 + 1] = (byte)(colors[i] >> 8);
            plte[i * 3 + 2] = (byte)colors[i];
        }

        WriteChunk(output, "PLTE"u8, plte);
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)data.Length);
        output.Write(buffer);
        output.Write(type);
        output.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, Crc32.Compute(type, data));
        output.Write(buffer);
    }
}

internal static class Crc32
{
    private static readonly uint[] Table = CreateTable();

    public static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var crc = Update(0xFFFFFFFFu, first);
        crc = Update(crc, second);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}