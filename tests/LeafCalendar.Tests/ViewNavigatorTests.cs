using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class ViewNavigatorTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Theory]
    [InlineData(CalendarViewMode.Day, 3, true, 1)]
    [InlineData(CalendarViewMode.Week, 3, true, 7)]
    [InlineData(CalendarViewMode.Week, 3, false, 5)]
    [InlineData(CalendarViewMode.Days, 4, false, 4)]
    [InlineData(CalendarViewMode.Days, 99, true, 31)]
    [InlineData(CalendarViewMode.Month, 3, false, 5)]
    public void VisibleColumnCount_Mode_MatchesSpec(CalendarViewMode mode, int days, bool weekends, int expected)
    {
        Assert.Equal(expected, ViewNavigator.VisibleColumnCount(mode, days, weekends));
    }

    [Theory]
    [InlineData(DayOfWeek.Sunday, 2026, 9, 27)]
    [InlineData(DayOfWeek.Monday, 2026, 9, 28)]
    [InlineData(DayOfWeek.Saturday, 2026, 9, 26)]
    public void WeekStartOf_Thursday_UsesSetting(DayOfWeek start, int y, int m, int d)
    {
        Assert.Equal(D(y, m, d), ViewNavigator.WeekStartOf(D(2026, 10, 1), start));
    }

    [Fact]
    public void Step_EachMode_MovesOnePeriod()
    {
        Assert.Equal(D(2026, 10, 2), ViewNavigator.Step(CalendarViewMode.Day, D(2026, 10, 1), 1, 3));
        Assert.Equal(D(2026, 9, 20), ViewNavigator.Step(CalendarViewMode.Week, D(2026, 9, 27), -1, 3));
        Assert.Equal(D(2026, 11, 1), ViewNavigator.Step(CalendarViewMode.Month, D(2026, 10, 17), 1, 3));
        Assert.Equal(D(2026, 10, 5), ViewNavigator.Step(CalendarViewMode.Days, D(2026, 10, 1), 1, 4));
    }

    [Fact]
    public void Step_WeekendsHidden_SkipsWeekendDays()
    {
        // Oct 5 2026 is a Monday, Oct 2 a Friday, Oct 1 a Thursday
        Assert.Equal(D(2026, 10, 2), ViewNavigator.Step(CalendarViewMode.Day, D(2026, 10, 5), -1, 3, showWeekends: false));
        Assert.Equal(D(2026, 10, 5), ViewNavigator.Step(CalendarViewMode.Day, D(2026, 10, 2), 1, 3, showWeekends: false));
        Assert.Equal(D(2026, 10, 6), ViewNavigator.Step(CalendarViewMode.Days, D(2026, 10, 1), 1, 3, showWeekends: false));
        Assert.Equal(D(2026, 10, 1), ViewNavigator.Step(CalendarViewMode.Days, D(2026, 10, 6), -1, 3, showWeekends: false));
    }

    [Fact]
    public void Step_WeekendsShown_StepsCalendarDays()
    {
        Assert.Equal(D(2026, 10, 4), ViewNavigator.Step(CalendarViewMode.Day, D(2026, 10, 5), -1, 3, showWeekends: true));
        Assert.Equal(D(2026, 10, 4), ViewNavigator.Step(CalendarViewMode.Days, D(2026, 10, 1), 1, 3, showWeekends: true));
    }

    [Theory]
    [InlineData(2026, 10, 4, 2026, 10, 10, "October 2026")]
    [InlineData(2026, 9, 27, 2026, 10, 3, "Sep – Oct 2026")]
    [InlineData(2026, 12, 27, 2027, 1, 2, "Dec 2026 – Jan 2027")]
    public void PeriodTitle_Range_FormatsLikeSpec(int y1, int m1, int d1, int y2, int m2, int d2, string expected)
    {
        Assert.Equal(expected, ViewNavigator.PeriodTitle(D(y1, m1, d1), D(y2, m2, d2)));
    }

    [Fact]
    public void MonthGrid_October2026SundayStart_StartsSep27SixWeeksNotNeeded()
    {
        var (start, weeks) = ViewNavigator.MonthGrid(D(2026, 10, 15), DayOfWeek.Sunday);

        Assert.Equal(D(2026, 9, 27), start);
        Assert.Equal(5, weeks);
    }

    [Fact]
    public void WeekNumber_Iso_Correct()
    {
        Assert.Equal(40, ViewNavigator.WeekNumber(D(2026, 10, 1)));
        Assert.Equal(53, ViewNavigator.WeekNumber(D(2026, 12, 31)));
    }

    [Fact]
    public void MiniMonthAnchor_TodayInSpan_UsesToday()
    {
        // Sep 27 - Oct 3 with today Oct 1 shows October
        Assert.Equal(D(2026, 10, 1), ViewNavigator.MiniMonthAnchor(CalendarViewMode.Week, D(2026, 9, 27), 7, D(2026, 10, 1)));
    }

    [Fact]
    public void MiniMonthAnchor_TodayOutsideSpan_UsesMiddleDay()
    {
        Assert.Equal(D(2026, 9, 30), ViewNavigator.MiniMonthAnchor(CalendarViewMode.Week, D(2026, 9, 27), 7, D(2026, 10, 20)));
        Assert.Equal(D(2026, 10, 1), ViewNavigator.MiniMonthAnchor(CalendarViewMode.Days, D(2026, 9, 30), 3, D(2026, 12, 1)));
    }

    [Fact]
    public void MiniMonthAnchor_WeekendsHidden_CountsShownDays()
    {
        // Fri Oct 30, Mon Nov 2, Tue Nov 3 with today Nov 2 shows November
        Assert.Equal(D(2026, 11, 2), ViewNavigator.MiniMonthAnchor(CalendarViewMode.Days, D(2026, 10, 30), 3, D(2026, 11, 2), showWeekends: false));

        // Today elsewhere: the middle shown day (Mon Nov 2), not Sat Oct 31
        Assert.Equal(D(2026, 11, 2), ViewNavigator.MiniMonthAnchor(CalendarViewMode.Days, D(2026, 10, 30), 3, D(2026, 12, 1), showWeekends: false));
    }

    [Fact]
    public void MiniMonthAnchor_Month_UsesPeriodStart()
    {
        Assert.Equal(D(2026, 10, 1), ViewNavigator.MiniMonthAnchor(CalendarViewMode.Month, D(2026, 10, 1), 7, D(2026, 10, 15)));
    }
}
