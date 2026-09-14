using System.Runtime.InteropServices;

namespace Sharpshot.Native;

internal static partial class NativeMethods
{
    /// <summary>Windows 11 rounds popup windows and draws their border; Windows 10 can't.</summary>
    public static readonly bool SystemRoundsCorners = Environment.OSVersion.Version.Build >= 22000;

    // Window messages
    public const int WM_SETREDRAW = 0x000B;
    public const int WM_PAINT = 0x000F;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int WM_NCCALCSIZE = 0x0083;
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_HOTKEY = 0x0312;
    public const int WM_DPICHANGED = 0x02E0;
    public const int MA_NOACTIVATE = 3;
    public const int HTTRANSPARENT = -1;
    public const int HTCLIENT = 1;
    public const int HTCAPTION = 2;

    // Window styles
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int CS_DROPSHADOW = 0x00020000;

    // SetWindowPos and RedrawWindow
    public static readonly IntPtr HWND_MESSAGE = new(-3);
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint RDW_INVALIDATE = 0x0001;
    public const uint RDW_ERASE = 0x0004;
    public const uint RDW_ALLCHILDREN = 0x0080;
    public const uint RDW_UPDATENOW = 0x0100;

    // DWM
    public const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWCP_DONOTROUND = 1;
    public const int DWMWCP_ROUND = 2;

    // Keyboard and focus
    public const uint MOD_NOREPEAT = 0x4000;
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;
    public const int ASFW_ANY = -1;

    // GDI
    public const int SRCCOPY = 0x00CC0020;

    /// <summary>Includes layered (translucent) windows in the copy.</summary>
    public const int CAPTUREBLT = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NCCALCSIZE_PARAMS
    {
        public RECT Rect0;
        public RECT Rect1;
        public RECT Rect2;
        public IntPtr WindowPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    public static partial IntPtr SendMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RedrawWindow(IntPtr hWnd, IntPtr updateRect, IntPtr updateRegion, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(int processId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll")]
    public static partial short GetKeyState(int virtualKey);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetDC(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height, IntPtr hdcSource, int xSource, int ySource, int rop);

    [LibraryImport("user32.dll")]
    private static partial int GetUpdateRgn(IntPtr hWnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr handle);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [LibraryImport("user32.dll")]
    public static partial IntPtr MonitorFromPoint(POINT point, uint flags);

    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    public static bool IsKeyDown(int virtualKey) => (GetKeyState(virtualKey) & 0x8000) != 0;

    /// <summary>COLORREF is 0x00BBGGRR.</summary>
    public static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    public static void SetDwmInt(IntPtr hwnd, int attribute, int value) =>
        _ = DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));

    /// <summary>Display scale (1.0 = 100%) of the monitor containing <paramref name="point"/>.</summary>
    public static float ScaleAt(Point point)
    {
        const uint MONITOR_DEFAULTTONEAREST = 2;
        const int MDT_EFFECTIVE_DPI = 0;
        var monitor = MonitorFromPoint(new POINT { X = point.X, Y = point.Y }, MONITOR_DEFAULTTONEAREST);
        return GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96f : 1f;
    }

    /// <summary>The rectangles making up a window's pending repaint region (call before painting). Null if unavailable.</summary>
    public static Rectangle[]? GetUpdateRectangles(IntPtr hwnd)
    {
        const int NULLREGION = 1;
        var hrgn = CreateRectRgn(0, 0, 0, 0);
        if (hrgn == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (GetUpdateRgn(hwnd, hrgn, false) <= NULLREGION)
            {
                return null;
            }

            using var region = Region.FromHrgn(hrgn);
            using var identity = new System.Drawing.Drawing2D.Matrix();
            return region.GetRegionScans(identity).Select(Rectangle.Round).ToArray();
        }
        finally
        {
            DeleteObject(hrgn);
        }
    }
}