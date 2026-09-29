using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class ViewNavigatorTests
{
    static DateOnly D(int y, int m, int d) => new(y, m, d);

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
}
