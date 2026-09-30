using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace LeafCalendar.App.Interop;

/// <summary>Opening placement for secondary windows (Settings, onboarding), following Layers' settings window.</summary>
internal static class WindowPlacement
{
    /// <summary>
    /// Moves the window onto the monitor under the cursor first, so Windows rescales it for that monitor, then sizes
    /// its client area to <paramref name="width"/> × <paramref name="height"/> DIPs from that monitor's DPI (sizing
    /// first would get scaled twice), capped to the work area, and centers it there. Returns the monitor's scale.
    /// </summary>
    public static double CenterOnCursorMonitor(AppWindow window, double width, double height)
    {
        PInvoke.GetCursorPos(out var cursor);
        var work = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest).WorkArea;
        window.Move(Centered(work, window.Size));

        var scale  = PInvoke.GetDpiForWindow(new HWND(Win32Interop.GetWindowFromWindowId(window.Id))) / 96.0;
        var frameW = window.Size.Width - window.ClientSize.Width;
        var frameH = window.Size.Height - window.ClientSize.Height;
        var pixelW = Math.Min((int)Math.Ceiling(width * scale) + frameW, work.Width);
        var pixelH = Math.Min((int)Math.Ceiling(height * scale) + frameH, work.Height);
        window.Resize(new SizeInt32(pixelW, pixelH));
        window.Move(Centered(work, window.Size));
        return scale;
    }

    // Centered on the work area, with the title bar never above its top
    static PointInt32 Centered(RectInt32 work, SizeInt32 size) =>
        new(work.X + (work.Width - size.Width) / 2, Math.Max(work.Y, work.Y + (work.Height - size.Height) / 2));
}
