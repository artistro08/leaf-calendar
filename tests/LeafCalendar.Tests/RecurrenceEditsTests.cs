using LeafCalendar.Core.Editing;
using LeafCalendar.Core.Recurrence;

namespace LeafCalendar.Tests;

public class RecurrenceEditsTests
{
    // Mon/Wed/Fri 9:30 New York from Mon Oct 5, 2026
    static readonly DateTimeOffset SeriesStart = new(2026, 10, 5, 9, 30, 0, TimeSpan.FromHours(-4));
    static readonly DateTimeOffset Split       = new(2026, 10, 12, 9, 30, 0, TimeSpan.FromHours(-4));

    [Fact]
    public void EndBefore_Timed_SetsUntilOneSecondBeforeAndDropsCount()
    {
        var ended = RecurrenceEdits.EndBefore(["RRULE:FREQ=WEEKLY;BYDAY=MO,WE,FR;COUNT=10", "EXDATE;TZID=America/New_York:20261007T093000"], Split, isAllDay: false);

        Assert.Equal(["RRULE:FREQ=WEEKLY;BYDAY=MO,WE,FR;UNTIL=20261012T132959Z", "EXDATE;TZID=America/New_York:20261007T093000"], ended);
    }

    [Fact]
    public void EndBefore_AllDay_UsesPreviousDate()
    {
        var ended = RecurrenceEdits.EndBefore(["RRULE:FREQ=DAILY;UNTIL=20261231"], new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero), isAllDay: true);

        Assert.Equal(["RRULE:FREQ=DAILY;UNTIL=20261011"], ended);
    }

    [Fact]
    public void FollowingFrom_CountSeries_KeepsTotalCount()
    {
        string[] recurrence = ["RRULE:FREQ=WEEKLY;BYDAY=MO,WE,FR;COUNT=10"];

        var following = RecurrenceEdits.FollowingFrom(recurrence, SeriesStart, "America/New_York", Split, isAllDay: false);
        var ended     = RecurrenceEdits.EndBefore(recurrence, Split, isAllDay: false);

        // Oct 5, 7, 9 stay in the old series; the new one has the other 7
        Assert.Equal(["RRULE:FREQ=WEEKLY;BYDAY=MO,WE,FR;COUNT=7"], following);
        var before = RecurrenceExpander.ExpandTimed(ended, SeriesStart, "America/New_York", DateTimeOffset.MinValue, DateTimeOffset.MaxValue).Count;
        var after  = RecurrenceExpander.ExpandTimed(following!, Split, "America/New_York", DateTimeOffset.MinValue, DateTimeOffset.MaxValue).Count;
        Assert.Equal(3, before);
        Assert.Equal(7, after);
        Assert.Equal(10, before + after);
    }

    [Fact]
    public void FollowingFrom_AllDayCountSeries_KeepsTotalCount()
    {
        string[] recurrence = ["RRULE:FREQ=DAILY;COUNT=10"];
        var start = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var split = new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);

        var following = RecurrenceEdits.FollowingFrom(recurrence, start, null, split, isAllDay: true);
        var ended     = RecurrenceEdits.EndBefore(recurrence, split, isAllDay: true);

        Assert.Equal(["RRULE:FREQ=DAILY;COUNT=7"], following);
        Assert.Equal(["RRULE:FREQ=DAILY;UNTIL=20261007"], ended);
        var before = RecurrenceExpander.ExpandAllDay(ended, new DateOnly(2026, 10, 5), DateOnly.MinValue, DateOnly.MaxValue).Count;
        var after  = RecurrenceExpander.ExpandAllDay(following!, new DateOnly(2026, 10, 8), DateOnly.MinValue, DateOnly.MaxValue).Count;
        Assert.Equal(10, before + after);
    }

    [Fact]
    public void FollowingFrom_KeepsExdatesDropsRdates()
    {
        var following = RecurrenceEdits.FollowingFrom(
            ["RRULE:FREQ=WEEKLY;BYDAY=MO,WE,FR", "EXDATE;TZID=America/New_York:20261014T093000", "RDATE;TZID=America/New_York:20261003T093000"],
            SeriesStart,
            "America/New_York",
            Split,
            isAllDay: false);

        Assert.Equal(["RRULE:FREQ=WEEKLY;BYDAY=MO,WE,FR", "EXDATE;TZID=America/New_York:20261014T093000"], following);
    }

    [Fact]
    public void ShiftWeekdays_PlusOne_RotatesDaysKeepingOrdinals()
    {
        Assert.Equal(["RRULE:FREQ=MONTHLY;BYDAY=2WE"], RecurrenceEdits.ShiftWeekdays(["RRULE:FREQ=MONTHLY;BYDAY=2TU"], 1));
        Assert.Equal(["RRULE:FREQ=WEEKLY;BYDAY=SA,MO"], RecurrenceEdits.ShiftWeekdays(["RRULE:FREQ=WEEKLY;BYDAY=FR,SU"], 1));
        Assert.Equal(["RRULE:FREQ=WEEKLY;BYDAY=SU"], RecurrenceEdits.ShiftWeekdays(["RRULE:FREQ=WEEKLY;BYDAY=MO"], -1));
    }

    [Fact]
    public void ShiftWeekdays_Zero_ReturnsSameLines()
    {
        string[] lines = ["RRULE:FREQ=WEEKLY;BYDAY=MO"];

        Assert.Same(lines, RecurrenceEdits.ShiftWeekdays(lines, 0));
    }

    [Fact]
    public void FollowingFrom_SplitAtOrAfterLastCountInstance_ReturnsNull()
    {
        // COUNT=3 is Oct 5, 7, 9; splitting at Oct 12 leaves nothing
        Assert.Null(RecurrenceEdits.FollowingFrom(["RRULE:FREQ=WEEKLY;BYDAY=MO,WE,FR;COUNT=3"], SeriesStart, "America/New_York", Split, isAllDay: false));
    }

    [Fact]
    public void ShiftWeekdays_EmptyByDayEntry_DoesNotThrow()
    {
        Assert.Equal(["RRULE:FREQ=WEEKLY;BYDAY=TU,"], RecurrenceEdits.ShiftWeekdays(["RRULE:FREQ=WEEKLY;BYDAY=MO,"], 1));
    }
}
