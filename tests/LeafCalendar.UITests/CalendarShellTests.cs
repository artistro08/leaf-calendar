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
    public void AccountsButton_OpensAccounts_BackReturns()
    {
        using var leaf = Launch();

        leaf.WaitFor("AccountsButton").AsButton().Invoke();
        Assert.NotNull(leaf.WaitFor("AddAccountButton"));

        leaf.WaitForName("Back").AsButton().Invoke();
        Assert.NotNull(leaf.WaitFor("CalendarRoot"));
    }

    [Fact]
    public void ThemeDark_Persists()
    {
        using (var leaf = Launch())
        {
            leaf.WaitFor("ViewModeButton").AsButton().Invoke();
            leaf.WaitForName("Theme").AsMenuItem().Expand();
            leaf.WaitForAnywhere("ThemeDark").Patterns.Toggle.Pattern.Toggle();
        }

        using var relaunched = Launch();
        relaunched.WaitFor("ViewModeButton").AsButton().Invoke();
        relaunched.WaitForName("Theme").AsMenuItem().Expand();
        Assert.True(Retry.WhileFalse(() => relaunched.WaitForAnywhere("ThemeDark").Patterns.SelectionItem.PatternOrDefault?.IsSelected.ValueOrDefault == true
            || relaunched.WaitForAnywhere("ThemeDark").AsMenuItem().IsChecked == true, TimeSpan.FromSeconds(10)).Success);
    }
}
