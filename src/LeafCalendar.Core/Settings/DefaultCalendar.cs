using LeafCalendar.Core.Data;

namespace LeafCalendar.Core.Settings;

/// <summary>Chooses the calendar new events go to.</summary>
public static class DefaultCalendar
{
    /// <summary>
    /// Picks where a new event goes.
    /// </summary>
    /// <remarks>
    /// The preferred calendar wins when it is still known, writable (<c>owner</c> or <c>writer</c>), in a connected account,
    /// and in <paramref name="accountId"/> when that is given. Otherwise the main calendar you can write to: the main
    /// account's first, then primary, then shown ones. Returns null when nothing can be written to.
    /// </remarks>
    /// <param name="calendars">Every known calendar.</param>
    /// <param name="accountIds">The connected accounts' IDs.</param>
    /// <param name="preferred">The user's choice, or null for their main Google calendar.</param>
    /// <param name="accountId">Limit the answer to one account, or null for any.</param>
    /// <param name="mainAccountId">The main account (Settings), or null for none.</param>
    public static CalendarInfo? Pick(IReadOnlyList<CalendarInfo> calendars, IReadOnlySet<string> accountIds, CalendarRef? preferred, string? accountId = null, string? mainAccountId = null)
    {
        var writable = calendars
            .Where(c => c.AccessRole is "owner" or "writer" && accountIds.Contains(c.AccountId) && (accountId is null || c.AccountId == accountId))
            .ToList();

        // The User's Choice
        if (preferred is not null && writable.FirstOrDefault(c => c.AccountId == preferred.AccountId && c.Id == preferred.CalendarId) is { } chosen)
        {
            return chosen;
        }

        // The Main Google Calendar
        return writable
            .OrderByDescending(c => c.AccountId == mainAccountId)
            .ThenByDescending(c => c.IsPrimary)
            .ThenByDescending(c => c.IsVisible)
            .FirstOrDefault();
    }
}
