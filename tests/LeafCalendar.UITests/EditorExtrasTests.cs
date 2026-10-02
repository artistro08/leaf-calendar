using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.Core.Settings;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class EditorExtrasTests : IDisposable
{
    const string Dentist = "Event_evt-single_202610011300";
    const string Meeting = "Event_evt-meeting_202610011800";

    readonly FakeGoogleServer _google = new();
    string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    // A profile with settings saved before launch (the default one is deleted first)
    void Seed(LeafSettings settings)
    {
        LeafApp.DeleteProfile(_profile);
        _profile = SeededProfile.Create(settings);
    }

    // Launches and waits until the account's Workspace domain was looked up (the event type depends on it)
    LeafApp LaunchKnowingTheDomain()
    {
        var leaf = Launch();
        leaf.WaitFor(Dentist);
        Assert.True(Retry.WhileFalse(() => _google.Requests.Any(r => r.StartsWith("GET /userinfo", StringComparison.Ordinal)), TimeSpan.FromSeconds(20)).Success, "Leaf never looked up the account's domain.");
        return leaf;
    }

    static void EditDentist(LeafApp leaf)
    {
        leaf.WaitFor(Dentist).Click();
        leaf.Press(VirtualKeyShort.KEY_E);
        leaf.WaitFor("EditorTitle");
    }

    static void SaveWithCtrlEnter(LeafApp leaf)
    {
        leaf.WaitFor("EditorTitle").AsTextBox().Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.RETURN);
    }

    FakeWrite DentistPatch() =>
        _google.WaitForWrite(w => w.Method == "PATCH" && w.Path.EndsWith("/events/evt-single", StringComparison.Ordinal));

    static TextBox ZoneEdit(LeafApp leaf) =>
        Retry.WhileNull(() => leaf.WaitFor("EditorTimeZoneBox").FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)), TimeSpan.FromSeconds(10)).Result?.AsTextBox()
        ?? throw new InvalidOperationException("The time zone box has no text box inside.");

    [Fact]
    public void ShowAsFree_PatchesTransparencyOnly()
    {
        using var leaf = Launch();
        EditDentist(leaf);

        leaf.WaitFor("EditorShowAs").AsComboBox().Select("Show me as free");
        SaveWithCtrlEnter(leaf);

        Assert.Equal("""{"transparency":"transparent"}""", JsonNode.Parse(DentistPatch().Body)!.ToJsonString());
    }

    [Fact]
    public void VisibilityPrivate_Patches()
    {
        using var leaf = Launch();
        EditDentist(leaf);

        leaf.WaitFor("EditorVisibility").AsComboBox().Select("Private");
        SaveWithCtrlEnter(leaf);

        Assert.Equal("private", (string?)JsonNode.Parse(DentistPatch().Body)!["visibility"]);
    }

    [Fact]
    public void TimeZoneTokyo_KeepsTheClock_AndShowsYourTime()
    {
        // Leaf shows Eastern time, the dentist's own zone, whatever this PC's zone is
        Seed(new LeafSettings { PrimaryTimeZone = "America/New_York" });
        using var leaf = Launch();
        EditDentist(leaf);
        Assert.False(leaf.Exists("EditorLocalTimeText"), "The 'In your time' line shows for an event in the zone on screen.");

        // Pick Tokyo (click into the dropdown's text the way a person does, replace it, and press Enter)
        var edit = ZoneEdit(leaf);
        edit.Click();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type("Tokyo");
        Keyboard.Type(VirtualKeyShort.RETURN);
        Assert.True(Retry.WhileFalse(() => edit.Text == "(UTC+09:00) Tokyo (JST)", TimeSpan.FromSeconds(5)).Success, $"The box reads \"{edit.Text}\".");

        // The Clock Stays 9-10, Now In Tokyo
        var local = leaf.WaitFor("EditorLocalTimeText");
        Assert.True(Retry.WhileFalse(() => local.Name == "In your time: Wed, Sep 30, 8:00 PM–9:00 PM", TimeSpan.FromSeconds(5)).Success, $"The line reads \"{local.Name}\".");
        SaveWithCtrlEnter(leaf);

        var start = JsonNode.Parse(DentistPatch().Body)!["start"]!;
        Assert.Equal("2026-10-01T09:00:00+09:00", (string?)start["dateTime"]);
        Assert.Equal("Asia/Tokyo", (string?)start["timeZone"]);
    }

    [Fact]
    public void EThenZ_FocusesTheTimeZoneBox()
    {
        using var leaf = Launch();
        leaf.WaitFor(Dentist).Click();

        leaf.Press(VirtualKeyShort.KEY_E);
        Keyboard.Type(VirtualKeyShort.KEY_Z);

        Assert.True(
            Retry.WhileFalse(() => ZoneEdit(leaf).Properties.HasKeyboardFocus.ValueOrDefault || leaf.WaitFor("EditorTimeZoneBox").Properties.HasKeyboardFocus.ValueOrDefault, TimeSpan.FromSeconds(5)).Success,
            "E then Z didn't focus the time zone box.");
    }

    [Fact]
    public void EventType_HiddenForPersonalAccounts()
    {
        using var leaf = LaunchKnowingTheDomain();

        leaf.Press(VirtualKeyShort.KEY_C);
        leaf.WaitFor("EditorTitle");

        Assert.False(Retry.WhileFalse(() => leaf.Exists("EditorEventType"), TimeSpan.FromSeconds(2)).Success, "The event type shows for a personal account.");
    }

    [Fact]
    public void FocusTime_OnAWorkspacePrimaryCalendar()
    {
        _google.HostedDomain = "example.com";
        using var leaf = LaunchKnowingTheDomain();

        leaf.Press(VirtualKeyShort.KEY_C);
        leaf.WaitFor("EditorEventType").AsComboBox().Select("Focus time");

        var title = leaf.WaitFor("EditorTitle").AsTextBox();
        Assert.True(Retry.WhileFalse(() => title.Text == "Focus time", TimeSpan.FromSeconds(5)).Success, $"The title reads \"{title.Text}\".");
        Assert.True(Retry.WhileTrue(() => leaf.Exists("EditorGuestInput"), TimeSpan.FromSeconds(5)).Success, "Focus time still offers guests.");
        Assert.False(leaf.WaitFor("EditorShowAs").IsEnabled, "Focus time can be shown as free.");
        SaveWithCtrlEnter(leaf);

        var body = JsonNode.Parse(_google.WaitForWrite(w => w.Method == "POST" && w.Body.Contains("\"Focus time\"", StringComparison.Ordinal)).Body)!.AsObject();
        Assert.Equal("focusTime", (string?)body["eventType"]);
        Assert.NotNull(body["focusTimeProperties"]);
        Assert.False(body.ContainsKey("attendees"));
        Assert.False(body.ContainsKey("transparency"));
    }

    [Fact]
    public void MeetByDefault_NewEventsGetMeet()
    {
        Seed(new LeafSettings { MeetByDefaultAccounts = [SeededProfile.AccountId] });
        using var leaf = Launch();
        leaf.WaitFor(Dentist);

        leaf.Press(VirtualKeyShort.KEY_C);
        leaf.WaitFor("EditorTitle").AsTextBox().Text = "x";
        Assert.NotNull(leaf.WaitFor("EditorRemoveConference"));
        SaveWithCtrlEnter(leaf);

        var body = JsonNode.Parse(_google.WaitForWrite(w => w.Method == "POST" && w.Body.Contains("\"x\"", StringComparison.Ordinal)).Body)!;
        Assert.NotNull(body["conferenceData"]?["createRequest"]);
    }

    [Fact]
    public void TimeZoneDropdown_ListsEveryZone_AndTakesOnlyAListedOne()
    {
        Seed(new LeafSettings { PrimaryTimeZone = "America/New_York" });
        using var leaf = Launch();
        EditDentist(leaf);
        var combo  = leaf.WaitFor("EditorTimeZoneBox").AsComboBox();
        var edit   = ZoneEdit(leaf);
        var before = edit.Text;
        Assert.StartsWith("(UTC-0", before, StringComparison.Ordinal);

        // The stock dropdown lists every zone, not a short list of cities
        combo.Expand();
        Assert.True(Retry.WhileFalse(() => combo.Items.Length > 100, TimeSpan.FromSeconds(10)).Success, $"The dropdown listed {combo.Items.Length} zones.");
        combo.Collapse();

        // Typed text that matches no zone changes nothing, on Enter or when focus leaves; the editor stays open
        edit.Click();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type("Nowhere at all");
        Keyboard.Type(VirtualKeyShort.RETURN);
        Assert.True(Retry.WhileFalse(() => ZoneEdit(leaf).Text == before, TimeSpan.FromSeconds(5)).Success, $"Enter left \"{ZoneEdit(leaf).Text}\".");
        Assert.True(leaf.Exists("EditorTitle"), "Enter in the zone box closed the editor.");

        edit.Click();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type("Nowhere at all");
        leaf.WaitFor("EditorTitle").AsTextBox().Focus();
        Assert.True(Retry.WhileFalse(() => ZoneEdit(leaf).Text == before, TimeSpan.FromSeconds(5)).Success, $"The box kept \"{ZoneEdit(leaf).Text}\".");
        Assert.False(leaf.Exists("EditorLocalTimeText"), "Typed text picked a zone.");
    }

    [Fact]
    public void BingMaps_OpensBing()
    {
        Seed(new LeafSettings { MapProvider = MapProvider.Bing });
        using var leaf = Launch();
        leaf.WaitFor(Meeting).Click();

        var button = leaf.WaitFor("DetailsMapsLink");
        Assert.True(Retry.WhileFalse(() => button.Name == "Open in Bing Maps", TimeSpan.FromSeconds(5)).Success, $"The button reads \"{button.Name}\".");
        button.AsButton().Invoke();

        Assert.True(
            Retry.WhileFalse(() => LeafApp.LaunchedLinks(_profile).Contains("https://www.bing.com/maps?q=Room%204"), TimeSpan.FromSeconds(10)).Success,
            $"Launched: {string.Join(" | ", LeafApp.LaunchedLinks(_profile))}");
    }
}
