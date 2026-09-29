using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class TimeGridTests : IDisposable
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
    public void WeekView_OnStartDate_ShowsSyncedEvent()
    {
        using var leaf = Launch();

        Assert.NotNull(leaf.WaitFor("TimeGrid"));
        Assert.NotNull(leaf.WaitFor("DayHeader_2026-10-01"));
        Assert.NotNull(leaf.WaitFor("Event_evt-single_202610011300"));
    }

    [Fact]
    public void Next_ShowsRepeatingSeriesWithoutCanceledInstance()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");

        leaf.WaitFor("NextButton").AsButton().Invoke();

        Assert.NotNull(leaf.WaitFor("Event_evt-weekly_202610051330"));
        Assert.NotNull(leaf.WaitFor("Event_evt-weekly_202610091330"));
        Assert.False(leaf.Exists("Event_evt-weekly_202610071330"));
    }

    [Fact]
    public void AllDayEvent_ShownInAllDayRow()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");

        leaf.WaitFor("NextButton").AsButton().Invoke();
        leaf.WaitFor("NextButton").AsButton().Invoke();

        Assert.NotNull(leaf.WaitFor("AllDay_evt-allday_20261012"));
    }

    [Fact]
    public void DayView_FromMenu_ShowsOneColumn()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");

        leaf.WaitFor("ViewModeButton").AsButton().Invoke();
        leaf.WaitForAnywhere("ViewDay").AsMenuItem().Invoke();

        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("ViewModeButton").Name.Contains("Day", StringComparison.Ordinal), TimeSpan.FromSeconds(5)).Success);
        Assert.NotNull(leaf.WaitFor("DayHeader_2026-10-01"));
    }
}
