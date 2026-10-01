using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class DetailsPanelTests : IDisposable
{
    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    [Fact]
    public void ClickEvent_ShowsDetails_EscapeReturnsToUpcoming()
    {
        using var leaf = Launch();

        leaf.WaitFor("Event_evt-single_202610011300").Click();

        Assert.Equal("Dentist appointment", leaf.WaitFor("DetailsTitle").Name);
        Assert.Contains("Thursday, October 1", leaf.WaitFor("DetailsWhen").Name, StringComparison.Ordinal);

        Keyboard.Press(VirtualKeyShort.ESCAPE);

        Assert.NotNull(leaf.WaitFor("UpcomingHeader"));
    }

    [Fact]
    public void Details_ShowBusyAndVisibility()
    {
        _google.EditOnGoogle(SeededProfile.Email, "evt-single", e =>
        {
            e["transparency"] = "transparent";
            e["visibility"]   = "private";
        });
        using var leaf = Launch();

        leaf.WaitFor("Event_evt-single_202610011300").Click();
        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("DetailsStatus").Name == "Free · Private", TimeSpan.FromSeconds(5)).Success, $"Status says \"{leaf.WaitFor("DetailsStatus").Name}\".");

        // Another Event Keeps Google's Defaults
        leaf.WaitFor("Event_evt-meeting_202610011800").Click();
        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("DetailsStatus").Name == "Busy · Default visibility", TimeSpan.FromSeconds(5)).Success, $"Status says \"{leaf.WaitFor("DetailsStatus").Name}\".");
    }

    [Fact]
    public void ClickEmptyGridSpace_ClearsSelection()
    {
        using var leaf = Launch();

        var dentist = leaf.WaitFor("Event_evt-single_202610011300");
        dentist.Click();
        Assert.Equal("Dentist appointment", leaf.WaitFor("DetailsTitle").Name);

        // Same Day Column, Well Below The Event
        var box = dentist.BoundingRectangle;
        Mouse.Click(new System.Drawing.Point(box.X + box.Width / 2, box.Bottom + 150));

        Assert.NotNull(leaf.WaitFor("UpcomingHeader"));
        Assert.True(Retry.WhileTrue(() => leaf.Exists("DetailsTitle"), TimeSpan.FromSeconds(5)).Success);
    }

    [Fact]
    public void Wheel_OverDetailsPanel_ScrollsIt()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");

        // The family calendar's school play has a long description; N steps through events until it's selected
        Assert.True(Retry.WhileFalse(() =>
        {
            leaf.Press(VirtualKeyShort.KEY_N);
            Thread.Sleep(300);
            return leaf.Exists("DetailsTitle") && leaf.WaitFor("DetailsTitle").Name == "School play";
        }, TimeSpan.FromSeconds(10)).Success);

        // At the smallest window height the details overflow
        leaf.Resize(1300, 200);
        var scroll = leaf.WaitFor("DetailsScroll").Patterns.Scroll.Pattern;
        Assert.True(Retry.WhileFalse(() => scroll.VerticallyScrollable.ValueOrDefault, TimeSpan.FromSeconds(5)).Success);

        LeafApp.WheelOver(leaf.WaitFor("DetailsWhen"), -3);

        Assert.True(Retry.WhileFalse(() => scroll.VerticalScrollPercent.ValueOrDefault > 0, TimeSpan.FromSeconds(5)).Success);
    }

    [Fact]
    public void Upcoming_OnStartDateMorning_ListsDentist()
    {
        using var leaf = Launch();

        Assert.NotNull(leaf.WaitForName("Dentist appointment"));
        Assert.NotNull(leaf.WaitFor("UpcomingList"));
    }

    [Fact]
    public void DetailsToggle_HidesAndPersists()
    {
        using (var leaf = Launch())
        {
            leaf.WaitFor("DetailsToggleButton").AsToggleButton().Toggle();
            Assert.True(Retry.WhileTrue(() => leaf.Exists("UpcomingHeader"), TimeSpan.FromSeconds(5)).Success);
        }

        using var relaunched = Launch();
        relaunched.WaitFor("CalendarRoot");
        Assert.False(relaunched.Exists("UpcomingHeader"));
    }
}
