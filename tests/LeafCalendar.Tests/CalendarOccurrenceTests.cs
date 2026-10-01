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

    [Fact]
    public void HasEndedBy_MultiDayAllDayEvent_EndsAfterItsLastDate()
    {
        var o = AllDay(new DateOnly(2026, 10, 1), 3);

        Assert.False(o.HasEndedBy(Local(3, 23, 59), NewYork));
        Assert.True(o.HasEndedBy(Local(4, 0), NewYork));
    }

    // Nov 1, 2026 is the day New York falls back, so the day ends at midnight EST (05:00Z), not EDT
    [Fact]
    public void HasEndedBy_AllDayEventOnFallBackDay_EndsAtMidnightStandardTime()
    {
        var o = AllDay(new DateOnly(2026, 11, 1), 1);

        Assert.False(o.HasEndedBy(new DateTimeOffset(2026, 11, 2, 4, 59, 0, TimeSpan.Zero), NewYork));
        Assert.True(o.HasEndedBy(new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.Zero), NewYork));
    }
}
