using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;
using LeafCalendar.Core.Tray;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Data;

/// <summary>A saved share: its title and message as typed, the zone its text is written in, and its free times (merged, in order).</summary>
public sealed record ShareGroup(long Id, string Title, string Message, string ZoneId, IReadOnlyList<BusyRange> Slots);

/// <summary>
/// Reads and writes the <c>share_groups</c> and <c>share_slots</c> tables: availability you copied, kept so it stays on
/// the calendar until you approve a time, delete the group, or its times pass.
/// </summary>
public static class ShareGroupStore
{
    /// <summary>The longest title kept.</summary>
    public const int MaxTitleLength = 100;

    /// <summary>Every group with a time that hasn't ended by <paramref name="now"/>, oldest first, ended times left out.</summary>
    public static IReadOnlyList<ShareGroup> GetAll(SqliteConnection conn, DateTimeOffset now)
    {
        var groups = conn.Query(null, "SELECT id, title, message, zone_id FROM share_groups ORDER BY created_utc, id;", r => (Id: r.GetInt64(0), Title: r.GetString(1), Message: r.GetString(2), Zone: r.GetString(3)));
        var slots = conn.Query(
            null,
            "SELECT group_id, start_utc, end_utc FROM share_slots WHERE end_utc > $now;",
            r => (Group: r.GetInt64(0), Range: new BusyRange(DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)))),
            ("$now", now.ToUnixTimeMilliseconds()))
            .ToLookup(s => s.Group, s => s.Range);

        return [.. groups
            .Where(g => slots[g.Id].Any())
            .Select(g => new ShareGroup(g.Id, g.Title, g.Message, g.Zone, BusyMath.Merge(slots[g.Id])))];
    }

    /// <summary>Saves a new group and returns its ID. The title is cleaned and capped, the message capped, the times merged.</summary>
    public static long Insert(SqliteConnection conn, string title, string message, string zoneId, IReadOnlyList<BusyRange> slots, DateTimeOffset now)
    {
        using var tx = conn.BeginTransaction();
        conn.Execute(
            tx,
            "INSERT INTO share_groups (title, message, zone_id, created_utc) VALUES ($title, $message, $zone, $now);",
            ("$title", DisplayText.Clean(title, MaxTitleLength)),
            ("$message", Cap(message)),
            ("$zone", zoneId),
            ("$now", now.ToUnixTimeMilliseconds()));
        var id = conn.Query(tx, "SELECT last_insert_rowid();", r => r.GetInt64(0)).Single();
        WriteSlots(conn, tx, id, slots);
        tx.Commit();
        return id;
    }

    /// <summary>Replaces a group's title, message, zone and times. With no times left the group is deleted.</summary>
    public static void Update(SqliteConnection conn, long id, string title, string message, string zoneId, IReadOnlyList<BusyRange> slots)
    {
        if (slots.Count == 0)
        {
            Delete(conn, id);
            return;
        }

        using var tx = conn.BeginTransaction();
        conn.Execute(
            tx,
            "UPDATE share_groups SET title = $title, message = $message, zone_id = $zone WHERE id = $id;",
            ("$id", id),
            ("$title", DisplayText.Clean(title, MaxTitleLength)),
            ("$message", Cap(message)),
            ("$zone", zoneId));
        conn.Execute(tx, "DELETE FROM share_slots WHERE group_id = $id;", ("$id", id));
        WriteSlots(conn, tx, id, slots);
        tx.Commit();
    }

    /// <summary>Deletes a group and its times.</summary>
    public static void Delete(SqliteConnection conn, long id) =>
        conn.Execute(null, "DELETE FROM share_groups WHERE id = $id;", ("$id", id));

    /// <summary>Deletes times that ended by <paramref name="now"/>, then groups with none left.</summary>
    public static void Prune(SqliteConnection conn, DateTimeOffset now)
    {
        using var tx = conn.BeginTransaction();
        conn.Execute(tx, "DELETE FROM share_slots WHERE end_utc <= $now;", ("$now", now.ToUnixTimeMilliseconds()));
        conn.Execute(tx, "DELETE FROM share_groups WHERE id NOT IN (SELECT group_id FROM share_slots);");
        tx.Commit();
    }

    private static void WriteSlots(SqliteConnection conn, SqliteTransaction tx, long id, IReadOnlyList<BusyRange> slots)
    {
        foreach (var s in BusyMath.Merge(slots))
        {
            conn.Execute(
                tx,
                "INSERT INTO share_slots (group_id, start_utc, end_utc) VALUES ($id, $start, $end);",
                ("$id", id),
                ("$start", s.Start.ToUnixTimeMilliseconds()),
                ("$end", s.End.ToUnixTimeMilliseconds()));
        }
    }

    private static string Cap(string message) => message.Length > AvailabilityText.MaxMessageLength ? message[..AvailabilityText.MaxMessageLength] : message;
}
