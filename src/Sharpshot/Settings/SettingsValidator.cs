using System.Text.RegularExpressions;
using Sharpshot.Links;

namespace Sharpshot.Settings;

/// <param name="Field">The <see cref="R2Settings"/> property the problem is about.</param>
/// <param name="Message">A full sentence.</param>
/// <param name="ShortMessage">A few words, shown next to the field.</param>
internal sealed record SettingsProblem(string Field, string Message, string ShortMessage);

internal static partial class SettingsValidator
{
    public static bool IsR2Ready(AppSettings settings) => FindR2Problems(settings.R2).Count == 0;

    public static IReadOnlyList<SettingsProblem> FindR2Problems(R2Settings r2)
    {
        var problems = new List<SettingsProblem>();

        if (string.IsNullOrWhiteSpace(r2.Endpoint))
        {
            if (!AccountIdPattern().IsMatch(r2.AccountId.Trim()))
            {
                problems.Add(new(nameof(R2Settings.AccountId),
                    "Account ID should be the 32-character ID shown in your Cloudflare dashboard.",
                    string.IsNullOrWhiteSpace(r2.AccountId) ? "Required" : "Should be 32 characters"));
            }
        }
        else if (!Uri.TryCreate(r2.Endpoint.Trim(), UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https")
        {
            problems.Add(new(nameof(R2Settings.Endpoint), "Custom endpoint must be a full https:// address, or left empty.", "Must start with https://"));
        }

        if (string.IsNullOrWhiteSpace(r2.AccessKeyId))
        {
            problems.Add(new(nameof(R2Settings.AccessKeyId), "Access Key ID is required.", "Required"));
        }

        if (string.IsNullOrWhiteSpace(r2.SecretAccessKey))
        {
            problems.Add(new(nameof(R2Settings.SecretAccessKey), "Secret Access Key is required.", "Required"));
        }

        if (!BucketPattern().IsMatch(r2.Bucket.Trim()))
        {
            problems.Add(new(nameof(R2Settings.Bucket), "Bucket name should be 3 to 63 lowercase letters, numbers or hyphens.",
                string.IsNullOrWhiteSpace(r2.Bucket) ? "Required" : "Lowercase, numbers, hyphens"));
        }

        if (!ObjectNaming.IsValidBaseUrl(r2.PublicUrl))
        {
            var plainHttp = r2.PublicUrl.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase);
            problems.Add(new(nameof(R2Settings.PublicUrl),
                plainHttp ? "Public URL must use https://, so links can't be tampered with on the way." : "Public URL should be your bucket's custom domain, like https://cdn.example.com.",
                string.IsNullOrWhiteSpace(r2.PublicUrl) ? "Required" : plainHttp ? "Must start with https://" : "Use your domain, like https://cdn.example.com"));
        }

        if (!ObjectNaming.IsValidPrefix(r2.PathPrefix))
        {
            problems.Add(new(nameof(R2Settings.PathPrefix), "Path prefix can only use letters, numbers, '-', '_', '.' and '/'.", "Letters, numbers, - _ . /"));
        }

        return problems;
    }

    public static IReadOnlyList<string> Validate(AppSettings settings)
    {
        var problems = FindR2Problems(settings.R2).Select(p => p.Message).ToList();
        var capture = settings.Capture;

        if (!capture.RegionHotkey.IsEmpty && capture.RegionHotkey == capture.FullscreenHotkey)
        {
            problems.Add("The region and full screen hotkeys can't be the same.");
        }

        if (capture.SaveLocalCopy)
        {
            if (string.IsNullOrWhiteSpace(capture.LocalFolder) || !Path.IsPathFullyQualified(capture.LocalFolder.Trim()))
            {
                problems.Add("Choose a folder for local copies (a full path like C:\\Users\\you\\Pictures\\Sharpshot).");
            }
        }

        if (settings.Embed.Enabled && !EmbedPage.IsValidColor(settings.Embed.Color))
        {
            problems.Add("The embed colour should look like #5B6CFF.");
        }

        return problems;
    }

    [GeneratedRegex("^[0-9a-fA-F]{32}$")]
    private static partial Regex AccountIdPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$")]
    private static partial Regex BucketPattern();
}