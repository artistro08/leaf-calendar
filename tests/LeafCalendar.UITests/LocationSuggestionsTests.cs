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

    // Settings › General: turns location suggestions on and types a key; Tab moves focus off the box, which saves it
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
        Keyboard.Press(VirtualKeyShort.TAB);
    }

    private static string CheckText(LeafApp leaf) =>
        Retry.WhileEmpty(() => leaf.WaitInSettings("MapsKeyCheckText").Name, TimeSpan.FromSeconds(10)).Result ?? "";

    [Fact]
    public void Settings_GoodKey_SaysItWorks()
    {
        using var leaf = Launch();
        TurnOn(leaf, FakeGoogleServer.MapsKey);

        Assert.Equal("The key works.", CheckText(leaf));
    }

    [Fact]
    public void Settings_BadKey_SaysGoogleRefusedIt()
    {
        using var leaf = Launch();
        TurnOn(leaf, "wrong-key");

        Assert.Equal(Refused, CheckText(leaf));
    }

    [Fact]
    public void Editor_On_SuggestsPlaces_AndPickFillsLocation()
    {
        using var leaf = Launch();
        TurnOn(leaf, FakeGoogleServer.MapsKey);
        Assert.Equal("The key works.", CheckText(leaf));
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
