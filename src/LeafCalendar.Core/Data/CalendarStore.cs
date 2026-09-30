using System.Text.Json;
using System.Text.RegularExpressions;
using LeafCalendar.Core.Google;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Data;

/// <summary>
/// A stored calendar. <see cref="SyncToken"/> is null until the first full sync finishes.
/// <see cref="Hidden"/> is Google's flag; <see cref="LeafHidden"/> and <see cref="LeafColor"/> are
/// Leaf's own display choices, kept locally and never sent to Google.
/// </summary>
public sealed record CalendarInfo(
    string AccountId,
    string Id,
    string Summary,
    string? BackgroundColor,
    string AccessRole,
    bool IsPrimary,
    bool Hidden,
    string? SyncToken,
    bool LeafHidden,
    string? LeafColor,
    int SortOrder)
{
    /// <summary>Fallback when Google gives no color.</summary>
    public const string DefaultColor = "#4285F4";

    /// <summary>Color to draw with: Leaf's override, then Google's, then the default.</summary>
    public string DisplayColor => LeafColor ?? BackgroundColor ?? DefaultColor;

    /// <summary>True when the calendar's events are shown.</summary>
    public bool IsVisible => !LeafHidden;
}

/// <summary>Reads and writes the <c>calendars</c> table.</summary>
public static partial class CalendarStore
{
    const string SelectColumns = """
        SELECT c.account_id, c.id, COALESCE(c.summary_override, c.summary), c.background_color, c.access_role,
               c.is_primary, c.hidden, c.sync_token, COALESCE(c.leaf_hidden, c.hidden), c.leaf_color, c.sort_order
        FROM calendars c
        """;

    /// <summary>
    /// Makes the account's calendars match Google's list.
    /// </summary>
    /// <remarks>
    /// Calendars missing from the list (or marked deleted) are removed along with their events.
    /// Existing calendars keep their sync token, local order, color, and visibility. A newly seen
    /// calendar starts visible only when it is ticked ("selected") in Google Calendar.
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
        var nextOrder = conn.Query(tx, "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM calendars WHERE account_id = $account;", r => r.GetInt32(0), ("$account", accountId)).Single();
        foreach (var entry in incoming)
        {
            conn.Execute(
                tx,
                """
                INSERT INTO calendars (account_id, id, summary, summary_override, time_zone, background_color, foreground_color,
                                       access_role, is_primary, hidden, sort_order, default_reminders, leaf_hidden)
                VALUES ($account, $id, $summary, $override, $zone, $background, $foreground, $role, $primary, $hidden, $order, $reminders, $leafHidden)
                ON CONFLICT (account_id, id) DO UPDATE SET
                    summary           = excluded.summary,
                    summary_override  = excluded.summary_override,
                    time_zone         = excluded.time_zone,
                    background_color  = excluded.background_color,
                    foreground_color  = excluded.foreground_color,
                    access_role       = excluded.access_role,
                    is_primary        = excluded.is_primary,
                    hidden            = excluded.hidden,
                    default_reminders = excluded.default_reminders,
                    leaf_hidden       = COALESCE(calendars.leaf_hidden, excluded.leaf_hidden);
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
                ("$order", nextOrder++),
                ("$reminders", JsonSerializer.Serialize(entry.DefaultReminders ?? [], GoogleJsonContext.Default.ListReminderOverride)),
                ("$leafHidden", !entry.Selected));
        }

        tx.Commit();
    }

    /// <summary>An account's calendars in Leaf's order (inside <paramref name="tx"/> when one is open).</summary>
    public static IReadOnlyList<CalendarInfo> GetForAccount(SqliteConnection conn, string accountId, SqliteTransaction? tx = null) =>
        conn.Query(tx, SelectColumns + " WHERE c.account_id = $account ORDER BY c.sort_order;", Map, ("$account", accountId));

    /// <summary>Every calendar, grouped by account email, in Leaf's order.</summary>
    public static IReadOnlyList<CalendarInfo> GetAll(SqliteConnection conn) =>
        conn.Query(null, SelectColumns + " JOIN accounts a ON a.id = c.account_id ORDER BY a.email, c.sort_order;", Map);

    /// <summary>Saves the token for the calendar's next incremental sync.</summary>
    public static void SetSyncToken(SqliteConnection conn, SqliteTransaction? tx, string accountId, string calendarId, string? syncToken) =>
        conn.Execute(
            tx,
            "UPDATE calendars SET sync_token = $token WHERE account_id = $account AND id = $id;",
            ("$token", syncToken),
            ("$account", accountId),
            ("$id", calendarId));

    /// <summary>Shows or hides a calendar in Leaf (Google is not changed).</summary>
    public static void SetHidden(SqliteConnection conn, string accountId, string calendarId, bool hidden) =>
        conn.Execute(
            null,
            "UPDATE calendars SET leaf_hidden = $hidden WHERE account_id = $account AND id = $id;",
            ("$hidden", hidden),
            ("$account", accountId),
            ("$id", calendarId));

    /// <summary>Sets Leaf's color for a calendar (<c>#RRGGBB</c>), or null to use Google's color.</summary>
    /// <exception cref="ArgumentException">The color is not <c>#RRGGBB</c>.</exception>
    public static void SetColor(SqliteConnection conn, string accountId, string calendarId, string? color)
    {
        if (color is not null && !HexColor().IsMatch(color))
        {
            throw new ArgumentException("Color must be #RRGGBB.", nameof(color));
        }

        conn.Execute(
            null,
            "UPDATE calendars SET leaf_color = $color WHERE account_id = $account AND id = $id;",
            ("$color", color),
            ("$account", accountId),
            ("$id", calendarId));
    }

    /// <summary>Stores the account's calendar order (IDs not listed keep their place after the listed ones).</summary>
    public static void Reorder(SqliteConnection conn, string accountId, IReadOnlyList<string> calendarIds)
    {
        using var tx = conn.BeginTransaction();

        var others = conn.Query(tx, "SELECT id FROM calendars WHERE account_id = $account ORDER BY sort_order;", r => r.GetString(0), ("$account", accountId))
            .Where(id => !calendarIds.Contains(id, StringComparer.Ordinal));

        var order = 0;
        foreach (var id in calendarIds.Concat(others))
        {
            conn.Execute(tx, "UPDATE calendars SET sort_order = $order WHERE account_id = $account AND id = $id;", ("$order", order++), ("$account", accountId), ("$id", id));
        }

        tx.Commit();
    }

    static CalendarInfo Map(SqliteDataReader r) => new(
        r.GetString(0),
        r.GetString(1),
        r.GetString(2),
        r.GetStringOrNull(3),
        r.GetString(4),
        r.GetBoolean(5),
        r.GetBoolean(6),
        r.GetStringOrNull(7),
        r.GetBoolean(8),
        r.GetStringOrNull(9),
        r.GetInt32(10));

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();
}
