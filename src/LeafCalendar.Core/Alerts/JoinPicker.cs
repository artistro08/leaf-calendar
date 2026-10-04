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
/// A meeting qualifies when it's yours (<see cref="IsMine"/>), timed, has a meeting link (Google's conference data, or a
/// known meeting host pasted in the location or description), and starts within <see cref="Lead"/> or is running. The
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

    /// <summary>
    /// The meeting the join shortcut opens: the one <see cref="Pick"/> chooses, else the soonest one starting within
    /// <paramref name="lookahead"/> (like the main window's Ctrl+J, which doesn't wait for the 10 minutes either), or null.
    /// </summary>
    public static JoinTarget? PickNext(IEnumerable<JoinTarget> candidates, DateTimeOffset now, TimeSpan lookahead)
    {
        var list = candidates.ToList();
        return Pick(list, now)
            ?? list.Where(c => !c.Occurrence.IsAllDay && c.Occurrence.Start > now && c.Occurrence.Start - now <= lookahead).MinBy(c => c.Occurrence.Start);
    }

    /// <summary>Your timed meetings around <paramref name="now"/> that have a meeting link and could qualify.</summary>
    public static IReadOnlyList<JoinTarget> Candidates(SqliteConnection conn, DateTimeOffset now, TimeZoneInfo zone) => Candidates(conn, now, zone, Lead);

    /// <summary>
    /// Your timed meetings (<see cref="IsMine"/>) with a known meeting host's link (<see cref="LinkSafety.ProviderOf"/>)
    /// that are running or start within <paramref name="lookahead"/>. A meeting on several calendars comes once, as its
    /// first copy that's yours.
    /// </summary>
    public static IReadOnlyList<JoinTarget> Candidates(SqliteConnection conn, DateTimeOffset now, TimeZoneInfo zone, TimeSpan lookahead)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now + lookahead, zone).DateTime);
        var calendars = CalendarStore.GetAll(conn).ToDictionary(c => (c.AccountId, c.Id));
        var seen = new HashSet<(string, long)>();
        var result = new List<JoinTarget>();
        foreach (var o in OccurrenceQuery.Load(conn, today.AddDays(-1), last.AddDays(2), zone, includeDeclined: false, keepSharedCopies: true))
        {
            if (o.IsAllDay || o.End <= now || o.Start - now > lookahead)
            {
                continue;
            }

            // Only Your Own Meetings (a colleague's calendar holds theirs)
            if (EventStore.Get(conn, o.AccountId, o.CalendarId, o.EventId) is not { } stored || !IsMine(stored.RawJson, calendars.GetValueOrDefault((o.AccountId, o.CalendarId))))
            {
                continue;
            }

            if (MeetingLink(conn, o) is { } link && AlertPlanner.First(seen, o))
            {
                result.Add(new JoinTarget(o, link));
            }
        }

        return result;
    }

    /// <summary>
    /// True when the meeting is yours: on your own (primary) calendar you're a guest who hasn't declined, or its
    /// organizer; or it has no guests and sits on a calendar you own. Elsewhere Google's <c>self</c> guest is the
    /// calendar's owner, not you (as <see cref="InviteWatcher"/> notes), so a colleague's meetings never count.
    /// </summary>
    /// <param name="rawJson">The event as stored.</param>
    /// <param name="calendar">The calendar holding this copy; null (not listed) is never yours.</param>
    public static bool IsMine(string rawJson, CalendarInfo? calendar)
    {
        if (calendar is null)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // No Guests: yours on a calendar you own
            if (!root.TryGetProperty("attendees", out var attendees) || attendees.ValueKind != JsonValueKind.Array || attendees.GetArrayLength() == 0)
            {
                return calendar.AccessRole == "owner";
            }

            return calendar.IsPrimary && (InviteWatcher.SelfResponse(root) is { } response ? response != "declined" : InviteWatcher.IsOrganizer(root));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The instance's meeting link as the event has it (descriptions included, like the alert planner), or null. The
    /// join shortcut, the tray, and notifications open it blind, so only a known meeting host's link
    /// (<see cref="KnownHost"/>); any other link waits for the details panel's Join button, which shows the address.
    /// </summary>
    public static Uri? MeetingLink(SqliteConnection conn, CalendarOccurrence occurrence)
    {
        if (EventStore.Get(conn, occurrence.AccountId, occurrence.CalendarId, occurrence.EventId) is not { } stored)
        {
            return null;
        }

        try
        {
            return KnownHost(EventDetailsParser.Parse(stored.RawJson, includeDescription: true).ConferenceUri);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><paramref name="link"/> when it's on a known meeting host (<see cref="LinkSafety.ProviderOf"/>), else null.</summary>
    public static Uri? KnownHost(Uri? link) => link is not null && LinkSafety.ProviderOf(link) is not null ? link : null;

    /// <summary>The address to open: Meet links with the event's account as <c>authuser</c>, others as they are.</summary>
    public static Uri JoinLink(SqliteConnection conn, JoinTarget target)
    {
        var email = AccountStore.GetAll(conn).FirstOrDefault(a => a.Id == target.Occurrence.AccountId)?.Email ?? "";
        return LinkSafety.JoinUri(target.Link, email);
    }

    /// <summary>The address the join shortcut opens now, or null when no meeting qualifies ("No meeting to join").</summary>
    public static Uri? Find(SqliteConnection conn, DateTimeOffset now, TimeZoneInfo zone) =>
        Pick(Candidates(conn, now, zone), now) is { } target ? JoinLink(conn, target) : null;

    /// <summary>The join shortcut's link (<see cref="PickNext"/>), or null when nothing with a link starts within <paramref name="lookahead"/>.</summary>
    public static Uri? FindNext(SqliteConnection conn, DateTimeOffset now, TimeZoneInfo zone, TimeSpan lookahead) =>
        PickNext(Candidates(conn, now, zone, lookahead), now, lookahead) is { } target ? JoinLink(conn, target) : null;
}
