using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Sharpshot.UI;

internal sealed class Palette
{
    public required bool IsDark { get; init; }

    public required Color Window { get; init; }

    public required Color Card { get; init; }

    public required Color CardBorder { get; init; }

    public required Color Divider { get; init; }

    public required Color Control { get; init; }

    public required Color ControlHover { get; init; }

    public required Color ControlPressed { get; init; }

    public required Color ControlFocused { get; init; }

    public required Color ControlBorder { get; init; }

    public required Color ControlBottom { get; init; }

    public required Color Text { get; init; }

    public required Color SecondaryText { get; init; }

    public required Color DisabledText { get; init; }

    public required Color Accent { get; init; }

    public required Color AccentHover { get; init; }

    public required Color AccentPressed { get; init; }

    public required Color OnAccent { get; init; }

    public required Color LinkText { get; init; }

    public required Color NavHover { get; init; }

    public required Color NavSelected { get; init; }

    public required Color Success { get; init; }

    public required Color Error { get; init; }

    /// <summary>Destructive buttons (and the close button's hover), with white text.</summary>
    public required Color Danger { get; init; }

    public required Color DangerHover { get; init; }

    public required Color DangerPressed { get; init; }

    public required Color Menu { get; init; }

    public required Color MenuHover { get; init; }

    public required Color MenuBorder { get; init; }
}

internal static class Theme
{
    /// <summary>Brand accent, used where the theme doesn't matter (e.g. the capture overlay).</summary>
    public static readonly Color Accent = Color.FromArgb(0x5B, 0x6C, 0xFF);

    public static readonly Palette Dark = new()
    {
        IsDark = true,
        Window = Color.FromArgb(32, 32, 32),
        Card = Color.FromArgb(43, 43, 43),
        CardBorder = Color.FromArgb(24, 24, 24),
        Divider = Color.FromArgb(32, 32, 32),
        Control = Color.FromArgb(55, 55, 55),
        ControlHover = Color.FromArgb(62, 62, 62),
        ControlPressed = Color.FromArgb(48, 48, 48),
        ControlFocused = Color.FromArgb(30, 30, 30),
        ControlBorder = Color.FromArgb(66, 66, 66),
        ControlBottom = Color.FromArgb(135, 135, 135),
        Text = Color.FromArgb(255, 255, 255),
        SecondaryText = Color.FromArgb(197, 197, 197),
        DisabledText = Color.FromArgb(120, 120, 120),
        Accent = Color.FromArgb(0x5B, 0x6C, 0xFF),
        AccentHover = Color.FromArgb(0x70, 0x7F, 0xFF),
        AccentPressed = Color.FromArgb(0x4C, 0x5C, 0xE0),
        OnAccent = Color.White,
        LinkText = Color.FromArgb(0x9D, 0xAB, 0xFF),
        NavHover = Color.FromArgb(40, 40, 40),
        NavSelected = Color.FromArgb(45, 45, 45),
        Success = Color.FromArgb(108, 203, 95),
        Error = Color.FromArgb(255, 153, 164),
        Danger = Color.FromArgb(196, 43, 28),
        DangerHover = Color.FromArgb(214, 56, 40),
        DangerPressed = Color.FromArgb(155, 31, 20),
        Menu = Color.FromArgb(44, 44, 44),
        MenuHover = Color.FromArgb(61, 61, 61),
        MenuBorder = Color.FromArgb(70, 70, 70),
    };

