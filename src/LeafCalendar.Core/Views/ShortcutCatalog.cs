namespace LeafCalendar.Core.Views;

/// <summary>One cheat-sheet row: its section, the keys as the spec writes them, the action, and the command it runs (if any).</summary>
public sealed record ShortcutRow(string Section, string Keys, string Action, CalendarCommand Command = CalendarCommand.None);

/// <summary>
/// The in-app shortcut cheat sheet (spec 8.7), copied verbatim in spec order. "Shift+drag · Box select" is left out
/// until Milestone 6 builds it, because a sheet mustn't list a key that does nothing.
/// </summary>
public static class ShortcutCatalog
{
    /// <summary>The section headings, in spec order.</summary>
    public static IReadOnlyList<string> Sections { get; } = ["Navigation", "App", "Events", "Selection", "Scheduling and people"];

    /// <summary>The spec's note under the App table.</summary>
    public const string Footnote = "Ctrl+wheel over the time grid zooms like Ctrl+= / Ctrl+-.";

    /// <summary>Every row, in spec order.</summary>
    public static IReadOnlyList<ShortcutRow> Rows { get; } =
    [
        // Navigation
        new("Navigation", "T", "Today", CalendarCommand.Today),
        new("Navigation", "← / →", "Previous / next period", CalendarCommand.Previous),
        new("Navigation", "J / K", "Next / previous period", CalendarCommand.Next),
        new("Navigation", "N", "Next event", CalendarCommand.NextEvent),
        new("Navigation", "B or Shift+N", "Previous event", CalendarCommand.PreviousEvent),
        new("Navigation", ".", "Go to date", CalendarCommand.GoToDate),
        new("Navigation", "D or 1", "Day view", CalendarCommand.DayView),
        new("Navigation", "W or 0", "Week view", CalendarCommand.WeekView),
        new("Navigation", "M", "Month view", CalendarCommand.MonthView),
        new("Navigation", "2–9", "Show that many days", CalendarCommand.Days),
        new("Navigation", "Z", "Time travel to another time zone", CalendarCommand.TimeTravel),

        // App
        new("App", "Ctrl+K", "Command menu", CalendarCommand.CommandMenu),
        new("App", "Ctrl+F or /", "Search (command menu)", CalendarCommand.Search),
        new("App", "?", "Shortcut cheat sheet", CalendarCommand.ShortcutSheet),
        new("App", "Ctrl+,", "Settings", CalendarCommand.OpenSettings),
        new("App", "Ctrl+Z", "Undo the last delete (this session)", CalendarCommand.Undo),
        new("App", "Alt+Left / Alt+Right, mouse back / forward", "Back / forward through visited views", CalendarCommand.NavigateBack),
        new("App", "Ctrl+Shift+L", "Toggle light / dark", CalendarCommand.ToggleTheme),
        new("App", "Ctrl+= / Ctrl+- / Ctrl+0", "Zoom in / out / reset", CalendarCommand.ZoomIn),
        new("App", "Ctrl+Shift+E", "Show / hide weekends", CalendarCommand.ToggleWeekends),
        new("App", "Ctrl+Shift+D", "Show / hide declined events", CalendarCommand.ToggleDeclined),

        // Events
        new("Events", "C", "Create event", CalendarCommand.CreateEvent),
        new("Events", "E", "Edit selected event", CalendarCommand.EditEvent),
        new("Events", "E then Y / N / M", "RSVP yes / no / maybe", CalendarCommand.RsvpYes),
        new("Events", "E then E", "Email guests", CalendarCommand.EmailGuests),
        new("Events", "E then U", "Edit duration", CalendarCommand.EditDuration),
        new("Events", "E then Z", "Edit time zone", CalendarCommand.EditTimeZone),
        new("Events", "E then F", "Participant overlay", CalendarCommand.ParticipantOverlay),
        new("Events", "Ctrl+Enter", "Save and email guests"),
        new("Events", "Ctrl+Shift+Enter", "Save without emailing"),
        new("Events", "Ctrl+Shift+Delete", "Cancel event without emailing", CalendarCommand.CancelEventQuietly),
        new("Events", "Delete", "Delete selected", CalendarCommand.DeleteSelected),
        new("Events", "Ctrl+J", "Join selected or next meeting", CalendarCommand.JoinMeeting),
        new("Events", "V", "Open meeting link in browser", CalendarCommand.OpenMeetingLink),

        // Selection (Shift+drag box select waits for Milestone 6)
        new("Selection", "X", "Select / deselect", CalendarCommand.ToggleSelect),
        new("Selection", "Ctrl+A", "Select all visible", CalendarCommand.SelectAll),
        new("Selection", "Ctrl+click", "Add to / remove from selection"),
        new("Selection", "Ctrl+C / Ctrl+X / Ctrl+V", "Copy / cut / paste events", CalendarCommand.Copy),
        new("Selection", "Alt+drag", "Duplicate"),
        new("Selection", "Esc", "Clear selection or close panel"),

        // Scheduling And People
        new("Scheduling and people", "S", "Share availability", CalendarCommand.ShareAvailability),
        new("Scheduling and people", "P", "Overlay a teammate's calendar", CalendarCommand.PeopleOverlay),
        new("Scheduling and people", "F", "Meet with", CalendarCommand.MeetWith),
    ];

    /// <summary>The rows where every word of <paramref name="query"/> is in the keys or the action (case-insensitive); all rows when it's blank.</summary>
    public static IReadOnlyList<ShortcutRow> Filter(string? query)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. Rows.Where(r => words.All(w => r.Keys.Contains(w, StringComparison.OrdinalIgnoreCase) || r.Action.Contains(w, StringComparison.OrdinalIgnoreCase)))];
    }
}
