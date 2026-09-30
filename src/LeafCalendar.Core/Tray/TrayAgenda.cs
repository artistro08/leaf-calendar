using System.Globalization;
using LeafCalendar.Core.Alerts;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Views;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Tray;

/// <summary>One flyout row: the instance, its one-line title, "9 AM – 10 AM" (or "All day"), and its meeting link.</summary>
public sealed record AgendaItem(CalendarOccurrence Occurrence, string Title, string When, Uri? Link);

/// <summary>One day of the flyout's agenda, headed "Today", "Tomorrow", or the date.</summary>
public sealed record AgendaDay(DateOnly Date, string Header, IReadOnlyList<AgendaItem> Items);

/// <summary>The flyout header's and the tooltip's next event, with "in 12 min" or "Now".</summary>
public sealed record NextUp(AgendaItem Item, string Countdown);

/// <summary>
/// What the tray shows: the flyout's agenda by day, its "next event" header, and the icon's tooltip (spec 8.1, 8.2).
/// </summary>
/// <remarks>
/// Days start today and skip empty ones. Events that already ended leave today; one still running since yesterday
/// shows under today. All-day events come first, and only when asked for. Declined events and hidden calendars are left
/// out, like everywhere else. The next event is the soonest one starting within the lookahead, except that a running
/// meeting stays in the header until the next one is within 10 minutes of starting (the join rule's window).
/// </remarks>
public static class TrayAgenda
{
    /// <summary>The longest tooltip the shell shows (<c>NOTIFYICONDATA.szTip</c> is 128 characters with the terminator).</summary>
    public const int MaxTooltip = 127;

    const int MaxTitle = 200;

    /// <summary>The agenda for <paramref name="days"/> days from today (local to <paramref name="zone"/>).</summary>
    public static IReadOnlyList<AgendaDay> Load(SqliteConnection conn, DateTimeOffset now, TimeZoneInfo zone, int days, bool includeAllDay, bool use24Hour)
    {
        var today       = LocalDate(now, zone);
        var occurrences = OccurrenceQuery.Load(conn, today.AddDays(-1), today.AddDays(days), zone, includeDeclined: false);
        var links       = new Dictionary<(string, string, string), Uri?>();
        var result      = new List<AgendaDay>();

        for (var i = 0; i < days; i++)
        {
            var date  = today.AddDays(i);
            var items = occurrences
                .Where(o => o.Kind != EventKind.WorkingLocation && (includeAllDay || !o.IsAllDay) && o.EndIn(zone) > now && IsOn(o, date, today, now, zone))
                .OrderBy(o => !o.IsAllDay)
                .ThenBy(o => o.Start)
                .Select(o => new AgendaItem(
                    o,
                    Title(o),
                    o.IsAllDay ? "All day" : TimeLabels.Range(o.Start, o.End, zone, use24Hour),
                    o.IsAllDay ? null : Link(conn, links, o)))
                .ToList();

            if (items.Count > 0)
            {
                result.Add(new AgendaDay(date, DayHeader(date, today), items));
            }
        }

        return result;
    }

    /// <summary>The header's event within <paramref name="lookahead"/>, or null.</summary>
    public static NextUp? Next(IReadOnlyList<AgendaDay> days, DateTimeOffset now, TimeSpan lookahead)
    {
        var timed = days
            .SelectMany(d => d.Items)
            .Where(i => !i.Occurrence.IsAllDay && i.Occurrence.End > now)
            .DistinctBy(i => i.Occurrence.Key)
            .ToList();

        var upcoming = timed.Where(i => i.Occurrence.Start > now && i.Occurrence.Start - now <= lookahead).MinBy(i => i.Occurrence.Start);
        var running  = timed.Where(i => i.Occurrence.Start <= now).MaxBy(i => i.Occurrence.Start);
        var pick     = upcoming is not null && (running is null || upcoming.Occurrence.Start - now <= JoinPicker.Lead) ? upcoming : running ?? upcoming;

        return pick is null ? null : new NextUp(pick, TimeLabels.Relative(pick.Occurrence.Start, pick.Occurrence.End, now));
    }

    /// <summary>"Standup in 12 min", "Standup now", or "Leaf Calendar" when nothing is coming up; at most 127 characters.</summary>
    public static string Tooltip(NextUp? next)
    {
        if (next is null)
        {
            return "Leaf Calendar";
        }

        var suffix = " " + char.ToLowerInvariant(next.Countdown[0]) + next.Countdown[1..];
        return DisplayText.Clean(next.Item.Title, MaxTooltip - suffix.Length) + suffix;
    }

    /// <summary>The header's empty sentence for a lookahead: "Nothing in the next hour."</summary>
    public static string NothingNext(int lookaheadMinutes) => lookaheadMinutes switch
    {
        60   => "Nothing in the next hour.",
        < 60 => string.Create(CultureInfo.InvariantCulture, $"Nothing in the next {lookaheadMinutes} minutes."),
        _    => string.Create(CultureInfo.InvariantCulture, $"Nothing in the next {lookaheadMinutes / 60} hours."),
    };

    /// <summary>"Today", "Tomorrow", or "Saturday, October 3".</summary>
    public static string DayHeader(DateOnly day, DateOnly today) =>
        day == today ? "Today" : day == today.AddDays(1) ? "Tomorrow" : TimeLabels.LongDate(day);

    // One-Line Title, Or "(No title)" When Nothing Visible Is Left
    static string Title(CalendarOccurrence o) => DisplayText.Clean(o.Title, MaxTitle) is { Length: > 0 } title ? title : EventDetailsParser.NoTitle;

    static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    // All-day: every date it covers. Timed: the day it starts, or today when it's been running since before today.
    static bool IsOn(CalendarOccurrence o, DateOnly date, DateOnly today, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (o.IsAllDay)
        {
            return date >= o.AllDayStart && date < o.AllDayEnd;
        }

        var startDay = LocalDate(o.Start, zone);
        return startDay == date || (date == today && startDay < today && o.Start <= now);
    }

    // One lookup per stored row (a series' instances share their master's link)
    static Uri? Link(SqliteConnection conn, Dictionary<(string, string, string), Uri?> links, CalendarOccurrence o)
    {
        var key = (o.AccountId, o.CalendarId, o.EventId);
        if (!links.TryGetValue(key, out var link))
        {
            links[key] = link = JoinPicker.MeetingLink(conn, o);
        }

        return link;
    }
}
