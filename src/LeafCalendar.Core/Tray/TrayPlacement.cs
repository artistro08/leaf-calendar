namespace LeafCalendar.Core.Tray;

/// <summary>A screen rectangle in physical pixels; right and bottom are exclusive.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    /// <summary>Width in pixels.</summary>
    public int Width => Right - Left;

    /// <summary>Height in pixels.</summary>
    public int Height => Bottom - Top;
}

/// <summary>The screen edge the taskbar sits on.</summary>
public enum TaskbarEdge
{
    /// <summary>Along the bottom (the default).</summary>
    Bottom,

    /// <summary>Along the top.</summary>
    Top,

    /// <summary>Along the left.</summary>
    Left,

    /// <summary>Along the right.</summary>
    Right,
}

/// <summary>
/// Where the tray flyout and the tray menu open, for any taskbar edge (spec 8.2, 8.3; design standard window sizes).
/// </summary>
/// <remarks>
/// The flyout is 360 × 560 DIPs (shorter when the screen is), 12 DIPs from the taskbar and the screen edges, next to
/// the tray icon: centered on it along a top or bottom taskbar, level with it beside a left or right one, and at the
/// far end when the icon's place is unknown. It never leaves the usable area, which is the icon monitor's work area
/// minus an auto-hidden taskbar (auto-hide gives the taskbar no work area, but it pops up over the edge). Coordinates
/// may be negative (monitors left of or above the primary one).
/// </remarks>
public static class TrayPlacement
{
    /// <summary>Flyout width in DIPs.</summary>
    public const double WidthDip = 360;

    /// <summary>Flyout height in DIPs (the tallest it gets).</summary>
    public const double HeightDip = 560;

    /// <summary>Gap to the taskbar and the screen edges in DIPs.</summary>
    public const double MarginDip = 12;

    /// <summary>The edge from <c>APPBARDATA.uEdge</c>.</summary>
    public static TaskbarEdge EdgeFromAppBar(uint edge) => edge switch
    {
        0 => TaskbarEdge.Left,
        1 => TaskbarEdge.Top,
        2 => TaskbarEdge.Right,
        _ => TaskbarEdge.Bottom,
    };

    /// <summary>The edge from the part of the monitor its work area leaves out (bottom when nothing is, like auto-hide).</summary>
    public static TaskbarEdge DetectEdge(PixelRect monitor, PixelRect workArea)
    {
        if (workArea.Bottom < monitor.Bottom)
        {
            return TaskbarEdge.Bottom;
        }

        if (workArea.Top > monitor.Top)
        {
            return TaskbarEdge.Top;
        }

        if (workArea.Left > monitor.Left)
        {
            return TaskbarEdge.Left;
        }

        return workArea.Right < monitor.Right ? TaskbarEdge.Right : TaskbarEdge.Bottom;
    }

    /// <summary>The work area, pulled in off a taskbar that overlaps it (auto-hide).</summary>
    public static PixelRect UsableArea(PixelRect workArea, PixelRect? taskbar, TaskbarEdge edge)
    {
        if (taskbar is not { } bar || bar.Right <= workArea.Left || bar.Left >= workArea.Right || bar.Bottom <= workArea.Top || bar.Top >= workArea.Bottom)
        {
            return workArea;
        }

        return edge switch
        {
            TaskbarEdge.Top => workArea with { Top = Math.Max(workArea.Top, bar.Bottom) },
            TaskbarEdge.Left => workArea with { Left = Math.Max(workArea.Left, bar.Right) },
            TaskbarEdge.Right => workArea with { Right = Math.Min(workArea.Right, bar.Left) },
            _ => workArea with { Bottom = Math.Min(workArea.Bottom, bar.Top) },
        };
    }

    /// <summary>The flyout panel's rectangle.</summary>
    public static PixelRect Flyout(PixelRect area, TaskbarEdge edge, PixelRect? icon, double scale)
    {
        var margin = Px(MarginDip, scale);
        var width = Math.Min(Px(WidthDip, scale), area.Width - 2 * margin);
        var height = Math.Min(Px(HeightDip, scale), area.Height - 2 * margin);

        // Next To The Icon, Or At The Far End (Quick Settings' corner)
        var (cx, cy) = icon is { } i ? ((i.Left + i.Right) / 2, (i.Top + i.Bottom) / 2) : (area.Right, area.Bottom);
        var minLeft = area.Left + margin;
        var maxLeft = area.Right - margin - width;
        var minTop = area.Top + margin;
        var maxTop = area.Bottom - margin - height;

        var (left, top) = edge switch
        {
            TaskbarEdge.Top => (Clamp(cx - width / 2, minLeft, maxLeft), minTop),
            TaskbarEdge.Left => (minLeft, Clamp(cy - height / 2, minTop, maxTop)),
            TaskbarEdge.Right => (maxLeft, Clamp(cy - height / 2, minTop, maxTop)),
            _ => (Clamp(cx - width / 2, minLeft, maxLeft), maxTop),
        };

        return new PixelRect(left, top, left + width, top + height);
    }

    /// <summary>The panel plus one margin on every side: its taskbar-side edge is the usable area's edge, where the slide is clipped.</summary>
    public static PixelRect Frame(PixelRect panel, double scale)
    {
        var margin = Px(MarginDip, scale);
        return new PixelRect(panel.Left - margin, panel.Top - margin, panel.Right + margin, panel.Bottom + margin);
    }

    /// <summary>The frame corner the flyout opens from: its bottom-left for a bottom or left taskbar, top-left for a top one, bottom-right for a right one.</summary>
    public static (int X, int Y) FlyoutAnchor(PixelRect frame, TaskbarEdge edge) => edge switch
    {
        TaskbarEdge.Top => (frame.Left, frame.Top),
        TaskbarEdge.Right => (frame.Right, frame.Bottom),
        _ => (frame.Left, frame.Bottom),
    };

    /// <summary>Where the menu opens for a click: moved off the taskbar to the usable area's edge plus the margin.</summary>
    public static (int X, int Y) MenuAnchor(int x, int y, PixelRect area, TaskbarEdge edge, double scale)
    {
        var margin = Px(MarginDip, scale);
        return edge switch
        {
            TaskbarEdge.Top => (x, area.Top + margin),
            TaskbarEdge.Left => (area.Left + margin, y),
            TaskbarEdge.Right => (area.Right - margin, y),
            _ => (x, area.Bottom - margin),
        };
    }

    private static int Px(double dip, double scale) => (int)Math.Round(dip * scale);

    private static int Clamp(int value, int min, int max) => max < min ? min : Math.Clamp(value, min, max);
}
