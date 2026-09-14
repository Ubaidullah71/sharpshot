using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using Sharpshot.Links;
using Sharpshot.Settings;
using Sharpshot.Storage;

namespace Sharpshot.Uploads;

internal sealed record StoredShot(
    string ImageKey,
    string? PageKey,
    long SizeBytes,
    DateTimeOffset UploadedAt,
    int Width,
    int Height,
    string? LocalCopyPath);

internal sealed record BucketContents(IReadOnlyList<StoredShot> Shots, long TotalBytes);

/// <param name="Error">Why deleting stopped early, or null if everything was deleted.</param>
internal sealed record DeleteResult(IReadOnlyList<StoredShot> Deleted, string? Error);

/// <summary>
/// Only the R2 side is ever changed, never local copies. Listing costs one request per 1,000 files; deleting is free.
/// </summary>
internal sealed class UploadLibrary(UploadHistory history, string thumbnailDirectory, HttpClient http)
{
    /// <summary>R2's free storage allowance.</summary>
    public const long FreeTierBytes = 10L * 1000 * 1000 * 1000;

    /// <summary>Embed pages are a couple of KB; anything bigger with the same name isn't one of ours.</summary>
    private const long MaxEmbedPageBytes = 64_000;

    /// <summary>Limits for images downloaded for thumbnails; real screenshots, even of many 4K monitors, fit easily.</summary>
    private const int MaxImageBytes = 64_000_000;
    private const int MaxImageSide = 32_000;
    private const long MaxImagePixels = 100_000_000;

    private readonly Lock _clientGate = new();
    private R2Client? _client;

    public async Task<BucketContents> LoadAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var prefix = ObjectNaming.NormalizePrefix(settings.R2.PathPrefix);
        var objects = await Client(settings).ListObjectsAsync(prefix, cancellationToken).ConfigureAwait(false);

        IReadOnlyDictionary<string, HistoryEntry> known;
        try
        {
            known = history.ReadAll();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // History only adds dimensions and local paths; carry on without it.
            Log.Error("Couldn't read upload history", ex);
            known = new Dictionary<string, HistoryEntry>();
        }

