using LeafCalendar.Core.Events;

namespace LeafCalendar.Tests.Support;

/// <summary>Builds occurrences with neutral values for the fields a test doesn't care about.</summary>
public static class TestOccurrences
{
    /// <summary>An occurrence titled <paramref name="id"/> on account "acct", calendar "cal".</summary>
    public static CalendarOccurrence Make(string id, DateTimeOffset start, DateTimeOffset end, bool isAllDay) =>
        new("acct", "cal", id, null, null, start, end, isAllDay, id, EventKind.Default, ResponseStatus.Accepted, "#039BE5", null, false, false);
}
