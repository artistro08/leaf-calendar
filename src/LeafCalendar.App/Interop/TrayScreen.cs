using LeafCalendar.Core.Tray;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.Shell;

namespace LeafCalendar.App.Interop;

/// <summary>A monitor's usable area (work area minus an auto-hidden taskbar), taskbar edge, and scale (DPI / 96).</summary>
internal readonly record struct TrayScreenInfo(PixelRect Area, TaskbarEdge Edge, double Scale);

/// <summary>Reads where the taskbar is on a monitor (spec 8.2): Windows' own answer when that taskbar is on it, else what its work area leaves out.</summary>
internal static unsafe class TrayScreen
{
    // SHAppBarMessage: the taskbar's rectangle and edge
    const uint AbmGetTaskbarPos = 5;

    /// <summary>The monitor holding a screen point (the tray icon or a click).</summary>
    public static TrayScreenInfo At(int x, int y) =>
        For(PInvoke.MonitorFromPoint(new System.Drawing.Point(x, y), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST));

    /// <summary>The primary monitor (where the tray is when the icon's place is unknown).</summary>
    public static TrayScreenInfo Primary() =>
        For(PInvoke.MonitorFromPoint(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY));

    static TrayScreenInfo For(HMONITOR monitor)
    {
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (!PInvoke.GetMonitorInfo(monitor, ref info))
        {
            // The monitor went away since the point was taken (a display unplugged): the primary stands in, and failing
            // that a plain 1080p area, so the flyout still has a size and a place
            monitor = PInvoke.MonitorFromPoint(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
            if (!PInvoke.GetMonitorInfo(monitor, ref info))
            {
                info.rcMonitor = new RECT { right = 1920, bottom = 1080 };
                info.rcWork    = info.rcMonitor;
            }
        }

        var bounds = Rect(info.rcMonitor);
        var work   = Rect(info.rcWork);
        var scale  = PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpi, out _).Succeeded ? dpi / 96.0 : 1.0;

        // The Taskbar (ABM_GETTASKBARPOS answers for the primary taskbar, so it's used only when that one is on this
        // monitor; it gives an auto-hidden taskbar's full rectangle, not the visible sliver, so the usable area clears it)
        var edge           = TrayPlacement.DetectEdge(bounds, work);
        PixelRect? taskbar = null;
        var bar            = new APPBARDATA { cbSize = (uint)sizeof(APPBARDATA) };
        if (PInvoke.SHAppBarMessage(AbmGetTaskbarPos, ref bar) != 0)
        {
            var rect = Rect(bar.rc);
            if (rect.Left >= bounds.Left && rect.Right <= bounds.Right && rect.Top >= bounds.Top && rect.Bottom <= bounds.Bottom)
            {
                taskbar = rect;
                edge    = TrayPlacement.EdgeFromAppBar(bar.uEdge);
            }
        }

        return new TrayScreenInfo(TrayPlacement.UsableArea(work, taskbar, edge), edge, scale);
    }

    static PixelRect Rect(RECT r) => new(r.left, r.top, r.right, r.bottom);
}
