using System.Diagnostics;

namespace Sharpshot;

internal static class Shell
{
    /// <summary>Refuses anything that isn't an http(s) link (a file, a program, another URI scheme).</summary>
    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            Log.Warn("Didn't open a link that isn't a web address");
            return;
        }

        Start(uri.AbsoluteUri); // percent-encodes Unicode file names for the browser
    }

    public static void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            Log.Warn("Didn't open a folder that doesn't exist");
            return;
        }

        Start(Path.GetFullPath(path));
    }

    private static void Start(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Couldn't open a link or folder: {ex.Message}");
        }
    }
}