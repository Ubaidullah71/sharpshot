using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Sharpshot.Settings;

namespace Sharpshot.Links;

/// <summary>URLs must already be percent-encoded.</summary>
internal sealed record EmbedImage(string PageUrl, string ImageUrl, int Width, int Height, long SizeBytes, DateTimeOffset TakenAt);

/// <summary>
/// Discord reads the Open Graph tags: <c>theme-color</c> colours the card's bar, <c>twitter:card=summary_large_image</c>
/// shows the image large, and og:site_name / og:title / og:description provide the text.
/// </summary>
internal static partial class EmbedPage
{
    public const string ContentType = "text/html; charset=utf-8";

    internal const string InvisibleTitle = "\u200B";

    public static bool IsValidColor(string? color) => color is not null && ColorPattern().IsMatch(color.Trim());

    /// <summary>"5b6cff" or "#5b6cff" -> "#5B6CFF". Returns the input unchanged if it isn't a colour.</summary>
    public static string NormalizeColor(string? color)
    {
        var trimmed = (color ?? "").Trim();
        if (!trimmed.StartsWith('#'))
        {
            trimmed = "#" + trimmed;
        }

        return IsValidColor(trimmed) ? trimmed.ToUpperInvariant() : color ?? "";
    }

    /// <summary>Replaces {date}, {time}, {size}, {dimensions}, {width} and {height}.</summary>
    public static string Expand(string? template, int width, int height, long sizeBytes, DateTimeOffset takenAt, CultureInfo? culture = null)
    {
        if (string.IsNullOrEmpty(template))
        {
            return "";
        }

        culture ??= CultureInfo.CurrentCulture;
        var local = takenAt.ToLocalTime();
        return template
            .Replace("{date}", local.ToString("d MMM yyyy", culture), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", local.ToString("t", culture), StringComparison.OrdinalIgnoreCase)
            .Replace("{size}", Sizes.Format(sizeBytes), StringComparison.OrdinalIgnoreCase)
            .Replace("{dimensions}", $"{width} × {height}", StringComparison.OrdinalIgnoreCase)
            .Replace("{width}", width.ToString(culture), StringComparison.OrdinalIgnoreCase)
            .Replace("{height}", height.ToString(culture), StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    public static byte[] Build(EmbedSettings settings, EmbedImage image, CultureInfo? culture = null)
    {
        string Text(string template) => Expand(template, image.Width, image.Height, image.SizeBytes, image.TakenAt, culture);

        var color = IsValidColor(settings.Color) ? NormalizeColor(settings.Color) : EmbedSettings.DefaultColor;
        var siteName = Text(settings.SiteName);
        var title = Text(settings.Title);
        var description = Text(settings.Description);

        var html = new StringBuilder(1600);
        html.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n");
        html.Append("<meta charset=\"utf-8\">\n");
        html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        html.Append("<meta name=\"robots\" content=\"noindex, nofollow\">\n");
        if (title.Length > 0)
        {
            html.Append("<title>").Append(Encode(title)).Append("</title>\n");
        }

        Meta(html, "name", "theme-color", color);
        Meta(html, "property", "og:type", "website");
        Meta(html, "property", "og:url", image.PageUrl);
        Meta(html, "property", "og:site_name", siteName);
        // Discord only builds a card (colour bar, text) when there's a title; without one it shows a bare image.
        // An invisible title keeps the card without showing any title text.
        Meta(html, "property", "og:title", title.Length > 0 ? title : InvisibleTitle);
        Meta(html, "property", "og:description", description);
        Meta(html, "property", "og:image", image.ImageUrl);
        Meta(html, "property", "og:image:type", "image/png");
        Meta(html, "property", "og:image:width", image.Width.ToString(CultureInfo.InvariantCulture));
        Meta(html, "property", "og:image:height", image.Height.ToString(CultureInfo.InvariantCulture));
        Meta(html, "name", "twitter:card", "summary_large_image");
        Meta(html, "name", "twitter:image", image.ImageUrl);

        html.Append("<style>\n");
        html.Append("html,body{margin:0;min-height:100%;background:#0f1012;color:#c9cbd0;font:14px/1.5 system-ui,-apple-system,\"Segoe UI\",sans-serif}\n");
        html.Append("body{display:flex;flex-direction:column;align-items:center;justify-content:center;gap:14px;padding:24px;box-sizing:border-box;min-height:100vh}\n");
        html.Append("img{display:block;max-width:100%;max-height:calc(100vh - 110px);width:auto;height:auto;border-radius:8px;box-shadow:0 12px 40px rgba(0,0,0,.55)}\n");
        html.Append(".meta{display:flex;gap:10px;align-items:center;flex-wrap:wrap;justify-content:center}\n");
        html.Append(".dot{width:10px;height:10px;border-radius:50%;background:").Append(color).Append("}\n");
        html.Append("a{color:#c9cbd0;text-decoration:none;border-bottom:1px solid #3a3c42}a:hover{color:#fff}\n");
        html.Append("</style>\n</head>\n<body>\n");

        var alt = title.Length > 0 ? title : "Screenshot";
        html.Append("<a href=\"").Append(Encode(image.ImageUrl)).Append("\" style=\"border:0\"><img src=\"").Append(Encode(image.ImageUrl))
            .Append("\" width=\"").Append(image.Width.ToString(CultureInfo.InvariantCulture))
            .Append("\" height=\"").Append(image.Height.ToString(CultureInfo.InvariantCulture))
            .Append("\" alt=\"").Append(Encode(alt)).Append("\"></a>\n");

        html.Append("<div class=\"meta\"><span class=\"dot\"></span>");
        if (title.Length > 0)
        {
            html.Append("<span>").Append(Encode(title)).Append("</span><span>·</span>");
        }

        html.Append("<span>").Append(image.Width.ToString(CultureInfo.InvariantCulture)).Append(" × ")
            .Append(image.Height.ToString(CultureInfo.InvariantCulture)).Append("</span><span>·</span>");
        html.Append("<a href=\"").Append(Encode(image.ImageUrl)).Append("\">Open original</a></div>\n");
        html.Append("</body>\n</html>\n");
        return Encoding.UTF8.GetBytes(html.ToString());
    }

    private static void Meta(StringBuilder html, string attribute, string name, string content)
    {
        if (content.Length == 0)
        {
            return;
        }

        html.Append("<meta ").Append(attribute).Append("=\"").Append(name).Append("\" content=\"").Append(Encode(content)).Append("\">\n");
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();
}