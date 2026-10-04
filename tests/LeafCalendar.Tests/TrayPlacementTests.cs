using LeafCalendar.Core.Tray;

namespace LeafCalendar.Tests;

public class TrayPlacementTests
{
    // A 1920 × 1080 primary monitor
    private static readonly PixelRect Monitor = new(0, 0, 1920, 1080);
    private static readonly PixelRect BottomWork = new(0, 0, 1920, 1032);

    private static bool Inside(PixelRect inner, PixelRect outer, int margin) =>
        inner.Left >= outer.Left + margin && inner.Top >= outer.Top + margin && inner.Right <= outer.Right - margin && inner.Bottom <= outer.Bottom - margin;

    [Theory]
    [InlineData(0u, TaskbarEdge.Left)]
    [InlineData(1u, TaskbarEdge.Top)]
    [InlineData(2u, TaskbarEdge.Right)]
    [InlineData(3u, TaskbarEdge.Bottom)]
    [InlineData(9u, TaskbarEdge.Bottom)]
    public void EdgeFromAppBar_MapsTheWin32Values(uint edge, TaskbarEdge expected)
    {
        Assert.Equal(expected, TrayPlacement.EdgeFromAppBar(edge));
    }

    [Fact]
    public void DetectEdge_FromTheMissingPartOfTheWorkArea()
    {
        Assert.Equal(TaskbarEdge.Bottom, TrayPlacement.DetectEdge(Monitor, BottomWork));
        Assert.Equal(TaskbarEdge.Top, TrayPlacement.DetectEdge(Monitor, new(0, 48, 1920, 1080)));
        Assert.Equal(TaskbarEdge.Left, TrayPlacement.DetectEdge(Monitor, new(48, 0, 1920, 1080)));
        Assert.Equal(TaskbarEdge.Right, TrayPlacement.DetectEdge(Monitor, new(0, 0, 1872, 1080)));
        Assert.Equal(TaskbarEdge.Bottom, TrayPlacement.DetectEdge(Monitor, Monitor));
    }

    [Fact]
    public void Flyout_BottomTaskbar_CenteredOnTheIconAboveTheTaskbar()
    {
        var panel = TrayPlacement.Flyout(BottomWork, TaskbarEdge.Bottom, new PixelRect(1700, 1040, 1724, 1064), 1.0);

        Assert.Equal(new PixelRect(1532, 460, 1892, 1020), panel);
    }

    [Fact]
    public void Flyout_IconNearTheCornerOrUnknown_KeepsTheMarginFromTheEdge()
    {
        var corner = TrayPlacement.Flyout(BottomWork, TaskbarEdge.Bottom, new PixelRect(1890, 1040, 1914, 1064), 1.0);
        var unknown = TrayPlacement.Flyout(BottomWork, TaskbarEdge.Bottom, null, 1.0);

        Assert.Equal(1908, corner.Right);
        Assert.Equal(corner, unknown);
    }

