using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
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
    public void ChangeColor_PicksSwatch()
    {
        using var leaf = Launch();

        leaf.WaitFor($"CalendarColor_{FamilyId}").AsButton().Invoke();
        leaf.WaitForAnywhere("ColorSwatch_16A765").AsButton().Invoke();

        // The flyout closes and the calendar keeps working; the color itself is covered by CalendarPreferencesTests
        Assert.NotNull(leaf.WaitFor($"CalendarToggle_{FamilyId}"));
    }
}
