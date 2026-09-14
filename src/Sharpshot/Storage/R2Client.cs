using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using Sharpshot.Settings;

namespace Sharpshot.Storage;

internal interface IObjectStore
{
    /// <summary>Never overwrites a different existing object. Throws <see cref="StorageException"/>.</summary>
    Task PutNewObjectAsync(string key, byte[] data, string contentType, CancellationToken cancellationToken);
}

internal sealed record StoredObject(string Key, long Size, DateTimeOffset LastModified);

internal sealed record R2Config(string Endpoint, string AccessKeyId, string SecretAccessKey, string Bucket)
{
    public static R2Config From(R2Settings settings) => new(
        settings.EffectiveEndpoint,
        settings.AccessKeyId.Trim(),
        settings.SecretAccessKey.Trim(),
        settings.Bucket.Trim());

    /// <summary>Never includes the secret, so the config is safe to print or log.</summary>
    public override string ToString() => $"R2Config {{ Endpoint = {Endpoint}, Bucket = {Bucket} }}";
}

internal sealed class R2Client(R2Config config, HttpClient http, TimeProvider? timeProvider = null) : IObjectStore
{
    public const string Region = "auto";
    public const string Service = "s3";

    /// <summary>One day, so deleted screenshots drop out of caches reasonably soon.</summary>
    public const string CacheControl = "public, max-age=86400";

    /// <summary>A bucket of millions of files lists in far fewer pages than this; more means the listing is looping.</summary>
    private const int MaxListingPages = 10_000;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private TimeSpan _clockOffset;

    public R2Config Config => config;

    public async Task PutNewObjectAsync(string key, byte[] data, string contentType, CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync(HttpMethod.Put, key, data, contentType, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageException ex) when (ex.Kind == StorageErrorKind.AlreadyExists)
        {
            // A random name clash is practically impossible; more likely an earlier attempt succeeded but its response was lost.
            if (await HasSameContentAsync(key, data, cancellationToken).ConfigureAwait(false))
            {
                Log.Info("Object already uploaded by an earlier attempt; treating as success");
                return;
            }

            throw;
        }
    }

    /// <summary>Deleting a key that doesn't exist also succeeds.</summary>
    public async Task DeleteObjectAsync(string key, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Delete, key, body: null, contentType: null, cancellationToken).ConfigureAwait(false);

