using LeafCalendar.Core.Events;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class SpanLayoutTests
{
    static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    static DateOnly D(int day) => new(2026, 10, day);

    static CalendarOccurrence AllDay(string id, int startDay, int days)
    {
        var s = new DateTimeOffset(D(startDay).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return new("a", "c", id, null, null, s, s.AddDays(days), true, id, EventKind.Default, ResponseStatus.Accepted, "#4285F4", null, false, false);
    }

    static CalendarOccurrence Timed(string id, int day, int hour) =>
        new("a", "c", id, null, null,
            new DateTimeOffset(2026, 10, day, hour, 0, 0, TimeSpan.FromHours(-4)),
            new DateTimeOffset(2026, 10, day, hour + 1, 0, 0, TimeSpan.FromHours(-4)),
            false, id, EventKind.Default, ResponseStatus.Accepted, "#4285F4", null, false, false);

    static List<DateOnly> Week(int firstDay, bool skipWeekends = false) =>
        [.. Enumerable.Range(0, 7).Select(i => D(firstDay + i)).Where(d => !skipWeekends || !ViewNavigator.IsWeekend(d))];

    [Fact]
    public void Layout_ThreeDayEvent_SpansColumns()
    {
        var b = Assert.Single(SpanLayout.Layout(Week(4), [AllDay("trip", 6, 3)], Zone, includeTimed: false));

        Assert.Equal((2, 3, 0), (b.FirstColumn, b.ColumnSpan, b.Lane));
        Assert.False(b.ContinuesBefore);
        Assert.False(b.ContinuesAfter);
    }

    [Fact]
    public void Layout_EventRunningPastBothEdges_FlagsContinuation()
    {
        var b = Assert.Single(SpanLayout.Layout(Week(4), [AllDay("long", 1, 14)], Zone, includeTimed: false));

        Assert.Equal((0, 7), (b.FirstColumn, b.ColumnSpan));
        Assert.True(b.ContinuesBefore);
        Assert.True(b.ContinuesAfter);
    }

    [Fact]
    public void Layout_OverlappingSpans_StackInLanes()
    {
        var blocks = SpanLayout.Layout(Week(4), [AllDay("a", 4, 3), AllDay("b", 5, 3), AllDay("c", 8, 2)], Zone, includeTimed: false)
            .ToDictionary(b => b.Occurrence.EventId);

        Assert.Equal(0, blocks["a"].Lane);
        Assert.Equal(1, blocks["b"].Lane);
        Assert.Equal(0, blocks["c"].Lane);
    }

    [Fact]
    public void Layout_WeekendsHidden_WeekendOnlyEventDropped()
    {
        var columns = Week(4, skipWeekends: true);

        Assert.Empty(SpanLayout.Layout(columns, [AllDay("sat", 10, 1)], Zone, includeTimed: false));
        Assert.Equal(5, Assert.Single(SpanLayout.Layout(columns, [AllDay("wk", 4, 7)], Zone, includeTimed: false)).ColumnSpan);
    }

    [Fact]
    public void Layout_TimedEvents_OnlyWhenIncluded()
    {
        Assert.Empty(SpanLayout.Layout(Week(4), [Timed("t", 6, 9)], Zone, includeTimed: false));

        var b = Assert.Single(SpanLayout.Layout(Week(4), [Timed("t", 6, 9)], Zone, includeTimed: true));
        Assert.Equal((2, 1), (b.FirstColumn, b.ColumnSpan));
    }

    [Fact]
    public void Layout_SpanningPlacedAboveTimedOnSameDay()
    {
        var blocks = SpanLayout.Layout(Week(4), [Timed("t", 6, 9), AllDay("a", 6, 1)], Zone, includeTimed: true)
            .ToDictionary(b => b.Occurrence.EventId);

        Assert.Equal(0, blocks["a"].Lane);
        Assert.Equal(1, blocks["t"].Lane);
    }
}
