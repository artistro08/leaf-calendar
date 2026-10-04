using LeafCalendar.Core.Events;

namespace LeafCalendar.Tests;

public class OccurrenceOrderTests
{
    private static readonly DateTimeOffset Ten = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    private static CalendarOccurrence At(string id, DateTimeOffset start, int minutes = 60, bool allDay = false) =>
        new("a", "c", id, null, null, start, start.AddMinutes(minutes),
            allDay, id, EventKind.Default, ResponseStatus.Accepted, "#4285F4", null, false, false);

    // Walks Next/Previous from an anchor until nothing is left, returning the event IDs visited
    private static List<string> Walk(IReadOnlyList<CalendarOccurrence> items, CalendarOccurrence anchor, int direction, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Utc;
        var visited = new List<string>();
        var current = anchor;
        while (OccurrenceOrder.Adjacent(items, current.StartIn(zone), current.Key, direction, zone) is { } next)
        {
            visited.Add(next.EventId);
            current = next;
        }

        return visited;
    }

    [Fact]
    public void Adjacent_SameStart_ForwardVisitsAllInKeyOrder()
    {
        // Longer events sort first in a day list, so list order differs from key order
        var a = At("a", Ten, 120);
        var c = At("c", Ten, 90);
        var b = At("b", Ten, 60);
        var later = At("later", Ten.AddHours(1));
        var items = new[] { a, c, b, later };

        Assert.Equal(["b", "c", "later"], Walk(items, a, 1));
    }

    [Fact]
    public void Adjacent_SameStart_BackwardVisitsAllInReverseKeyOrder()
    {
        var earlier = At("earlier", Ten.AddHours(-1));
        var b = At("b", Ten, 30);
        var z = At("z", Ten, 90);
        var c = At("c", Ten, 60);
        var items = new[] { earlier, z, c, b };

        Assert.Equal(["c", "b", "earlier"], Walk(items, z, -1));
    }

    [Fact]
    public void Adjacent_NoAnchor_PicksFirstStrictlyAfterOrBeforeNow()
    {
        var atNow = At("now", Ten);
        var before = At("before", Ten.AddMinutes(-30));
        var after = At("after", Ten.AddMinutes(30));
        var items = new[] { before, atNow, after };

        Assert.Equal("after", OccurrenceOrder.Adjacent(items, Ten, null, 1, TimeZoneInfo.Utc)?.EventId);
        Assert.Equal("before", OccurrenceOrder.Adjacent(items, Ten, null, -1, TimeZoneInfo.Utc)?.EventId);
        Assert.Null(OccurrenceOrder.Adjacent([atNow], Ten, null, 1, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Adjacent_AllDayEvent_IsOrderedAtLocalMidnightOfItsDate()
    {
        // West of UTC an all-day event's UTC midnight falls on the evening before; it still comes after that evening's events
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        var afternoon = At("afternoon", new DateTimeOffset(2026, 10, 11, 15, 0, 0, TimeSpan.FromHours(-7)));
        var dinner = At("dinner", new DateTimeOffset(2026, 10, 11, 19, 0, 0, TimeSpan.FromHours(-7)));
        var holiday = At("holiday", new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero), 24 * 60, allDay: true);
        var items = new[] { holiday, afternoon, dinner };

        Assert.Equal(["dinner", "holiday"], Walk(items, afternoon, 1, zone));
        Assert.Equal(["dinner", "afternoon"], Walk(items, holiday, -1, zone));
    }
}
