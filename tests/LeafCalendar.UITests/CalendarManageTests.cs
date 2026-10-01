using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using LeafCalendar.Core.Settings;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class CalendarManageTests : IDisposable
{
    const string Family = "family123@group.calendar.google.com";
    const string Toggle = $"CalendarToggle_{Family}";

    readonly FakeGoogleServer _google = new();
    string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch(string extra = "") => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01 {extra}");

    // Right-click a sidebar calendar and pick a menu item
    static void SidebarMenu(LeafApp leaf, string item)
    {
        leaf.WaitFor(Toggle).RightClick();
        leaf.WaitForAnywhere(item).AsMenuItem().Invoke();
    }

    // The rename dialog: type a name and press Rename
    static void Rename(LeafApp leaf, string name)
    {
        SidebarMenu(leaf, "CalendarMenu_Rename");
        leaf.WaitForAnywhere("RenameCalendarBox").AsTextBox().Text = name;
        leaf.WaitForAnywhere("PrimaryButton").AsButton().Invoke();
    }

    // The latest calendar-list PATCH for the family calendar, as JSON
    JsonNode FamilyPatch(Func<string, bool> match) =>
        JsonNode.Parse(_google.WaitForWrite(w => w.Method == "PATCH" && w.Path.Contains("calendarList/family123", StringComparison.Ordinal) && match(w.Body)).Body)!;

    static void WaitForName(AutomationElement element, string name) =>
        Assert.True(Retry.WhileFalse(() => element.Name == name, TimeSpan.FromSeconds(15)).Success, $"The row reads \"{element.Name}\", not \"{name}\".");

    [Fact]
    public void Rename_FromTheSidebar_SavesToGoogle()
    {
        using var leaf = Launch();

        Rename(leaf, "Kids");

        Assert.Equal("Kids", (string?)FamilyPatch(_ => true)["summaryOverride"]);
        WaitForName(leaf.WaitFor(Toggle), "Kids");
    }

    [Fact]
    public void Rename_Blank_ResetsToGooglesName()
    {
        using var leaf = Launch();
        Rename(leaf, "Kids");
        WaitForName(leaf.WaitFor(Toggle), "Kids");

        Rename(leaf, "");

        var patch = FamilyPatch(body => body.Contains("null", StringComparison.Ordinal)).AsObject();
        Assert.True(patch.ContainsKey("summaryOverride"));
        Assert.Null(patch["summaryOverride"]);
        WaitForName(leaf.WaitFor(Toggle), "Family");
    }

    [Fact]
    public void Rename_Offline_KeepsTheName()
    {
        using var leaf = Launch();
        leaf.WaitFor(Toggle);
        _google.Offline = true;
        Assert.True(Retry.WhileFalse(() => leaf.Exists("OfflineIndicator"), TimeSpan.FromSeconds(60)).Success, "Leaf never noticed it was offline.");

        Rename(leaf, "X");

        Assert.True(Retry.WhileFalse(() => leaf.AnyTextContains("Connect to the internet to rename a calendar."), TimeSpan.FromSeconds(10)).Success, "No offline message.");
        Assert.Equal("Family", leaf.WaitFor(Toggle).Name);
        Assert.DoesNotContain(_google.Writes, w => w.Path.Contains("calendarList", StringComparison.Ordinal));
    }

    [Fact]
    public void ShowUpcoming_ForOneCalendar()
    {
        _google.AddEvent(Family, new JsonObject
        {
            ["id"]      = "evt-family-soccer",
            ["status"]  = "confirmed",
            ["summary"] = "Soccer practice",
            ["start"]   = new JsonObject { ["dateTime"] = "2026-10-02T10:00:00-04:00", ["timeZone"] = "America/New_York" },
            ["end"]     = new JsonObject { ["dateTime"] = "2026-10-02T11:00:00-04:00", ["timeZone"] = "America/New_York" },
        });
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");

        SidebarMenu(leaf, "CalendarMenu_Upcoming");

        var header = leaf.WaitFor("UpcomingHeader");
        WaitForName(header, "Upcoming in Family");
        var list = leaf.WaitFor("UpcomingList");
        Assert.True(Retry.WhileFalse(() => list.FindFirstDescendant(cf => cf.ByName("Soccer practice")) is not null, TimeSpan.FromSeconds(10)).Success, "The family event isn't listed.");
        Assert.NotNull(list.FindFirstDescendant(cf => cf.ByName("School play")));
        Assert.Null(list.FindFirstDescendant(cf => cf.ByName("Design review")));

        leaf.WaitFor("UpcomingShowAll").AsButton().Invoke();
        WaitForName(header, "Upcoming");
    }

    [Fact]
    public void ChangeColor_OpensSettingsCalendars()
    {
        using var leaf = Launch();

        SidebarMenu(leaf, "CalendarMenu_Color");

        Assert.NotNull(leaf.WaitInSettings($"CalendarColor_{Family}"));
    }

    [Fact]
    public void Settings_MoveDown_ReordersTheSidebar()
    {
        using var leaf = Launch();
        leaf.WaitFor(Toggle);
        leaf.OpenSettings("Calendars");

        leaf.WaitInSettings($"CalendarMore_{SeededProfile.Email}").AsButton().Invoke();
        leaf.WaitForAnywhere("CalendarMenu_MoveDown").AsMenuItem().Invoke();

        Assert.True(
            Retry.WhileFalse(() => leaf.WaitFor(Toggle).BoundingRectangle.Top < leaf.WaitFor($"CalendarToggle_{SeededProfile.Email}").BoundingRectangle.Top, TimeSpan.FromSeconds(10)).Success,
            "Family isn't listed above the primary calendar.");
    }

    [Fact]
    public void Settings_DefaultReminders_PatchesGoogle()
    {
        using var leaf = Launch();
        leaf.WaitFor(Toggle);
        leaf.OpenSettings("Calendars");

        leaf.WaitInSettings($"CalendarMore_{Family}").AsButton().Invoke();
        leaf.WaitForAnywhere("CalendarMenu_Reminders").AsMenuItem().Invoke();
        leaf.WaitForAnywhere("AddReminderButton").AsButton().Invoke();
        leaf.WaitForAnywhere("ReminderRow_0").AsComboBox().Select("30 min");
        leaf.WaitForAnywhere("PrimaryButton").AsButton().Invoke();

        Assert.Equal("""{"defaultReminders":[{"method":"popup","minutes":30}]}""", FamilyPatch(body => body.Contains("defaultReminders", StringComparison.Ordinal)).ToJsonString());
    }

    [Fact]
    public void Reveal_AllDayEvent_LandsOnItsOwnDay()
    {
        // Day view, and the tray flyout lists two weeks with all-day events (the company holiday is Oct 12)
        LeafApp.DeleteProfile(_profile);
        _profile = SeededProfile.Create(new LeafSettings { ViewMode = CalendarViewMode.Day, FlyoutDays = 14, FlyoutAllDay = true });
        using var leaf = Launch("--now 2026-10-01T08:00:00-04:00");
        leaf.WaitFor("Event_evt-single_202610011300");

        leaf.PostTrayMessage(LeafApp.TraySelect);
        leaf.WaitForPopup("FlyoutEvent_evt-allday_202610120000").AsButton().Invoke();

        // West of UTC the holiday's UTC midnight is Oct 11 local time; the view still lands on Oct 12
        Assert.NotNull(leaf.WaitFor("DayHeader_2026-10-12"));
        // (neighboring days are built offscreen, so the check is the day the grid shows first)
        Assert.True(Retry.WhileFalse(() => (leaf.WaitFor("TimeGrid").Properties.ItemStatus.ValueOrDefault ?? "").StartsWith("first=2026-10-12;", StringComparison.Ordinal), TimeSpan.FromSeconds(5)).Success, "The day view doesn't show Oct 12 first.");
    }
}
