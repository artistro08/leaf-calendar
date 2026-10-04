using LeafCalendar.Core.Search;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public sealed class CommandCatalogTests
{
    [Fact]
    public void All_IdsAreUnique() =>
        Assert.Equal(CommandCatalog.All.Count, CommandCatalog.All.Select(c => c.Id).Distinct().Count());

    [Fact]
    public void All_HasEverySpecAction()
    {
        // Spec 6.6: create, jump to date, switch view, join, overlay, Meet with, time travel, share availability,
        // toggle settings, open settings, show shortcuts
        var ids = CommandCatalog.All.Select(c => c.Id).ToHashSet();
        string[] expected = ["create-event", "go-to-date", "view-day", "view-week", "view-month", "join", "overlay",
            "meet-with", "time-travel", "share", "toggle-weekends", "settings", "shortcuts"];

        Assert.All(expected, id => Assert.Contains(id, ids));
    }

    [Fact]
    public void Match_Empty_ReturnsTheDefaults()
    {
        var match = CommandCatalog.Match("");

        Assert.Equal(CommandCatalog.Defaults, match);
        Assert.Equal("create-event", match[0].Id);
    }

    [Theory]
    [InlineData("share", "share")]
    [InlineData("time", "time-travel")]
    [InlineData("SETTINGS", "settings")]
    [InlineData("month", "view-month")]
    public void Match_TitleStart_RanksFirst(string query, string firstId) =>
        Assert.Equal(firstId, CommandCatalog.Match(query)[0].Id);

    [Fact]
    public void Match_Keywords_Count() =>
        Assert.Contains(CommandCatalog.Match("cheat"), c => c.Id == "shortcuts");

    [Fact]
    public void Match_EveryWordMustHit() => Assert.Empty(CommandCatalog.Match("share zzz"));

    [Fact]
    public void Match_HostileText_DoesNotThrow() => Assert.Empty(CommandCatalog.Match("‮%_\\" + new string('q', 5000)));

    [Fact]
    public void Items_WithACommand_RunThroughIt()
    {
        var days = CommandCatalog.All.Single(c => c.Id == "view-days-3");

        Assert.Equal(CalendarCommand.ShareAvailability, CommandCatalog.All.Single(c => c.Id == "share").Command);
        Assert.Equal((CalendarCommand.Days, 3), (days.Command, days.Days));
    }

    [Theory]
    [InlineData("sett", true)]
    [InlineData("Jump to", true)]
    [InlineData("create ev", true)]
    [InlineData("add", false)]      // a keyword of Create event, not its title
    [InlineData("standup", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void NamesAnAction_TitleWordsOnly(string? query, bool expected) => Assert.Equal(expected, CommandCatalog.NamesAnAction(query));

    [Theory]
    [InlineData(false, "theme-dark", "Use dark theme")]
    [InlineData(true, "theme-light", "Use light theme")]
    public void Match_Theme_OffersOnlyTheOtherTheme(bool dark, string id, string title)
    {
        var match = CommandCatalog.Match("theme", dark);

        Assert.Equal(id, Assert.Single(match, c => c.Command == CalendarCommand.ToggleTheme).Id);
        Assert.Equal(title, match.Single(c => c.Id == id).Title);
    }

    // "dark" in dark theme finds the dark theme marked in use (it runs nothing), never a switch to light, and never
    // nothing at all, where Enter would create an event titled "dark"
    [Fact]
    public void Match_DarkWhileDark_ShowsItInUse_NeverOffersLight()
    {
        var match = CommandCatalog.Match("dark", dark: true);

        Assert.DoesNotContain(match, c => c.Command == CalendarCommand.ToggleTheme);
        var inUse = Assert.Single(match);
        Assert.Equal(("theme-dark", "Use dark theme (in use)", "", CalendarCommand.None), (inUse.Id, inUse.Title, inUse.Keys, inUse.Command));
        Assert.True(CommandCatalog.NamesAnAction("dark", dark: true));
        Assert.Equal("theme-dark", CommandCatalog.Match("dark", dark: false)[0].Id);
        Assert.Equal("Use light theme (in use)", Assert.Single(CommandCatalog.Match("light", dark: false)).Title);
    }

    // "theme" leads with the theme that switches; the one in use comes after it
    [Fact]
    public void Match_Theme_PutsTheSwitchBeforeTheThemeInUse()
    {
        var themes = CommandCatalog.Match("theme", dark: true).Where(c => c.Id.StartsWith("theme-", StringComparison.Ordinal)).Select(c => c.Id).ToList();

        Assert.Equal(["theme-light", "theme-dark"], themes);
    }

    [Fact]
    public void Match_Settings_ListsEveryPage()
    {
        var ids = CommandCatalog.Match("settings").Select(c => c.Id).ToList();

        Assert.Equal(CommandCatalog.All.Count(c => c.Id.StartsWith("settings", StringComparison.Ordinal)), ids.Count);
        Assert.Contains("settings-about", ids);
    }

    [Fact]
    public void SettingsPages_UseAPathArrow() =>
        Assert.All(CommandCatalog.All.Where(c => c.Id.StartsWith("settings-", StringComparison.Ordinal)), c => Assert.StartsWith("Settings › ", c.Title, StringComparison.Ordinal));

    [Fact]
    public void Ellipsis_OnlyOnActionsThatOpenMoreUi()
    {
        string[] opensMore = ["create-event", "go-to-date", "overlay", "meet-with", "time-travel", "share", "settings"];

        Assert.All(CommandCatalog.All, c => Assert.Equal(opensMore.Contains(c.Id), c.Title.EndsWith('…')));
    }
}
