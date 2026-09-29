using System.Globalization;

namespace LeafCalendar.Core.Events;

/// <summary>
/// One event instance on screen: a single event, one instance of a repeating series, or a
/// moved or edited instance.
/// </summary>
/// <remarks>
/// For all-day events, <see cref="Start"/> and <see cref="End"/> are UTC midnights of the dates.
/// Use <see cref="AllDayStart"/> and <see cref="AllDayEnd"/>, because all-day dates float across time zones.
/// </remarks>
public sealed record CalendarOccurrence(
    string AccountId,
    string CalendarId,
    string EventId,
    string? ICalUid,
    string? RecurringEventId,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsAllDay,
    string Title,
    EventKind Kind,
    ResponseStatus SelfResponse,
    string CalendarColor,
    string? ColorId,
    bool IsFree,
    bool HasConference)
{
    /// <summary>First all-day date.</summary>
    public DateOnly AllDayStart => DateOnly.FromDateTime(Start.UtcDateTime);

    /// <summary>All-day end date (exclusive).</summary>
    public DateOnly AllDayEnd => DateOnly.FromDateTime(End.UtcDateTime);

    /// <summary>Unique per instance (a series' instances differ by start).</summary>
    public string Key => string.Create(CultureInfo.InvariantCulture, $"{AccountId}|{CalendarId}|{EventId}|{Start.UtcTicks}");
}
