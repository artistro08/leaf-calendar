using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class MonthMetricsTests
{
    [Fact]
    public void MonthMetrics_At100Percent_AreTodaysNumbers()
    {
        var m = MonthMetrics.For(1.0);

        Assert.Equal(new MonthMetrics(20, 26, 22, 96), m);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(2.25)]
    public void MonthMetrics_At225Percent_FitsTheText(double scale)
    {
        // 12 px Segoe UI needs about 16 px of line height; a chip keeps 2 px around it, a day button 3 px above and below
        var m = MonthMetrics.For(scale);

        Assert.True(m.ChipHeight - 2 >= Math.Ceiling(16 * scale));
        Assert.True(m.DayButtonHeight >= Math.Ceiling(16 * scale) + 6);
        Assert.True(m.DayNumberHeight >= m.DayButtonHeight + 4);
        Assert.True(m.MinRowHeight >= m.DayNumberHeight + 3 * m.ChipHeight);
    }

    [Fact]
    public void MonthMetrics_BelowOne_StaysAtTodaysNumbers() =>
        Assert.Equal(MonthMetrics.For(1.0), MonthMetrics.For(0.8));
}