    public static readonly Palette Light = new()
    {
        IsDark = false,
        Window = Color.FromArgb(243, 243, 243),
        Card = Color.FromArgb(251, 251, 251),
        CardBorder = Color.FromArgb(229, 229, 229),
        Divider = Color.FromArgb(234, 234, 234),
        Control = Color.FromArgb(255, 255, 255),
        ControlHover = Color.FromArgb(249, 249, 249),
        ControlPressed = Color.FromArgb(243, 243, 243),
        ControlFocused = Color.FromArgb(255, 255, 255),
        ControlBorder = Color.FromArgb(222, 222, 222),
        ControlBottom = Color.FromArgb(135, 135, 135),
        Text = Color.FromArgb(27, 27, 27),
        SecondaryText = Color.FromArgb(96, 96, 96),
        DisabledText = Color.FromArgb(160, 160, 160),
        Accent = Color.FromArgb(0x4B, 0x5A, 0xE6),
        AccentHover = Color.FromArgb(0x5E, 0x6C, 0xF0),
        AccentPressed = Color.FromArgb(0x41, 0x4E, 0xCC),
        OnAccent = Color.White,
        LinkText = Color.FromArgb(0x37, 0x46, 0xD2),
        NavHover = Color.FromArgb(234, 234, 234),
        NavSelected = Color.FromArgb(234, 234, 234),
        Success = Color.FromArgb(15, 123, 15),
        Error = Color.FromArgb(196, 43, 28),
        Danger = Color.FromArgb(196, 43, 28),
        DangerHover = Color.FromArgb(214, 56, 40),
        DangerPressed = Color.FromArgb(155, 31, 20),
        Menu = Color.FromArgb(249, 249, 249),
        MenuHover = Color.FromArgb(234, 234, 234),
        MenuBorder = Color.FromArgb(222, 222, 222),
    };

    private static Palette? _current;

    /// <summary>
    /// Follows the Windows app theme, but only changes on <see cref="Refresh"/> (when a window opens), so an open
    /// window never mixes light and dark colours.
    /// </summary>
    public static Palette Current => _current ??= Detect();

    /// <summary>Call before opening a window, not while one is open.</summary>
    public static void Refresh() => _current = Detect();

    private static Palette Detect() => Application.IsDarkModeEnabled ? Dark : Light;

    public static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (d <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRounded(Graphics g, Color color, RectangleF bounds, float radius)
    {
        using var path = RoundedRectangle(bounds, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    public static void DrawRounded(Graphics g, Color color, RectangleF bounds, float radius, float width = 1f)
    {
        using var path = RoundedRectangle(bounds, radius);
        using var pen = new Pen(color, width);
        g.DrawPath(pen, path);
    }

    public static Color Blend(Color from, Color to, float amount) => Color.FromArgb(
        (int)(from.R + (to.R - from.R) * amount),
        (int)(from.G + (to.G - from.G) * amount),
        (int)(from.B + (to.B - from.B) * amount));
}

internal static class UiFonts
{
    private static readonly HashSet<string> Installed = LoadInstalled();

    public static string Text { get; } = Pick("Segoe UI Variable Text", "Segoe UI");

    public static string TextSemibold { get; } = Pick("Segoe UI Variable Text Semibold", "Segoe UI Semibold");

    public static string Display { get; } = Pick("Segoe UI Variable Display Semib", "Segoe UI Semibold");

    public static string Icons { get; } = Pick("Segoe Fluent Icons", "Segoe MDL2 Assets");

    private static string Pick(params string[] names) => names.FirstOrDefault(Installed.Contains) ?? "Segoe UI";

    private static HashSet<string> LoadInstalled()
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>Icon glyphs shared by Segoe Fluent Icons (Windows 11) and Segoe MDL2 Assets (Windows 10).</summary>
internal static class Glyphs
{
    public const string Cloud = "\uE753";
    public const string Link = "\uE71B";
    public const string Camera = "\uE722";
    public const string Settings = "\uE713";
    public const string ChevronDown = "\uE70D";
    public const string Add = "\uE710";
    public const string Remove = "\uE738";
    public const string Refresh = "\uE72C";
    public const string Folder = "\uE8B7";
    public const string Completed = "\uE930";
    public const string ErrorBadge = "\uEA39";
    public const string Sync = "\uE895";
    public const string Keyboard = "\uE765";
    public const string FullScreen = "\uE740";
    public const string Monitor = "\uE7F4";
    public const string Copy = "\uE8C8";
    public const string FontSize = "\uE8E9";
    public const string Power = "\uE7E8";
    public const string Ringer = "\uEA8F";
    public const string OpenInNew = "\uE8A7";
    public const string Globe = "\uE774";
    public const string FolderOpen = "\uE838";
    public const string Picture = "\uE8B9";
    public const string Document = "\uE8A5";
    public const string ChevronRight = "\uE76C";
    public const string ChromeMinimize = "\uE921";
    public const string CheckMark = "\uE73E";
    public const string Library = "\uE8F1";
    public const string ChromeClose = "\uE8BB";
}