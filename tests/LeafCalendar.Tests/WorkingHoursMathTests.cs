using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public sealed class WorkingHoursMathTests
{
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    static readonly TimeZoneInfo Tokyo   = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    // 2026-10-01 is a Thursday; 2026-10-03 a Saturday
    static readonly DateOnly Thursday = new(2026, 10, 1);
    static readonly DateOnly Saturday = new(2026, 10, 3);

    [Fact]
    public void DaysLabel_NamesTheCommonSetsAndListsTheRestInWeekOrder()
    {
        DayOfWeek[] weekdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

        Assert.Equal("Weekdays", WorkingHoursMath.DaysLabel(weekdays, DayOfWeek.Sunday));
        Assert.Equal("Every day", WorkingHoursMath.DaysLabel(Enum.GetValues<DayOfWeek>(), DayOfWeek.Monday));
        Assert.Equal("No days", WorkingHoursMath.DaysLabel([], DayOfWeek.Sunday));
        Assert.Equal("Sun, Fri", WorkingHoursMath.DaysLabel([DayOfWeek.Friday, DayOfWeek.Sunday], DayOfWeek.Sunday));
        Assert.Equal("Fri, Sun", WorkingHoursMath.DaysLabel([DayOfWeek.Friday, DayOfWeek.Sunday], DayOfWeek.Monday));
    }

    [Fact]
    public void Workday_ShadesBeforeAndAfter() =>
        Assert.Equal([(0, 540), (1020, 1440)], WorkingHoursMath.OffHours(new WorkingHours(), Thursday, NewYork, NewYork));

    [Fact]
    public void DayOff_ShadesAllDay() =>
        Assert.Equal([(0, 1440)], WorkingHoursMath.OffHours(new WorkingHours(), Saturday, NewYork, NewYork));

    [Fact]
    public void Disabled_ShadesNothing() =>
        Assert.Empty(WorkingHoursMath.OffHours(new WorkingHours { Enabled = false }, Saturday, NewYork, NewYork));

    [Fact]
    public void MidnightToMidnight_ShadesNothingOnWorkdays() =>
        Assert.Empty(WorkingHoursMath.OffHours(new WorkingHours { StartMinute = 0, EndMinute = 1440 }, Thursday, NewYork, NewYork));

    [Fact]
    public void Traveling_YourNineToFiveLandsOnTheTravelClock_SplitAcrossMidnight()
    {
        // 9 AM-5 PM New York (EDT) is 10 PM-6 AM Tokyo, so each Tokyo weekday is working 0-6 AM (last night's) and 10 PM-midnight (tonight's)
        Assert.Equal([(360, 1320)], WorkingHoursMath.OffHours(new WorkingHours(), Thursday, NewYork, Tokyo));
        Assert.Equal([(360, 1320)], WorkingHoursMath.OffHours(new WorkingHours(), new DateOnly(2026, 10, 2), NewYork, Tokyo));
    }

    [Fact]
    public void Traveling_AFridayShiftSpillsIntoTokyoSaturday()
    {
        // 4 PM-11 PM Friday New York is 5 AM-12 PM Saturday in Tokyo, so that Saturday is only partly shaded
        var hours = new WorkingHours { StartMinute = 960, EndMinute = 1380 };
        Assert.Equal([(0, 300), (720, 1440)], WorkingHoursMath.OffHours(hours, Saturday, NewYork, Tokyo));
    }
}
