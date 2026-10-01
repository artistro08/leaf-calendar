using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public sealed class ShortcutCatalogTests
{
    [Fact]
    public void Sections_InSpecOrder() =>
        Assert.Equal(["Navigation", "App", "Events", "Selection", "Scheduling and people"], ShortcutCatalog.Sections);

    [Fact]
    public void EveryMappedCommand_IsListed()
    {
        // One sample chord per command ShortcutMap can return
        (string Key, bool Ctrl, bool Shift, bool Alt)[] chords =
        [
            ("T", false, false, false), ("Left", false, false, false), ("Right", false, false, false), ("N", false, false, false),
            ("B", false, false, false), ("190", false, false, false), ("D", false, false, false), ("W", false, false, false),
            ("M", false, false, false), ("Number3", false, false, false), ("Z", false, false, false), ("K", true, false, false),
            ("F", true, false, false), ("191", false, true, false), ("188", true, false, false), ("Z", true, false, false),
            ("Left", false, false, true), ("L", true, true, false), ("187", true, false, false), ("E", true, true, false),
            ("D", true, true, false), ("C", false, false, false), ("Delete", false, false, false), ("Delete", true, true, false),
            ("J", true, false, false), ("V", false, false, false), ("X", false, false, false), ("A", true, false, false),
            ("C", true, false, false), ("S", false, false, false), ("P", false, false, false), ("F", false, false, false),
        ];
        var listed = ShortcutCatalog.Rows.Select(r => r.Command).ToHashSet();

        Assert.All(chords, c => Assert.Contains(ShortcutMap.Resolve(c.Key, c.Ctrl, c.Shift, c.Alt).Command, listed));
        Assert.Contains(CalendarCommand.EditTimeZone, listed);
        Assert.Contains(CalendarCommand.ParticipantOverlay, listed);
    }

    [Fact]
    public void EveryCommand_IsListed_OrSharesItsPairsRow()
    {
        // Commands the spec lists on their pair's row ("Ctrl+= / Ctrl+- / Ctrl+0", "E then Y / N / M", ...), and the two that aren't shortcuts
        CalendarCommand[] shared =
        [
            CalendarCommand.ZoomOut, CalendarCommand.ZoomReset, CalendarCommand.NavigateForward, CalendarCommand.RsvpNo,
            CalendarCommand.RsvpMaybe, CalendarCommand.Cut, CalendarCommand.Paste, CalendarCommand.None, CalendarCommand.SequenceStarted,
        ];
        var listed = ShortcutCatalog.Rows.Select(r => r.Command).ToHashSet();

        Assert.All(Enum.GetValues<CalendarCommand>().Except(shared), c => Assert.Contains(c, listed));
    }

    [Fact]
    public void Rows_LeaveOutBoxSelect() => Assert.DoesNotContain(ShortcutCatalog.Rows, r => r.Action == "Box select");

    [Theory]
    [InlineData("rsvp", 1)]
    [InlineData("ctrl+k", 1)]
    [InlineData("time zone", 2)] // "Z" time travel, "E then Z"
    public void Filter_MatchesKeysAndActions(string query, int count) => Assert.Equal(count, ShortcutCatalog.Filter(query).Count);

    [Fact]
    public void Filter_Empty_IsEverything() => Assert.Equal(ShortcutCatalog.Rows.Count, ShortcutCatalog.Filter(" ").Count);
}
