namespace Sharpshot.UI;

internal static class AppIcons
{
    public const string Idle = "Sharpshot.ico";
    public const string Busy = "Sharpshot.Busy.ico";

    /// <summary>Picks the frame closest to <paramref name="size"/>.</summary>
    public static Icon Load(string resource, Size size)
    {
        using var stream = typeof(AppIcons).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded resource '{resource}' is missing.");
        return new Icon(stream, size);
    }

}

/// <summary>Reloaded only when the size changes, e.g. on a monitor with different scaling.</summary>
internal sealed class ScaledAppIcon : IDisposable
{
    private Icon? _icon;
    private int _size;

    /// <returns>Null if the icon can't be loaded; it's only decorative.</returns>
    public Icon? Get(int size)
    {
        if (_icon is not null && _size == size)
        {
            return _icon;
        }

        try
        {
            var icon = AppIcons.Load(AppIcons.Idle, new Size(size, size));
            _icon?.Dispose();
            (_icon, _size) = (icon, size);
        }
        catch (InvalidOperationException)
        {
            // Keep whatever we had.
        }

        return _icon;
    }

    public void Dispose() => _icon?.Dispose();
}