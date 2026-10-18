using LeafCalendar.Core.Events;

namespace LeafCalendar.Core.Sunsama;

/// <summary>
/// Turns a day's Sunsama tasks into the rows Leaf Calendar saves, matching tasks that are already Google events
/// (spec 2026-10-16, section 4.4).
/// </summary>
public static class SunsamaMatch
{
    /// <summary>
    /// Builds one day's rows. A task tied to a Google-side event gets only a <see cref="SunsamaBlockKind.Matched"/> row per
    /// such event found among <paramref name="cached"/> (same calendar, same start, preferring the same title); one with no
    /// cached match gives nothing. Any other task gets a <see cref="SunsamaBlockKind.Projected"/> row per projected entry and
    /// a <see cref="SunsamaBlockKind.Timebox"/> row per event on a non-Google calendar.
    /// </summary>
    /// <param name="day">The local day the tasks were fetched for.</param>
    /// <param name="tasks">The day's tasks.</param>
    /// <param name="cached">The Google occurrences Leaf Calendar has cached for that day.</param>
    /// <returns>The rows, in task order.</returns>
    public static IReadOnlyList<SunsamaBlock> Blocks(DateOnly day, IReadOnlyList<SunsamaTask> tasks, IReadOnlyList<CalendarOccurrence> cached)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(cached);

        var blocks = new List<SunsamaBlock>();
        foreach (var task in tasks)
        {
            SunsamaBlock Row(DateTimeOffset start, DateTimeOffset end, SunsamaBlockKind kind, CalendarOccurrence? match = null) =>
                new(day, task.Id, task.Title, task.Channel, start, end, task.EstimateMinutes, task.TrackedMinutes, task.Completed, kind, match?.AccountId, match?.CalendarId, match?.EventId);

            // A Task Already on Google Draws No Block of Its Own (spec goal 4), matched or not
            if (!task.Events.Any(e => e.OnGoogle))
            {
                blocks.AddRange(task.Projected.Select(p => Row(p.Start, p.End, SunsamaBlockKind.Projected)));
                blocks.AddRange(task.Events.Select(e => Row(e.Start, e.End, SunsamaBlockKind.Timebox)));
                continue;
            }

            foreach (var e in task.Events.Where(e => e.OnGoogle))
            {
                var sameStart = cached.Where(o => !o.IsAllDay && o.CalendarId == e.CalendarId && o.Start == e.Start).ToList();
                if ((sameStart.FirstOrDefault(o => string.Equals(o.Title, e.Title, StringComparison.Ordinal)) ?? sameStart.FirstOrDefault()) is { } match)
                {
                    blocks.Add(Row(match.Start, match.End, SunsamaBlockKind.Matched, match));
                }
            }
        }

        return blocks;
    }
}
