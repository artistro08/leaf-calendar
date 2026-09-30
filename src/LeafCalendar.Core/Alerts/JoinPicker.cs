using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Alerts;

/// <summary>A meeting that could be joined and its own meeting link.</summary>
public sealed record JoinTarget(CalendarOccurrence Occurrence, Uri Link);

/// <summary>
/// Picks the meeting the join shortcut, the tray menu, and notifications open (spec 8.5).
/// </summary>
/// <remarks>
/// A meeting qualifies when it's timed, has a meeting link (Google's conference data, or a known meeting host pasted in
/// the location or description), you haven't declined it, and it starts within <see cref="Lead"/> or is running. The
/// soonest-starting one that hasn't started wins; with none, the running one that started most recently. Google Meet
/// links get <c>authuser=&lt;email&gt;</c> of the account the event came from, so the right Google account joins.
/// Links are found the same way <see cref="AlertPlanner"/> finds them, so the shortcut and "Join now" alerts agree.
/// </remarks>
public static class JoinPicker
{
    /// <summary>How soon a meeting must start to qualify.</summary>
    public static readonly TimeSpan Lead = TimeSpan.FromMinutes(10);

    /// <summary>The meeting to join from <paramref name="candidates"/>, or null.</summary>
    public static JoinTarget? Pick(IEnumerable<JoinTarget> candidates, DateTimeOffset now)
    {
        var open = candidates.Where(c => !c.Occurrence.IsAllDay && c.Occurrence.End > now).ToList();

        // Soonest Upcoming Within The Lead, Else The Latest-Started Running One
        return open.Where(c => c.Occurrence.Start > now && c.Occurrence.Start - now <= Lead).MinBy(c => c.Occurrence.Start)
            ?? open.Where(c => c.Occurrence.Start <= now).MaxBy(c => c.Occurrence.Start);
    }

    /// <summary>Timed, not declined instances around <paramref name="now"/> that have a meeting link and could qualify.</summary>
    public static IReadOnlyList<JoinTarget> Candidates(SqliteConnection conn, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today  = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var result = new List<JoinTarget>();
        foreach (var o in OccurrenceQuery.Load(conn, today.AddDays(-1), today.AddDays(2), zone, includeDeclined: false))
        {
            if (o.IsAllDay || o.End <= now || o.Start - now > Lead)
            {
                continue;
            }

            if (MeetingLink(conn, o) is { } link)
            {
                result.Add(new JoinTarget(o, link));
            }
        }

        return result;
    }

    /// <summary>The instance's meeting link as the event has it (descriptions included, like the alert planner), or null.</summary>
    public static Uri? MeetingLink(SqliteConnection conn, CalendarOccurrence occurrence)
    {
        if (EventStore.Get(conn, occurrence.AccountId, occurrence.CalendarId, occurrence.EventId) is not { } stored)
        {
            return null;
        }

        try
        {
            return EventDetailsParser.Parse(stored.RawJson, includeDescription: true).ConferenceUri;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The address to open: Meet links with the event's account as <c>authuser</c>, others as they are.</summary>
    public static Uri JoinLink(SqliteConnection conn, JoinTarget target)
    {
        var email = AccountStore.GetAll(conn).FirstOrDefault(a => a.Id == target.Occurrence.AccountId)?.Email ?? "";
        return LinkSafety.JoinUri(target.Link, email);
    }

    /// <summary>The address the join shortcut opens now, or null when no meeting qualifies ("No meeting to join").</summary>
    public static Uri? Find(SqliteConnection conn, DateTimeOffset now, TimeZoneInfo zone) =>
        Pick(Candidates(conn, now, zone), now) is { } target ? JoinLink(conn, target) : null;
}
