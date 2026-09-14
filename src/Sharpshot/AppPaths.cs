namespace Sharpshot;

internal sealed class AppPaths(string configDirectory, string dataDirectory)
{
    public static AppPaths Default { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Name),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name));

    public static string DefaultScreenshotFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), AppInfo.Name);

    /// <summary>Roaming folder: settings.json.</summary>
    public string ConfigDirectory { get; } = configDirectory;

    /// <summary>Local folder: upload queue, history and log.</summary>
    public string DataDirectory { get; } = dataDirectory;

    public string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");

    public string QueueDirectory => Path.Combine(DataDirectory, "queue");

    public string HistoryFile => Path.Combine(DataDirectory, "history.jsonl");

    public string LogFile => Path.Combine(DataDirectory, "sharpshot.log");

    public string ThumbnailDirectory => Path.Combine(DataDirectory, "thumbnails");
}