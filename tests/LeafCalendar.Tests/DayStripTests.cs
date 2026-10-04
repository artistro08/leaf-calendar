using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class DayStripTests
{
    static readonly DateOnly Origin = new(2026, 10, 1); // Thursday

    [Fact]
    public void Strip_AllDays_IndexRoundTrips()
    {
        var strip = new DayStrip(Origin, 10, 10, skipWeekends: false);

        Assert.Equal(21, strip.Count);
        Assert.Equal(Origin, strip[strip.IndexOf(Origin)]);
        Assert.Equal(Origin.AddDays(-10), strip.First);
    }

    [Fact]
    public void Strip_SkipWeekends_HasNoWeekendDays()
    {
        var strip = new DayStrip(Origin, 14, 14, skipWeekends: true);

        Assert.All(Enumerable.Range(0, strip.Count), i => Assert.False(ViewNavigator.IsWeekend(strip[i])));
    }

    [Fact]
    public void IndexOf_WeekendWhileHidden_SnapsToNextWeekday()
    {
        var strip = new DayStrip(Origin, 14, 14, skipWeekends: true);

        Assert.Equal(new DateOnly(2026, 10, 5), strip[strip.IndexOf(new DateOnly(2026, 10, 3))]);
        Assert.Equal(new DateOnly(2026, 10, 5), strip[strip.IndexOf(new DateOnly(2026, 10, 4))]);
    }

    [Fact]
    public void Between_WeekendWhileHidden_LeavesItOut()
    {
        var strip      = new DayStrip(Origin, 14, 14, skipWeekends: true);
        var (fri, mon) = (new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 12));

        Assert.Equal([fri, mon], strip.Between(mon, fri));
        Assert.Equal(4, new DayStrip(Origin, 14, 14, skipWeekends: false).Between(fri, mon).Count);
    }

    [Fact]
    public void IndexOf_OutsideStrip_Clamps()
    {
        var strip = new DayStrip(Origin, 5, 5, skipWeekends: false);

        Assert.Equal(0, strip.IndexOf(Origin.AddDays(-100)));
        Assert.Equal(strip.Count - 1, strip.IndexOf(Origin.AddDays(100)));
        Assert.Equal(strip.Last, strip[10_000]);
    }
}
