using LeafCalendar.Core.Events;

namespace LeafCalendar.Core.Views;

/// <summary>A timed event's position in one day: minutes from local midnight, and its side-by-side column.</summary>
public sealed record TimedBlock(CalendarOccurrence Occurrence, double StartMinute, double EndMinute, int Column, int ColumnCount);

/// <summary>
/// Lays out one day's timed events so overlapping ones sit side by side.
/// </summary>
/// <remarks>
/// Events are sorted by start (longer first) and packed into the leftmost free column. A cluster is
/// a run of events that overlap in a chain. Every event in a cluster gets the cluster's column count,
/// so widths line up. Each event occupies at least <see cref="MinVisualMinutes"/> for overlap purposes,
/// so tiny back-to-back events never draw on top of each other, and a short one in the day's last minutes
/// starts early enough to fit (<see cref="TimedBlock.StartMinute"/> is where the card is drawn from). Events
/// of 24 hours or more belong to the span row (<see cref="SpanLayout"/>).
/// </remarks>
public static class DayLayout
{
    /// <summary>Smallest height an event takes when deciding overlaps (minutes).</summary>
    public const double MinVisualMinutes = 20;

    /// <summary>Lays out <paramref name="day"/>.</summary>
    public static IReadOnlyList<TimedBlock> Layout(DateOnly day, IEnumerable<CalendarOccurrence> occurrences, TimeZoneInfo zone)
    {
        // The Day As Instants: a midnight the clock repeats (falling back from 1 AM) counts from its first pass, as in
        // DragMath, so an event made in that hour is on the day it's filed under
        var dayStart = DragMath.Instant(day, 0, zone);
        var dayEnd   = DragMath.Instant(day.AddDays(1), 0, zone);

        // A zero-minute event belongs to the day it starts, midnight included
        var items = occurrences
            .Where(o => !o.IsAllDay && !SpanLayout.IsSpanning(o) && o.Start < dayEnd && (o.End > dayStart || o.Start >= dayStart))
            .Select(o => (Occurrence: o, Start: DrawnStart(MinuteOfDay(o.Start < dayStart ? dayStart : o.Start, day, zone)), End: o.End >= dayEnd ? 1440 : MinuteOfDay(o.End, day, zone)))
            .OrderBy(i => i.Start)
            .ThenByDescending(i => i.End - i.Start)
            .ThenBy(i => i.Occurrence.Key, StringComparer.Ordinal)
            .ToList();

        var result     = new List<TimedBlock>(items.Count);
        var cluster    = new List<(int Index, int Column)>();
        var columnEnds = new List<double>();
        var clusterEnd = double.MinValue;

        foreach (var (item, index) in items.Select((item, index) => (item, index)))
        {
            // Close The Cluster When Nothing Overlaps Anymore
            if (cluster.Count > 0 && item.Start >= clusterEnd)
            {
                Flush();
            }

            var visualEnd = Math.Max(item.End, item.Start + MinVisualMinutes);
            var column    = columnEnds.FindIndex(end => end <= item.Start);
            if (column < 0)
            {
                column = columnEnds.Count;
                columnEnds.Add(visualEnd);
            }
            else
            {
                columnEnds[column] = visualEnd;
            }

            clusterEnd = cluster.Count == 0 ? visualEnd : Math.Max(clusterEnd, visualEnd);
            cluster.Add((index, column));
        }

        Flush();
        return result;

        void Flush()
        {
            foreach (var (index, column) in cluster)
            {
                var item = items[index];
                result.Add(new TimedBlock(item.Occurrence, item.Start, Math.Max(item.End, item.Start), column, columnEnds.Count));
            }

            cluster.Clear();
            columnEnds.Clear();
        }
    }

    /// <summary>
    /// Where the card of an event starting at <paramref name="startMinute"/> is drawn from. A card is at least
    /// <see cref="MinVisualMinutes"/> tall, so one in the day's last minutes moves up to end at midnight instead of
    /// running past the bottom of the grid.
    /// </summary>
    internal static double DrawnStart(double startMinute) => Math.Min(startMinute, 1440 - MinVisualMinutes);

    internal static double MinuteOfDay(DateTimeOffset instant, DateOnly day, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        var date  = DateOnly.FromDateTime(local.DateTime);

        return date < day ? 0 : date > day ? 1440 : local.TimeOfDay.TotalMinutes;
    }
}
