using System.Security.Cryptography;
using System.Text;

namespace Sharpshot.Links;

internal static class KeyGenerator
{
    /// <summary>Cryptographically random and unbiased.</summary>
    public static string NewId(LinkStyleInfo style, int length)
    {
        length = style.ClampLength(length);
        var builder = new StringBuilder(length * 2);
        for (var i = 0; i < length; i++)
        {
            builder.Append(style.Alphabet[RandomNumberGenerator.GetInt32(style.Alphabet.Count)]);
        }

        return builder.ToString();
    }
}