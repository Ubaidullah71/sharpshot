using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Sharpshot.Native;
using Sharpshot.Settings;

namespace Sharpshot.Capture;

internal static class ScreenCapture
{
    /// <summary>Bounds are physical pixels, as the process is per-monitor DPI aware. The caller owns the result.</summary>
    public static Bitmap CaptureArea(Rectangle bounds)
    {
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
        try
        {
            // BitBlt directly: Graphics.CopyFromScreen rejects the SRCCOPY | CAPTUREBLT combination,
            // and without CAPTUREBLT translucent (layered) windows can be missing from the capture.
            using var graphics = Graphics.FromImage(bitmap);
            var screen = NativeMethods.GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero)
            {
                throw new Win32Exception("Couldn't access the screen.");
            }

            try
            {
                var target = graphics.GetHdc();
                try
                {
                    if (!NativeMethods.BitBlt(target, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y,
                            NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "Couldn't copy the screen.");
                    }
                }
                finally
                {
                    graphics.ReleaseHdc(target);
                }
            }
            finally
            {
                _ = NativeMethods.ReleaseDC(IntPtr.Zero, screen);
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    public static Rectangle VirtualScreen => SystemInformation.VirtualScreen;

    public static Rectangle FullscreenBounds(FullscreenTarget target) => target switch
    {
        FullscreenTarget.AllMonitors => SystemInformation.VirtualScreen,
        FullscreenTarget.PrimaryMonitor => (Screen.PrimaryScreen ?? Screen.FromPoint(Cursor.Position)).Bounds,
        _ => Screen.FromPoint(Cursor.Position).Bounds,
    };
}