        return Build(objects, prefix, known);
    }

    internal static BucketContents Build(IReadOnlyList<StoredObject> objects, string prefix, IReadOnlyDictionary<string, HistoryEntry> history)
    {
        var byKey = new Dictionary<string, StoredObject>(StringComparer.Ordinal);
        foreach (var item in objects)
        {
            byKey[item.Key] = item;
        }

        var shots = new List<StoredShot>();
        foreach (var item in objects)
        {
            if (!item.Key.EndsWith(ObjectNaming.Extension, StringComparison.OrdinalIgnoreCase)
                || item.Key.AsSpan(Math.Min(prefix.Length, item.Key.Length)).StartsWith(ConnectionTester.TestFilePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var page = byKey.TryGetValue(ObjectNaming.PageKeyFor(item.Key), out var candidate) && candidate.Size <= MaxEmbedPageBytes ? candidate : null;
            history.TryGetValue(item.Key, out var entry);
            shots.Add(new StoredShot(
                item.Key,
                page?.Key,
                item.Size + (page?.Size ?? 0),
                entry?.CreatedAt ?? item.LastModified,
                entry?.Width ?? 0,
                entry?.Height ?? 0,
                entry?.LocalCopyPath));
        }

        shots.Sort((a, b) => b.UploadedAt.CompareTo(a.UploadedAt));
        return new BucketContents(shots, objects.Sum(o => o.Size));
    }

    /// <summary>Also deletes embed pages. Stops at the first failure.</summary>
    public async Task<DeleteResult> DeleteAsync(
        AppSettings settings, IReadOnlyList<StoredShot> shots, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var client = Client(settings);
        var deleted = new List<StoredShot>();
        string? error = null;
        try
        {
            foreach (var shot in shots)
            {
                progress?.Report(deleted.Count);
                // The page is matched by name only, so make sure it really is an embed page before deleting it.
                var deletePage = shot.PageKey is not null
                    && await client.GetContentTypeAsync(shot.PageKey, cancellationToken).ConfigureAwait(false) is { } type
                    && type.Equals("text/html", StringComparison.OrdinalIgnoreCase);

                // Page first: if the image delete then fails, the shot is still listed and can be deleted again.
                if (deletePage)
                {
                    await client.DeleteObjectAsync(shot.PageKey!, cancellationToken).ConfigureAwait(false);
                }

                await client.DeleteObjectAsync(shot.ImageKey, cancellationToken).ConfigureAwait(false);

                deleted.Add(shot);
                TryDelete(ThumbnailPath(shot.ImageKey));
            }
        }
        catch (StorageException ex)
        {
            error = ex.Message;
        }
        finally
        {
            // Remove deleted items from history even if a later delete failed.
            try
            {
                history.Remove(deleted.Select(s => s.ImageKey).ToHashSet(StringComparer.Ordinal));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("Couldn't update upload history after deleting", ex);
            }

            Log.Info($"Deleted {deleted.Count} of {shots.Count} screenshots from R2");
        }

        return new DeleteResult(deleted, error);
    }

    /// <summary>
    /// From the local copy if there is one, else the thumbnail cache, else downloaded once through the public URL
    /// and cached. Returns null if the image can't be read.
    /// </summary>
    public async Task<Bitmap?> LoadThumbnailAsync(AppSettings settings, StoredShot shot, Size size, CancellationToken cancellationToken)
    {
        try
        {
            if (shot.LocalCopyPath is { } local && File.Exists(local))
            {
                await using var file = File.OpenRead(local);
                return IsSafeToDecode(file) ? CreateThumbnail(file, size) : null;
            }

            var cached = ThumbnailPath(shot.ImageKey);
            if (File.Exists(cached))
            {
                try
                {
                    await using var file = File.OpenRead(cached);
                    return CreateThumbnail(file, size); // a small JPEG this app wrote
                }
                catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
                {
                    TryDelete(cached); // damaged; download it again
                }
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var url = ObjectNaming.BuildRequestUrl(settings.R2.PublicUrl, shot.ImageKey);
            using var request = new HttpRequestMessage(HttpMethod.Get, ObjectNaming.ToExactUri(url));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxImageBytes)
            {
                return null;
            }

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var image = await ReadAtMostAsync(body, MaxImageBytes, timeout.Token).ConfigureAwait(false);
            if (image is null || !IsSafeToDecode(image))
            {
                Log.Warn("Skipped a thumbnail: the file isn't a PNG that's safe to read");
                return null;
            }

            var thumbnail = CreateThumbnail(image, size);
            try
            {
                Directory.CreateDirectory(thumbnailDirectory);
                thumbnail.Save(cached + ".tmp", ImageFormat.Jpeg);
                File.Move(cached + ".tmp", cached, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
            {
                Log.Warn($"Couldn't cache a thumbnail: {ex.Message}"); // still worth showing
            }

            return thumbnail;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or ArgumentException
            or OutOfMemoryException or System.Runtime.InteropServices.ExternalException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // GDI+ reports unreadable images as ArgumentException/OutOfMemoryException.
            Log.Warn($"Couldn't load a thumbnail: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Only PNGs of a sensible size reach GDI+: the bucket may hold files Sharpshot didn't upload, and GDI+ would
    /// otherwise parse any format it knows, or allocate gigabytes for a tiny file claiming enormous dimensions.
    /// Leaves the stream where it started.
    /// </summary>
    internal static bool IsSafeToDecode(Stream stream)
    {
        Span<byte> header = stackalloc byte[24];
        var start = stream.Position;
        var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        stream.Position = start;

        // The 8-byte PNG signature, then the IHDR chunk: 4-byte length, "IHDR", big-endian width and height.
        if (read < header.Length || !header[..8].SequenceEqual(Imaging.PngEncoder.Signature) || !header[12..16].SequenceEqual("IHDR"u8))
        {
            return false;
        }

        long width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[16..]);
        long height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[20..]);
        return width is > 0 and <= MaxImageSide && height is > 0 and <= MaxImageSide && width * height <= MaxImagePixels;
    }

    /// <returns>The whole stream in memory, or null if it's longer than <paramref name="limit"/> bytes.</returns>
    private static async Task<MemoryStream?> ReadAtMostAsync(Stream source, int limit, CancellationToken cancellationToken)
    {
        var result = new MemoryStream();
        var buffer = new byte[81_920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (result.Length + read > limit)
            {
                await result.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            result.Write(buffer, 0, read);
        }

        result.Position = 0;
        return result;
    }

    /// <summary>Crops to fill <paramref name="size"/>.</summary>
    internal static Bitmap CreateThumbnail(Stream stream, Size size)
    {
        using var source = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
        var thumbnail = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(thumbnail);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;

        var scale = Math.Max((float)size.Width / source.Width, (float)size.Height / source.Height);
        var cropWidth = size.Width / scale;
        var cropHeight = size.Height / scale;
        var crop = new RectangleF((source.Width - cropWidth) / 2f, (source.Height - cropHeight) / 2f, cropWidth, cropHeight);
        g.DrawImage(source, new RectangleF(0, 0, size.Width, size.Height), crop, GraphicsUnit.Pixel);
        return thumbnail;
    }

    private string ThumbnailPath(string key) =>
        Path.Combine(thumbnailDirectory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32] + ".jpg");

    /// <summary>Reuses the client while the settings stay the same, so a corrected clock offset is remembered.</summary>
    private R2Client Client(AppSettings settings)
    {
        var config = R2Config.From(settings.R2);
        lock (_clientGate)
        {
            if (_client is null || _client.Config != config)
            {
                _client = new R2Client(config, http);
            }

            return _client;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Couldn't delete cached thumbnail {path}: {ex.Message}");
        }
    }
}