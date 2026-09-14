using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Sharpshot.Storage;

/// <summary>AWS Signature Version 4, as used by R2's S3-compatible API. Verified against AWS's examples in SigV4Tests.</summary>
internal static class SigV4
{
    public const string Algorithm = "AWS4-HMAC-SHA256";

    public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public static string HashHex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static string AmzDate(DateTime utc) => utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    public static string Scope(DateTime utc, string region, string service) =>
        $"{utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}/{region}/{service}/aws4_request";

    /// <summary>S3-style URI encoding: every byte except RFC 3986 unreserved characters and '/' is percent-encoded.</summary>
    public static string EncodePath(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        var builder = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes)
        {
            if (b is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9')
                or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~' or (byte)'/')
            {
                builder.Append((char)b);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>URI-encodes a query-string name or value: like <see cref="EncodePath"/>, but '/' is encoded too.</summary>
    public static string EncodeQueryComponent(string value) => EncodePath(value).Replace("/", "%2F", StringComparison.Ordinal);

    /// <summary>Sorted, encoded query string, used both on the wire and in the canonical request.</summary>
    public static string CanonicalQuery(IEnumerable<KeyValuePair<string, string>> parameters) => string.Join('&', parameters
        .Select(p => (Name: EncodeQueryComponent(p.Key), Value: EncodeQueryComponent(p.Value)))
        .OrderBy(p => p.Name, StringComparer.Ordinal)
        .ThenBy(p => p.Value, StringComparer.Ordinal)
        .Select(p => $"{p.Name}={p.Value}"));

    public static string CanonicalRequest(
        string method,
        string canonicalUri,
        string canonicalQuery,
        IEnumerable<KeyValuePair<string, string>> headers,
        string payloadHash,
        out string signedHeaders)
    {
        var sorted = headers
            .Select(h => (Name: h.Key.Trim().ToLowerInvariant(), Value: CollapseWhitespace(h.Value)))
            .OrderBy(h => h.Name, StringComparer.Ordinal)
            .ToList();

        signedHeaders = string.Join(';', sorted.Select(h => h.Name));

        var builder = new StringBuilder();
        builder.Append(method).Append('\n');
        builder.Append(canonicalUri).Append('\n');
        builder.Append(canonicalQuery).Append('\n');
        foreach (var (name, value) in sorted)
        {
            builder.Append(name).Append(':').Append(value).Append('\n');
        }

        builder.Append('\n');
        builder.Append(signedHeaders).Append('\n');
        builder.Append(payloadHash);
        return builder.ToString();
    }

    public static string StringToSign(DateTime utc, string scope, string canonicalRequest) =>
        $"{Algorithm}\n{AmzDate(utc)}\n{scope}\n{HashHex(Encoding.UTF8.GetBytes(canonicalRequest))}";

    public static string Signature(string secretAccessKey, DateTime utc, string region, string service, string stringToSign)
    {
        var date = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), Encoding.UTF8.GetBytes(utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture)));
        var regionKey = HMACSHA256.HashData(date, Encoding.UTF8.GetBytes(region));
        var serviceKey = HMACSHA256.HashData(regionKey, Encoding.UTF8.GetBytes(service));
        var signingKey = HMACSHA256.HashData(serviceKey, "aws4_request"u8);
        return Convert.ToHexStringLower(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign)));
    }

    public static string AuthorizationHeader(
        string accessKeyId,
        string secretAccessKey,
        DateTime utc,
        string region,
        string service,
        string canonicalRequest,
        string signedHeaders)
    {
        var scope = Scope(utc, region, service);
        var signature = Signature(secretAccessKey, utc, region, service, StringToSign(utc, scope, canonicalRequest));
        return $"{Algorithm} Credential={accessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
    }

    private static string CollapseWhitespace(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.Contains("  ", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var builder = new StringBuilder(trimmed.Length);
        var previousWasSpace = false;
        foreach (var c in trimmed)
        {
            var isSpace = c == ' ';
            if (!(isSpace && previousWasSpace))
            {
                builder.Append(c);
            }

            previousWasSpace = isSpace;
        }

        return builder.ToString();
    }
}