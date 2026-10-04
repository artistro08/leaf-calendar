using LeafCalendar.Core.Events;

namespace LeafCalendar.Core.Views;

/// <summary>A horizontal bar across visible day columns, in a lane (row) of the all-day area or month cell.</summary>
public sealed record SpanBlock(CalendarOccurrence Occurrence, int FirstColumn, int ColumnSpan, int Lane, bool ContinuesBefore, bool ContinuesAfter);

/// <summary>
/// Lays out events that run across day columns: all-day and 24-hour-plus events in the time grid's
/// all-day row, and every event in the month view.
/// </summary>
/// <remarks>
/// Columns can skip days (hidden weekends). An event is clipped to the columns it touches and flags
/// when it continues beyond them. Lanes are packed greedily: spanning events first, then wider ones,
/// then earlier ones.
/// </remarks>
public static class SpanLayout
{
    /// <summary>True for all-day events and events lasting 24 hours or more.</summary>
    public static bool IsSpanning(CalendarOccurrence occurrence) =>
        occurrence.IsAllDay || occurrence.End - occurrence.Start >= TimeSpan.FromHours(24);

    /// <summary>The local dates an event touches (inclusive).</summary>
    public static (DateOnly First, DateOnly Last) CoveredDates(CalendarOccurrence occurrence, TimeZoneInfo zone)
    {
        var (first, last) = occurrence.IsAllDay
            ? (occurrence.AllDayStart, occurrence.AllDayEnd.AddDays(-1))
            : (LocalDate(occurrence.Start, zone), LocalDate(occurrence.End.AddTicks(-1), zone));

        return (first, last < first ? first : last);
    }

    /// <summary>Lays out events over <paramref name="columns"/> (ascending dates).</summary>
    public static IReadOnlyList<SpanBlock> Layout(IReadOnlyList<DateOnly> columns, IEnumerable<CalendarOccurrence> occurrences, TimeZoneInfo zone, bool includeTimed)
    {
        if (columns.Count == 0)
        {
            return [];
        }

        // Clip To Columns
        var candidates = new List<(CalendarOccurrence Occurrence, int First, int Last, bool Before, bool After)>();
        foreach (var occurrence in occurrences.DistinctBy(o => o.Key))
        {
            if (!includeTimed && !IsSpanning(occurrence))
            {
                continue;
            }

            var (first, last) = CoveredDates(occurrence, zone);
            var firstColumn = FirstIndex(columns, d => d >= first && d <= last);
            var lastColumn = LastIndex(columns, d => d >= first && d <= last);
            if (firstColumn < 0)
            {
                continue;
            }

            candidates.Add((occurrence, firstColumn, lastColumn, first < columns[firstColumn], last > columns[lastColumn]));
        }

        // Pack Lanes
        var laneEnds = new List<int>();
        var result = new List<SpanBlock>(candidates.Count);
        foreach (var c in candidates
            .OrderBy(c => c.First)
            .ThenBy(c => IsSpanning(c.Occurrence) ? 0 : 1)
            .ThenByDescending(c => c.Last - c.First)
            .ThenBy(c => c.Occurrence.Start))
        {
            var lane = laneEnds.FindIndex(end => end < c.First);
            if (lane < 0)
            {
                lane = laneEnds.Count;
                laneEnds.Add(c.Last);
            }
            else
            {
                laneEnds[lane] = c.Last;
            }

            result.Add(new SpanBlock(c.Occurrence, c.First, c.Last - c.First + 1, lane, c.Before, c.After));
        }

        return result;
    }

    private static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    private static int FirstIndex(IReadOnlyList<DateOnly> columns, Func<DateOnly, bool> match)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (match(columns[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static int LastIndex(IReadOnlyList<DateOnly> columns, Func<DateOnly, bool> match)
    {
        for (var i = columns.Count - 1; i >= 0; i--)
        {
            if (match(columns[i]))
            {
                return i;
            }
        }

        return -1;
    }
}
