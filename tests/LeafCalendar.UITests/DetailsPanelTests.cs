using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
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
            Assert.True(FlaUI.Core.Tools.Retry.WhileTrue(() => leaf.Exists("UpcomingHeader"), TimeSpan.FromSeconds(5)).Success);
        }

        using var relaunched = Launch();
        relaunched.WaitFor("CalendarRoot");
        Assert.False(relaunched.Exists("UpcomingHeader"));
    }
}
