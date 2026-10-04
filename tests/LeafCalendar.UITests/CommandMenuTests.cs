using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class CommandMenuTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly FakeGoogleServer _google = new();
    private readonly string _profile = SeededProfile.Create();

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    private LeafApp Launch()
    {
        var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
        leaf.WaitFor("Event_evt-single_202610011300");
        return leaf;
    }

    // Ctrl+K, then waits for the box to have focus
    private static AutomationElement OpenMenu(LeafApp leaf)
    {
        leaf.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_K);
        var box = leaf.WaitForAnywhere("CommandSearchBox");
        Assert.True(Retry.WhileFalse(() => box.Properties.HasKeyboardFocus.ValueOrDefault, Wait).Success, "The search box didn't get focus.");
        return box;
    }

    // An element's automation ID, or "" for parts that don't support one (the list's scroll bar pieces)
    private static string Id(AutomationElement e) => e.Properties.AutomationId.ValueOrDefault ?? "";

    // The result rows' automation IDs, top to bottom
    private static List<string> RowIds(LeafApp leaf) =>
        [.. leaf.WaitForAnywhere("CommandResults")
            .FindAllDescendants()
            .Where(e => Id(e).StartsWith("CommandResult_", StringComparison.Ordinal) || Id(e).StartsWith("SearchResult_", StringComparison.Ordinal))
            .OrderBy(e => e.BoundingRectangle.Top)
            .Select(Id)];

    // Waits until the first row is the expected one (typing settles for 120 ms before the search runs)
    private static void WaitForFirstRow(LeafApp leaf, string id) =>
        Assert.True(Retry.WhileFalse(() => RowIds(leaf) is [var first, ..] && first == id, Wait).Success, $"The first row isn't {id}; rows: {string.Join(", ", RowIds(leaf))}.");

    private static string PeriodTitle(LeafApp leaf) => leaf.WaitFor("PeriodTitle").Name;

    // The menu sits centered over the window, and nothing in it scrolls sideways (it was 2 DIPs wider than its presenter)
    [Fact]
    public void Menu_IsCenteredOverTheWindow_WithNoHorizontalScrollBar()
    {
        using var leaf = Launch();
        OpenMenu(leaf);
        Keyboard.Type("e");
        var menu = leaf.WaitForAnywhere("CommandMenu");
        Thread.Sleep(500);

        var box = menu.BoundingRectangle;
        var client = leaf.ClientBounds;
        var offset = box.Left + box.Width / 2.0 - (client.Left + client.Width / 2.0);
        Assert.True(Math.Abs(offset) <= 2, $"The menu's center is {offset} px off the window's.");

        // Vertically, The Menu Sits In The Window's Middle Band: Not Under The Title Bar, And Inside The Window
        var vertical = box.Top + box.Height / 2.0 - (client.Top + client.Height / 2.0);
        Assert.True(Math.Abs(vertical) <= client.Height * 0.2 && box.Top >= client.Top && box.Bottom <= client.Bottom, $"The menu {box} isn't centered in the window {client}.");

        // Every Scroll Bar Around The Menu Is Vertical (the presenter's, the results')
        var popup = menu.Parent!.Parent ?? menu;
        var bars = popup.FindAllDescendants(cf => cf.ByControlType(ControlType.ScrollBar)).Where(b => !b.IsOffscreen && b.BoundingRectangle.Width > b.BoundingRectangle.Height).ToList();
        Assert.Empty(bars.Select(b => b.BoundingRectangle.ToString()));
    }

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
        var next = leaf.WaitFor("MiniMonthNext");
        var sidebar = leaf.WaitFor("Sidebar");
        LeafApp.WaitUntilStill(sidebar);
        LeafApp.WaitUntilStill(next);

        // At The Right End Of The Sidebar's Title Bar Row, Centered Over Next Month
        var searchBox = search.BoundingRectangle;
        var nextBox = next.BoundingRectangle;
        var sidebarBox = sidebar.BoundingRectangle;
        var client = leaf.ClientBounds;
        Assert.True(Math.Abs((searchBox.Left + searchBox.Right) / 2.0 - (nextBox.Left + nextBox.Right) / 2.0) <= 1,
            $"Search {searchBox} isn't centered over Next month {nextBox}.");
        // Inside The Right Half Of The 264 DIP Sidebar Pane (the 32 DIP icon is 4 wider than Next, so it can pass the
        // sidebar content's 5 DIP right padding by 2, but never the pane)
        var paneLeft = leaf.WaitFor("CalendarRoot").BoundingRectangle.Left;
        var paneRight = paneLeft + 264 * leaf.Scale;
        Assert.True(searchBox.Left >= paneLeft + 132 * leaf.Scale && searchBox.Right <= paneRight + 1,
            $"Search {searchBox} isn't in the right half of the sidebar pane ({paneLeft} to {paneRight}; sidebar {sidebarBox}).");
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
        var title = leaf.WaitFor("PeriodTitle");
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
    public void Dim_ShowsWhileOpen_GoneAfterEscAndAfterAPick()
    {
        using var leaf = Launch();
        Assert.False(leaf.Exists("CommandMenuDim"));

        // Open, Then Esc
        OpenMenu(leaf);
        Assert.NotNull(leaf.WaitFor("CommandMenuDim"));
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Assert.True(Retry.WhileTrue(() => leaf.Exists("CommandMenuDim"), Wait).Success, "The dim stayed after Esc.");

        // Open, Then Pick A Row ("today" lists Go to today once: no date row repeats it)
        OpenMenu(leaf);
        Assert.NotNull(leaf.WaitFor("CommandMenuDim"));
        Keyboard.Type("today");
        WaitForFirstRow(leaf, "CommandResult_today");
        Assert.DoesNotContain("CommandResult_date", RowIds(leaf));
        Keyboard.Type(VirtualKeyShort.RETURN);
        Assert.True(Retry.WhileTrue(() => leaf.Exists("CommandMenuDim"), Wait).Success, "The dim stayed after a pick.");
    }

    [Fact]
    public void Headers_AreSkippedByTheArrowKeys()
    {
        using var leaf = Launch();
        OpenMenu(leaf);

        // A Date, Then Actions ("2 days" is both): Down From The Date Lands On The First Action, Not The "Actions" Header
        Keyboard.Type("2 days");
        WaitForFirstRow(leaf, "CommandResult_date");
        Assert.True(leaf.ExistsAnywhere("CommandHeader_actions"));
        Keyboard.Type(VirtualKeyShort.DOWN);

        var results = leaf.WaitForAnywhere("CommandResults");
        var second = RowIds(leaf)[1];
        Assert.True(Retry.WhileFalse(() => results.Patterns.Selection.Pattern.Selection.Value is [var selected]
            && selected.FindFirstDescendant(cf => cf.ByAutomationId(second)) is not null, Wait).Success, $"Down didn't select {second}.");
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
        Assert.Equal(["CommandResult_create-event-titled"], RowIds(leaf));

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(VirtualKeyShort.BACK);
        Keyboard.Type("dentist");

        WaitForFirstRow(leaf, "SearchResult_evt-single");
    }

    // Nothing matches: the empty state says so, and offers the typed words as a new event's title
    [Fact]
    public void NothingMatches_OffersToCreateTheEvent()
    {
        using var leaf = Launch();

        OpenMenu(leaf);
        Keyboard.Type("zzzzqq");
        WaitForFirstRow(leaf, "CommandResult_create-event-titled");
        Assert.Equal("No events, actions, or dates match.", leaf.WaitForAnywhere("CommandEmpty").Name);
        Assert.Equal("Create event “zzzzqq”", leaf.WaitForAnywhere("CommandResult_create-event-titled").Name);
        Assert.Equal("Run", leaf.WaitForAnywhere("CommandFooterVerb").Name);
        Keyboard.Type(VirtualKeyShort.RETURN);

        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("EditorTitle").AsTextBox().Text == "zzzzqq", Wait).Success, "The new event isn't titled zzzzqq.");
    }

    // The theme row offers the theme that isn't showing, so running it and typing "theme" again offers the other one
    [Fact]
    public void ThemeRow_OffersTheThemeNotShowing()
    {
        using var leaf = Launch();

        OpenMenu(leaf);
        Keyboard.Type("theme");
        Assert.True(Retry.WhileFalse(() => RowIds(leaf) is [var id, ..] && id.StartsWith("CommandResult_theme-", StringComparison.Ordinal), Wait).Success);
        var first = RowIds(leaf)[0];
        Assert.Single(RowIds(leaf), id => id.StartsWith("CommandResult_theme-", StringComparison.Ordinal));
        Keyboard.Type(VirtualKeyShort.RETURN);

        OpenMenu(leaf);
        Keyboard.Type("theme");
        WaitForFirstRow(leaf, first == "CommandResult_theme-dark" ? "CommandResult_theme-light" : "CommandResult_theme-dark");
    }

    // "settings" lists every page (none cut by a cap), each as a path
    [Fact]
    public void Settings_ListsEveryPage()
    {
        using var leaf = Launch();

        OpenMenu(leaf);
        Keyboard.Type("settings");
        WaitForFirstRow(leaf, "CommandResult_settings");

        Assert.Equal("Settings › About", leaf.WaitForAnywhere("CommandResult_settings-about").Name);
        Assert.Equal(9, RowIds(leaf).Count(id => id.StartsWith("CommandResult_settings", StringComparison.Ordinal)));
    }

    // Jump to date keeps its footer before a date is typed, and says when what's typed isn't a date
    [Fact]
    public void JumpToDate_KeepsItsFooter_AndSaysWhenItCantReadADate()
    {
        using var leaf = Launch();

        leaf.Press(VirtualKeyShort.OEM_PERIOD);
        Assert.NotNull(leaf.WaitForAnywhere("CommandModeChip"));
        Assert.Equal("Go", leaf.WaitForAnywhere("CommandFooterVerb").Name);

        Keyboard.Type("zzzzqq");
        Assert.True(Retry.WhileFalse(() => leaf.ExistsAnywhere("CommandEmpty") && leaf.WaitForAnywhere("CommandEmpty").Name == "Leaf can't read that as a date.", Wait).Success);
        Assert.Empty(RowIds(leaf));
        Assert.Equal("Go", leaf.WaitForAnywhere("CommandFooterVerb").Name);
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
