using LeafCalendar.Core.Editing;
using LeafCalendar.Core.Recurrence;

namespace LeafCalendar.Tests;

public class RepeatRuleTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [Fact]
    public void Parse_WeeklyWithDaysAndUntil_ReadsFields()
    {
        var rule = RepeatRule.Parse("RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;UNTIL=20270101T045959Z", NewYork)!;

        Assert.Equal(RepeatFrequency.Weekly, rule.Frequency);
        Assert.Equal(2, rule.Interval);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Wednesday], rule.Weekdays);
        Assert.Equal(new DateOnly(2026, 12, 31), rule.Until);
        Assert.Null(rule.Count);
    }

    [Theory]
    [InlineData("RRULE:FREQ=MONTHLY;BYDAY=2TU")]
    [InlineData("RRULE:FREQ=WEEKLY;BYSETPOS=1;BYDAY=MO")]
    [InlineData("RRULE:FREQ=HOURLY")]
    [InlineData("EXDATE:20261007T133000Z")]
    [InlineData("RRULE:FREQ=WEEKLY;INTERVAL=0")]
    public void Parse_RuleTheEditorCantShow_ReturnsNull(string line)
    {
        Assert.Null(RepeatRule.Parse(line, NewYork));
    }

    [Fact]
    public void ToRRule_TimedUntil_IsEndOfThatLocalDayInUtc()
    {
        var rule = new RepeatRule(RepeatFrequency.Weekly, 2, [DayOfWeek.Monday, DayOfWeek.Wednesday], Until: new DateOnly(2026, 12, 31));

        Assert.Equal("RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;UNTIL=20270101T045959Z", rule.ToRRule(isAllDay: false, NewYork));
    }

    [Fact]
    public void ToRRule_AllDayCount_UsesCount()
    {
        Assert.Equal("RRULE:FREQ=YEARLY;COUNT=5", new RepeatRule(RepeatFrequency.Yearly, Count: 5).ToRRule(isAllDay: true, NewYork));
    }

    [Fact]
    public void ToRRule_AllDayUntil_UsesDate()
    {
        Assert.Equal("RRULE:FREQ=DAILY;UNTIL=20261031", new RepeatRule(RepeatFrequency.Daily, Until: new DateOnly(2026, 10, 31)).ToRRule(isAllDay: true, NewYork));
    }

    [Theory]
    [InlineData("RRULE:FREQ=DAILY", "Every day")]
    [InlineData("RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE", "Every 2 weeks on Mon, Wed")]
    [InlineData("RRULE:FREQ=MONTHLY;COUNT=3", "Monthly, 3 times")]
    [InlineData("RRULE:FREQ=YEARLY;UNTIL=20271231", "Yearly, until Dec 31, 2027")]
    public void Describe_ReadsLikeGoogle(string line, string expected)
    {
        Assert.Equal(expected, RepeatRule.Parse(line, NewYork)!.Describe());
    }

    [Fact]
    public void RoundTrip_KeepsWkst_AndSameDates()
    {
        const string line = "RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=SU,MO;WKST=SU";
        var rule = RepeatRule.Parse(line, NewYork)!;

        Assert.Equal(DayOfWeek.Sunday, rule.Wkst);

        var written = rule.ToRRule(isAllDay: false, NewYork);
        Assert.Contains("WKST=SU", written);

        // Sunday Oct 4, 2026 start
        var start = new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.FromHours(-4));
        var original = RecurrenceExpander.ExpandTimed([line], start, "America/New_York", start, start.AddDays(60));
        var again = RecurrenceExpander.ExpandTimed([written], start, "America/New_York", start, start.AddDays(60));
        Assert.Equal(original, again);
    }

    [Fact]
    public void RoundTrip_TokyoEventWithAnEndDate_InItsOwnZone_KeepsTheCount()
    {
        // Daily at 9 AM Tokyo, ending Oct 5 (Tokyo's end of day is 14:59:59Z): five times
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        const string line = "RRULE:FREQ=DAILY;UNTIL=20261005T145959Z";

        // The editor writes the rule in the event's zone, whatever zone is on screen
        var written = RepeatRule.Parse(line, tokyo)!.ToRRule(isAllDay: false, tokyo);
        var count = RecurrenceExpander.ExpandTimed([written], start, "Asia/Tokyo", start, start.AddDays(30)).Count;

        Assert.Equal(line, written);
        Assert.Equal(5, count);

        // Written in New York's zone, the end would slip a day and add a sixth
        var shifted = RepeatRule.Parse(line, tokyo)!.ToRRule(isAllDay: false, NewYork);
        Assert.Equal(6, RecurrenceExpander.ExpandTimed([shifted], start, "Asia/Tokyo", start, start.AddDays(30)).Count);
    }

    [Fact]
    public void Parse_AllDayRuleWithUtcUntil_KeepsItsUtcDate()
    {
        // Written by another client; the expander still shows Dec 31, so the editor must too
        const string line = "RRULE:FREQ=WEEKLY;UNTIL=20261231T000000Z";

        var rule = RepeatRule.Parse(line, NewYork, isAllDay: true)!;

        Assert.Equal(new DateOnly(2026, 12, 31), rule.Until);
        Assert.Contains(new DateOnly(2026, 12, 31), RecurrenceExpander.ExpandAllDay([line], new DateOnly(2026, 12, 3), DateOnly.MinValue, DateOnly.MaxValue));
        Assert.Equal("RRULE:FREQ=WEEKLY;UNTIL=20261231", rule.ToRRule(isAllDay: true, NewYork));
    }
}
