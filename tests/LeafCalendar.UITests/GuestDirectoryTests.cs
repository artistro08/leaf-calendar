using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class GuestDirectoryTests : IDisposable
{
    private const string Room = "c_1888room4west@resource.calendar.google.com";
    private const string Dana = "Dana Director <dana@example.com>";
    private const string Frank = "Frank Often <frank@example.com>";
    private const string UserInfo = "GET /userinfo";

    private readonly FakeGoogleServer _google = new();
    private readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    private LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

    // Launches and waits until the seeded account's Workspace domain was looked up (the room box depends on it)
    private LeafApp LaunchKnowingTheDomain()
    {
        var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");
        Assert.True(Retry.WhileFalse(() => _google.Requests.Any(r => r.StartsWith(UserInfo, StringComparison.Ordinal)), TimeSpan.FromSeconds(20)).Success, "Leaf never looked up the account's domain.");
        return leaf;
    }

    // The text box inside an AutoSuggestBox (type or set text here)
    private static TextBox Edit(LeafApp leaf, string automationId) =>
        Retry.WhileNull(() => leaf.WaitFor(automationId).FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)), TimeSpan.FromSeconds(10)).Result?.AsTextBox()
        ?? throw new InvalidOperationException($"'{automationId}' has no text box inside.");

    // Types the way a person does (only typing searches)
    private static void Type(LeafApp leaf, string automationId, string text)
    {
        var edit = Edit(leaf, automationId);
        edit.Text = "";
        edit.Focus();
        Keyboard.Type(text);
    }

    // The suggestion rows' names (the list is a popup, so every window is searched)
    private static IReadOnlyList<AutomationElement> Suggestions(LeafApp leaf) =>
        [.. leaf.FindAllAnywhere("SuggestionsList").SelectMany(list => list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)))];

    private static AutomationElement WaitForSuggestion(LeafApp leaf, string name) =>
        Retry.WhileNull(() => Suggestions(leaf).FirstOrDefault(item => item.Properties.Name.ValueOrDefault == name), TimeSpan.FromSeconds(10)).Result
        ?? throw new InvalidOperationException($"The suggestion '{name}' didn't show. Shown: {string.Join(" | ", Suggestions(leaf).Select(s => s.Properties.Name.ValueOrDefault))}");

    // C opens a new event in the primary calendar
    private static void CreateNew(LeafApp leaf, string title)
    {
        leaf.Press(VirtualKeyShort.KEY_C);
        leaf.WaitFor("EditorTitle").AsTextBox().Text = title;
    }

    private static void SaveWithCtrlEnter(LeafApp leaf)
    {
        leaf.WaitFor("EditorTitle").AsTextBox().Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.RETURN);
    }

    private string ReadLog()
    {
        var log = Path.Combine(LeafApp.ProfileFolder(_profile), "Logs", "leaf.log");
        Assert.True(File.Exists(log), "The app wrote no log.");
        using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Directory_SuggestsColleagues()
    {
        _google.HostedDomain = "example.com";
        using var leaf = LaunchKnowingTheDomain();
        CreateNew(leaf, "Directory test");

        Type(leaf, "EditorGuestInput", "dana");
        WaitForSuggestion(leaf, Dana).Click();

        Assert.NotNull(leaf.WaitFor("EditorGuest_dana@example.com"));
        Assert.Contains(_google.Requests, r => r.Contains("people:searchDirectoryPeople", StringComparison.Ordinal));

        // The Chip: Name, Then The Address, Then Optional, Each On Its Own Line; Remove Pinned Top Right
        var name = leaf.WaitFor("EditorGuest_dana@example.com").BoundingRectangle;
        var email = leaf.WaitFor("EditorGuestEmail_dana@example.com");
        var optional = leaf.WaitFor("EditorGuestOptional_dana@example.com").BoundingRectangle;
        var remove = leaf.WaitFor("EditorGuestRemove_dana@example.com").BoundingRectangle;
        Assert.Equal("Dana Director", leaf.WaitFor("EditorGuest_dana@example.com").Name);
        Assert.Equal("dana@example.com", email.Name);
        Assert.True(name.Bottom <= email.BoundingRectangle.Top && email.BoundingRectangle.Bottom <= optional.Top, $"Name {name}, email {email.BoundingRectangle}, optional {optional} aren't stacked.");
        Assert.True(remove.Top <= name.Top + 4 * leaf.Scale && remove.Left >= name.Right && remove.Left >= optional.Right, $"Remove ({remove}) isn't in the top right corner.");

        // A typed address has no name: the address alone on line 1
        ConferencingAndContactsTests.GuestEdit(leaf).Text = "sam@example.com";
        leaf.WaitFor("EditorAddGuest").AsButton().Invoke();
        Assert.Equal("sam@example.com", leaf.WaitFor("EditorGuest_sam@example.com").Name);
        Assert.False(leaf.Exists("EditorGuestEmail_sam@example.com"), "An unnamed guest shows its address twice.");
    }

    [Fact]
    public void PeopleYouMeetOften_ComeFirst()
    {
        using var leaf = Launch();
        leaf.WaitFor("Event_evt-single_202610011300");
        CreateNew(leaf, "Often test");

        Type(leaf, "EditorGuestInput", "fra");

        WaitForSuggestion(leaf, Frank);
        Assert.Equal(Frank, Suggestions(leaf)[0].Properties.Name.ValueOrDefault);
    }

    [Fact]
    public void Rooms_WorkspaceOnly_AddedAsResources()
    {
        _google.HostedDomain = "example.com";
        using var leaf = LaunchKnowingTheDomain();
        CreateNew(leaf, "Room test");

        Assert.NotNull(leaf.WaitFor("EditorRoomInput"));
        Type(leaf, "EditorRoomInput", "room");
        WaitForSuggestion(leaf, "Room 4 West").Click();

        var chip = leaf.WaitFor($"EditorGuest_{Room}");
        Assert.True(Retry.WhileFalse(() => chip.Name == "Room 4 West (room)", TimeSpan.FromSeconds(5)).Success, $"The room chip reads \"{chip.Name}\".");
        Assert.False(leaf.Exists($"EditorGuestOptional_{Room}"), "A room has no optional toggle.");
        SaveWithCtrlEnter(leaf);

        var write = _google.WaitForWrite(w => w.Method == "POST" && w.Body.Contains("\"Room test\"", StringComparison.Ordinal));
        var attendee = Assert.Single(JsonNode.Parse(write.Body)!["attendees"]!.AsArray(), a => (string?)a!["email"] == Room)!;
        Assert.True((bool)attendee["resource"]!);
    }

    [Fact]
    public void Rooms_HiddenForPersonalAccounts()
    {
        using var leaf = LaunchKnowingTheDomain();
        CreateNew(leaf, "Personal test");

        Assert.NotNull(leaf.WaitFor("EditorGuestInput"));
        Assert.False(Retry.WhileFalse(() => leaf.Exists("EditorRoomInput"), TimeSpan.FromSeconds(2)).Success, "The room box shows for a personal account.");
    }

    [Fact]
    public void ExistingAccount_LooksUpItsDomainOnce()
    {
        _google.HostedDomain = "example.com";
        using (var leaf = LaunchKnowingTheDomain())
        {
            // The answer was saved, so the room box shows
            CreateNew(leaf, "First launch");
            Assert.NotNull(leaf.WaitFor("EditorRoomInput"));
        }

        using (var leaf = Launch())
        {
            leaf.WaitFor("Event_evt-single_202610011300");
            CreateNew(leaf, "Second launch");
            Assert.NotNull(leaf.WaitFor("EditorRoomInput"));
        }

        Assert.Single(_google.Requests, r => r.StartsWith(UserInfo, StringComparison.Ordinal));
    }

    [Fact]
    public void Suggestions_WriteNoNamesToTheLog()
    {
        _google.HostedDomain = "example.com";
        using (var leaf = LaunchKnowingTheDomain())
        {
            CreateNew(leaf, "Log test");

            // Directory, Then People You Meet Often, Then A Room
            Type(leaf, "EditorGuestInput", "dana");
            WaitForSuggestion(leaf, Dana).Click();
            leaf.WaitFor("EditorGuest_dana@example.com");
            Type(leaf, "EditorGuestInput", "fra");
            WaitForSuggestion(leaf, Frank).Click();
            leaf.WaitFor("EditorGuest_frank@example.com");
            Type(leaf, "EditorRoomInput", "room");
            WaitForSuggestion(leaf, "Room 4 West").Click();
            leaf.WaitFor($"EditorGuest_{Room}");
        }

        // The searches really ran (so a clean log means something)
        Assert.Contains(_google.Requests, r => r.Contains("people:searchDirectoryPeople", StringComparison.Ordinal));

        var log = ReadLog();
        Assert.NotEmpty(log);
        foreach (var text in new[] { "dana", "frank", "Room 4", "[email]" })
        {
            Assert.DoesNotContain(text, log, StringComparison.OrdinalIgnoreCase);
        }
    }
}
