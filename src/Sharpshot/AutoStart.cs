using Microsoft.Win32;

namespace Sharpshot;

/// <summary>"Start with Windows" via the current user's Run key (no admin rights needed).</summary>
internal static class AutoStart
{
    public const string Argument = "--autostart";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(AppInfo.Name) is string;
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } exe)
        {
            key.SetValue(AppInfo.Name, $"\"{exe}\" {Argument}");
        }
        else
        {
            key.DeleteValue(AppInfo.Name, throwOnMissingValue: false);
        }
    }

    /// <summary>Keeps the registered path correct if the app has been moved or updated.</summary>
    public static void RefreshPath()
    {
        try
        {
            if (IsEnabled())
            {
                Set(true);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warn($"Couldn't update the startup entry: {ex.Message}");
        }
    }
}