    // The owner's rule: centered on the icon, but never off the work area. Every icon spot along each taskbar, on a
    // primary monitor, one left of it, and one above it, at 100% to 250%: the panel stays 12 DIPs inside, and centers on
    // the icon whenever there's room
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2.5)]
    public void Flyout_AnyIconSpot_CenteredWhenItFits_NeverOffTheWorkArea(double scale)
    {
        var margin = (int)Math.Round(TrayPlacement.MarginDip * scale);
        var bar = (int)Math.Round(48 * scale);
        var monitors = new[] { new PixelRect(0, 0, 2560, 1440), new PixelRect(-1920, 0, 0, 1080), new PixelRect(0, -1200, 1920, 0) };

        foreach (var monitor in monitors)
        {
            foreach (var edge in new[] { TaskbarEdge.Bottom, TaskbarEdge.Top, TaskbarEdge.Left, TaskbarEdge.Right })
            {
                var work = edge switch
                {
                    TaskbarEdge.Top => monitor with { Top = monitor.Top + bar },
                    TaskbarEdge.Left => monitor with { Left = monitor.Left + bar },
                    TaskbarEdge.Right => monitor with { Right = monitor.Right - bar },
                    _ => monitor with { Bottom = monitor.Bottom - bar },
                };
                var along = edge is TaskbarEdge.Bottom or TaskbarEdge.Top;
                var length = along ? monitor.Width : monitor.Height;

                for (var at = 0; at < length; at += 37)
                {
                    var icon = along
                        ? new PixelRect(monitor.Left + at, edge == TaskbarEdge.Top ? monitor.Top : monitor.Bottom - bar, monitor.Left + at + bar, edge == TaskbarEdge.Top ? monitor.Top + bar : monitor.Bottom)
                        : new PixelRect(edge == TaskbarEdge.Left ? monitor.Left : monitor.Right - bar, monitor.Top + at, edge == TaskbarEdge.Left ? monitor.Left + bar : monitor.Right, monitor.Top + at + bar);

                    var panel = TrayPlacement.Flyout(work, edge, icon, scale);

                    Assert.True(Inside(panel, work, margin), $"{panel} leaves {work} at {scale}x, icon {icon}.");
                    var (center, panelCenter, min, max) = along
                        ? ((icon.Left + icon.Right) / 2, (panel.Left + panel.Right) / 2, work.Left + margin + panel.Width / 2, work.Right - margin - panel.Width / 2)
                        : ((icon.Top + icon.Bottom) / 2, (panel.Top + panel.Bottom) / 2, work.Top + margin + panel.Height / 2, work.Bottom - margin - panel.Height / 2);
                    if (center >= min && center <= max)
                    {
                        Assert.True(Math.Abs(center - panelCenter) <= 1, $"{panel} isn't centered on {icon} at {scale}x.");
                    }
                }
            }
        }
    }

    [Fact]
    public void Flyout_TopTaskbar_BelowIt()
    {
        var work = new PixelRect(0, 48, 1920, 1080);
        var panel = TrayPlacement.Flyout(work, TaskbarEdge.Top, new PixelRect(1700, 12, 1724, 36), 1.0);

        Assert.Equal(60, panel.Top);
        Assert.True(Inside(panel, work, 12));
    }

    [Fact]
    public void Flyout_RightTaskbar_BesideItNearTheIcon()
    {
        var work = new PixelRect(0, 0, 1872, 1080);
        var panel = TrayPlacement.Flyout(work, TaskbarEdge.Right, new PixelRect(1884, 1000, 1908, 1024), 1.0);

        Assert.Equal(1860, panel.Right);
        Assert.Equal(508, panel.Top);
        Assert.True(Inside(panel, work, 12));
    }

    [Fact]
    public void Flyout_LeftTaskbarOnMonitorLeftOfPrimary_StaysInside()
    {
        // A monitor at x -1920..0 with a 48-pixel taskbar on its left edge
        var work = new PixelRect(-1872, 0, 0, 1080);
        var panel = TrayPlacement.Flyout(work, TaskbarEdge.Left, new PixelRect(-1910, 1000, -1886, 1024), 1.0);

        Assert.Equal(new PixelRect(-1860, 508, -1500, 1068), panel);
        Assert.True(Inside(panel, work, 12));
    }

    [Fact]
    public void Flyout_AutoHiddenBottomTaskbar_StaysAboveIt()
    {
        // Auto-hide: the work area is the whole monitor, and the taskbar pops up over its bottom 48 pixels
        var area = TrayPlacement.UsableArea(Monitor, new PixelRect(0, 1032, 1920, 1080), TaskbarEdge.Bottom);
        var panel = TrayPlacement.Flyout(area, TaskbarEdge.Bottom, null, 1.0);

        Assert.Equal(1020, panel.Bottom);
    }

    [Fact]
    public void UsableArea_TaskbarOnAnotherMonitor_LeavesTheWorkArea()
    {
        Assert.Equal(BottomWork, TrayPlacement.UsableArea(BottomWork, new PixelRect(-1920, 1032, 0, 1080), TaskbarEdge.Bottom));
    }

    [Fact]
    public void Flyout_ShortScreen_GetsShorter()
    {
        var work = new PixelRect(0, 0, 1280, 500);
        var panel = TrayPlacement.Flyout(work, TaskbarEdge.Bottom, null, 1.0);

        Assert.Equal(476, panel.Height);
        Assert.True(Inside(panel, work, 12));
    }

    [Fact]
    public void Flyout_At150Percent_ScalesSizeAndMargin()
    {
        var work = new PixelRect(0, 0, 2880, 1548);
        var panel = TrayPlacement.Flyout(work, TaskbarEdge.Bottom, null, 1.5);

        Assert.Equal(new PixelRect(2322, 690, 2862, 1530), panel);
    }

    [Fact]
    public void FrameAndAnchor_OnTheTaskbarSide()
    {
        var panel = new PixelRect(1532, 460, 1892, 1020);
        var frame = TrayPlacement.Frame(panel, 1.0);

        Assert.Equal(new PixelRect(1520, 448, 1904, 1032), frame);
        Assert.Equal((1520, 1032), TrayPlacement.FlyoutAnchor(frame, TaskbarEdge.Bottom));
        Assert.Equal((1520, 448), TrayPlacement.FlyoutAnchor(frame, TaskbarEdge.Top));
        Assert.Equal((1520, 1032), TrayPlacement.FlyoutAnchor(frame, TaskbarEdge.Left));
        Assert.Equal((1904, 1032), TrayPlacement.FlyoutAnchor(frame, TaskbarEdge.Right));
    }

    [Fact]
    public void MenuAnchor_MovesOffTheTaskbarByTheMargin()
    {
        Assert.Equal((1700, 1020), TrayPlacement.MenuAnchor(1700, 1050, BottomWork, TaskbarEdge.Bottom, 1.0));
        Assert.Equal((1700, 60), TrayPlacement.MenuAnchor(1700, 20, new PixelRect(0, 48, 1920, 1080), TaskbarEdge.Top, 1.0));
        Assert.Equal((60, 900), TrayPlacement.MenuAnchor(20, 900, new PixelRect(48, 0, 1920, 1080), TaskbarEdge.Left, 1.0));
        Assert.Equal((1860, 900), TrayPlacement.MenuAnchor(1900, 900, new PixelRect(0, 0, 1872, 1080), TaskbarEdge.Right, 1.0));
    }
}
