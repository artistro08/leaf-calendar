using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace LeafCalendar.App.Interop;

/// <summary>Opening placement for Leaf's windows (secondary ones follow Layers' settings window), and their remembered sizes.</summary>
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

        var scale = PInvoke.GetDpiForWindow(new HWND(Win32Interop.GetWindowFromWindowId(window.Id))) / 96.0;
        var frameW = window.Size.Width - window.ClientSize.Width;
        var frameH = window.Size.Height - window.ClientSize.Height;
        var pixelW = Math.Min((int)Math.Ceiling(width * scale) + frameW, work.Width);
        var pixelH = Math.Min((int)Math.Ceiling(height * scale) + frameH, work.Height);
        window.Resize(new SizeInt32(pixelW, pixelH));
        window.Move(Centered(work, window.Size));
        return scale;
    }

    /// <summary>
    /// Opens a window at a remembered (or default) size: moved onto the monitor under the cursor first, so Windows
    /// rescales it for that monitor, then sized to <paramref name="size"/> (whole-window DIPs) at that monitor's DPI,
    /// no bigger than the work area, and centered there; maximized after, when it closed maximized.
    /// </summary>
    public static void Restore(AppWindow window, OverlappedPresenter presenter, Core.Views.WindowSize size)
    {
        PInvoke.GetCursorPos(out var cursor);
        var work = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest).WorkArea;
        window.Move(Centered(work, window.Size));

        var place = size.PlaceIn(work.X, work.Y, work.Width, work.Height, ScaleOf(window));
        window.MoveAndResize(new RectInt32(place.X, place.Y, place.Width, place.Height));
        if (size.Maximized)
        {
            presenter.Maximize();
        }
    }

    /// <summary>The window's size in DIPs (the whole window, frame included), at the DPI of the monitor it's on.</summary>
    public static Core.Views.WindowSize SizeOf(AppWindow window)
    {
        var scale = ScaleOf(window);
        return new(Math.Round(window.Size.Width / scale), Math.Round(window.Size.Height / scale));
    }

    /// <summary>The scale of the monitor the window is on (its DPI over 96), usable before its content has a XamlRoot.</summary>
    public static double ScaleOf(AppWindow window) => PInvoke.GetDpiForWindow(new HWND(Win32Interop.GetWindowFromWindowId(window.Id))) / 96.0;

    // Centered on the work area, with the title bar never above its top
    private static PointInt32 Centered(RectInt32 work, SizeInt32 size) =>
        new(work.X + (work.Width - size.Width) / 2, Math.Max(work.Y, work.Y + (work.Height - size.Height) / 2));
}
