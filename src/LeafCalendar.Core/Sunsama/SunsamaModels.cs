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
/// <param name="Notes">The task's notes as Sunsama's HTML ("" when none); shown only as plain text.</param>
/// <param name="Subtasks">The subtasks, in order (none when null).</param>
/// <param name="CompletedAt">When the task was done, or null.</param>
/// <param name="CompletedOn">The day (in the Sunsama zone) the task was done, or null.</param>
/// <param name="HasEventIds">The task names a timebox or imported-from event, found among the day's events or not.</param>
public sealed record SunsamaTask(
    string Id,
    string Title,
    string Channel,
    int? EstimateMinutes,
    int? TrackedMinutes,
    bool Completed,
    IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> Projected,
    IReadOnlyList<SunsamaEventRef> Events,
    string Notes = "",
    IReadOnlyList<SunsamaSubtask>? Subtasks = null,
    DateTimeOffset? CompletedAt = null,
    DateOnly? CompletedOn = null,
    bool HasEventIds = false);

/// <summary>One step of a Sunsama task. Untrusted text, shown plain.</summary>
/// <param name="Title">Title ("" when missing).</param>
/// <param name="Completed">The step is done.</param>
public sealed record SunsamaSubtask(string Title, bool Completed);

/// <summary>
/// Subtasks as one line each, so a saved row keeps them as text and compares by value: <c>1</c> (done) or <c>0</c>,
/// then the title.
/// </summary>
public static class SunsamaSubtasks
{
    /// <summary>The subtasks as text; a line break in a title becomes a space.</summary>
    /// <param name="subtasks">The subtasks, in order.</param>
    /// <returns>The text ("" for none).</returns>
    public static string Encode(IEnumerable<SunsamaSubtask> subtasks)
    {
        ArgumentNullException.ThrowIfNull(subtasks);
        return string.Join('\n', subtasks.Select(s => (s.Completed ? "1" : "0") + s.Title.Replace('\r', ' ').Replace('\n', ' ')));
    }

    /// <summary>Reads text from <see cref="Encode"/>.</summary>
    /// <param name="text">The saved text.</param>
    /// <returns>The subtasks, in order.</returns>
    public static IReadOnlyList<SunsamaSubtask> Decode(string? text) =>
        string.IsNullOrEmpty(text) ? [] : [.. text.Split('\n').Select(line => new SunsamaSubtask(line.Length > 0 ? line[1..] : "", line.StartsWith('1')))];
}
