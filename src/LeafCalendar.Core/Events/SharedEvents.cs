using LeafCalendar.Core.Views;

namespace LeafCalendar.Core.Events;

/// <summary>
/// Finds the same event shown on more than one calendar (usually a calendar shared between two of your accounts, or an
/// invite that landed on several of them), so the views draw it once with a color stripe per calendar instead of
/// side-by-side copies.
/// </summary>
/// <remarks>
/// Copies are the same event when they share Google's iCalendar UID and the same start, end, and all-day flag, and sit
/// on different calendars. The first copy in the input is the one drawn (and selected, opened, and edited); the
/// others are only remembered as stripes. Events without a UID are never merged.
/// </remarks>
public static class SharedEvents
{
    /// <summary>The occurrences to draw, and for each one shown on more than one calendar (by its <see cref="CalendarOccurrence.Key"/>) the accent of every copy, the drawn one first.</summary>
    public sealed record Result(IReadOnlyList<CalendarOccurrence> Shown, IReadOnlyDictionary<string, IReadOnlyList<string>> Stripes);

    /// <summary>Merges the copies of shared events in <paramref name="occurrences"/>, keeping the input order.</summary>
    public static Result Merge(IEnumerable<CalendarOccurrence> occurrences)
    {
        ArgumentNullException.ThrowIfNull(occurrences);

        var shown   = new List<CalendarOccurrence>();
        var stripes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var firsts  = new Dictionary<(string Uid, long Start, long End, bool AllDay), (CalendarOccurrence First, List<CalendarOccurrence> Copies)>();

        foreach (var o in occurrences)
        {
            if (o.ICalUid is not { Length: > 0 } uid)
            {
                shown.Add(o);
                continue;
            }

            var key = (uid, o.Start.UtcTicks, o.End.UtcTicks, o.IsAllDay);
            if (!firsts.TryGetValue(key, out var group))
            {
                firsts[key] = (o, [o]);
                shown.Add(o);
                continue;
            }

            // Two events on one calendar are two events, not a shared copy
            if (group.Copies.Exists(c => c.AccountId == o.AccountId && c.CalendarId == o.CalendarId))
            {
                shown.Add(o);
                continue;
            }

            group.Copies.Add(o);
        }

        foreach (var (first, copies) in firsts.Values.Where(g => g.Copies.Count > 1))
        {
            stripes[first.Key] = [.. copies.Select(c => EventColors.ResolveAccent(c.ColorId, c.CalendarColor))];
        }

        return new Result(shown, stripes);
    }
}
