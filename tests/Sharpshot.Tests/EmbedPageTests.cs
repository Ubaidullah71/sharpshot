using System.Globalization;
using System.Text;
using Sharpshot.Links;
using Sharpshot.Settings;

namespace Sharpshot.Tests;

public class EmbedPageTests
{
    private static readonly EmbedImage Image = new(
        "https://cdn.example.com/%E2%A0%93%E2%A0%95",
        "https://cdn.example.com/%E2%A0%93%E2%A0%95.png",
        1920,
        1080,
        245_000,
        new DateTimeOffset(2026, 9, 14, 16, 58, 0, TimeSpan.Zero));

    [Fact]
    public void PageHasTheTagsDiscordUsesForALargeColouredEmbed()
    {
        var settings = new EmbedSettings { Enabled = true, Color = "#ed4245", SiteName = "example.com", Title = "Look at this", Description = "Just a test" };

        var html = Build(settings);

        Assert.Contains("<meta name=\"theme-color\" content=\"#ED4245\">", html);
        Assert.Contains("<meta name=\"twitter:card\" content=\"summary_large_image\">", html);
        Assert.Contains($"<meta property=\"og:image\" content=\"{Image.ImageUrl}\">", html);
        Assert.Contains("<meta property=\"og:image:width\" content=\"1920\">", html);
        Assert.Contains("<meta property=\"og:image:height\" content=\"1080\">", html);
        Assert.Contains($"<meta property=\"og:url\" content=\"{Image.PageUrl}\">", html);
        Assert.Contains("<meta property=\"og:site_name\" content=\"example.com\">", html);
        Assert.Contains("<meta property=\"og:title\" content=\"Look at this\">", html);
        Assert.Contains("<meta property=\"og:description\" content=\"Just a test\">", html);
        Assert.Contains("<meta name=\"robots\" content=\"noindex, nofollow\">", html);
        Assert.Contains($"<img src=\"{Image.ImageUrl}\"", html);
    }

    [Fact]
    public void EmptyTitleBecomesInvisibleSoDiscordStillBuildsACard()
    {
        var html = Build(new EmbedSettings { Enabled = true, SiteName = "", Title = "", Description = "" });

        Assert.Contains("<meta property=\"og:title\" content=\"\u200B\">", html);
        Assert.DoesNotContain("og:site_name", html);
        Assert.DoesNotContain("og:description", html);
        Assert.DoesNotContain("<title>", html);
        Assert.Contains("og:image", html);
    }

    [Fact]
    public void UserTextIsEscaped()
    {
        var html = Build(new EmbedSettings { Title = "<script>alert(1)</script> & \"quotes\"" });

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; &amp; &quot;quotes&quot;", html);
    }

    [Fact]
    public void PlaceholdersAreFilledIn()
    {
        var culture = CultureInfo.GetCultureInfo("en-GB");
        var local = Image.TakenAt.ToLocalTime();

        var text = EmbedPage.Expand("{date} at {time} · {size} · {dimensions} · {width}x{height}", 1920, 1080, 245_000, Image.TakenAt, culture);

        Assert.Equal($"{local.ToString("d MMM yyyy", culture)} at {local.ToString("t", culture)} · 245 KB · 1920 × 1080 · 1920x1080", text);
    }

    [Fact]
    public void InvalidColourFallsBackToTheDefault()
    {
        var html = Build(new EmbedSettings { Color = "not a colour" });

        Assert.Contains($"content=\"{EmbedSettings.DefaultColor}\"", html);
    }

    [Theory]
    [InlineData("#5b6cff", true, "#5B6CFF")]
    [InlineData("5B6CFF", true, "#5B6CFF")]
    [InlineData(" #abcdef ", true, "#ABCDEF")]
    [InlineData("#abc", false, "#abc")]
    [InlineData("blue", false, "blue")]
    public void ColoursAreValidatedAndNormalised(string input, bool valid, string normalized)
    {
        Assert.Equal(valid, EmbedPage.IsValidColor(EmbedPage.NormalizeColor(input)));
        Assert.Equal(normalized, EmbedPage.NormalizeColor(input));
    }

    [Fact]
    public void EmbedNamesPointTheLinkAtThePage()
    {
        var settings = SettingsTests.ValidSettings();
        settings.Links.Style = LinkStyle.Braille;
        settings.Embed.Enabled = true;
        settings.R2.PathPrefix = "shots/";

        var names = ObjectNaming.NewNames(settings);

        Assert.NotNull(names.PageKey);
        Assert.Equal(names.PageKey + ".png", names.ImageKey);
        Assert.StartsWith("shots/", names.PageKey);
        Assert.Equal("https://cdn.example.com/" + names.PageKey, names.Link);

        settings.Embed.Enabled = false;
        var plain = ObjectNaming.NewNames(settings);
        Assert.Null(plain.PageKey);
        Assert.EndsWith(".png", plain.Link);
    }

    private static string Build(EmbedSettings settings) => Encoding.UTF8.GetString(EmbedPage.Build(settings, Image, CultureInfo.InvariantCulture));
}