using System.Drawing.Imaging;
using System.Net;
using Sharpshot.Imaging;
using Sharpshot.Links;
using Sharpshot.Settings;

namespace Sharpshot.Storage;

internal sealed record ConnectionTestResult(bool Success, string Message);

/// <summary>
/// Round-trips a tiny PNG through the public domain to catch wrong keys, wrong domains and Cloudflare settings
/// that would stop Discord from showing previews.
/// </summary>
internal static class ConnectionTester
{
    /// <summary>Test files are deleted afterwards; the Uploads page hides any that are left behind.</summary>
    internal const string TestFilePrefix = "sharpshot-test-";

    public static async Task<ConnectionTestResult> RunAsync(AppSettings settings, HttpClient http, CancellationToken cancellationToken)
    {
        var client = new R2Client(R2Config.From(settings.R2), http);
        var style = LinkStyles.Get(settings.Links.Style);
        var key = ObjectNaming.BuildKey(settings.R2.PathPrefix, TestFilePrefix + KeyGenerator.NewId(style, style.DefaultLength));
        var image = CreateTestImage();

        try
        {
            await client.PutNewObjectAsync(key, image, "image/png", cancellationToken).ConfigureAwait(false);
        }
        catch (StorageException ex)
        {
            return new(false, $"Upload failed. {ex.Message}");
        }

        string? pageKey = null;
        try
        {
            var imageResult = await CheckPublicUrlAsync(settings, key, image, http, cancellationToken).ConfigureAwait(false);
            if (!imageResult.Success || !settings.Embed.Enabled)
            {
                return imageResult;
            }

            // Embeds: the page must also be served, as HTML, from the same domain.
            pageKey = ObjectNaming.PageKeyFor(key);
            var page = EmbedPage.Build(settings.Embed, new EmbedImage(
                ObjectNaming.BuildRequestUrl(settings.R2.PublicUrl, pageKey),
                ObjectNaming.BuildRequestUrl(settings.R2.PublicUrl, key),
                32, 32, image.Length, DateTimeOffset.Now));
            try
            {
                await client.PutNewObjectAsync(pageKey, page, EmbedPage.ContentType, cancellationToken).ConfigureAwait(false);
            }
            catch (StorageException ex)
            {
                return new(false, $"The image works, but uploading the embed page failed. {ex.Message}");
            }

            var pageResult = await CheckPublicUrlAsync(settings, pageKey, page, http, cancellationToken, "text/html").ConfigureAwait(false);
            return pageResult.Success
                ? new(true, $"Everything works, including embeds: uploaded to \"{settings.R2.Bucket}\" and downloaded unchanged from {new Uri(ObjectNaming.NormalizeBaseUrl(settings.R2.PublicUrl)).Host}.")
                : pageResult;
        }
        finally
        {
            foreach (var cleanup in new[] { key, pageKey })
            {
                if (cleanup is null)
                {
                    continue;
                }

                try
                {
                    await client.DeleteObjectAsync(cleanup, CancellationToken.None).ConfigureAwait(false);
                }
                catch (StorageException ex)
                {
                    Log.Warn($"Couldn't delete connection test file: {ex.Message}");
                }
            }
        }
    }

    private static async Task<ConnectionTestResult> CheckPublicUrlAsync(
        AppSettings settings, string key, byte[] expected, HttpClient http, CancellationToken cancellationToken, string expectedType = "image/png")
    {
        var what = expectedType == "image/png" ? "image" : "embed page";
        var url = ObjectNaming.BuildRequestUrl(settings.R2.PublicUrl, key);
        var host = new Uri(url).Host;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ObjectNaming.ToExactUri(url));
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new(false,
                    $"Uploading works, but {host} answered 404 Not Found. Connect this domain to the \"{settings.R2.Bucket}\" bucket " +
                    "(R2 > your bucket > Settings > Custom Domains) and check the Public URL.");
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable)
            {
                return new(false,
                    $"Uploading works, but {host} blocked the download (HTTP {(int)response.StatusCode}). " +
                    "Turn off Bot Fight Mode, Hotlink Protection or WAF rules for this domain, otherwise Discord can't show previews.");
            }

            if ((int)response.StatusCode is >= 300 and < 400)
            {
                return new(false,
                    $"Uploading works, but {host} redirects to another address (HTTP {(int)response.StatusCode}). " +
                    "Use the custom domain connected to your bucket as the Public URL, and check for Cloudflare redirect rules.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new(false, $"Uploading works, but {host} answered HTTP {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            if (!body.AsSpan().SequenceEqual(expected))
            {
                return new(false,
                    $"Uploading works, but {host} returned a different file. Something (like Polish or an image resizing rule) " +
                    "is changing images. Turn it off for this domain to keep full quality.");
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(contentType, expectedType, StringComparison.OrdinalIgnoreCase))
            {
                return new(false, $"Uploading works, but {host} served the {what} as \"{contentType ?? "unknown"}\" instead of {expectedType}, so previews may not show.");
            }

            return new(true, $"Everything works: uploaded to \"{settings.R2.Bucket}\" and downloaded unchanged from {host}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new(false, $"Uploading works, but {host} couldn't be reached: {ex.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, $"Uploading works, but {host} took too long to respond.");
        }
    }

    internal static byte[] CreateTestImage()
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppRgb);
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                bitmap.SetPixel(x, y, Color.FromArgb(0x3B + x * 3, 0x82 - y, 0xF6 - x));
            }
        }

        return PngEncoder.Encode(bitmap);
    }
}