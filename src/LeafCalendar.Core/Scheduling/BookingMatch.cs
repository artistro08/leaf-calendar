using LeafCalendar.Core.Data;

namespace LeafCalendar.Core.Scheduling;

/// <summary>A booking and the saved groups it closes.</summary>
public sealed record BookingMatchResult(SchedulingBooking Booking, IReadOnlyList<long> GroupIds);

/// <summary>Finds the saved groups a booking closes (spec 3.6).</summary>
public static class BookingMatch
{
    /// <summary>
    /// For each booking, the groups on the same provider and link, saved at or before the booking was made, with a saved
    /// range the booking starts inside (start included, end not). A group is reported for its first matching booking only.
    /// Bookings that close no group are left out.
    /// </summary>
    public static IReadOnlyList<BookingMatchResult> Find(IReadOnlyList<ShareGroup> groups, string providerId, string linkId, IReadOnlyList<SchedulingBooking> bookings)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(bookings);

        var candidates = groups.Where(g => g.ProviderId == providerId && g.LinkId == linkId).ToList();
        var taken = new HashSet<long>();
        var results = new List<BookingMatchResult>();
        foreach (var booking in bookings.OrderBy(b => b.Created))
        {
            var ids = candidates
                .Where(g => !taken.Contains(g.Id) && booking.Created >= g.CreatedUtc && g.Slots.Any(s => booking.Start >= s.Start && booking.Start < s.End))
                .Select(g => g.Id)
                .ToList();
            if (ids.Count == 0)
            {
                continue;
            }

            taken.UnionWith(ids);
            results.Add(new BookingMatchResult(booking, ids));
        }

        return results;
    }
}
