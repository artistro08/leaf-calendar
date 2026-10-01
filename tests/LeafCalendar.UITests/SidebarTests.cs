using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class SidebarTests : IDisposable
{
    const string FamilyId = "family123@group.calendar.google.com";

    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    [Fact]
    public void Sidebar_AfterSync_ListsBothCalendarsUnderAccount()
    {
        using var leaf = Launch();

        Assert.NotNull(leaf.WaitFor($"CalendarToggle_{FamilyId}"));
        Assert.NotNull(leaf.WaitForName("leaf.tester@gmail.com"));
        Assert.NotNull(leaf.WaitFor("MiniMonth"));
    }

    [Fact]
    public void HideCalendar_PersistsAcrossLaunches()
    {
        using (var leaf = Launch())
        {
            leaf.WaitFor($"CalendarToggle_{FamilyId}").AsCheckBox().Toggle();
            Assert.True(Retry.WhileFalse(() => leaf.WaitFor($"CalendarToggle_{FamilyId}").AsCheckBox().ToggleState == ToggleState.Off, TimeSpan.FromSeconds(5)).Success);
        }

        using var relaunched = Launch();
        Assert.Equal(ToggleState.Off, relaunched.WaitFor($"CalendarToggle_{FamilyId}").AsCheckBox().ToggleState);
    }

    [Fact]
    public void Content_SharesOneLeftEdge()
    {
        using var leaf = Launch();
        var toggle = leaf.WaitFor($"CalendarToggle_{FamilyId}");
        LeafApp.WaitUntilStill(toggle);
        var box = toggle.BoundingRectangle;

        // The settings glyph starts 8 DIPs in on its button (a mini-month day button is 28 DIPs wide)
        var inset = 8 * leaf.WaitFor("MiniDay_2026-10-01").BoundingRectangle.Width / 28.0;
        var edges = new Dictionary<string, double>
        {
            ["checkbox"]    = box.Left,
            ["mini month"]  = leaf.WaitFor("MiniMonthTitle").BoundingRectangle.Left,
            ["account"]     = leaf.MainWindow.FindAllDescendants(cf => cf.ByName("leaf.tester@gmail.com")).Where(e => e.ControlType == ControlType.Text).MinBy(e => e.BoundingRectangle.Top)!.BoundingRectangle.Left,
            ["settings"]    = leaf.WaitFor("SettingsButton").BoundingRectangle.Left + inset,
        };

        Assert.All(edges, e => Assert.True(Math.Abs(e.Value - box.Left) <= 1.5, $"{e.Key} starts at {e.Value}, the checkboxes at {box.Left}"));
    }

    [Fact]
    public void MiniMonth_SpansTheSidebarWithMirroredInsets()
    {
        using var leaf = Launch();
        leaf.WaitFor($"CalendarToggle_{FamilyId}");

        // The sidebar pane is the page's left 264 DIPs (a mini-month day button is 28 DIPs wide)
        var scale = leaf.WaitFor("MiniDay_2026-10-01").BoundingRectangle.Width / 28.0;
        var pane  = leaf.WaitFor("CalendarRoot").BoundingRectangle.Left;
        var month = leaf.WaitFor("MiniMonth").BoundingRectangle;
        var next  = leaf.WaitFor("MiniMonthNext").BoundingRectangle;
        var left  = month.Left - pane;
        var right = pane + 264 * scale - month.Right;

        Assert.True(Math.Abs(left - right) <= 1.5, $"The mini month is {left} in from the left and {right} in from the right.");
        Assert.True(Math.Abs(next.Right - month.Right) <= 1.5, $"The next-month arrow ends at {next.Right}, the mini month at {month.Right}.");
    }

    [Fact]
    public void Wheel_AnywhereOverCalendarList_ScrollsOnlyTheList()
    {
        _google.ManyCalendars = true;
        using var leaf = Launch();
        leaf.WaitFor($"CalendarToggle_{FamilyId}");
        leaf.WaitFor("CalendarToggle_church@group.calendar.google.com");
        leaf.Resize(1300, 200);
        var list   = leaf.WaitFor("SidebarScroll");
        var scroll = list.Patterns.Scroll.Pattern;
        Assert.True(Retry.WhileFalse(() => scroll.VerticallyScrollable.ValueOrDefault, TimeSpan.FromSeconds(5)).Success);

        // The mini month sits above the list and the settings button below it, outside the scrolling part
        var box    = list.BoundingRectangle;
        var month  = leaf.WaitFor("MiniMonth").BoundingRectangle;
        var footer = leaf.WaitFor("SettingsButton").BoundingRectangle;
        Assert.True(month.Bottom <= box.Top && footer.Top >= box.Bottom, $"The list ({box}) overlaps the mini month ({month}) or the footer ({footer}).");

        // Near the top (account header), the middle, and the bottom (calendar rows)
        foreach (var y in new[] { box.Top + 12, box.Top + box.Height / 2, box.Bottom - 12 })
        {
            // Back to the top (again until it sticks: the last wheel scroll may still be gliding)
            Assert.True(Retry.WhileFalse(() =>
            {
                scroll.SetScrollPercent(-1, 0);
                Thread.Sleep(300);
                return scroll.VerticalScrollPercent.ValueOrDefault == 0;
            }, TimeSpan.FromSeconds(5)).Success);

            Mouse.MoveTo(new System.Drawing.Point(box.X + box.Width / 2, y));
            Thread.Sleep(100);
            Mouse.Scroll(-2);

            Assert.True(Retry.WhileFalse(() => scroll.VerticalScrollPercent.ValueOrDefault > 0, TimeSpan.FromSeconds(5)).Success, $"The wheel at y={y} didn't scroll the calendar list.");
        }

        // Scrolled, the mini month and the settings button haven't moved
        Assert.Equal(month, leaf.WaitFor("MiniMonth").BoundingRectangle);
        Assert.Equal(footer, leaf.WaitFor("SettingsButton").BoundingRectangle);
    }

    [Fact]
    public void Footer_HasSettingsThenShareAvailability()
    {
        using var leaf = Launch();
        leaf.WaitFor($"CalendarToggle_{FamilyId}");

        var settings = leaf.WaitFor("SettingsButton");
        var share    = leaf.WaitFor("SidebarShareAvailability");
        Assert.Equal("Settings", settings.Name);
        Assert.Equal("Share availability", share.Name);
        Assert.Equal(settings.BoundingRectangle.Top, share.BoundingRectangle.Top);
        Assert.True(share.BoundingRectangle.Left > settings.BoundingRectangle.Right, "Share availability sits right of Settings.");
        Assert.False(leaf.Exists("BookingPagesLink"));
        Assert.False(leaf.Exists("AccountsButton"));
        Assert.False(leaf.Exists($"CalendarColor_{FamilyId}"));
    }

    // Each calendar row's checkbox, by automation ID, with its UI Automation runtime ID (a rebuilt row gets a new one).
    // Found under the scroll viewer: the list itself (an ItemsControl) isn't in the automation tree.
    static Dictionary<string, string> RowElements(LeafApp leaf) =>
        leaf.WaitFor("SidebarScroll")
            .FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox))
            .ToDictionary(r => r.AutomationId, r => string.Join(".", r.Properties.RuntimeId.Value));

    // Runs "Sync now" from Settings › Accounts and waits until Leaf has fetched the calendar list again and had time to show it
    void SyncNow(LeafApp leaf)
    {
        var lists = _google.Requests.Count(r => r.Contains("/calendarList", StringComparison.Ordinal));
        leaf.OpenSettings("Accounts");
        leaf.WaitInSettings("SyncNowButton").AsButton().Invoke();
        Assert.True(Retry.WhileFalse(() => _google.Requests.Count(r => r.Contains("/calendarList", StringComparison.Ordinal)) > lists, TimeSpan.FromSeconds(15)).Success);
        Thread.Sleep(1500);
    }

    // The list is already there at a later launch: it appears where it stays, with no add or slide animation
    [Fact]
    public void Relaunch_ListAppearsWithoutMoving()
    {
        using (var first = Launch())
        {
            first.WaitFor($"CalendarToggle_{FamilyId}");
        }

        using var leaf = Launch();
        var toggle = leaf.WaitFor($"CalendarToggle_{FamilyId}");
        var shown  = toggle.BoundingRectangle;
        Thread.Sleep(1000);

        Assert.Equal(shown, toggle.BoundingRectangle);
    }

    [Fact]
    public void UncheckCalendar_KeepsEveryRowInPlace()
    {
        using var leaf = Launch();
        leaf.WaitFor($"CalendarToggle_{FamilyId}");
        var rows   = leaf.WaitFor("SidebarScroll").FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox));
        var before = rows.Select(r => string.Join(".", r.Properties.RuntimeId.Value)).ToList();

        rows[1].AsCheckBox().Toggle();
        Thread.Sleep(1000);

        var after = leaf.WaitFor("SidebarScroll").FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox)).Select(r => string.Join(".", r.Properties.RuntimeId.Value)).ToList();
        Assert.Equal(before, after);
        Assert.False(rows[1].AsCheckBox().IsChecked);
    }

    [Fact]
    public void BackgroundSync_WithNoCalendarChanges_KeepsEveryRow()
    {
        using var leaf = Launch();
        leaf.WaitFor($"CalendarToggle_{FamilyId}");
        var before = RowElements(leaf);

        SyncNow(leaf);

        Assert.Equal(before, RowElements(leaf));
    }

    [Fact]
    public void CalendarRemovedOnGoogle_RemovesOnlyItsRow()
    {
        _google.ManyCalendars = true;
        using var leaf = Launch();
        leaf.WaitFor($"CalendarToggle_{FamilyId}");
        leaf.WaitFor("CalendarToggle_church@group.calendar.google.com");
        var before = RowElements(leaf);
        Assert.True(before.Count > 2, $"Only {before.Count} rows to compare.");

        _google.DroppedCalendarId = FamilyId;
        SyncNow(leaf);

        Assert.True(Retry.WhileTrue(() => leaf.Exists($"CalendarToggle_{FamilyId}"), TimeSpan.FromSeconds(10)).Success, "The removed calendar is still listed.");
        before.Remove($"CalendarToggle_{FamilyId}");
        Assert.Equal(before, RowElements(leaf));
    }

    // The calendar never slides with a pane: from the toggle on, the last day's header (one step to its new width at
    // most), the details panel, and the vertical scroll stay where the final layout puts them. An inline SplitView
    // resized the island at once and then slid it and the details panel by the pane's width, so the calendar's center jumped
    [Fact]
    public void SidebarToggle_KeepsTheCalendarInPlace()
    {
        using var leaf = Launch();
        leaf.WaitFor($"CalendarToggle_{FamilyId}");
        var toggle  = leaf.WaitFor("AppTitleBar").FindFirstDescendant(cf => cf.ByAutomationId("PART_PaneToggleButton"))!.AsButton();
        var last    = leaf.WaitFor("DayHeader_2026-10-03");
        var details = leaf.WaitFor("DetailsPanel");
        var grid    = leaf.WaitFor("TimeGrid");
        LeafApp.WaitUntilStill(last);
        Thread.Sleep(500);

        var right = last.BoundingRectangle.Right;
        var panel = details.BoundingRectangle;
        var top   = Top(grid);
        foreach (var open in new[] { false, true, false })
        {
            toggle.Invoke();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var edges = new HashSet<int> { right };
            while (watch.ElapsedMilliseconds < 400)
            {
                // A Header Being Rebuilt For The New Width Has No Box For A Moment
                var edge = last.BoundingRectangle.Right;
                if (edge > 0)
                {
                    edges.Add(edge);
                }

                var now = details.BoundingRectangle;
                Assert.True(now == panel, $"open={open} at {watch.ElapsedMilliseconds} ms: {now} (was {panel}); edges {string.Join(", ", edges)}");
            }

            // At Most One Step: The Columns Take Their New Width Once, Then Stay
            Assert.True(edges.Count <= 2, $"The last day's header slid through {string.Join(", ", edges)}.");
            right = last.BoundingRectangle.Right;

            Assert.Equal(open, leaf.Exists("MiniMonth"));
            Assert.True(Retry.WhileFalse(() => Top(grid) == top, TimeSpan.FromSeconds(3)).Success, $"The grid scrolled to {Top(grid)} (from {top}).");
        }
    }

    // Each pane toggle's glyph has a narrow panel (the sidebar's on the left, the details panel's on the right), filled
    // with the icon's color while its pane is open and outlined while it's closed: the panel's middle pixel is the
    // icon's color, then the background
    [Fact]
    public void PaneToggleGlyphs_FillTheirPanelWhileOpen()
    {
        using var leaf = Launch();
        leaf.WaitFor($"CalendarToggle_{FamilyId}");
        var sidebar = leaf.WaitFor("AppTitleBar").FindFirstDescendant(cf => cf.ByAutomationId("PART_PaneToggleButton"))!;
        var details = leaf.WaitFor("DetailsToggleButton");
        Thread.Sleep(500);

        // The Panel's Middle: 5 DIPs from the button's center, toward its side
        Color Panel(AutomationElement button, int side)
        {
            var box = button.BoundingRectangle;
            var x   = (int)Math.Round(box.X + box.Width / 2.0 + side * 5 * leaf.Scale);
            var y   = (int)Math.Round(box.Y + box.Height / 2.0);
            using var shot = Capture.Rectangle(new Rectangle(x, y, 1, 1));
            return shot.Bitmap.GetPixel(0, 0);
        }

        static int Distance(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

        var sidebarOpen = Panel(sidebar, -1);
        var detailsOpen = Panel(details, 1);
        sidebar.AsButton().Invoke();
        details.AsToggleButton().Toggle();
        Thread.Sleep(800);

        Assert.True(Distance(sidebarOpen, Panel(sidebar, -1)) > 120, $"The sidebar glyph's panel is {sidebarOpen} open and {Panel(sidebar, -1)} closed.");
        Assert.True(Distance(detailsOpen, Panel(details, 1)) > 120, $"The details glyph's panel is {detailsOpen} open and {Panel(details, 1)} closed.");
    }

    // The time grid's vertical offset, from the state it publishes when it comes to rest
    static string Top(AutomationElement grid) =>
        (grid.Properties.ItemStatus.ValueOrDefault ?? "").Split(';').FirstOrDefault(p => p.StartsWith("top=", StringComparison.Ordinal)) ?? "";

    // Measured from the page's corner: the sidebar pane runs down the page's left edge, while the Sidebar element's own
    // automation box only covers what it draws (inside its padding)
    [Fact]
    public void SettingsButton_SitsAsFarFromTheBottomAsFromTheLeft()
    {
        using var leaf = Launch();
        var sidebar = leaf.WaitFor("CalendarRoot").BoundingRectangle;
        var glyph   = leaf.WaitFor("SettingsButton").BoundingRectangle;
        var fromLeft   = glyph.X + glyph.Width / 2.0 - sidebar.X;
        var fromBottom = sidebar.Bottom - (glyph.Y + glyph.Height / 2.0);
        Assert.True(Math.Abs(fromLeft - fromBottom) <= 1.5, $"{fromLeft} from the left, {fromBottom} from the bottom.");
    }
}
