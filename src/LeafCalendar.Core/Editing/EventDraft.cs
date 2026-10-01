using LeafCalendar.Core.Events;

namespace LeafCalendar.Core.Editing;

/// <summary>
/// A guest as the editor shows it. Google owns <see cref="IsSelf"/> and <see cref="IsOrganizer"/>.
/// <see cref="IsResource"/> marks a room (Google's <c>resource</c> attendees).
/// </summary>
public sealed record Guest(
    string Email,
    bool Optional = false,
    ResponseStatus Response = ResponseStatus.NeedsAction,
    string? Comment = null,
    bool IsSelf = false,
    bool IsOrganizer = false,
    bool IsResource = false);

/// <summary>
/// The fields Leaf's editor changes. Comparing a "before" and an "after" draft gives the patch Leaf sends
/// (<see cref="EventJson.BuildPatch"/>), so fields the editor doesn't know about are never touched.
/// </summary>
/// <remarks>
/// Times follow <see cref="CalendarOccurrence"/>: all-day events use UTC midnights with an exclusive end.
/// <see cref="Description"/> is Leaf-normalized description HTML (<see cref="DescriptionHtml.Normalize"/>); it's only written back when it changed.
/// </remarks>
public sealed record EventDraft
{
    /// <summary>Google account.</summary>
    public required string AccountId { get; init; }

    /// <summary>Calendar (changing it moves the event).</summary>
    public required string CalendarId { get; init; }

    /// <summary>Title (empty is allowed; views show "(No title)").</summary>
    public string Title { get; init; } = "";

    /// <summary>Start instant (all-day: UTC midnight of the first date).</summary>
    public DateTimeOffset Start { get; init; }

    /// <summary>End instant (all-day: UTC midnight after the last date).</summary>
    public DateTimeOffset End { get; init; }

    /// <summary>All-day event.</summary>
    public bool IsAllDay { get; init; }

    /// <summary>The event's IANA time zone (Google needs one for repeating timed events).</summary>
    public string? TimeZone { get; init; }

    /// <summary>Location (plain text).</summary>
    public string Location { get; init; } = "";

    /// <summary>Description as Leaf-normalized description HTML (empty when none).</summary>
    public string Description { get; init; } = "";

    /// <summary>
    /// True when reading cut the description short (<see cref="DescriptionHtml.IsTooLong"/>). Leaf then never writes the
    /// description back, since that would delete the rest of it on Google, and the editor shows it read-only.
    /// </summary>
    public bool DescriptionTooLong { get; init; }

    /// <summary>Google event color 1-11, or null for the calendar's color.</summary>
    public string? ColorId { get; init; }

    /// <summary>Guests in Google's order.</summary>
    public IReadOnlyList<Guest> Guests { get; init; } = [];

    /// <summary>True to use the calendar's default reminders.</summary>
    public bool UseDefaultReminders { get; init; } = true;

    /// <summary>Custom popup reminders, minutes before start (used when <see cref="UseDefaultReminders"/> is false).</summary>
    public IReadOnlyList<int> ReminderMinutes { get; init; } = [];

    /// <summary>Google's recurrence lines (RRULE/EXDATE/RDATE); empty for a single event.</summary>
    public IReadOnlyList<string> Recurrence { get; init; } = [];

    /// <summary>The video call link (shown, never edited).</summary>
    public Uri? ConferenceUri { get; init; }

    /// <summary>The event has a video call from Google (or one is being added); off for new events.</summary>
    public bool HasConference { get; init; }
}
