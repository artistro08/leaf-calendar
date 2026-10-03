using System.Globalization;
using LeafCalendar.Core.Recurrence;

namespace LeafCalendar.Tests;

public class RecurrenceExpanderTests
{
    [Theory]
    [InlineData("Europe/Kyiv")]
    [InlineData("America/New_York")]
    [InlineData("Eastern Standard Time")]
    public void FindZone_KnownOrRenamedZone_KeepsItsDaylightRules(string id)
    {
        var zone = RecurrenceExpander.FindZone(id);

        Assert.NotNull(zone);
        Assert.True(zone.SupportsDaylightSavingTime);
    }

    [Theory]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("")]
    [InlineData(null)]
    public void FindZone_Unknown_IsNull(string? id) => Assert.Null(RecurrenceExpander.FindZone(id));

    const string NewYork = "America/New_York";

    static readonly DateTimeOffset Always = DateTimeOffset.MinValue;
    static readonly DateTimeOffset Never  = DateTimeOffset.MaxValue;

    static DateTimeOffset Ny(int year, int month, int day, int hour, int minute, int offsetHours) =>
        new(year, month, day, hour, minute, 0, TimeSpan.FromHours(offsetHours));

    static string[] Utc(IEnumerable<DateTimeOffset> values) =>
        [.. values.Select(v => v.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))];

    [Fact]
    public void ExpandTimed_WeeklyAcrossDst_KeepsWallClock()
    {
        var result = RecurrenceExpander.ExpandTimed(["RRULE:FREQ=WEEKLY;BYDAY=MO,WE;COUNT=6"], Ny(2026, 10, 26, 9, 0, -4), NewYork, Always, Never);

        Assert.Equal(
            ["2026-10-26 13:00", "2026-10-28 13:00", "2026-11-02 14:00", "2026-11-04 14:00", "2026-11-09 14:00", "2026-11-11 14:00"],
            Utc(result));
    }

    [Theory]
    [InlineData("EXDATE;TZID=America/New_York:20261104T090000")]
    [InlineData("EXDATE:20261104T140000Z")]
    public void ExpandTimed_Exdate_RemovesOccurrence(string exdate)
    {
        var result = RecurrenceExpander.ExpandTimed(["RRULE:FREQ=WEEKLY;BYDAY=MO,WE;COUNT=6", exdate], Ny(2026, 10, 26, 9, 0, -4), NewYork, Always, Never);

        Assert.Equal(5, result.Count);
        Assert.DoesNotContain("2026-11-04 14:00", Utc(result));
    }

    [Fact]
    public void ExpandTimed_ExdateInFallBackHour_RemovesFirstOccurrence()
    {
        var result = RecurrenceExpander.ExpandTimed(
            ["RRULE:FREQ=DAILY;COUNT=4", "EXDATE;TZID=America/New_York:20261101T013000"],
            Ny(2026, 10, 30, 1, 30, -4),
            NewYork,
            Always,
            Never);

        Assert.Equal(3, result.Count);
        Assert.DoesNotContain("2026-11-01 05:30", Utc(result));
    }

    [Fact]
    public void ExpandTimed_Rdate_AddsOccurrencesInOrder()
    {
        var result = RecurrenceExpander.ExpandTimed(
            ["RRULE:FREQ=WEEKLY;BYDAY=MO;COUNT=2", "RDATE;TZID=America/New_York:20261029T090000,20261030T090000"],
            Ny(2026, 10, 26, 9, 0, -4),
            NewYork,
            Always,
            Never);

        Assert.Equal(["2026-10-26 13:00", "2026-10-29 13:00", "2026-10-30 13:00", "2026-11-02 14:00"], Utc(result));
    }

    [Fact]
    public void ExpandTimed_LastFridayBySetPos_ReturnsLastFridays()
    {
        var result = RecurrenceExpander.ExpandTimed(["RRULE:FREQ=MONTHLY;BYDAY=FR;BYSETPOS=-1;COUNT=4"], Ny(2026, 10, 30, 17, 0, -4), NewYork, Always, Never);

        Assert.Equal(["2026-10-30 21:00", "2026-11-27 22:00", "2026-12-25 22:00", "2027-01-29 22:00"], Utc(result));
    }

    [Fact]
    public void ExpandTimed_SecondTuesdayUntilUtc_StopsAtUntil()
    {
        var result = RecurrenceExpander.ExpandTimed(["RRULE:FREQ=MONTHLY;BYDAY=2TU;UNTIL=20270101T000000Z"], Ny(2026, 10, 13, 10, 0, -4), NewYork, Always, Never);

        Assert.Equal(["2026-10-13 14:00", "2026-11-10 15:00", "2026-12-08 15:00"], Utc(result));
    }

    [Fact]
    public void ExpandTimed_Window_ReturnsOnlyStartsInside()
    {
        var result = RecurrenceExpander.ExpandTimed(
            ["RRULE:FREQ=DAILY"],
            Ny(2026, 1, 1, 9, 0, -5),
            NewYork,
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(["2026-10-01 13:00", "2026-10-02 13:00", "2026-10-03 13:00"], Utc(result));
    }

    [Fact]
    public void ExpandTimed_NoRule_ReturnsStartOnly()
    {
        var start = Ny(2026, 10, 1, 9, 0, -4);

        Assert.Equal([start], RecurrenceExpander.ExpandTimed([], start, NewYork, Always, Never));
    }

    [Theory]
    [InlineData("RRULE:FREQ=NONSENSE")]
    [InlineData("RRULE:")]
    public void ExpandTimed_UnreadableRule_ReturnsStartOnly(string rule)
    {
        var start = Ny(2026, 10, 1, 9, 0, -4);

        Assert.Equal([start], RecurrenceExpander.ExpandTimed([rule, "EXDATE;TZID=Nowhere/Zone:garbage"], start, NewYork, Always, Never));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Not/AZone")]
    public void ExpandTimed_MissingOrUnknownZone_UsesStartOffset(string? zone)
    {
        var result = RecurrenceExpander.ExpandTimed(["RRULE:FREQ=DAILY;COUNT=2"], Ny(2026, 10, 31, 9, 0, -4), zone, Always, Never);

        Assert.Equal(["2026-10-31 13:00", "2026-11-01 13:00"], Utc(result));
    }

    [Fact]
    public async Task ExpandTimed_HostileSecondlyRule_IsCapped()
    {
        var run = Task.Run(() => RecurrenceExpander.ExpandTimed(["RRULE:FREQ=SECONDLY"], Ny(2026, 10, 1, 9, 0, -4), NewYork, Always, Never));

        var result = await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(RecurrenceExpander.MaxOccurrences, result.Count);
    }

    [Fact]
    public async Task ExpandTimed_RuleThatNeverMatches_ReturnsQuickly()
    {
        var run = Task.Run(() => RecurrenceExpander.ExpandTimed(["RRULE:FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=30"], Ny(2026, 1, 1, 9, 0, -5), NewYork, Always, Never));

        var result = await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public void ExpandAllDay_MonthDay31_SkipsShortMonths()
    {
        var result = RecurrenceExpander.ExpandAllDay(["RRULE:FREQ=MONTHLY;BYMONTHDAY=31;COUNT=4"], new DateOnly(2026, 1, 31), DateOnly.MinValue, DateOnly.MaxValue);

        Assert.Equal([new DateOnly(2026, 1, 31), new DateOnly(2026, 3, 31), new DateOnly(2026, 5, 31), new DateOnly(2026, 7, 31)], result);
    }

    [Fact]
    public void ExpandAllDay_YearlyWithExdate_SkipsExcludedYear()
    {
        var result = RecurrenceExpander.ExpandAllDay(
            ["RRULE:FREQ=YEARLY;COUNT=3", "EXDATE;VALUE=DATE:20270228"],
            new DateOnly(2026, 2, 28),
            DateOnly.MinValue,
            DateOnly.MaxValue);

        Assert.Equal([new DateOnly(2026, 2, 28), new DateOnly(2028, 2, 28)], result);
    }
}
