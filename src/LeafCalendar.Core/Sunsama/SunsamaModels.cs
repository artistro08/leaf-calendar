namespace LeafCalendar.Core.Sunsama;

/// <summary>Who is signed in to Sunsama: their email, their Sunsama time zone and which of their calendars are Google ones.</summary>
/// <param name="Email">The account email (shown in Settings, never logged).</param>
/// <param name="Zone">The zone Sunsama writes its projected local times in.</param>
/// <param name="GoogleCalendarIds">Calendar IDs whose provider is <c>google-calendar</c>; their events are already in Leaf Calendar.</param>
public sealed record SunsamaProfile(string Email, TimeZoneInfo Zone, IReadOnlySet<string> GoogleCalendarIds);

/// <summary>A timed calendar event a task is tied to (a timebox, or the event the task was imported from).</summary>
/// <param name="EventId">Sunsama's event ID.</param>
/// <param name="CalendarId">The calendar it's on (a Google calendar ID, or Sunsama's own calendar).</param>
/// <param name="Title">The event's title, for matching to the Leaf Calendar copy.</param>
/// <param name="Start">Start instant.</param>
/// <param name="End">End instant.</param>
/// <param name="OnGoogle">True when the calendar is a Google one, so Leaf Calendar already shows this event.</param>
public sealed record SunsamaEventRef(string EventId, string CalendarId, string Title, DateTimeOffset Start, DateTimeOffset End, bool OnGoogle);

/// <summary>One Sunsama task on a day, as read: its text, times and totals. Untrusted text, shown plain.</summary>
/// <param name="Id">Sunsama's task ID.</param>
/// <param name="Title">Title ("" when missing).</param>
/// <param name="Channel">Channel name ("" when missing).</param>
/// <param name="EstimateMinutes">The planned time, or null when unreadable.</param>
/// <param name="TrackedMinutes">The tracked total, or null when unreadable or none.</param>
/// <param name="Completed">The task is done.</param>
/// <param name="Projected">Sunsama's automatic placement for the day, in order.</param>
/// <param name="Events">Timed events the task is tied to (timeboxes and the imported-from event).</param>
public sealed record SunsamaTask(
    string Id,
    string Title,
    string Channel,
    int? EstimateMinutes,
    int? TrackedMinutes,
    bool Completed,
    IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> Projected,
    IReadOnlyList<SunsamaEventRef> Events);
