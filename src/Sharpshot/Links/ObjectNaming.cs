using Sharpshot.Storage;

namespace Sharpshot.Links;

/// <param name="PageKey">Only set when sharing as an embed.</param>
internal sealed record ShotNames(string ImageKey, string? PageKey, string Link);

internal static class ObjectNaming
{
    public const string Extension = ".png";

    /// <summary>"shots", "/shots/", "shots\\2026" -> "shots/", "shots/2026/". Empty stays empty.</summary>
    public static string NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return "";
        }

        var segments = prefix.Trim().Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length == 0 ? "" : string.Join('/', segments) + "/";
    }

    public static bool IsValidPrefix(string? prefix)
    {
        var normalized = NormalizePrefix(prefix);
        return normalized.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/')
            && !normalized.Split('/').Any(segment => segment is "." or "..");
    }

    /// <summary>"cdn.example.com/" -> "https://cdn.example.com". Returns "" for blank input.</summary>
    public static string NormalizeBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "";
        }

        var trimmed = url.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "https://" + trimmed;
        }

        return trimmed.TrimEnd('/');
    }

    /// <summary>An https address with a real host name (plain http is only allowed for local testing).</summary>
    public static bool IsValidBaseUrl(string? url)
    {
        var normalized = NormalizeBaseUrl(url);
        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && (uri.Host.Contains('.') || uri.IsLoopback);
    }

    public static string BuildKey(string? prefix, string id) => NormalizePrefix(prefix) + id + Extension;

    /// <summary>The embed page for an image lives next to it, without the extension: "shots/⠓⠕⠍" for "shots/⠓⠕⠍.png".</summary>
    public static string BuildPageKey(string? prefix, string id) => NormalizePrefix(prefix) + id;

    /// <summary>"shots/⠓⠕⠍.png" -> "shots/⠓⠕⠍".</summary>
    public static string PageKeyFor(string imageKey) =>
        imageKey.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? imageKey[..^Extension.Length] : imageKey;

    public static ShotNames NewNames(Settings.AppSettings settings)
    {
        var style = LinkStyles.Get(settings.Links.Style);
        var id = KeyGenerator.NewId(style, settings.Links.EffectiveLength);
        var imageKey = BuildKey(settings.R2.PathPrefix, id);
        var pageKey = settings.Embed.Enabled ? BuildPageKey(settings.R2.PathPrefix, id) : null;
        return new ShotNames(imageKey, pageKey, BuildDisplayUrl(settings.R2.PublicUrl, pageKey ?? imageKey));
    }

    /// <summary>The link people see and paste: Unicode characters are left as-is.</summary>
    public static string BuildDisplayUrl(string publicBaseUrl, string key) => NormalizeBaseUrl(publicBaseUrl) + "/" + key;

    /// <summary>The same link, percent-encoded, for making HTTP requests or handing to a browser.</summary>
    public static string BuildRequestUrl(string publicBaseUrl, string key) =>
        NormalizeBaseUrl(publicBaseUrl) + "/" + SigV4.EncodePath(key);

    /// <summary>A Uri that HttpClient sends exactly as written (no re-escaping of the path).</summary>
    public static Uri ToExactUri(string encodedUrl) =>
        new(encodedUrl, new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
}