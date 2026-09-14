namespace Sharpshot;

/// <summary>Never throws. Never log secrets.</summary>
internal static class Log
{
    private const long MaxBytes = 1_000_000;
    private static readonly Lock Gate = new();
    private static string? _path;

    public static void Initialize(string path) => _path = path;

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    private static void Write(string level, string message)
    {
        var path = _path;
        if (path is null)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var file = new FileInfo(path);
                if (file.Exists && file.Length > MaxBytes)
                {
                    File.Move(path, path + ".old", overwrite: true);
                }

                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}