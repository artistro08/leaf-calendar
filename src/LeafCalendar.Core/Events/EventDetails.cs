namespace LeafCalendar.Core.Events;

/// <summary>Google's event type, as Leaf draws it.</summary>
public enum EventKind
{
    /// <summary>Ordinary event.</summary>
    Default,

    /// <summary>Focus time (Workspace).</summary>
    FocusTime,

    /// <summary>Out of office (Workspace).</summary>
    OutOfOffice,

    /// <summary>Birthday.</summary>
    Birthday,

    /// <summary>Working location (Workspace).</summary>
    WorkingLocation,
}

/// <summary>The signed-in user's response to an event.</summary>
public enum ResponseStatus
{
    /// <summary>Going (also used when you are the only attendee or the organizer).</summary>
    Accepted,

    /// <summary>Maybe.</summary>
    Tentative,

    /// <summary>Not going.</summary>
    Declined,

    /// <summary>Hasn't answered.</summary>
    NeedsAction,
}

/// <summary>
/// What Leaf shows about one event, read from Google's stored JSON. Everything here comes from people
/// who can send you invites, so it is displayed as plain text only.
/// </summary>
/// <param name="Title">The event's title, or "(No title)" when it has none.</param>
/// <param name="Location">The location as the organizer typed it, or null.</param>
/// <param name="Description">The description as plain text (empty when there is none).</param>
/// <param name="Kind">The kind of event (a meeting, Focus time, Out of office, a birthday, and so on).</param>
/// <param name="SelfResponse">Your answer to the invitation.</param>
/// <param name="ColorId">Google's event color ID, or null for the calendar's color.</param>
/// <param name="ConferenceUri">The meeting link, or null.</param>
/// <param name="IsFree">True when the event shows you as free.</param>
/// <param name="GuestCount">The number of guests.</param>
/// <param name="OrganizerEmail">The organizer's email address, or null.</param>
/// <param name="Visibility">Google's event visibility: <c>default</c>, <c>public</c>, <c>private</c>, or <c>confidential</c>. Anything else reads as <c>default</c>.</param>
/// <param name="HasOtherGuests">True when a guest other than you is on the event (rooms don't count).</param>
public sealed record EventDetails(
    string Title,
    string? Location,
    string Description,
    EventKind Kind,
    ResponseStatus SelfResponse,
    string? ColorId,
    Uri? ConferenceUri,
    bool IsFree,
    int GuestCount,
    string? OrganizerEmail,
    string Visibility = "default",
    bool HasOtherGuests = false);
