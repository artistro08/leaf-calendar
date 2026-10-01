using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class TraySettingsCalendarsTests : IDisposable
{
    const string FamilyId = "family123@group.calendar.google.com";

    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    // 1:50 PM in New York on Oct 1, with a family event at 3 PM the same day
    LeafApp Launch()
    {
        _google.AddEvent(FamilyId, new JsonObject
        {
            ["id"]      = "evt-family-today",
            ["summary"] = "Pick up groceries",
            ["start"]   = new JsonObject { ["dateTime"] = "2026-10-01T15:00:00-04:00" },
            ["end"]     = new JsonObject { ["dateTime"] = "2026-10-01T15:30:00-04:00" },
        });

        var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01 --now 2026-10-01T13:50:00-04:00");
        leaf.WaitFor("Event_evt-family-today_202610011900");
        return leaf;
    }

    [Fact]
    public void UncheckingACalendar_RemovesItFromTheFlyout()
    {
        using var leaf = Launch();

        // Listed At First
        leaf.PostTrayMessage(LeafApp.TraySelect);
        Assert.NotNull(leaf.WaitForPopup("FlyoutEvent_evt-family-today_202610011900"));

        // Uncheck The Family Calendar (Settings takes focus, which closes the flyout)
        leaf.OpenSettings("Tray");
        Assert.True(Retry.WhileTrue(() => leaf.PopupExists("FlyoutRoot"), TimeSpan.FromSeconds(5)).Success, "The flyout didn't close.");
        var box = leaf.WaitInSettings($"TrayCalendar_{FamilyId}").AsCheckBox();
        Assert.Equal(ToggleState.On, box.ToggleState);
        box.Toggle();
        Assert.True(Retry.WhileFalse(() => leaf.WaitInSettings($"TrayCalendar_{FamilyId}").AsCheckBox().ToggleState == ToggleState.Off, TimeSpan.FromSeconds(5)).Success);

        // Gone From The Flyout; The Primary Calendar's Events Stay
        leaf.PostTrayMessage(LeafApp.TraySelect);
        Assert.NotNull(leaf.WaitForPopup("FlyoutEvent_evt-meeting_202610011800"));
        Assert.False(leaf.PopupExists("FlyoutEvent_evt-family-today_202610011900"));
    }
}
