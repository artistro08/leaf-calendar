using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class CalendarShellTests : IDisposable
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
    public void Launch_SignedIn_ShowsCalendarWithToolbarAndTitle()
    {
        using var leaf = Launch();

        Assert.NotNull(leaf.WaitFor("CalendarRoot"));
        Assert.NotNull(leaf.WaitFor("TodayButton"));
        Assert.Contains("2026", leaf.WaitFor("PeriodTitle").Name, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewMenu_HasOnlyViewChoices()
    {
        using var leaf = Launch();

        leaf.WaitFor("ViewModeButton").AsButton().Invoke();

        Assert.NotNull(leaf.WaitForAnywhere("ViewMonth"));
        Assert.NotNull(leaf.WaitForAnywhere("ViewDay"));
        foreach (var setting in new[] { "ToggleWeekends", "ToggleDeclined", "ToggleWeekNumbers", "Toggle24Hour", "ThemeDark", "WeekStartMonday" })
        {
            Assert.False(leaf.ExistsAnywhere(setting), $"{setting} is still in the view menu.");
        }
    }

    [Fact]
    public void ViewMenu_Month_SwitchesAndLabelsTheButton()
    {
        using var leaf = Launch();

        leaf.WaitFor("ViewModeButton").AsButton().Invoke();
        leaf.WaitForAnywhere("ViewMonth").AsMenuItem().Invoke();

        Assert.NotNull(leaf.WaitFor("MonthGrid"));
        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("ViewModeButton").Name == "Month", TimeSpan.FromSeconds(5)).Success);
    }
}
