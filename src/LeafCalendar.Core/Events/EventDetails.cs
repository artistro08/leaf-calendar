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
/// <param name="Visibility">Google's event visibility: <c>default</c>, <c>public</c>, <c>private</c>, or <c>confidential</c>. Anything else reads as <c>default</c>.</param>
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
    string Visibility = "default");
