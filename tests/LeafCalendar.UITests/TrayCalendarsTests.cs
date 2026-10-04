using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class TrayCalendarsTests : IDisposable
{
    private const string FamilyId = "family123@group.calendar.google.com";

    private readonly FakeGoogleServer _google = new();
    private readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    // 1:50 PM in New York on Oct 1, with a family event at 3 PM the same day
    private LeafApp Launch()
    {
        _google.AddEvent(FamilyId, new JsonObject
        {
            ["id"] = "evt-family-today",
            ["summary"] = "Pick up groceries",
            ["start"] = new JsonObject { ["dateTime"] = "2026-10-01T15:00:00-04:00" },
            ["end"] = new JsonObject { ["dateTime"] = "2026-10-01T15:30:00-04:00" },
        });

        var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01 --now 2026-10-01T13:50:00-04:00");
        leaf.WaitFor("Event_evt-family-today_202610011900");
        return leaf;
    }

    [Fact]
    public void HidingACalendar_RemovesItFromTheFlyout()
    {
        using var leaf = Launch();

        // Listed At First
        leaf.PostTrayMessage(LeafApp.TraySelect);
        Assert.NotNull(leaf.WaitForPopup("FlyoutEvent_evt-family-today_202610011900"));

        // Settings › Tray Has No Calendar Choice Of Its Own (the main window takes focus, which closes the flyout)
        var settings = leaf.OpenSettings("Tray");
        Assert.True(Retry.WhileTrue(() => leaf.PopupExists("FlyoutRoot"), TimeSpan.FromSeconds(5)).Success, "The flyout didn't close.");
        leaf.WaitInSettings("LookaheadComboBox");
        Assert.Null(settings.FindFirstDescendant(cf => cf.ByAutomationId("TrayCalendarList")));

        // Hide The Family Calendar In Leaf (Settings › Calendars), Then Back To The Calendar
        leaf.OpenSettings("Calendars");
        leaf.WaitInSettings($"CalendarVisible_{FamilyId}").AsToggleButton().Toggle();
        leaf.CloseSettings();
        Assert.True(Retry.WhileFalse(() => leaf.WaitFor($"CalendarToggle_{FamilyId}").AsCheckBox().ToggleState == ToggleState.Off, TimeSpan.FromSeconds(5)).Success);

        // Gone From The Flyout; The Primary Calendar's Events Stay (its next meeting shows at the top, not as a row)
        leaf.PostTrayMessage(LeafApp.TraySelect);
        Assert.Equal("Design review", leaf.WaitForPopup("FlyoutNext").Name);
        Assert.False(leaf.PopupExists("FlyoutEvent_evt-family-today_202610011900"));
    }
}
