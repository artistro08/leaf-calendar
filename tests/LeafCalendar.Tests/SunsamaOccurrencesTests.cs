using LeafCalendar.Core.Events;
using LeafCalendar.Core.Sunsama;

namespace LeafCalendar.Tests;

public sealed class SunsamaOccurrencesTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 15, 50, 0, TimeSpan.Zero);

    private static SunsamaBlock Block(string task, SunsamaBlockKind kind, int minutes = 60, int? tracked = 30, DateTimeOffset? start = null) =>
        new(new DateOnly(2026, 10, 16), task, "Home Page Redesign", "Illuminate Digital", start ?? Start, (start ?? Start).AddMinutes(minutes), 120, tracked, false, kind,
            kind == SunsamaBlockKind.Matched ? "acct" : null, kind == SunsamaBlockKind.Matched ? "cal" : null, kind == SunsamaBlockKind.Matched ? "ev" : null);

    [Fact]
    public void ToOccurrence_Projected_LooksLikeAnUnansweredInvite()
    {
        var o = SunsamaOccurrences.ToOccurrence(Block("t1", SunsamaBlockKind.Projected));

        Assert.True(SunsamaOccurrences.IsSunsama(o));
        Assert.Equal(ResponseStatus.NeedsAction, o.SelfResponse);
        Assert.Equal("Home Page Redesign", o.Title);
        Assert.Equal(SunsamaOccurrences.Color, o.CalendarColor);
        Assert.Equal(SunsamaOccurrences.ProjectedCalendarId, o.CalendarId);
        Assert.Equal((Start, Start.AddHours(1)), (o.Start, o.End));
        Assert.False(o.IsAllDay);
    }

    [Fact]
    public void ToOccurrence_PickedColor_IsTheCalendarColor() =>
        Assert.Equal("#A47AE2", SunsamaOccurrences.ToOccurrence(Block("t1", SunsamaBlockKind.Projected), "#A47AE2").CalendarColor);

    [Fact]
    public void Build_PickedColor_PaintsEveryBlock() =>
        Assert.All(
            SunsamaOccurrences.Build([Block("t1", SunsamaBlockKind.Projected), Block("t2", SunsamaBlockKind.Timebox)], "#A47AE2").Blocks,
            o => Assert.Equal("#A47AE2", o.CalendarColor));

    [Fact]
    public void ToOccurrence_Timebox_LooksLikeARegularEvent()
    {
        var o = SunsamaOccurrences.ToOccurrence(Block("t1", SunsamaBlockKind.Timebox));

        Assert.Equal(ResponseStatus.Accepted, o.SelfResponse);
        Assert.Equal(SunsamaOccurrences.TimeboxCalendarId, o.CalendarId);
    }

    [Fact]
    public void ToOccurrence_TwoBlocksOfOneTask_HaveDifferentKeys() =>
        Assert.NotEqual(
            SunsamaOccurrences.ToOccurrence(Block("t1", SunsamaBlockKind.Projected)).Key,
            SunsamaOccurrences.ToOccurrence(Block("t1", SunsamaBlockKind.Projected, start: Start.AddHours(2))).Key);

    [Fact]
    public void Build_DrawsProjectedAndTimeboxButNotMatched()
    {
        var (blocks, byKey) = SunsamaOccurrences.Build([Block("t1", SunsamaBlockKind.Projected), Block("t2", SunsamaBlockKind.Timebox), Block("t3", SunsamaBlockKind.Matched)]);

        Assert.Equal(2, blocks.Count);
        Assert.Equal(0.5, byKey[blocks[0].Key].Fill);
        Assert.Equal("t3", byKey[SunsamaOccurrences.MatchKey("acct", "cal", "ev", Start)].Block.TaskId);
    }

    [Fact]
    public void Build_SplitTask_FillsInTimeOrder()
    {
        var (blocks, byKey) = SunsamaOccurrences.Build([
            Block("t1", SunsamaBlockKind.Projected, 40, 84),
            Block("t1", SunsamaBlockKind.Projected, 80, 84, Start.AddMinutes(100)),
        ]);

        Assert.Equal(1.0, byKey[blocks[0].Key].Fill, 3);
        Assert.Equal(0.55, byKey[blocks[1].Key].Fill, 3);
    }

    [Fact]
    public void Build_MatchedFill_IsAgainstTheEventLength() =>
        Assert.Equal(0.5, SunsamaOccurrences.Build([Block("t3", SunsamaBlockKind.Matched, 60, 30)]).ByKey[SunsamaOccurrences.MatchKey("acct", "cal", "ev", Start)].Fill);

    [Fact]
    public void Build_NotStartedYet_HasNoTrackedTime()
    {
        var (blocks, byKey) = SunsamaOccurrences.Build([Block("t1", SunsamaBlockKind.Projected), Block("t3", SunsamaBlockKind.Matched)], now: Start.AddMinutes(-1));

        Assert.Equal(0.0, byKey[blocks[0].Key].Fill);
        Assert.Equal(0.0, byKey[SunsamaOccurrences.MatchKey("acct", "cal", "ev", Start)].Fill);
    }

    [Fact]
    public void Build_MatchedOverLength_IsFull() =>
        Assert.Equal(1.0, SunsamaOccurrences.Build([Block("t3", SunsamaBlockKind.Matched, 60, 500)]).ByKey[SunsamaOccurrences.MatchKey("acct", "cal", "ev", Start)].Fill);

    [Fact]
    public void MatchKey_IsTheGoogleOccurrencesKey() =>
        Assert.Equal(
            new CalendarOccurrence("acct", "cal", "ev", null, null, Start, Start.AddHours(1), false, "x", EventKind.Default, ResponseStatus.Accepted, "#000000", null, false, false).Key,
            SunsamaOccurrences.MatchKey("acct", "cal", "ev", Start));

    [Fact]
    public void IsSunsama_GoogleOccurrence_IsFalse() =>
        Assert.False(SunsamaOccurrences.IsSunsama(new CalendarOccurrence("acct", "cal", "ev", null, null, Start, Start.AddHours(1), false, "x", EventKind.Default, ResponseStatus.Accepted, "#000000", null, false, false)));
}
