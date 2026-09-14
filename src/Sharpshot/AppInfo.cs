namespace Sharpshot;

internal static class AppInfo
{
    public const string Name = "Sharpshot";
    public const string RepositoryUrl = "https://github.com/Ubaidullah71/sharpshot";
    public const string SetupGuideUrl = RepositoryUrl + "#set-up-cloudflare-r2";

    public static string Version { get; } = typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public static string UserAgent => $"{Name}/{Version} (+{RepositoryUrl})";
}