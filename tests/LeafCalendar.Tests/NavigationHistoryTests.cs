using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class NavigationHistoryTests
{
    static ViewPlace Week(int day) => new(CalendarViewMode.Week, 0, new DateOnly(2026, 10, day));

    [Fact]
    public void Back_ThenForward_WalksTheVisits()
    {
        var h = new NavigationHistory();
        h.Visit(Week(1)); h.Visit(Week(8)); h.Visit(Week(15));
        Assert.Equal(Week(8), h.Back());
        Assert.Equal(Week(1), h.Back());
        Assert.Null(h.Back());
        Assert.Equal(Week(8), h.Forward());
    }

    [Fact]
    public void Visit_AfterBack_DropsTheForwardPlaces()
    {
        var h = new NavigationHistory();
        h.Visit(Week(1)); h.Visit(Week(8));
        h.Back();
        h.Visit(Week(22));
        Assert.False(h.CanGoForward);
        Assert.Equal(Week(1), h.Back());
    }

    [Fact]
    public void Visit_SamePlaceTwice_IsOneEntry()
    {
        var h = new NavigationHistory();
        h.Visit(Week(1)); h.Visit(Week(1));
        Assert.False(h.CanGoBack);
    }

    [Fact]
    public void Visit_PastCapacity_ForgetsTheOldest()
    {
        var h = new NavigationHistory();
        for (var i = 0; i < NavigationHistory.Capacity + 5; i++) { h.Visit(new(CalendarViewMode.Day, 3, new DateOnly(2026, 1, 1).AddDays(i))); }
        var steps = 0;
        while (h.Back() is not null) { steps++; }
        Assert.Equal(NavigationHistory.Capacity - 1, steps);
    }

    [Fact]
    public void Visit_OutsideDaysView_IgnoresTheDayCount()
    {
        var h = new NavigationHistory();
        h.Visit(new(CalendarViewMode.Week, 3, new DateOnly(2026, 10, 1)));
        h.Visit(new(CalendarViewMode.Week, 7, new DateOnly(2026, 10, 1)));
        Assert.False(h.CanGoBack);

        h.Visit(new(CalendarViewMode.Days, 5, new DateOnly(2026, 10, 1)));
        h.Visit(new(CalendarViewMode.Days, 6, new DateOnly(2026, 10, 1)));
        Assert.Equal(new ViewPlace(CalendarViewMode.Days, 5, new DateOnly(2026, 10, 1)), h.Back());
    }
}
