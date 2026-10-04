using LeafCalendar.Core.Views;

namespace LeafCalendar.Core.Events;

/// <summary>
/// Finds the same event shown on more than one calendar (usually a calendar shared between two of your accounts, or an
/// invite that landed on several of them), so the views draw it once with a color stripe per calendar instead of
/// side-by-side copies.
/// </summary>
/// <remarks>
/// Copies are the same event when they share Google's iCalendar UID and the same start, end, and all-day flag, and sit
/// on different calendars. One copy is drawn (and selected, opened, and edited): the one <c>preference</c> ranks lowest
/// (Leaf ranks a calendar you can edit, then your main account, first), else the first in the input. It takes the place
/// of the group's first copy, so the input order holds; the others are remembered as stripes and as aliases of the
/// drawn one. Events without a UID are never merged.
/// </remarks>
public static class SharedEvents
{
    /// <summary>
    /// The occurrences to draw; for each one shown on more than one calendar (by its <see cref="CalendarOccurrence.Key"/>)
    /// the accent of every copy, the drawn one first; and for each copy not drawn (by its key), the copy drawn instead.
    /// </summary>
    public sealed record Result(
        IReadOnlyList<CalendarOccurrence> Shown,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Stripes,
        IReadOnlyDictionary<string, CalendarOccurrence> Aliases);

    /// <summary>Merges the copies of shared events in <paramref name="occurrences"/>, keeping the input order.</summary>
    /// <param name="occurrences">The occurrences, in the order they're drawn.</param>
    /// <param name="preference">Ranks a copy for being the one drawn (lower wins; ties keep the input order). Null: the first copy.</param>
    public static Result Merge(IEnumerable<CalendarOccurrence> occurrences, Func<CalendarOccurrence, int>? preference = null)
    {
        ArgumentNullException.ThrowIfNull(occurrences);

        var shown = new List<CalendarOccurrence>();
        var stripes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var aliases = new Dictionary<string, CalendarOccurrence>(StringComparer.Ordinal);
        var groups = new Dictionary<(string Uid, long Start, long End, bool AllDay), (int Slot, List<CalendarOccurrence> Copies)>();

        foreach (var o in occurrences)
        {
            if (o.ICalUid is not { Length: > 0 } uid)
            {
                shown.Add(o);
                continue;
            }

            var key = (uid, o.Start.UtcTicks, o.End.UtcTicks, o.IsAllDay);
            if (!groups.TryGetValue(key, out var group))
            {
                groups[key] = (shown.Count, [o]);
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

        foreach (var (slot, copies) in groups.Values.Where(g => g.Copies.Count > 1))
        {
            // The Preferred Copy Is Drawn In The Group's Place (MinBy keeps the first of equals)
            var drawn = preference is null ? copies[0] : copies.MinBy(preference)!;
            shown[slot] = drawn;

            List<CalendarOccurrence> ordered = [drawn, .. copies.Where(c => !ReferenceEquals(c, drawn))];
            stripes[drawn.Key] = [.. ordered.Select(c => EventColors.ResolveAccent(c.ColorId, c.CalendarColor))];
            foreach (var other in ordered.Skip(1))
            {
                aliases[other.Key] = drawn;
            }
        }

        return new Result(shown, stripes, aliases);
    }
}
