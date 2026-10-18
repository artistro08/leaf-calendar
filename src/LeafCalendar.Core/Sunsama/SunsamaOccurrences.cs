using System.Globalization;
using LeafCalendar.Core.Events;

namespace LeafCalendar.Core.Sunsama;

/// <summary>A saved Sunsama row as shown: the row and how much of its card the tracked overlay covers (0 to 1).</summary>
/// <param name="Block">The saved row.</param>
/// <param name="Fill">The tracked fraction (spec 2026-10-16, section 6.3.1).</param>
public sealed record SunsamaShown(SunsamaBlock Block, double Fill);

/// <summary>
/// Turns saved Sunsama rows into read-only <see cref="CalendarOccurrence"/>s the calendar grid draws with the existing
/// event code (spec 2026-10-16, section 6.3), and recognizes them so every action that could change an event skips them.
/// </summary>
public static class SunsamaOccurrences
{
    /// <summary>The account ID every Sunsama occurrence and sidebar row uses.</summary>
    public const string AccountId = "sunsama";

    /// <summary>The sidebar row (and occurrence calendar) for projected tasks.</summary>
    public const string ProjectedCalendarId = "projected";

    /// <summary>The sidebar row for tracked time (it draws no blocks).</summary>
    public const string TrackedCalendarId = "tracked";

    /// <summary>The occurrence calendar for timeboxed tasks.</summary>
    public const string TimeboxCalendarId = "timebox";

    /// <summary>The default Sunsama color, Tangerine (Settings › Accounts › Sunsama › Color changes it).</summary>
    public const string Color = "#F4511E";

    /// <summary>True for an occurrence built from a Sunsama row.</summary>
    /// <param name="occurrence">Any occurrence.</param>
    /// <returns>True when it isn't a Google event.</returns>
    public static bool IsSunsama(CalendarOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        return occurrence.AccountId == AccountId;
    }

    /// <summary>
    /// The occurrence for a projected or timeboxed row: projected looks like an unanswered invitation, timeboxed like a
    /// regular event. Free time, so it never counts as busy.
    /// </summary>
    /// <param name="block">A projected or timebox row.</param>
    /// <param name="color">The Sunsama color (<see cref="Color"/> unless one was picked).</param>
    /// <returns>The read-only occurrence.</returns>
    public static CalendarOccurrence ToOccurrence(SunsamaBlock block, string color = Color)
    {
        ArgumentNullException.ThrowIfNull(block);
        var projected = block.Kind == SunsamaBlockKind.Projected;
        return new CalendarOccurrence(
            AccountId,
            projected ? ProjectedCalendarId : TimeboxCalendarId,
            string.Create(CultureInfo.InvariantCulture, $"{block.TaskId}@{block.Start.UtcTicks}"),
            null,
            null,
            block.Start,
            block.End,
            false,
            block.Title,
            EventKind.Default,
            projected ? ResponseStatus.NeedsAction : ResponseStatus.Accepted,
            color,
            null,
            IsFree: true,
            HasConference: false);
    }

    /// <summary>
    /// Builds the occurrences to draw (projected and timeboxed rows) and a lookup of every row as shown: each drawn
    /// occurrence under its <see cref="CalendarOccurrence.Key"/>, each matched row under its Google occurrence's key
    /// (<see cref="MatchKey"/>). A task's blocks on one day share its tracked total in time order; a matched event is
    /// filled against its own length.
    /// </summary>
    /// <param name="rows">Saved rows.</param>
    /// <param name="color">The Sunsama color (<see cref="Color"/> unless one was picked).</param>
    /// <returns>The occurrences and the lookup.</returns>
    public static (List<CalendarOccurrence> Blocks, Dictionary<string, SunsamaShown> ByKey) Build(IReadOnlyList<SunsamaBlock> rows, string color = Color)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var blocks = new List<CalendarOccurrence>();
        var byKey = new Dictionary<string, SunsamaShown>(StringComparer.Ordinal);
        foreach (var group in rows.Where(r => r.Kind != SunsamaBlockKind.Matched).GroupBy(r => (r.Day, r.TaskId)))
        {
            var taskBlocks = group.ToList();
            var fills = SunsamaTracked.Fill(taskBlocks);
            for (var i = 0; i < taskBlocks.Count; i++)
            {
                var occurrence = ToOccurrence(taskBlocks[i], color);
                blocks.Add(occurrence);
                byKey[occurrence.Key] = new SunsamaShown(taskBlocks[i], fills[i]);
            }
        }

        foreach (var row in rows.Where(r => r.Kind == SunsamaBlockKind.Matched && r.MatchedEventId is not null))
        {
            var length = (row.End - row.Start).TotalMinutes;
            var fill = length > 0 ? Math.Min(1, (row.TrackedMinutes ?? 0) / length) : 0;
            byKey[MatchKey(row.MatchedAccountId!, row.MatchedCalendarId!, row.MatchedEventId!, row.Start)] = new SunsamaShown(row, fill);
        }

        return (blocks, byKey);
    }

    /// <summary>
    /// The lookup key of a matched Google occurrence: the same as its <see cref="CalendarOccurrence.Key"/>, so each
    /// instance of a repeating event is matched on its own.
    /// </summary>
    /// <param name="accountId">The Google account.</param>
    /// <param name="calendarId">The calendar.</param>
    /// <param name="eventId">The event ID.</param>
    /// <param name="start">The occurrence's start.</param>
    /// <returns>The key.</returns>
    public static string MatchKey(string accountId, string calendarId, string eventId, DateTimeOffset start) =>
        string.Create(CultureInfo.InvariantCulture, $"{accountId}|{calendarId}|{eventId}|{start.UtcTicks}");
}
