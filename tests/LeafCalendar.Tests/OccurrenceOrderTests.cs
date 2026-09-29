using LeafCalendar.Core.Events;

namespace LeafCalendar.Tests;

public class OccurrenceOrderTests
{
    static readonly DateTimeOffset Ten = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    static CalendarOccurrence At(string id, DateTimeOffset start, int minutes = 60) =>
        new("a", "c", id, null, null, start, start.AddMinutes(minutes),
            false, id, EventKind.Default, ResponseStatus.Accepted, "#4285F4", null, false, false);

    // Walks Next/Previous from an anchor until nothing is left, returning the event IDs visited
    static List<string> Walk(IReadOnlyList<CalendarOccurrence> items, CalendarOccurrence anchor, int direction)
    {
        var visited = new List<string>();
        var current = anchor;
        while (OccurrenceOrder.Adjacent(items, current.Start, current.Key, direction) is { } next)
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
        var atNow  = At("now", Ten);
        var before = At("before", Ten.AddMinutes(-30));
        var after  = At("after", Ten.AddMinutes(30));
        var items  = new[] { before, atNow, after };

        Assert.Equal("after", OccurrenceOrder.Adjacent(items, Ten, null, 1)?.EventId);
        Assert.Equal("before", OccurrenceOrder.Adjacent(items, Ten, null, -1)?.EventId);
        Assert.Null(OccurrenceOrder.Adjacent([atNow], Ten, null, 1));
    }
}
