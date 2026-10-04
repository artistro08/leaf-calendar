using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Alerts;

/// <summary>
/// A notification that should appear at <see cref="FireAt"/> for one event instance. <see cref="MinutesBefore"/> is the
/// reminder's lead (0 for "Join now"), and <see cref="MeetingLink"/> the event's meeting link, if any (it gives the
/// reminder a Join button).
/// </summary>
public sealed record Alert(AlertKind Kind, CalendarOccurrence Occurrence, DateTimeOffset FireAt, int MinutesBefore, Uri? MeetingLink)
{
    /// <summary>Names this alert in the ledger: kind, instance, and lead. A moved meeting has a new instance key, so it reminds again.</summary>
    public string Key => string.Create(CultureInfo.InvariantCulture, $"{Kind}|{Occurrence.Key}|{MinutesBefore}");

    /// <summary>The Windows tag of this alert's notification.</summary>
    public string Tag => TagFor(Key);

    /// <summary>
    /// A 16-character hex hash of <paramref name="key"/>. Windows tags are at most 64 characters, and the hash keeps
    /// calendar IDs (often email addresses) out of anything Windows or the log sees.
    /// </summary>
    public static string TagFor(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 8);
}

/// <summary>
/// Works out which notifications fall in a time window, from Google's own reminder settings (spec 8.4).
/// </summary>
/// <remarks>
/// <para>
/// An event on <c>reminders.useDefault</c> (or with no <c>reminders</c> at all) gets its calendar's default popups; one
/// with overrides gets its own popups instead. A calendar in several of your accounts takes the defaults set under any
/// of them, and reminds once even when it's shown under more than one. Email reminders are Google's to send. Timed
/// reminders count back from the start; all-day ones from local midnight of the first day, and only from overrides, since Google's API doesn't expose
/// the all-day defaults. Minutes outside 0–40,320 (Google's range) are ignored.
/// </para>
/// <para>
/// A timed meeting of yours (<see cref="JoinPicker.IsMine"/>) with a meeting link also gets "Join now" at its start, so a
/// colleague's meetings on a calendar you can see never do. Instances come from <see cref="OccurrenceQuery"/>, so repeating
/// events keep their local time across daylight saving, changed instances use their own row, declined events and hidden
/// calendars are left out, and a canceled instance has no alerts. An event on several calendars reminds from its first
/// copy, as the views show it, and joins from its first copy that's yours.
/// </para>
/// </remarks>
public static class AlertPlanner
{
    /// <summary>Google's longest reminder: four weeks.</summary>
    public const int MaxMinutes = 40320;

    /// <summary>
    /// The alerts with <paramref name="from"/> &lt; fire time ≤ <paramref name="to"/>, by fire time, then kind. With
    /// <paramref name="includeMissed"/>, also every reminder that came due at or before <paramref name="from"/> for an
    /// instance that hasn't ended by then (however long ago it came due).
    /// </summary>
    public static IReadOnlyList<Alert> Plan(SqliteConnection conn, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone, bool includeMissed = false)
    {
        // Instances That Can Have An Alert In The Window: starting up to four weeks after it (the longest reminder)
        var fromDate = LocalDate(from, zone).AddDays(-1);
        var toDate   = LocalDate(to, zone).AddDays(MaxMinutes / 1440 + 2);
        var defaults  = CalendarStore.PopupDefaults(conn);
        var calendars = CalendarStore.GetAll(conn).ToDictionary(c => (c.AccountId, c.Id));
        var rows      = new Dictionary<(string, string, string), RowAlerts?>();
        var reminded  = new HashSet<(string, long)>();
        var joined    = new HashSet<(string, long)>();
        var alerts    = new List<Alert>();

        foreach (var o in OccurrenceQuery.Load(conn, fromDate, toDate, zone, includeDeclined: false, keepSharedCopies: true))
        {
            // One Parse Per Stored Row (a series' instances share their master's)
            var id = (o.AccountId, o.CalendarId, o.EventId);
            if (!rows.TryGetValue(id, out var row))
            {
                rows[id] = row = Read(conn, o, calendars.GetValueOrDefault((o.AccountId, o.CalendarId)));
            }

            if (row is null)
            {
                continue;
            }

            // Reminders (a shared event's other copies have none)
            IReadOnlyList<int> minutes = !First(reminded, o) ? []
                : !row.UseDefault ? row.Overrides
                : o.IsAllDay ? []
                : defaults.GetValueOrDefault((o.AccountId, o.CalendarId)) ?? [];
            var anchor = o.StartIn(zone);
            var missed = includeMissed && o.EndIn(zone) > from;
            foreach (var m in minutes)
            {
                var at = anchor.AddMinutes(-m);
                if ((at > from || missed) && at <= to)
                {
                    alerts.Add(new Alert(AlertKind.Reminder, o, at, m, row.Link));
                }
            }

            // Join Now, Only For Your Own Meetings (the first copy that's yours)
            if (!o.IsAllDay && row.Link is not null && row.IsMine && o.Start > from && o.Start <= to && First(joined, o))
            {
                alerts.Add(new Alert(AlertKind.JoinNow, o, o.Start, 0, row.Link));
            }
        }

        // A Calendar Shown Under Two Accounts Reminds Once (the first account's copy)
        return [.. alerts
            .OrderBy(a => a.FireAt)
            .ThenBy(a => a.Kind)
            .DistinctBy(a => (a.Kind, a.Occurrence.CalendarId, a.Occurrence.EventId, a.Occurrence.Start, a.MinutesBefore))];
    }

    static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    // True for the first copy seen of an event (same iCalendar UID and start), as OccurrenceQuery keeps it; events without a UID always
    internal static bool First(HashSet<(string, long)> seen, CalendarOccurrence o) =>
        o.ICalUid is null || seen.Add((o.ICalUid, o.Start.ToUnixTimeMilliseconds()));

    // The row's reminder choice, meeting link, and whether it's yours; null when it's gone or unreadable (Google's JSON, so that's rare)
    static RowAlerts? Read(SqliteConnection conn, CalendarOccurrence o, CalendarInfo? calendar)
    {
        if (EventStore.Get(conn, o.AccountId, o.CalendarId, o.EventId) is not { } stored)
        {
            return null;
        }

        try
        {
            using var doc  = JsonDocument.Parse(stored.RawJson);
            var root       = doc.RootElement;
            var useDefault = true;
            var overrides  = new List<int>();

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("reminders", out var reminders) && reminders.ValueKind == JsonValueKind.Object)
            {
                useDefault = reminders.TryGetProperty("useDefault", out var flag) && flag.ValueKind == JsonValueKind.True;
                if (reminders.TryGetProperty("overrides", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in list.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object
                            && item.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String && method.GetString() == "popup"
                            && item.TryGetProperty("minutes", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var m)
                            && m is >= 0 and <= MaxMinutes && !overrides.Contains(m))
                        {
                            overrides.Add(m);
                        }
                    }
                }
            }

            // A Toast's Join Opens Blind, So Only A Known Meeting Host's Link
            return new RowAlerts(useDefault, overrides, JoinPicker.KnownHost(EventDetailsParser.Parse(stored.RawJson).ConferenceUri), JoinPicker.IsMine(stored.RawJson, calendar));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    sealed record RowAlerts(bool UseDefault, IReadOnlyList<int> Overrides, Uri? Link, bool IsMine);
}
