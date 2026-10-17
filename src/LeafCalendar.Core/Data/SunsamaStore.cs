using System.Globalization;
using LeafCalendar.Core.Sunsama;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Data;

/// <summary>
/// Reads and writes the <c>sunsama_block</c> table: the Sunsama task times saved per local day so they show offline
/// (spec 2026-10-16, section 5.2). Days are <c>yyyy-MM-dd</c>; instants are round-trip UTC text.
/// </summary>
public static class SunsamaStore
{
    private const string Columns = """
        day, task_id, title, channel, start_utc, end_utc, estimate_minutes, tracked_minutes, completed, kind,
        matched_account_id, matched_calendar_id, matched_event_id
        """;

    /// <summary>Replaces one day's rows with <paramref name="blocks"/>, in one transaction.</summary>
    /// <param name="conn">An open connection.</param>
    /// <param name="tx">The caller's transaction, or null to use one of its own.</param>
    /// <param name="day">The local day being replaced.</param>
    /// <param name="blocks">The day's new rows.</param>
    public static void ReplaceDay(SqliteConnection conn, SqliteTransaction? tx, DateOnly day, IReadOnlyList<SunsamaBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(blocks);

        using var own = tx is null ? conn.BeginTransaction() : null;
        var t = tx ?? own;
        conn.Execute(t, "DELETE FROM sunsama_block WHERE day = $day;", ("$day", Day(day)));
        foreach (var b in blocks)
        {
            conn.Execute(
                t,
                $"""
                INSERT INTO sunsama_block ({Columns})
                VALUES ($day, $task, $title, $channel, $start, $end, $estimate, $tracked, $completed, $kind, $account, $calendar, $event);
                """,
                ("$day", Day(day)),
                ("$task", b.TaskId),
                ("$title", b.Title),
                ("$channel", b.Channel),
                ("$start", Instant(b.Start)),
                ("$end", Instant(b.End)),
                ("$estimate", b.EstimateMinutes),
                ("$tracked", b.TrackedMinutes),
                ("$completed", b.Completed ? 1 : 0),
                ("$kind", Kind(b.Kind)),
                ("$account", b.MatchedAccountId),
                ("$calendar", b.MatchedCalendarId),
                ("$event", b.MatchedEventId));
        }

        own?.Commit();
    }

    /// <summary>Every saved row from <paramref name="from"/> up to (not including) <paramref name="toExclusive"/>, by day then start.</summary>
    /// <param name="conn">An open connection.</param>
    /// <param name="from">The first local day.</param>
    /// <param name="toExclusive">The day after the last.</param>
    /// <returns>The rows.</returns>
    public static IReadOnlyList<SunsamaBlock> Range(SqliteConnection conn, DateOnly from, DateOnly toExclusive) =>
        conn.Query(
            null,
            $"SELECT {Columns} FROM sunsama_block WHERE day >= $from AND day < $to ORDER BY day, start_utc, task_id;",
            r => new SunsamaBlock(
                DateOnly.ParseExact(r.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.GetString(1),
                r.GetString(2),
                r.GetString(3),
                DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                r.IsDBNull(6) ? null : r.GetInt32(6),
                r.IsDBNull(7) ? null : r.GetInt32(7),
                r.GetInt64(8) != 0,
                r.GetString(9) switch
                {
                    "timebox" => SunsamaBlockKind.Timebox,
                    "matched" => SunsamaBlockKind.Matched,
                    _ => SunsamaBlockKind.Projected,
                },
                r.GetStringOrNull(10),
                r.GetStringOrNull(11),
                r.GetStringOrNull(12)),
            ("$from", Day(from)),
            ("$to", Day(toExclusive)));

    /// <summary>Deletes every saved row (Sign out).</summary>
    /// <param name="conn">An open connection.</param>
    public static void Clear(SqliteConnection conn) => conn.Execute(null, "DELETE FROM sunsama_block;");

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Instant(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static string Kind(SunsamaBlockKind kind) => kind switch
    {
        SunsamaBlockKind.Timebox => "timebox",
        SunsamaBlockKind.Matched => "matched",
        _ => "projected",
    };
}
