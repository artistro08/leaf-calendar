using System.Text.Json;
using LeafCalendar.Core.Google;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Data;

/// <summary>A stored calendar. <see cref="SyncToken"/> is null until the first full sync finishes.</summary>
public sealed record CalendarInfo(
    string AccountId,
    string Id,
    string Summary,
    string? BackgroundColor,
    string AccessRole,
    bool IsPrimary,
    bool Hidden,
    string? SyncToken);

/// <summary>Reads and writes the <c>calendars</c> table.</summary>
public static class CalendarStore
{
    /// <summary>
    /// Makes the account's calendars match Google's list.
    /// </summary>
    /// <remarks>
    /// Calendars missing from the list (or marked deleted) are removed along with their events.
    /// Existing calendars keep their sync token, so their next sync stays incremental.
    /// </remarks>
    public static void ReplaceForAccount(SqliteConnection conn, string accountId, IReadOnlyList<CalendarListEntry> entries)
    {
        var incoming    = entries.Where(e => !e.Deleted).ToList();
        var incomingIds = incoming.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

        using var tx = conn.BeginTransaction();

        // Remove Calendars No Longer Listed
        var existingIds = conn.Query(tx, "SELECT id FROM calendars WHERE account_id = $account;", r => r.GetString(0), ("$account", accountId));
        foreach (var id in existingIds.Where(id => !incomingIds.Contains(id)))
        {
            conn.Execute(tx, "DELETE FROM calendars WHERE account_id = $account AND id = $id;", ("$account", accountId), ("$id", id));
        }

        // Upsert Listed Calendars
        for (var i = 0; i < incoming.Count; i++)
        {
            var entry = incoming[i];
            conn.Execute(
                tx,
                """
                INSERT INTO calendars (account_id, id, summary, summary_override, time_zone, background_color, foreground_color,
                                       access_role, is_primary, hidden, sort_order, default_reminders)
                VALUES ($account, $id, $summary, $override, $zone, $background, $foreground, $role, $primary, $hidden, $order, $reminders)
                ON CONFLICT (account_id, id) DO UPDATE SET
                    summary           = excluded.summary,
                    summary_override  = excluded.summary_override,
                    time_zone         = excluded.time_zone,
                    background_color  = excluded.background_color,
                    foreground_color  = excluded.foreground_color,
                    access_role       = excluded.access_role,
                    is_primary        = excluded.is_primary,
                    hidden            = excluded.hidden,
                    default_reminders = excluded.default_reminders;
                """,
                ("$account", accountId),
                ("$id", entry.Id),
                ("$summary", entry.Summary),
                ("$override", entry.SummaryOverride),
                ("$zone", entry.TimeZone),
                ("$background", entry.BackgroundColor),
                ("$foreground", entry.ForegroundColor),
                ("$role", entry.AccessRole),
                ("$primary", entry.Primary),
                ("$hidden", entry.Hidden),
                ("$order", i),
                ("$reminders", JsonSerializer.Serialize(entry.DefaultReminders ?? [], GoogleJsonContext.Default.ListReminderOverride)));
        }

        tx.Commit();
    }

    /// <summary>An account's calendars in list order.</summary>
    public static IReadOnlyList<CalendarInfo> GetForAccount(SqliteConnection conn, string accountId) =>
        conn.Query(
            null,
            """
            SELECT account_id, id, COALESCE(summary_override, summary), background_color, access_role, is_primary, hidden, sync_token
            FROM calendars WHERE account_id = $account ORDER BY sort_order;
            """,
            r => new CalendarInfo(
                r.GetString(0),
                r.GetString(1),
                r.GetString(2),
                r.GetStringOrNull(3),
                r.GetString(4),
                r.GetBoolean(5),
                r.GetBoolean(6),
                r.GetStringOrNull(7)),
            ("$account", accountId));

    /// <summary>Saves the token for the calendar's next incremental sync.</summary>
    public static void SetSyncToken(SqliteConnection conn, SqliteTransaction? tx, string accountId, string calendarId, string? syncToken) =>
        conn.Execute(
            tx,
            "UPDATE calendars SET sync_token = $token WHERE account_id = $account AND id = $id;",
            ("$token", syncToken),
            ("$account", accountId),
            ("$id", calendarId));
}
