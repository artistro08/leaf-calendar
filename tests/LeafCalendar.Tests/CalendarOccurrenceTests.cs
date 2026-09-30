using LeafCalendar.Core.Events;

namespace LeafCalendar.Tests;

public class CalendarOccurrenceTests
{
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    static CalendarOccurrence Timed(DateTimeOffset start, DateTimeOffset end) =>
        new("a", "c", "timed", null, null, start, end, false, "Timed", EventKind.Default, ResponseStatus.Accepted, "#4285F4", null, false, false);

    // All-day dates are stored as UTC midnights (end exclusive)
    static CalendarOccurrence AllDay(DateOnly first, int days)
    {
        var start = new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return new("a", "c", "allday", null, null, start, start.AddDays(days), true, "All day", EventKind.Default, ResponseStatus.Accepted, "#4285F4", null, false, false);
    }

    static DateTimeOffset Local(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, TimeSpan.FromHours(-4));

    [Fact]
    public void HasEndedBy_TimedEvent_EndsAtItsEnd()
    {
        var o = Timed(Local(1, 9), Local(1, 10));

        Assert.False(o.HasEndedBy(Local(1, 9, 59), NewYork));
        Assert.True(o.HasEndedBy(Local(1, 10), NewYork));
    }

    [Fact]
    public void HasEndedBy_AllDayEvent_EndsAtLocalMidnightNotUtcMidnight()
    {
        var o = AllDay(new DateOnly(2026, 10, 1), 1);

        // 8 PM in New York is already midnight UTC; the day isn't over yet
        Assert.False(o.HasEndedBy(Local(1, 20), NewYork));
        Assert.False(o.HasEndedBy(Local(1, 23, 59), NewYork));
        Assert.True(o.HasEndedBy(Local(2, 0), NewYork));
    }
}
