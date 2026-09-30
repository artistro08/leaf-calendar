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

        // The footer buttons pad their text 9 DIPs in (a mini-month day button is 28 DIPs wide)
        var inset = 9 * leaf.WaitFor("MiniDay_2026-10-01").BoundingRectangle.Width / 28.0;
        var edges = new Dictionary<string, double>
        {
            ["checkbox"]    = box.Left,
            ["mini month"]  = leaf.WaitFor("MiniMonthTitle").BoundingRectangle.Left,
            ["account"]     = leaf.MainWindow.FindAllDescendants(cf => cf.ByName("leaf.tester@gmail.com")).Where(e => e.ControlType == ControlType.Text).MinBy(e => e.BoundingRectangle.Top)!.BoundingRectangle.Left,
            ["booking"]     = leaf.WaitFor("BookingPagesLink").BoundingRectangle.Left + inset,
            ["accounts"]    = leaf.WaitFor("AccountsButton").BoundingRectangle.Left + inset,
        };

        Assert.All(edges, e => Assert.True(Math.Abs(e.Value - box.Left) <= 1.5, $"{e.Key} starts at {e.Value}, the checkboxes at {box.Left}"));
    }

    [Fact]
    public void Wheel_AnywhereOverSidebar_ScrollsIt()
    {
        _google.ManyCalendars = true;
        using var leaf = Launch();
        leaf.WaitFor($"CalendarToggle_{FamilyId}");
        leaf.WaitFor("CalendarToggle_church@group.calendar.google.com");
        leaf.Resize(1300, 200);
        var sidebar = leaf.WaitFor("SidebarScroll");
        var scroll  = sidebar.Patterns.Scroll.Pattern;
        Assert.True(Retry.WhileFalse(() => scroll.VerticallyScrollable.ValueOrDefault, TimeSpan.FromSeconds(5)).Success);

        // Near the top (mini month), the middle, and the bottom (account header and calendar rows)
        var box = sidebar.BoundingRectangle;
        foreach (var y in new[] { box.Top + 20, box.Top + box.Height / 2, box.Bottom - 20 })
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

            Assert.True(Retry.WhileFalse(() => scroll.VerticalScrollPercent.ValueOrDefault > 0, TimeSpan.FromSeconds(5)).Success, $"The wheel at y={y} didn't scroll the sidebar.");
        }
    }

    [Fact]
    public void ChangeColor_PicksSwatch()
    {
        using var leaf = Launch();

        leaf.WaitFor($"CalendarColor_{FamilyId}").AsButton().Invoke();
        leaf.WaitForAnywhere("ColorSwatch_16A765").AsButton().Invoke();

        // The flyout closes and the calendar keeps working; the color itself is covered by CalendarPreferencesTests
        Assert.NotNull(leaf.WaitFor($"CalendarToggle_{FamilyId}"));
    }
}
