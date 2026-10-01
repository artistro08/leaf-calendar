using System.Globalization;
using LeafCalendar.Core.Alerts;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Data;

/// <summary>A notification Leaf showed: its key, kind, Windows tag, and when its event ends.</summary>
public sealed record LedgerEntry(string Key, AlertKind Kind, string Tag, DateTimeOffset EventEnd);

/// <summary>
/// Reads and writes the <c>alert_ledger</c> table, Leaf's memory of the notifications it already showed.
/// </summary>
/// <remarks>
/// Rows stay until their event has been over for a while (<see cref="Prune"/>), so an invite for a meeting weeks away
/// isn't shown again when someone else replies to it. Small named marks (such as "this account's existing invites were
/// recorded") live in the <c>settings</c> table under <c>mark:&lt;name&gt;</c>.
/// </remarks>
public static class AlertLedger
{
    /// <summary>Records a shown notification. False when it was already recorded (so it must not show again).</summary>
    public static bool TryAdd(SqliteConnection conn, string key, AlertKind kind, string tag, DateTimeOffset eventEnd, DateTimeOffset now) =>
        conn.Execute(
            null,
            """
            INSERT INTO alert_ledger (key, kind, tag, event_end, delivered_utc) VALUES ($key, $kind, $tag, $end, $now)
            ON CONFLICT (key) DO NOTHING;
            """,
            ("$key", key),
            ("$kind", kind.ToString()),
            ("$tag", tag),
            ("$end", eventEnd.ToUnixTimeMilliseconds()),
            ("$now", now.ToUnixTimeMilliseconds())) == 1;

    /// <summary>True when a notification with this key was recorded.</summary>
    public static bool Contains(SqliteConnection conn, string key) =>
        conn.Query(null, "SELECT EXISTS (SELECT 1 FROM alert_ledger WHERE key = $key);", r => r.GetBoolean(0), ("$key", key)).Single();

    /// <summary>True when any recorded key starts with <paramref name="prefix"/> (compared exactly, no wildcards).</summary>
    public static bool HasPrefix(SqliteConnection conn, string prefix) =>
        conn.Query(
            null,
            "SELECT EXISTS (SELECT 1 FROM alert_ledger WHERE substr(key, 1, length($prefix)) = $prefix);",
            r => r.GetBoolean(0),
            ("$prefix", prefix)).Single();

    /// <summary>"Join now" notifications that are still on screen (not withdrawn).</summary>
    public static IReadOnlyList<LedgerEntry> OpenJoinNow(SqliteConnection conn) =>
        conn.Query(
            null,
            "SELECT key, tag, event_end FROM alert_ledger WHERE kind = $kind AND retracted = 0 ORDER BY delivered_utc;",
            r => new LedgerEntry(r.GetString(0), AlertKind.JoinNow, r.GetString(1), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2))),
            ("$kind", nameof(AlertKind.JoinNow)));

    /// <summary>Marks a "Join now" as withdrawn.</summary>
    public static void MarkRetracted(SqliteConnection conn, string key) =>
        conn.Execute(null, "UPDATE alert_ledger SET retracted = 1 WHERE key = $key;", ("$key", key));

    /// <summary>Forgets one notification, so it can show again (a withdrawn "Join now" whose meeting comes back).</summary>
    public static void Remove(SqliteConnection conn, string key) =>
        conn.Execute(null, "DELETE FROM alert_ledger WHERE key = $key;", ("$key", key));

    /// <summary>
    /// Forgets every notification about one account's events (the account was removed). Keys read
    /// <c>Kind|account|...</c>; the account ID is compared exactly, no wildcards.
    /// </summary>
    public static void RemoveForAccount(SqliteConnection conn, string accountId) =>
        conn.Execute(
            null,
            "DELETE FROM alert_ledger WHERE substr(key, instr(key, '|') + 1, length($account)) = $account;",
            ("$account", accountId + "|"));

    /// <summary>Forgets notifications for events that ended before <paramref name="endedBefore"/>.</summary>
    public static void Prune(SqliteConnection conn, DateTimeOffset endedBefore) =>
        conn.Execute(null, "DELETE FROM alert_ledger WHERE event_end < $cutoff;", ("$cutoff", endedBefore.ToUnixTimeMilliseconds()));

    /// <summary>A named mark's value, or null when it was never set.</summary>
    public static long? GetMark(SqliteConnection conn, string name)
    {
        var text = conn.Query(null, "SELECT value FROM settings WHERE key = $key;", r => r.GetString(0), ("$key", "mark:" + name)).SingleOrDefault();
        return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>Sets a named mark.</summary>
    public static void SetMark(SqliteConnection conn, string name, long value) =>
        conn.Execute(
            null,
            "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            ("$key", "mark:" + name),
            ("$value", value.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Deletes one named mark.</summary>
    public static void DeleteMark(SqliteConnection conn, string name) =>
        conn.Execute(null, "DELETE FROM settings WHERE key = $key;", ("$key", "mark:" + name));

    /// <summary>Deletes every named mark whose name starts with <paramref name="prefix"/> (compared exactly, no wildcards).</summary>
    public static void DeleteMarksStartingWith(SqliteConnection conn, string prefix) =>
        conn.Execute(
            null,
            "DELETE FROM settings WHERE substr(key, 1, length($prefix)) = $prefix;",
            ("$prefix", "mark:" + prefix));
}
