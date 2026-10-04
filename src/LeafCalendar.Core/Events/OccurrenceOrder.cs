namespace LeafCalendar.Core.Events;

/// <summary>
/// Picks the next or previous event when stepping through events with the keyboard.
/// </summary>
/// <remarks>
/// Events are placed in one total order: start instant, then <see cref="CalendarOccurrence.Key"/>
/// (ordinal). Events that share a start time are stepped through one by one in key order, whatever
/// order the day list holds them in.
/// </remarks>
public static class OccurrenceOrder
{
    /// <summary>
    /// The event right after (<paramref name="direction"/> &gt; 0) or right before (&lt; 0) the position
    /// (<paramref name="fromStart"/>, <paramref name="fromKey"/>), or null when there is none.
    /// </summary>
    /// <param name="candidates">Events to choose from, in any order.</param>
    /// <param name="fromStart">The anchor's start, or "now" when nothing is selected.</param>
    /// <param name="fromKey">The anchor's key, or null when nothing is selected (events starting exactly at <paramref name="fromStart"/> are then skipped).</param>
    /// <param name="direction">Positive for next, negative for previous.</param>
    /// <param name="zone">The zone whose clock orders the events, so an all-day event sits at local midnight of its date.</param>
    /// <returns>The adjacent event, or null.</returns>
    public static CalendarOccurrence? Adjacent(IEnumerable<CalendarOccurrence> candidates, DateTimeOffset fromStart, string? fromKey, int direction, TimeZoneInfo zone)
    {
        CalendarOccurrence? best = null;
        foreach (var o in candidates)
        {
            // Strictly past the anchor in the chosen direction
            if (Math.Sign(Compare(o, fromStart, fromKey, zone)) != Math.Sign(direction))
            {
                continue;
            }

            // Closest to the anchor wins
            if (best is null || Math.Sign(Compare(o, best.StartIn(zone), best.Key, zone)) == -Math.Sign(direction))
            {
                best = o;
            }
        }

        return best;
    }

    // Orders by start on the clock in the zone (an all-day event at its local midnight), then key; a null key ties with every event at that start
    private static int Compare(CalendarOccurrence o, DateTimeOffset start, string? key, TimeZoneInfo zone)
    {
        var byStart = o.StartIn(zone).UtcTicks.CompareTo(start.UtcTicks);
        if (byStart != 0 || key is null)
        {
            return byStart;
        }

        return string.CompareOrdinal(o.Key, key);
    }
}
