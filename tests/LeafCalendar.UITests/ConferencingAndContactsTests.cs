using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using LeafCalendar.UITests.Support;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.UITests;

public sealed class ConferencingAndContactsTests : IDisposable
{
    private const string Meeting = "Event_evt-meeting_202610011800";
    private const string Alice = "Alice Example <alice@example.com>";
    private const string Hostile = "Ali<b>ce</b> <ali.hostile@example.com>";

    private readonly FakeGoogleServer _google = new();
    private readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    private LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    /// <summary>The text box inside the guest <c>AutoSuggestBox</c> (type or set text here).</summary>
    internal static TextBox GuestEdit(LeafApp leaf) =>
        Retry.WhileNull(() => leaf.WaitFor("EditorGuestInput").FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)), TimeSpan.FromSeconds(10)).Result?.AsTextBox()
        ?? throw new InvalidOperationException("The guest box has no text box inside.");

    // Opens the editor on the dentist appointment (an event you own)
    private static void EditDentist(LeafApp leaf)
    {
        leaf.WaitFor("Event_evt-single_202610011300").Click();
        leaf.WaitFor("DetailsEditButton").AsButton().Invoke();
        leaf.WaitFor("EditorTitle");
    }

    // Types into the guest box the way a person does (only typing searches)
    private static void TypeGuest(LeafApp leaf, string text)
    {
        var edit = GuestEdit(leaf);
        edit.Text = "";
        edit.Focus();
        Keyboard.Type(text);
    }

    // The suggestion rows' names (the list is a popup, so every window is searched)
    private static IEnumerable<AutomationElement> Suggestions(LeafApp leaf) =>
        leaf.FindAllAnywhere("SuggestionsList").SelectMany(list => list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)));

    private static IReadOnlyList<string> SuggestionNames(LeafApp leaf) => [.. Suggestions(leaf).Select(item => item.Properties.Name.ValueOrDefault ?? "")];

    private static AutomationElement WaitForSuggestion(LeafApp leaf, string name) =>
        Retry.WhileNull(() => Suggestions(leaf).FirstOrDefault(item => item.Properties.Name.ValueOrDefault == name), TimeSpan.FromSeconds(10)).Result
        ?? throw new InvalidOperationException($"The suggestion '{name}' didn't show. Shown: {string.Join(" | ", SuggestionNames(leaf))}");

    // Ctrl+Enter from the title saves
    private static void SaveWithCtrlEnter(LeafApp leaf)
    {
        leaf.WaitFor("EditorTitle").AsTextBox().Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.RETURN);
    }

    // The meeting fixture gets Google's conference data for its Meet link (as a real Meet event has), and guests may
    // change it, so you can edit it
    private void GiveTheMeetingConferenceData() => _google.EditOnGoogle(SeededProfile.Email, "evt-meeting", ev =>
    {
        ev["guestsCanModify"] = true;
        ev["conferenceData"] = new JsonObject
        {
            ["conferenceId"] = "abc-defg-hij",
            ["entryPoints"] = new JsonArray(new JsonObject { ["entryPointType"] = "video", ["uri"] = "https://meet.google.com/abc-defg-hij" }),
        };
    });

    // The element shows inside the editor's scrolling body, above the pinned footer, without scrolling by hand
    private static void AssertVisibleAboveFooter(LeafApp leaf, AutomationElement element)
    {
        var footerTop = leaf.WaitFor("EditorSaveButton").BoundingRectangle.Top;
        var panelTop = leaf.WaitFor("DetailsPanel").BoundingRectangle.Top;
        Assert.True(
            Retry.WhileFalse(() => element.BoundingRectangle is { Height: > 0 } box && box.Top >= panelTop && box.Bottom <= footerTop, TimeSpan.FromSeconds(5)).Success,
            $"'{element.Properties.AutomationId.ValueOrDefault}' at {element.BoundingRectangle} is hidden (footer starts at {footerTop}).");
    }

    private static string NoSpaces(string text) => text.Replace(" ", string.Empty, StringComparison.Ordinal);

    // The steps both suggestion tests share: type "ali" in the dentist's guest box and pick Alice
    private static void PickAlice(LeafApp leaf)
    {
        EditDentist(leaf);
        TypeGuest(leaf, "ali");
        WaitForSuggestion(leaf, Alice).Click();
        leaf.WaitFor("EditorGuestOptional_alice@example.com");
    }

    [Fact]
    public void NewEvent_HasNoConferencingByDefault()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");

        leaf.Press(VirtualKeyShort.KEY_C);
        leaf.WaitFor("EditorTitle").AsTextBox().Text = "Plain";
        Assert.NotNull(leaf.WaitFor("EditorAddConference"));
        Assert.Equal("No video call", leaf.WaitFor("EditorConference").Name);
        Assert.False(leaf.Exists("EditorRemoveConference"));
        SaveWithCtrlEnter(leaf);

        var write = _google.WaitForWrite(w => w.Method == "POST" && w.Body.Contains("\"Plain\"", StringComparison.Ordinal));
        Assert.DoesNotContain("conferenceData", write.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void AddConferencing_SaveMakesAMeetLink()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");

        leaf.Press(VirtualKeyShort.KEY_C);
        leaf.WaitFor("EditorTitle").AsTextBox().Text = "Standup";
        leaf.WaitFor("EditorAddConference").AsButton().Invoke();

        // Remove takes Add's place, and the line says when the link comes
        Assert.NotNull(leaf.WaitFor("EditorRemoveConference"));
        Assert.True(Retry.WhileTrue(() => leaf.Exists("EditorAddConference"), TimeSpan.FromSeconds(5)).Success);
        Assert.Equal("Google Meet link is added when you save", leaf.WaitFor("EditorConference").Name);
        SaveWithCtrlEnter(leaf);

        var write = _google.WaitForWrite(w => w.Method == "POST" && w.Body.Contains("\"Standup\"", StringComparison.Ordinal));
        Assert.Contains("conferenceDataVersion=1", write.Query, StringComparison.Ordinal);
        Assert.NotNull(JsonNode.Parse(write.Body)!["conferenceData"]?["createRequest"]?["requestId"]);

        // Google's Meet link comes back with the sync
        var join = leaf.WaitFor("DetailsJoinButton");
        Assert.True(Retry.WhileFalse(() => join.Name == "Join Google Meet", TimeSpan.FromSeconds(20)).Success, $"The Join button says \"{join.Name}\".");
    }

    [Fact]
    public void RemoveConferencing_PatchesItAway()
    {
        GiveTheMeetingConferenceData();
        using var leaf = Launch();
        leaf.WaitFor(Meeting).Click();
        Assert.NotNull(leaf.WaitFor("DetailsJoinButton"));

        leaf.Press(VirtualKeyShort.KEY_E);
        leaf.WaitFor("EditorRemoveConference").AsButton().Invoke();
        Assert.NotNull(leaf.WaitFor("EditorAddConference"));
        Assert.Equal("No video call", leaf.WaitFor("EditorConference").Name);
        SaveWithCtrlEnter(leaf);

        var write = _google.WaitForWrite(w => w.Method == "PATCH" && w.Path.EndsWith("/events/evt-meeting", StringComparison.Ordinal));
        Assert.Contains("\"conferenceData\":null", NoSpaces(write.Body), StringComparison.Ordinal);
        Assert.Contains("conferenceDataVersion=1", write.Query, StringComparison.Ordinal);
        Assert.True(Retry.WhileTrue(() => leaf.Exists("DetailsJoinButton"), TimeSpan.FromSeconds(20)).Success, "The Join button stayed after the call was removed.");
    }

    [Fact]
    public void ExistingMeet_EditorShowsTheCallAndRemove()
    {
        GiveTheMeetingConferenceData();
        using var leaf = Launch();
        leaf.WaitFor(Meeting).Click();

        leaf.Press(VirtualKeyShort.KEY_E);

        Assert.NotNull(leaf.WaitFor("EditorRemoveConference"));
        Assert.False(leaf.Exists("EditorAddConference"));
        Assert.Equal("Video call: meet.google.com", leaf.WaitFor("EditorConference").Name);
    }

    [Fact]
    public void GuestInput_SuggestsContacts_PickAddsTheGuest()
    {
        using var leaf = Launch();
        EditDentist(leaf);

        TypeGuest(leaf, "ali");

        // Contacts first, then other contacts; Alan doesn't match "ali"
        WaitForSuggestion(leaf, Alice);
        WaitForSuggestion(leaf, "ali.other@example.com");
        Assert.DoesNotContain(SuggestionNames(leaf), n => n.Contains("alan@", StringComparison.Ordinal));

        WaitForSuggestion(leaf, Alice).Click();

        Assert.NotNull(leaf.WaitFor("EditorGuestOptional_alice@example.com"));
        Assert.True(Retry.WhileFalse(() => GuestEdit(leaf).Text.Length == 0, TimeSpan.FromSeconds(5)).Success, $"The guest box still says \"{GuestEdit(leaf).Text}\".");
        Assert.False(leaf.Exists("EditorGuestOptional_Alice Example <alice@example.com>"));
    }

    [Fact]
    public void GuestInput_HostileContactName_ShowsAsPlainText()
    {
        using var leaf = Launch();
        EditDentist(leaf);

        TypeGuest(leaf, "ali");

        var hostile = WaitForSuggestion(leaf, Hostile);
        Assert.Equal(Hostile, hostile.Name);
        Assert.DoesNotContain('‮', hostile.Name);
        Assert.DoesNotContain(hostile.FindAllDescendants(), e => (e.Properties.Name.ValueOrDefault ?? "").Contains('‮', StringComparison.Ordinal));
    }

    [Fact]
    public void GuestInput_WithoutTheContactsGrant_OffersAllow_ThenSuggests()
    {
        _google.ContactsGranted = false;
        using var leaf = Launch();
        EditDentist(leaf);

        TypeGuest(leaf, "ali");

        var allow = leaf.WaitFor("EditorAllowContacts");
        Assert.Empty(SuggestionNames(leaf));
        AssertVisibleAboveFooter(leaf, allow);

        // With the box emptied the link stays, and goes once the consent is done (the fake browser finishes it on its
        // own, as sign-in does in AccountFlowTests)
        GuestEdit(leaf).Text = "";
        allow.AsButton().Invoke();
        Assert.True(Retry.WhileFalse(() => _google.ContactsGranted && !leaf.Exists("EditorAllowContacts"), TimeSpan.FromSeconds(20)).Success, "Allowing contacts didn't finish.");

        TypeGuest(leaf, "ali");
        Assert.NotNull(WaitForSuggestion(leaf, Alice));
    }

    [Fact]
    public void AllowContacts_AnotherGoogleAccount_SavesNothingAndSaysWhy()
    {
        _google.ContactsGranted = false;
        _google.SignInAsOtherUser = true;
        using (var leaf = Launch())
        {
            EditDentist(leaf);
            TypeGuest(leaf, "ali");

            leaf.WaitFor("EditorAllowContacts").AsButton().Invoke();

            var error = leaf.WaitFor("EditorError");
            Assert.True(
                Retry.WhileFalse(() => error.Name == $"You signed in as {FakeGoogleServer.OtherUserEmail}. Sign in as {SeededProfile.Email} to allow suggestions.", TimeSpan.FromSeconds(20)).Success,
                $"The editor says \"{error.Name}\".");
            Assert.True(leaf.Exists("EditorAllowContacts"));
        }

        // Nothing of the other user was saved: no account row, no refresh token
        var database = new LeafDatabase(Path.Combine(LeafApp.ProfileFolder(_profile), "leaf.db"));
        using (var conn = database.Open())
        {
            Assert.Equal([SeededProfile.AccountId], AccountStore.GetAll(conn).Select(a => a.Id));
        }

        SqliteConnection.ClearAllPools();
        Assert.Null(new CredentialLockerTokenStore(_profile).GetRefreshToken(FakeGoogleServer.OtherUserId));
    }

    [Fact]
    public void GuestInput_EnterOnATypedAddress_AddsTheGuest()
    {
        using var leaf = Launch();
        EditDentist(leaf);

        TypeGuest(leaf, "sam@example.com");
        Keyboard.Press(VirtualKeyShort.RETURN);

        Assert.NotNull(leaf.WaitFor("EditorGuestOptional_sam@example.com"));
        Assert.True(Retry.WhileFalse(() => GuestEdit(leaf).Text.Length == 0, TimeSpan.FromSeconds(5)).Success, $"The guest box still says \"{GuestEdit(leaf).Text}\".");
    }

    [Fact]
    public void GuestInput_PeopleApiOff_SaysHowToTurnItOn()
    {
        _google.PeopleApiDisabled = true;
        using var leaf = Launch();
        EditDentist(leaf);

        TypeGuest(leaf, "ali");

        var line = leaf.WaitFor("EditorContactsApiOff");
        Assert.Equal("Turn on the People API in Google Cloud Console to get suggestions.", line.Name);
        AssertVisibleAboveFooter(leaf, line);
        Assert.Empty(SuggestionNames(leaf));
        Assert.False(leaf.Exists("EditorAllowContacts"));
    }

    [Fact]
    public void ContactSearch_WritesNoContactDataToTheLog()
    {
        using (var leaf = Launch())
        {
            PickAlice(leaf);
        }

        // The search really ran (so an empty log check means something)
        Assert.Contains(_google.Requests, r => r.Contains("people:searchContacts", StringComparison.Ordinal) && r.Contains("query=ali", StringComparison.Ordinal));

        var log = Path.Combine(LeafApp.ProfileFolder(_profile), "Logs", "leaf.log");
        Assert.True(File.Exists(log), "The app wrote no log.");
        string text;
        using (var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
        {
            text = reader.ReadToEnd();
        }

        Assert.NotEmpty(text);
        foreach (var contact in new[] { "Alice Example", "alice@example.com", "ali.other@example.com", "ali.hostile@example.com", "Ali<b>ce</b>", "query=ali" })
        {
            Assert.DoesNotContain(contact, text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
