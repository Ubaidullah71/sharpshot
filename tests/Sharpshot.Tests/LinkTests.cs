using Sharpshot.Links;

namespace Sharpshot.Tests;

public class LinkTests
{
    [Theory]
    [InlineData(LinkStyle.Plain, 62)]
    [InlineData(LinkStyle.Braille, 255)]
    [InlineData(LinkStyle.Blocks, 8)]
    [InlineData(LinkStyle.Emoji, 80)]
    [InlineData(LinkStyle.Invisible, 4)]
    public void AlphabetsHaveTheExpectedDistinctSymbols(LinkStyle style, int count)
    {
        var info = LinkStyles.Get(style);
        Assert.Equal(count, info.Alphabet.Count);
        Assert.Equal(count, info.Alphabet.Distinct().Count());
    }

    [Fact]
    public void EveryStyleIsUnguessableAtItsMinimumAndDefaultLength()
    {
        foreach (var style in LinkStyles.All)
        {
            Assert.True(style.BitsFor(style.MinLength) >= LinkStyleInfo.MinimumBits, $"{style.DisplayName} minimum is too weak");
            Assert.True(style.BitsFor(style.DefaultLength) >= LinkStyleInfo.MinimumBits, $"{style.DisplayName} default is too weak");
        }
    }

    [Fact]
    public void NoStyleUsesCharactersThatBreakUrlsOrPaths()
    {
        foreach (var style in LinkStyles.All)
        {
            foreach (var symbol in style.Alphabet)
            {
                Assert.DoesNotContain(symbol, s => char.IsWhiteSpace(s) || s is '/' or '\\' or '?' or '#' or '%' or '<' or '>' or '"');
            }
        }
    }

    [Theory]
    [InlineData(LinkStyle.Plain)]
    [InlineData(LinkStyle.Braille)]
    [InlineData(LinkStyle.Emoji)]
    [InlineData(LinkStyle.Invisible)]
    public void GeneratedIdsUseOnlyTheAlphabetAndDoNotRepeat(LinkStyle style)
    {
        var info = LinkStyles.Get(style);
        var ids = new HashSet<string>();
        for (var i = 0; i < 5000; i++)
        {
            var id = KeyGenerator.NewId(info, info.DefaultLength);
            Assert.True(ids.Add(id), "Duplicate ID generated");
            Assert.Equal(info.DefaultLength, CountSymbols(id, info));
        }
    }

    [Fact]
    public void LengthIsClampedToTheSafeRange()
    {
        var braille = LinkStyles.Get(LinkStyle.Braille);
        Assert.Equal(braille.DefaultLength, braille.ClampLength(0));
        Assert.Equal(braille.MinLength, braille.ClampLength(1));
        Assert.Equal(LinkStyleInfo.MaxLength, braille.ClampLength(1000));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  ", "")]
    [InlineData("shots", "shots/")]
    [InlineData("/shots/", "shots/")]
    [InlineData("shots\\2026", "shots/2026/")]
    [InlineData("a//b", "a/b/")]
    public void PrefixesAreNormalized(string? input, string expected) =>
        Assert.Equal(expected, ObjectNaming.NormalizePrefix(input));

    [Theory]
    [InlineData("shots/", true)]
    [InlineData("my-shots_2026.v1/", true)]
    [InlineData("../etc", false)]
    [InlineData("has space", false)]
    [InlineData("⠓", false)]
    public void PrefixValidation(string prefix, bool valid) =>
        Assert.Equal(valid, ObjectNaming.IsValidPrefix(prefix));

    [Theory]
    [InlineData("cdn.example.com", "https://cdn.example.com")]
    [InlineData("https://cdn.example.com/", "https://cdn.example.com")]
    [InlineData(" https://example.com/files/ ", "https://example.com/files")]
    public void BaseUrlsAreNormalized(string input, string expected) =>
        Assert.Equal(expected, ObjectNaming.NormalizeBaseUrl(input));

    [Theory]
    [InlineData("https://cdn.example.com", true)]
    [InlineData("cdn.example.com", true)]
    [InlineData("ftp://cdn.example.com", false)]
    [InlineData("http://cdn.example.com", false)]
    [InlineData("http://localhost:8080", true)]
    [InlineData("https://cdn.example.com?x=1", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    public void BaseUrlValidation(string url, bool valid) =>
        Assert.Equal(valid, ObjectNaming.IsValidBaseUrl(url));

    [Fact]
    public void DisplayAndRequestUrlsDescribeTheSameObject()
    {
        var key = ObjectNaming.BuildKey("shots", "⠓⠕⠍⠑⠎");

        Assert.Equal("shots/⠓⠕⠍⠑⠎.png", key);
        Assert.Equal("https://cdn.example.com/shots/⠓⠕⠍⠑⠎.png", ObjectNaming.BuildDisplayUrl("cdn.example.com", key));
        Assert.Equal("https://cdn.example.com/shots/%E2%A0%93%E2%A0%95%E2%A0%8D%E2%A0%91%E2%A0%8E.png", ObjectNaming.BuildRequestUrl("cdn.example.com/", key));
        Assert.Equal(
            new Uri(ObjectNaming.BuildDisplayUrl("cdn.example.com", key)).AbsoluteUri,
            ObjectNaming.BuildRequestUrl("cdn.example.com", key));
    }

    private static int CountSymbols(string id, LinkStyleInfo style)
    {
        var count = 0;
        var index = 0;
        while (index < id.Length)
        {
            var symbol = style.Alphabet.FirstOrDefault(s => string.CompareOrdinal(id, index, s, 0, s.Length) == 0)
                ?? throw new Xunit.Sdk.XunitException($"Unexpected character at {index} in {style.DisplayName} ID");
            index += symbol.Length;
            count++;
        }

        return count;
    }
}