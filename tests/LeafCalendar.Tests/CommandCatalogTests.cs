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
        Assert.Contains(CommandCatalog.Match("zoom"), c => c.Id.StartsWith("scale-", StringComparison.Ordinal));

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
}
