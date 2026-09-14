using System.Globalization;

namespace Sharpshot.Links;

public enum LinkStyle
{
    Plain,
    Braille,
    Blocks,
    Emoji,
    Invisible,
}

internal sealed class LinkStyleInfo
{
    /// <summary>
    /// Links must never be guessable: every style needs at least this much randomness (about 1 in 140 trillion per
    /// guess). Each style's default length is its shortest allowed length.
    /// </summary>
    public const double MinimumBits = 47;

    public const int MaxLength = 64;

    public required LinkStyle Style { get; init; }

    public required string DisplayName { get; init; }

    public required string Description { get; init; }

    /// <summary>Each entry is one visible symbol (some emoji are two UTF-16 chars).</summary>
    public required IReadOnlyList<string> Alphabet { get; init; }

    public required int DefaultLength { get; init; }

    public bool Experimental { get; init; }

    public double BitsPerSymbol => Math.Log2(Alphabet.Count);

    public int MinLength => (int)Math.Ceiling(MinimumBits / BitsPerSymbol);

    public double BitsFor(int length) => length * BitsPerSymbol;

    public int ClampLength(int length) => length <= 0 ? DefaultLength : Math.Clamp(length, MinLength, MaxLength);

    public override string ToString() => DisplayName;
}

internal static class LinkStyles
{
    public static IReadOnlyList<LinkStyleInfo> All { get; } =
    [
        new()
        {
            Style = LinkStyle.Plain,
            DisplayName = "Plain",
            Description = "Letters and numbers; works everywhere",
            Alphabet = Symbols("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"),
            DefaultLength = 8,
        },
        new()
        {
            Style = LinkStyle.Braille,
            DisplayName = "Braille",
            Description = "Unreadable braille dots, like ⠓⠕⠍⠑⠎⠊",
            Alphabet = Range(0x2801, 0x28FF),
            DefaultLength = 6,
        },
        new()
        {
            Style = LinkStyle.Blocks,
            DisplayName = "Blocks",
            Description = "Looks censored, like ▓█░▌▀▐▒▄",
            Alphabet = Symbols("█▓▒░▀▄▌▐"),
            DefaultLength = 16,
        },
        new()
        {
            Style = LinkStyle.Emoji,
            DisplayName = "Emoji",
            Description = "A row of emoji faces",
            Alphabet = Range(0x1F600, 0x1F64F),
            DefaultLength = 8,
        },
        new()
        {
            Style = LinkStyle.Invisible,
            DisplayName = "Invisible",
            Description = "Can't be seen at all; some apps strip it and break the link",
            // Listed one by one: grapheme splitting would glue joiners onto their neighbours.
            Alphabet = ["\u200B", "\u200C", "\u200D", "\u2060"],
            DefaultLength = 24,
            Experimental = true,
        },
    ];

    public static LinkStyleInfo Get(LinkStyle style) =>
        All.FirstOrDefault(s => s.Style == style) ?? All[0];

    private static string[] Symbols(string text)
    {
        var symbols = new List<string>();
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            symbols.Add(elements.GetTextElement());
        }

        return [.. symbols];
    }

    private static string[] Range(int firstCodePoint, int lastCodePoint) =>
        Enumerable.Range(firstCodePoint, lastCodePoint - firstCodePoint + 1).Select(char.ConvertFromUtf32).ToArray();
}