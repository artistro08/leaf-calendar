using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class CommandMenuTests : IDisposable
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp Launch()
    {
        var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
        leaf.WaitFor("Event_evt-single_202610011300");
        return leaf;
    }

    // Ctrl+K, then waits for the box to have focus
    static AutomationElement OpenMenu(LeafApp leaf)
    {
        leaf.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_K);
        var box = leaf.WaitForAnywhere("CommandSearchBox");
        Assert.True(Retry.WhileFalse(() => box.Properties.HasKeyboardFocus.ValueOrDefault, Wait).Success, "The search box didn't get focus.");
        return box;
    }

    // The result rows' automation IDs, top to bottom
    static List<string> RowIds(LeafApp leaf) =>
        [.. leaf.WaitForAnywhere("CommandResults")
            .FindAllDescendants()
            .Where(e => e.AutomationId.StartsWith("CommandResult_", StringComparison.Ordinal) || e.AutomationId.StartsWith("SearchResult_", StringComparison.Ordinal))
            .OrderBy(e => e.BoundingRectangle.Top)
            .Select(e => e.AutomationId)];

    // Waits until the first row is the expected one (typing settles for 120 ms before the search runs)
    static void WaitForFirstRow(LeafApp leaf, string id) =>
        Assert.True(Retry.WhileFalse(() => RowIds(leaf) is [var first, ..] && first == id, Wait).Success, $"The first row isn't {id}; rows: {string.Join(", ", RowIds(leaf))}.");

    static string PeriodTitle(LeafApp leaf) => leaf.WaitFor("PeriodTitle").Name;

    [Fact]
    public void CtrlK_OpensTheMenu_WithFocusInTheSearchBox()
    {
        using var leaf = Launch();

        OpenMenu(leaf);

        Assert.NotNull(leaf.WaitForAnywhere("CommandMenu"));
        WaitForFirstRow(leaf, "CommandResult_create-event");
    }

    [Theory]
    [InlineData("Ctrl+F")]
    [InlineData("/")]
    public void CtrlFAndSlash_OpenTheSameMenu(string keys)
    {
        using var leaf = Launch();

        if (keys == "/")
        {
            leaf.Press(VirtualKeyShort.OEM_2);
        }
        else
        {
            leaf.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_F);
        }

        var box = leaf.WaitForAnywhere("CommandSearchBox");
        Assert.True(Retry.WhileFalse(() => box.Properties.HasKeyboardFocus.ValueOrDefault, Wait).Success);

        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Assert.True(Retry.WhileTrue(() => leaf.ExistsAnywhere("CommandMenu"), Wait).Success, "Esc didn't close the menu.");
    }

    [Fact]
    public void SearchIcon_IsCenteredOverTheNextMonthButton_AndOpensTheMenu()
    {
        using var leaf = Launch();
        var search = leaf.WaitFor("SearchButton");
        var next   = leaf.WaitFor("MiniMonthNext");
        LeafApp.WaitUntilStill(next);

        var searchBox = search.BoundingRectangle;
        var nextBox   = next.BoundingRectangle;
        var client    = leaf.ClientBounds;
        Assert.True(Math.Abs((searchBox.Left + searchBox.Right) / 2.0 - (nextBox.Left + nextBox.Right) / 2.0) <= 1,
            $"Search {searchBox} isn't centered over Next month {nextBox}.");
        Assert.True(Math.Abs((searchBox.Top + searchBox.Bottom) / 2.0 - client.Top - 24 * leaf.Scale) <= 3,
            $"Search {searchBox} isn't centered in the 48 DIP title bar (client top {client.Top}).");

        search.Click();

        var box = leaf.WaitForAnywhere("CommandSearchBox");
        Assert.True(Retry.WhileFalse(() => box.Properties.HasKeyboardFocus.ValueOrDefault, Wait).Success);
    }

    [Fact]
    public void SearchIcon_SidebarClosed_StaysVisibleAfterTheTitle()
    {
        using var leaf = Launch();

        leaf.WaitFor("AppTitleBar").FindFirstDescendant(cf => cf.ByAutomationId("PART_PaneToggleButton"))!.AsButton().Invoke();
        Thread.Sleep(1000);

        var search = leaf.WaitFor("SearchButton");
        var title  = leaf.WaitFor("PeriodTitle");
        Assert.False(search.IsOffscreen);
        Assert.True(search.IsEnabled);
        Assert.True(search.BoundingRectangle.Right <= title.BoundingRectangle.Left,
            $"Search {search.BoundingRectangle} overlaps the period title {title.BoundingRectangle}.");
    }

    [Fact]
    public void TypeATitle_EnterOpensDetails_WithoutMovingTheCalendar()
    {
        using var leaf = Launch();

        // Somewhere Else First
        OpenMenu(leaf);
        Keyboard.Type("2026-11-02");
        WaitForFirstRow(leaf, "CommandResult_date");
        Keyboard.Type(VirtualKeyShort.RETURN);
        Assert.True(Retry.WhileFalse(() => PeriodTitle(leaf).Contains("November", StringComparison.Ordinal), Wait).Success);

        // Enter On The Result
        OpenMenu(leaf);
        Keyboard.Type("dentist");
        WaitForFirstRow(leaf, "SearchResult_evt-single");
        Keyboard.Type(VirtualKeyShort.RETURN);

        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("DetailsTitle").Name == "Dentist appointment", Wait).Success);
        Assert.Contains("November", PeriodTitle(leaf), StringComparison.Ordinal);
    }

    [Fact]
    public void AltEnter_JumpsToTheEvent_AndBackReturns()
    {
        using var leaf = Launch();
        Assert.NotNull(leaf.WaitFor("DayHeader_2026-10-01"));

        OpenMenu(leaf);
        Keyboard.Type("holiday");
        WaitForFirstRow(leaf, "SearchResult_evt-allday");
        Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, VirtualKeyShort.RETURN);

        // The Week Of Oct 12, With Back In The Title Bar
        Assert.NotNull(leaf.WaitFor("DayHeader_2026-10-12"));
        var back = Retry.WhileNull(() => leaf.WaitFor("AppTitleBar").FindFirstDescendant(cf => cf.ByName("Back")), Wait).Result;
        Assert.NotNull(back);
        Assert.False(back.IsOffscreen);

        back.Click();

        Assert.NotNull(leaf.WaitFor("DayHeader_2026-10-01"));
        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("AppTitleBar").FindFirstDescendant(cf => cf.ByName("Back")) is not { IsOffscreen: false }, Wait).Success, "Back is still showing.");
    }

    [Fact]
    public void TypeADate_GoesThere()
    {
        using var leaf = Launch();

        OpenMenu(leaf);
        Keyboard.Type("oct 20");
        WaitForFirstRow(leaf, "CommandResult_date");
        Assert.Equal("Go to Tue, Oct 20", leaf.WaitForAnywhere("CommandResult_date").Name);
        Keyboard.Type(VirtualKeyShort.RETURN);

        Assert.NotNull(leaf.WaitFor("DayHeader_2026-10-20"));
    }

    [Fact]
    public void TypeAnAction_RunsIt()
    {
        using var leaf = Launch();

        OpenMenu(leaf);
        Keyboard.Type("month");
        WaitForFirstRow(leaf, "CommandResult_view-month");
        Keyboard.Type(VirtualKeyShort.RETURN);

        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("ViewModeButton").Name == "Month", Wait).Success);
    }

    [Fact]
    public void ToggleFromTheMenu_ChangesTheSetting()
    {
        using var leaf = Launch();

        OpenMenu(leaf);
        Keyboard.Type("week numbers");
        WaitForFirstRow(leaf, "CommandResult_toggle-week-numbers");
        Keyboard.Type(VirtualKeyShort.RETURN);

        leaf.OpenSettings("General");
        Assert.Equal(ToggleState.On, leaf.WaitInSettings("WeekNumbersSwitch").AsToggleButton().ToggleState);
    }

    [Fact]
    public void ArrowKeys_MoveTheSelection_FocusStaysInTheBox()
    {
        using var leaf = Launch();
        var box = OpenMenu(leaf);
        WaitForFirstRow(leaf, "CommandResult_create-event");

        Keyboard.Type(VirtualKeyShort.DOWN);
        Keyboard.Type(VirtualKeyShort.DOWN);

        // The Third Default (Share Availability) Is Selected
        var results = leaf.WaitForAnywhere("CommandResults");
        Assert.True(Retry.WhileFalse(() => results.Patterns.Selection.Pattern.Selection.Value is [var selected]
            && selected.FindFirstDescendant(cf => cf.ByAutomationId("CommandResult_share")) is not null, Wait).Success, "Down twice didn't select the third row.");
        Assert.True(box.Properties.HasKeyboardFocus.ValueOrDefault);
    }

    [Fact]
    public void HostileQuery_ShowsNothing_AndLeafKeepsRunning()
    {
        using var leaf = Launch();

        OpenMenu(leaf);
        Keyboard.Type("%_\\'‮" + new string('x', 300));
        Assert.NotNull(leaf.WaitForAnywhere("CommandEmpty"));
        Assert.Empty(RowIds(leaf));

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(VirtualKeyShort.BACK);
        Keyboard.Type("dentist");

        WaitForFirstRow(leaf, "SearchResult_evt-single");
    }

    [Fact]
    public void Searching_WritesNoSearchTextToTheLog()
    {
        using (var leaf = Launch())
        {
            OpenMenu(leaf);
            Keyboard.Type("dentist");
            WaitForFirstRow(leaf, "SearchResult_evt-single");
        }

        var log = File.ReadAllText(Path.Combine(LeafApp.ProfileFolder(_profile), "Logs", "leaf.log"));
        Assert.DoesNotContain("dentist", log, StringComparison.OrdinalIgnoreCase);
    }
}
