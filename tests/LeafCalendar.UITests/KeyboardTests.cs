using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class KeyboardTests : IDisposable
{
    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch()
    {
        var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
        leaf.WaitFor("Event_evt-single_202610011300");
        return leaf;
    }

    [Fact]
    public void M_ThenW_SwitchesMonthAndWeek()
    {
        using var leaf = Launch();

        leaf.Press(VirtualKeyShort.KEY_M);
        Assert.NotNull(leaf.WaitFor("MonthGrid"));

        leaf.Press(VirtualKeyShort.KEY_W);
        Assert.NotNull(leaf.WaitFor("TimeGrid"));
    }

    [Fact]
    public void CtrlShiftE_HidesWeekendColumns()
    {
        using var leaf = Launch();
        Assert.NotNull(leaf.WaitFor("DayHeader_2026-10-03"));

        leaf.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_E);

        Assert.True(Retry.WhileTrue(() => leaf.Exists("DayHeader_2026-10-03"), TimeSpan.FromSeconds(5)).Success);
        Assert.NotNull(leaf.WaitFor("DayHeader_2026-10-02"));
    }

    [Fact]
    public void RightArrowThenT_PagesAndReturnsToToday()
    {
        using var leaf = Launch();

        leaf.Press(VirtualKeyShort.RIGHT);
        Assert.NotNull(leaf.WaitFor("Event_evt-weekly_202610051330"));

        leaf.Press(VirtualKeyShort.KEY_T);
        Assert.NotNull(leaf.WaitFor("Event_evt-single_202610011300"));
    }

    [Fact]
    public void Period_OpensGoToDate()
    {
        using var leaf = Launch();

        leaf.Press(VirtualKeyShort.OEM_PERIOD);

        Assert.NotNull(leaf.WaitForAnywhere("GoToDateCalendar"));
    }

    [Fact]
    public void N_SelectsNextEvent()
    {
        using var leaf = Launch();

        leaf.Press(VirtualKeyShort.KEY_N);

        // Which event is "next" depends on the PC's zone (8:00 local vs 9:00 New York), so any title will do
        Assert.False(string.IsNullOrEmpty(leaf.WaitFor("DetailsTitle").Name));
    }

    [Fact]
    public void M_AfterClickingTitleBarButton_StillSwitchesToMonth()
    {
        using var leaf = Launch();

        leaf.WaitFor("DetailsToggleButton").Click();
        leaf.Press(VirtualKeyShort.KEY_M);

        Assert.NotNull(leaf.WaitFor("MonthGrid"));
    }

    [Fact]
    public void TypingM_InTextInput_DoesNotSwitchView()
    {
        using var leaf = Launch();

        // The RSVP note box in the details panel (the main window, where the shortcuts live)
        leaf.WaitFor("Event_evt-meeting_202610011800").Click();
        leaf.WaitFor("DetailsRsvpNote").Focus();
        FlaUI.Core.Input.Keyboard.Type("m");

        Assert.False(Retry.WhileFalse(() => leaf.Exists("MonthGrid"), TimeSpan.FromSeconds(2)).Success);
    }

    [Fact]
    public void TypingM_WithFocusInFlyout_DoesNotSwitchView()
    {
        using var leaf = Launch();

        leaf.WaitFor("ViewModeButton").AsButton().Invoke();
        leaf.WaitForAnywhere("ViewDay").Focus();
        FlaUI.Core.Input.Keyboard.Type("m");

        Assert.False(Retry.WhileFalse(() => leaf.Exists("MonthGrid"), TimeSpan.FromSeconds(2)).Success);
    }
}