    /// <summary>The object's media type (like "text/html"), or null if it doesn't exist or has none.</summary>
    public async Task<string?> GetContentTypeAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await SendAsync(HttpMethod.Head, key, body: null, contentType: null, cancellationToken, query: null,
                (response, _) => Task.FromResult(response.Content.Headers.ContentType?.MediaType)).ConfigureAwait(false);
        }
        catch (StorageException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
    }

    /// <summary>Follows pagination (1,000 keys per request).</summary>
    public async Task<IReadOnlyList<StoredObject>> ListObjectsAsync(string prefix, CancellationToken cancellationToken)
    {
        var objects = new List<StoredObject>();
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        string? continuation = null;
        do
        {
            if (seenTokens.Count >= MaxListingPages || (continuation is not null && !seenTokens.Add(continuation)))
            {
                throw new StorageException(StorageErrorKind.Unknown, "Cloudflare R2 kept repeating the same page while listing files.");
            }

            var query = new List<KeyValuePair<string, string>> { new("list-type", "2"), new("max-keys", "1000") };
            if (prefix.Length > 0)
            {
                query.Add(new("prefix", prefix));
            }

            if (continuation is not null)
            {
                query.Add(new("continuation-token", continuation));
            }

            var xml = await SendAsync(HttpMethod.Get, key: "", body: null, contentType: null, cancellationToken, query,
                (response, token) => response.Content.ReadAsStringAsync(token)).ConfigureAwait(false);
            (continuation, var page) = ParseListing(xml);
            objects.AddRange(page);
        }
        while (continuation is not null);

        return objects;
    }

    internal static (string? Continuation, List<StoredObject> Objects) ParseListing(string xml)
    {
        XElement? root;
        try
        {
            root = ParseXml(xml).Root;
        }
        catch (XmlException ex)
        {
            throw new StorageException(StorageErrorKind.Unknown, "Cloudflare R2 sent a file listing that couldn't be read.", inner: ex);
        }

        if (root is null)
        {
            throw new StorageException(StorageErrorKind.Unknown, "Cloudflare R2 sent an empty listing.");
        }

        string? Child(XElement element, string name) => element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

        var objects = root.Elements()
            .Where(e => e.Name.LocalName == "Contents")
            .Select(e => new StoredObject(
                Child(e, "Key") ?? "",
                long.TryParse(Child(e, "Size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : 0,
                DateTimeOffset.TryParse(Child(e, "LastModified"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var modified) ? modified : DateTimeOffset.MinValue))
            .Where(o => o.Key.Length > 0)
            .ToList();

        var truncated = string.Equals(Child(root, "IsTruncated"), "true", StringComparison.OrdinalIgnoreCase);
        var next = Child(root, "NextContinuationToken");
        return (truncated && !string.IsNullOrEmpty(next) ? next : null, objects);
    }

    /// <summary>Parses an XML response with DTDs refused, so a hostile response can't expand entities.</summary>
    internal static XDocument ParseXml(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        return XDocument.Load(reader);
    }

    /// <summary>
    /// Only a definite answer counts: if the check itself fails (say, a timeout), the error is thrown so the upload
    /// is retried rather than needlessly renamed.
    /// </summary>
    private async Task<bool> HasSameContentAsync(string key, byte[] data, CancellationToken cancellationToken)
    {
        string? etag;
        try
        {
            etag = await SendAsync(HttpMethod.Head, key, body: null, contentType: null, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageException ex) when (ex.StatusCode == 404)
        {
            return false;
        }

#pragma warning disable CA5351 // MD5 isn't used for security: R2's ETag for a single-part upload is the MD5 of the content.
        var md5 = Convert.ToHexStringLower(MD5.HashData(data));
#pragma warning restore CA5351
        return string.Equals(etag?.Trim('"'), md5, StringComparison.OrdinalIgnoreCase);
    }

    /// <returns>The response ETag, if any.</returns>
    private Task<string?> SendAsync(HttpMethod method, string key, byte[]? body, string? contentType, CancellationToken cancellationToken) =>
        SendAsync(method, key, body, contentType, cancellationToken, query: null, (response, _) => Task.FromResult(response.Headers.ETag?.Tag));

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string key,
        byte[]? body,
        string? contentType,
        CancellationToken cancellationToken,
        IReadOnlyList<KeyValuePair<string, string>>? query,
        Func<HttpResponseMessage, CancellationToken, Task<T>> onSuccess)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = BuildRequest(method, key, body, contentType, query);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeoutFor(body?.Length ?? 0));

            int status;
            string? text;
            DateTimeOffset? serverTime;
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return await onSuccess(response, timeout.Token).ConfigureAwait(false);
                }

                status = (int)response.StatusCode;
                serverTime = response.Headers.Date;
                text = method == HttpMethod.Head ? null : await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw new StorageException(StorageErrorKind.Transient, $"Couldn't reach Cloudflare R2: {ex.Message}", inner: ex);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new StorageException(StorageErrorKind.Transient, "Cloudflare R2 took too long to respond.", inner: ex);
            }

            var error = StorageException.FromResponse(status, text, config.Bucket);

            // A PC clock that's more than 15 minutes off makes every signature invalid. Correct once using the server's clock.
            if (attempt == 0 && error.Code == "RequestTimeTooSkewed" && serverTime is { } now)
            {
                _clockOffset = now - _time.GetUtcNow();
                Log.Warn($"System clock is off by {_clockOffset}; compensating.");
                continue;
            }

            throw error;
        }
    }

    /// <summary>Generous enough for a big multi-monitor PNG on a slow (~250 kbit/s) uplink.</summary>
    internal static TimeSpan TimeoutFor(long bytes) =>
        TimeSpan.FromSeconds(Math.Min(30 + bytes / 32_000, 30 * 60));

    internal HttpRequestMessage BuildRequest(
        HttpMethod method, string key, byte[]? body, string? contentType, IReadOnlyList<KeyValuePair<string, string>>? query = null)
    {
        var now = (_time.GetUtcNow() + _clockOffset).UtcDateTime;
        var endpoint = new Uri(config.Endpoint);
        // An empty key addresses the bucket itself (for listing).
        var canonicalUri = "/" + SigV4.EncodePath(config.Bucket) + (key.Length > 0 ? "/" + SigV4.EncodePath(key) : "");
        var canonicalQuery = query is null ? "" : SigV4.CanonicalQuery(query);
        var uri = Links.ObjectNaming.ToExactUri(endpoint.GetLeftPart(UriPartial.Authority) + canonicalUri + (canonicalQuery.Length > 0 ? "?" + canonicalQuery : ""));
        var host = endpoint.IsDefaultPort ? endpoint.IdnHost : $"{endpoint.IdnHost}:{endpoint.Port}";
        var payloadHash = body is null ? SigV4.EmptyPayloadHash : SigV4.HashHex(body);
        var amzDate = SigV4.AmzDate(now);

        var request = new HttpRequestMessage(method, uri);
        var signed = new List<KeyValuePair<string, string>>
        {
            new("host", host),
            new("x-amz-content-sha256", payloadHash),
            new("x-amz-date", amzDate),
        };
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);

        if (method == HttpMethod.Put)
        {
            // Never overwrite: if the name is taken, R2 answers 412 Precondition Failed.
            request.Headers.TryAddWithoutValidation("If-None-Match", "*");
            signed.Add(new("if-none-match", "*"));
            request.Headers.TryAddWithoutValidation("Cache-Control", CacheControl);
        }

        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            if (contentType is not null)
            {
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType); // may include "; charset=utf-8"
            }
        }

        var canonicalRequest = SigV4.CanonicalRequest(method.Method, canonicalUri, canonicalQuery, signed, payloadHash, out var signedHeaders);
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            SigV4.AuthorizationHeader(config.AccessKeyId, config.SecretAccessKey, now, Region, Service, canonicalRequest, signedHeaders));
        return request;
    }
}