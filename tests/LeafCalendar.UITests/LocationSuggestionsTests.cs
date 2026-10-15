using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class LocationSuggestionsTests : IDisposable
{
    private const string Refused = "Google didn't accept this key. Check that Places API (New) and billing are turned on for it.";

    private readonly FakeGoogleServer _google = new();
    private readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    private LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");

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

    // The suggestion rows (the list is a popup, so every window is searched)
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

    // Settings › General: turns location suggestions on, types a key and presses Save
    private static void TurnOn(LeafApp leaf, string key)
    {
        leaf.OpenSettings("General");
        var on = leaf.WaitInSettings("PlacesSwitch").AsToggleButton();
        if (on.ToggleState != ToggleState.On)
        {
            on.Toggle();
        }

        leaf.ExpandInSettings("PlacesExpander");
        var box = leaf.WaitInSettings("MapsKeyBox");
        box.Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Press(VirtualKeyShort.DELETE);
        Keyboard.Type(key);
        leaf.WaitInSettings("MapsKeySaveButton").AsButton().Invoke();
    }

    private static string CheckText(LeafApp leaf) =>
        Retry.WhileEmpty(() => leaf.WaitInSettings("MapsKeyCheckText").Name, TimeSpan.FromSeconds(10)).Result ?? "";

    // Location suggestions on with a working key, then (when asked) city suggestions in time zone fields
    private static void TurnOnCities(LeafApp leaf, bool cities)
    {
        TurnOn(leaf, FakeGoogleServer.MapsKey);
        Assert.Equal("Key is set up and active.", CheckText(leaf));
        if (cities)
        {
            var zones = leaf.WaitInSettings("TimeZonePlacesSwitch").AsToggleButton();
            Assert.True(Retry.WhileFalse(() => zones.IsEnabled, TimeSpan.FromSeconds(5)).Success, "The time zone switch stayed off.");
            zones.Toggle();
        }
    }

    // Settings › Time zones: stops following Windows and types into Leaf Calendar's own zone box
    private static void TypeInPrimaryZone(LeafApp leaf, string text)
    {
        leaf.OpenSettings("TimeZones");
        leaf.ExpandInSettings("PrimaryZoneExpander");
        leaf.WaitInSettings("FollowWindowsZoneSwitch").AsToggleButton().Toggle();
        var box = leaf.WaitInSettings("PrimaryZoneBox");
        Assert.True(Retry.WhileFalse(() => box.IsEnabled, TimeSpan.FromSeconds(5)).Success);
        LeafApp.TextIn(box).Focus();
        Thread.Sleep(200);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(text);
    }

    private int CitySearches() => _google.Requests.Count(r => r.Contains("/places/", StringComparison.Ordinal));

    [Fact]
    public void TimeZone_CitiesOn_PickingACityPicksItsZone()
    {
        using var leaf = Launch();
        TurnOnCities(leaf, cities: true);

        // A city in another zone than this PC's, clicked
        TypeInPrimaryZone(leaf, "Lyon");
        WaitForSuggestion(leaf, "Google Maps");
        WaitForSuggestion(leaf, "Lyon, France").Click();

        Assert.True(Retry.WhileFalse(() => leaf.WaitInSettings("PrimaryZoneSummary").Name == "Paris", TimeSpan.FromSeconds(10)).Success, $"The summary reads {leaf.WaitInSettings("PrimaryZoneSummary").Name}.");
    }

    [Fact]
    public void TimeZone_CitiesOff_ShowsOnlyTheBuiltInZones()
    {
        using var leaf = Launch();
        TurnOnCities(leaf, cities: false);
        var before = CitySearches();

        TypeInPrimaryZone(leaf, "Chicago");

        Assert.True(Retry.WhileFalse(() => Suggestions(leaf).Any(s => s.Properties.Name.ValueOrDefault?.Contains("Chicago", StringComparison.Ordinal) == true), TimeSpan.FromSeconds(10)).Success, "Chicago's zone didn't show.");
        Thread.Sleep(1000);
        Assert.Equal(before, CitySearches());
    }

    [Fact]
    public void AddTimeZone_City_AddsItsZoneLabeledWithTheCity()
    {
        using var leaf = Launch();
        TurnOnCities(leaf, cities: true);
        leaf.OpenSettings("TimeZones");

        leaf.WaitInSettings("TimeZoneSearch").Focus();
        Thread.Sleep(200);
        Keyboard.Type("lyo");
        WaitForSuggestion(leaf, "Lyon, France").Click();

        // Paris's zone (never the zone you're in, which can't be added), labeled Lyon
        Assert.True(Retry.WhileFalse(() => leaf.WaitInSettings("ZoneLabelBox_Europe/Paris").AsTextBox().Text == "Lyon", TimeSpan.FromSeconds(10)).Success, "Lyon's zone wasn't added with its label.");
    }

    [Fact]
    public void Settings_GoodKey_SaysItWorks()
    {
        using var leaf = Launch();
        TurnOn(leaf, FakeGoogleServer.MapsKey);

        Assert.Equal("Key is set up and active.", CheckText(leaf));
    }

    [Fact]
    public void Settings_BadKey_SaysGoogleRefusedIt()
    {
        using var leaf = Launch();
        TurnOn(leaf, "wrong-key");

        Assert.Equal(Refused, CheckText(leaf));
    }

    [Fact]
    public void Settings_EnterSavesTheKey()
    {
        using var leaf = Launch();
        leaf.OpenSettings("General");
        leaf.WaitInSettings("PlacesSwitch").AsToggleButton().Toggle();
        var box = leaf.WaitInSettings("MapsKeyBox");
        Assert.True(Retry.WhileFalse(() => box.IsEnabled, TimeSpan.FromSeconds(5)).Success, "The key box stayed off.");
        box.Focus();
        // Enter right after typing, before Save turns on
        Keyboard.Type(FakeGoogleServer.MapsKey);
        Keyboard.Press(VirtualKeyShort.RETURN);

        Assert.Equal("Key is set up and active.", CheckText(leaf));
        Assert.False(leaf.WaitInSettings("MapsKeySaveButton").IsEnabled, "Save stayed on after the key was saved.");
    }

    [Fact]
    public void Settings_SavedKey_ClearShowsAfterReopening()
    {
        using var leaf = Launch();
        TurnOn(leaf, FakeGoogleServer.MapsKey);
        Assert.Equal("Key is set up and active.", CheckText(leaf));

        // Close the row and open it again: the key shows as dots, and its clear button shows without clicking in
        var expander = leaf.WaitInSettings("PlacesExpander").Patterns.ExpandCollapse.Pattern;
        expander.Collapse();
        expander.Expand();

        var clear = Retry.WhileNull(() => leaf.WaitInSettings("MapsKeyBox").FindFirstDescendant(cf => cf.ByAutomationId("PasswordClearButton")), TimeSpan.FromSeconds(5)).Result;
        Assert.NotNull(clear);
        Assert.False(clear.IsOffscreen, "The clear button is hidden.");
    }

    [Fact]
    public void Editor_On_SuggestsPlaces_AndPickFillsLocation()
    {
        using var leaf = Launch();
        TurnOn(leaf, FakeGoogleServer.MapsKey);
        Assert.Equal("Key is set up and active.", CheckText(leaf));
        leaf.CloseSettings();
        CreateNew(leaf, "Coffee");

        // Two steps, so an answer for the older text can't land over the newer one
        Type(leaf, "EditorLocation", "bl");
        Keyboard.Type("ue b");

        WaitForSuggestion(leaf, "Google Maps");
        WaitForSuggestion(leaf, "Blue Bottle Coffee").Click();

        Assert.Equal("Blue Bottle Coffee, Mint Street, San Francisco, CA, USA", Edit(leaf, "EditorLocation").Text);
    }

    [Fact]
    public void Editor_EscWithSuggestionsOpen_OnlyClosesTheList()
    {
        using var leaf = Launch();
        TurnOn(leaf, FakeGoogleServer.MapsKey);
        Assert.Equal("Key is set up and active.", CheckText(leaf));
        leaf.CloseSettings();
        CreateNew(leaf, "Keep me");

        Type(leaf, "EditorLocation", "blue");
        WaitForSuggestion(leaf, "Blue Bottle Coffee");
        Keyboard.Press(VirtualKeyShort.ESCAPE);

        // The list closes; the editor and its title stay
        Assert.True(Retry.WhileTrue(() => Suggestions(leaf).Count > 0, TimeSpan.FromSeconds(5)).Success, "Esc didn't close the suggestion list.");
        Assert.Equal("Keep me", leaf.WaitFor("EditorTitle").AsTextBox().Text);
    }

    [Fact]
    public void Editor_Off_NeverAsksGoogle()
    {
        using var leaf = Launch();
        CreateNew(leaf, "Coffee");

        Type(leaf, "EditorLocation", "blue");

        // Longer than the pause before a search
        Assert.False(Retry.WhileTrue(() => Suggestions(leaf).Count == 0, TimeSpan.FromSeconds(1)).Success, "Suggestions showed while location suggestions are off.");
        Assert.DoesNotContain(_google.Requests, r => r.Contains("/places/", StringComparison.Ordinal));
        Assert.Equal("blue", Edit(leaf, "EditorLocation").Text);
    }
}
