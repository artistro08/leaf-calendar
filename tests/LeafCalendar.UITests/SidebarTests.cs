using FlaUI.Core.AutomationElements;
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
        var box = leaf.WaitFor($"CalendarToggle_{FamilyId}").BoundingRectangle;

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
    public void Footer_HasOnlyTheSettingsButton()
    {
        using var leaf = Launch();
        leaf.WaitFor($"CalendarToggle_{FamilyId}");

        Assert.Equal("Settings", leaf.WaitFor("SettingsButton").Name);
        Assert.False(leaf.Exists("BookingPagesLink"));
        Assert.False(leaf.Exists("AccountsButton"));
        Assert.False(leaf.Exists($"CalendarColor_{FamilyId}"));
    }

    // Each calendar row's checkbox, by automation ID, with its UI Automation runtime ID (a rebuilt row gets a new one)
    static Dictionary<string, string> RowElements(LeafApp leaf) =>
        leaf.WaitFor("CalendarList")
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

    [Fact]
    public void UncheckCalendar_KeepsEveryRowInPlace()
    {
        using var leaf = Launch();
        leaf.WaitFor($"CalendarToggle_{FamilyId}");
        var rows   = leaf.WaitFor("CalendarList").FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox));
        var before = rows.Select(r => string.Join(".", r.Properties.RuntimeId.Value)).ToList();

        rows[1].AsCheckBox().Toggle();
        Thread.Sleep(1000);

        var after = leaf.WaitFor("CalendarList").FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox)).Select(r => string.Join(".", r.Properties.RuntimeId.Value)).ToList();
        Assert.Equal(before, after);
        Assert.False(rows[1].AsCheckBox().IsChecked);
    }

    [Fact]
    public void CalendarList_HasADividerUnderTheMiniMonth_AndNoneAboveTheFirstAccount()
    {
        using var leaf = Launch();
        leaf.WaitFor($"CalendarToggle_{FamilyId}");

        var divider = leaf.WaitFor("SidebarDivider").BoundingRectangle;
        Assert.True(divider.Top >= leaf.WaitFor("MiniMonth").BoundingRectangle.Bottom, "The divider overlaps the mini month.");
        var header = leaf.MainWindow.FindAllDescendants(cf => cf.ByName(SeededProfile.Email)).Where(e => e.ControlType == ControlType.Text).MinBy(e => e.BoundingRectangle.Top)!;
        Assert.True(divider.Bottom <= header.BoundingRectangle.Top, "The divider isn't above the calendar list.");
        Assert.False(leaf.Exists($"AccountDivider_{SeededProfile.Email}"));
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

    [Fact]
    public void SettingsButton_SitsAsFarFromTheBottomAsFromTheLeft()
    {
        using var leaf = Launch();
        var sidebar = leaf.WaitFor("Sidebar").BoundingRectangle;
        var glyph   = leaf.WaitFor("SettingsButton").BoundingRectangle;
        var fromLeft   = glyph.X + glyph.Width / 2.0 - sidebar.X;
        var fromBottom = sidebar.Bottom - (glyph.Y + glyph.Height / 2.0);
        Assert.True(Math.Abs(fromLeft - fromBottom) <= 1.5, $"{fromLeft} from the left, {fromBottom} from the bottom.");
    }
}
