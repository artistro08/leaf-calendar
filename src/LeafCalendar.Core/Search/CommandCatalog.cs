using System.Globalization;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Core.Search;

/// <summary>
/// An action in the command menu (spec 6.6). Items with a <see cref="Command"/> run exactly as their shortcut does;
/// the rest are run by <see cref="Id"/> in the app. <see cref="Keys"/> is the shortcut text shown on the right, or empty.
/// </summary>
public sealed record CommandItem(string Id, string Title, string Keys, CalendarCommand Command = CalendarCommand.None, int Days = 0, string Keywords = "");

/// <summary>Every command-menu action, and matching typed text against them.</summary>
public static class CommandCatalog
{
    /// <summary>Every action, in the order shown when scores tie.</summary>
    public static IReadOnlyList<CommandItem> All { get; } =
    [
        new("create-event", "Create event", "C", CalendarCommand.CreateEvent, Keywords: "new add"),
        new("go-to-date", "Jump to date…", ".", CalendarCommand.GoToDate, Keywords: "go calendar"),
        new("today", "Go to today", "T", CalendarCommand.Today),
        new("view-day", "Switch to day view", "D", CalendarCommand.DayView, Keywords: "view"),
        new("view-week", "Switch to week view", "W", CalendarCommand.WeekView, Keywords: "view"),
        new("view-month", "Switch to month view", "M", CalendarCommand.MonthView, Keywords: "view"),
        .. Enumerable.Range(2, 8).Select(n => new CommandItem($"view-days-{n}", $"Show {n} days", n.ToString(CultureInfo.InvariantCulture), CalendarCommand.Days, n, "view")),
        new("join", "Join next meeting", "Ctrl+J", CalendarCommand.JoinMeeting, Keywords: "call video meet"),
        new("overlay", "Overlay a teammate's calendar…", "P", CalendarCommand.PeopleOverlay, Keywords: "people person busy"),
        new("meet-with", "Meet with…", "F", CalendarCommand.MeetWith, Keywords: "people find time schedule"),
        new("time-travel", "Time travel to a time zone…", "Z", CalendarCommand.TimeTravel, Keywords: "zone timezone"),
        new("share", "Share availability", "S", CalendarCommand.ShareAvailability, Keywords: "free slots copy"),
        new("toggle-weekends", "Show or hide weekends", "Ctrl+Shift+E", CalendarCommand.ToggleWeekends, Keywords: "toggle"),
        new("toggle-declined", "Show or hide declined events", "Ctrl+Shift+D", CalendarCommand.ToggleDeclined, Keywords: "toggle"),
        new("toggle-week-numbers", "Show or hide week numbers", "", Keywords: "toggle"),
        new("toggle-24-hour", "Turn 24-hour time on or off", "", Keywords: "toggle clock"),
        new("toggle-working-hours", "Show or hide working hours", "", Keywords: "toggle shading"),
        new("toggle-theme", "Switch between light and dark", "Ctrl+Shift+L", CalendarCommand.ToggleTheme, Keywords: "theme"),
        new("settings", "Settings", "Ctrl+,", CalendarCommand.OpenSettings, Keywords: "open preferences options"),
        new("settings-general", "Settings: General", "", Keywords: "preferences"),
        new("settings-calendars", "Settings: Calendars", "", Keywords: "preferences colors"),
        new("settings-time-zones", "Settings: Time zones", "", Keywords: "preferences"),
        new("settings-notifications", "Settings: Notifications", "", Keywords: "preferences reminders"),
        new("settings-tray", "Settings: Tray", "", Keywords: "preferences"),
        new("settings-shortcuts", "Settings: Shortcuts", "", Keywords: "preferences keys"),
        new("settings-accounts", "Settings: Accounts", "", Keywords: "preferences google"),
        new("settings-about", "Settings: About", "", Keywords: "version licenses logs"),
        new("shortcuts", "Show keyboard shortcuts", "?", CalendarCommand.ShortcutSheet, Keywords: "keys help cheat sheet"),
        new("sync", "Sync now", "", Keywords: "refresh"),
        new("back", "Go back", "Alt+Left", CalendarCommand.NavigateBack),
        new("forward", "Go forward", "Alt+Right", CalendarCommand.NavigateForward),
        new("quit", "Quit Leaf", "", Keywords: "exit close"),
    ];

    /// <summary>What an empty menu shows.</summary>
    public static IReadOnlyList<CommandItem> Defaults { get; } =
        [.. new[] { "create-event", "go-to-date", "share", "meet-with", "overlay", "time-travel", "settings", "shortcuts" }.Select(id => All.Single(c => c.Id == id))];

    /// <summary>
    /// The actions matching every typed word, best first (at most <paramref name="max"/>); <see cref="Defaults"/> when nothing is typed.
    /// </summary>
    /// <remarks>
    /// Each word scores 3 when the title starts with it, 2 when a title word does, 1 when the title contains it or a
    /// keyword starts with it. A word that scores 0 drops the action. Ties keep <see cref="All"/>'s order.
    /// </remarks>
    public static IReadOnlyList<CommandItem> Match(string? query, int max = 8)
    {
        // Nothing Typed
        var words = EventSearch.Words(query).Select(w => w.ToLowerInvariant()).ToList();
        if (words.Count == 0)
        {
            return Defaults;
        }

        // Score Every Action; Any Missed Word Drops It
        var scored = new List<(CommandItem Item, int Score, int Order)>();
        for (var i = 0; i < All.Count; i++)
        {
            var title      = All[i].Title.ToLowerInvariant();
            var titleWords = title.Split(' ');
            var keywords   = All[i].Keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var scores     = words.Select(w =>
                title.StartsWith(w, StringComparison.Ordinal) ? 3
                : titleWords.Any(t => t.StartsWith(w, StringComparison.Ordinal)) ? 2
                : title.Contains(w, StringComparison.Ordinal) || keywords.Any(k => k.StartsWith(w, StringComparison.Ordinal)) ? 1
                : 0).ToList();

            if (!scores.Contains(0))
            {
                scored.Add((All[i], scores.Sum(), i));
            }
        }

        return [.. scored.OrderByDescending(s => s.Score).ThenBy(s => s.Order).Take(max).Select(s => s.Item)];
    }

    /// <summary>
    /// True when the typed words name an action: every word starts a word of some action's title ("sett", "jump to").
    /// A match through keywords alone ("go" for Jump to date) doesn't count, so events still come first for it.
    /// </summary>
    public static bool NamesAnAction(string? query)
    {
        var words = EventSearch.Words(query).Select(w => w.ToLowerInvariant()).ToList();
        return words.Count > 0 && All.Any(item =>
        {
            var titleWords = item.Title.ToLowerInvariant().Split(' ');
            return words.All(w => titleWords.Any(t => t.StartsWith(w, StringComparison.Ordinal)));
        });
    }
}
