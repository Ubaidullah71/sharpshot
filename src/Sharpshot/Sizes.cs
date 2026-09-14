namespace Sharpshot;

internal static class Sizes
{
    /// <summary>"245 KB", "2.4 MB", "1.24 GB". Decimal units, like Cloudflare's storage figures.</summary>
    public static string Format(long bytes) => bytes switch
    {
        < 1000 => $"{bytes} B",
        < 1000 * 1000 => $"{bytes / 1000.0:0} KB",
        < 1000L * 1000 * 1000 => $"{bytes / (1000.0 * 1000):0.#} MB",
        _ => $"{bytes / (1000.0 * 1000 * 1000):0.##} GB",
    };
}