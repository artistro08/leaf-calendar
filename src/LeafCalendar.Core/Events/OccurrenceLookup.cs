using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Events;

/// <summary>Finds an event instance again from what a notification carried (account, calendar, event ID, start).</summary>
public static class OccurrenceLookup
{
    /// <summary>
    /// The instance with this event ID that starts at <paramref name="start"/>, or, when it has moved since, the one with
    /// this ID starting nearest to it around that day; null when it's gone. Declined instances are found too (a reply can change a "No").
    /// </summary>
    public static CalendarOccurrence? Find(SqliteConnection conn, string accountId, string calendarId, string eventId, DateTimeOffset start, TimeZoneInfo zone)
    {
        var day     = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, zone).DateTime);
        var matches = OccurrenceQuery.Load(conn, day.AddDays(-1), day.AddDays(2), zone, includeDeclined: true)
            .Where(o => o.AccountId == accountId && o.CalendarId == calendarId && o.EventId == eventId)
            .ToList();

        return matches.MinBy(o => (o.Start - start).Duration());
    }
}
