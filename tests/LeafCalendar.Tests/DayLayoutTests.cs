using LeafCalendar.Core.Events;
using LeafCalendar.Core.Views;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public class DayLayoutTests
{
    static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    static readonly DateOnly Day = new(2026, 10, 1);

    static CalendarOccurrence At(string id, int startHour, int startMinute, int endHour, int endMinute, int endDayOffset = 0) =>
        new("a", "c", id, null, null,
            new DateTimeOffset(2026, 10, 1, startHour, startMinute, 0, TimeSpan.FromHours(-4)),
            new DateTimeOffset(2026, 10, 1 + endDayOffset, endHour, endMinute, 0, TimeSpan.FromHours(-4)),
            false, id, EventKind.Default, ResponseStatus.Accepted, "#4285F4", null, false, false);

    static IReadOnlyList<TimedBlock> Lay(params CalendarOccurrence[] items) => DayLayout.Layout(Day, items, Zone);

    [Fact]
    public void Layout_Alone_OneFullWidthColumn()
    {
        var b = Assert.Single(Lay(At("a", 9, 0, 10, 0)));

        Assert.Equal(540, b.StartMinute);
        Assert.Equal(600, b.EndMinute);
        Assert.Equal((0, 1), (b.Column, b.ColumnCount));
    }

    [Fact]
    public void Layout_BackToBack_SeparateClusters()
    {
        var blocks = Lay(At("a", 9, 0, 10, 0), At("b", 10, 0, 11, 0));

        Assert.All(blocks, b => Assert.Equal((0, 1), (b.Column, b.ColumnCount)));
    }

    [Fact]
    public void Layout_ChainedOverlaps_SharesColumns()
    {
        var blocks = Lay(At("a", 9, 0, 10, 0), At("b", 9, 30, 11, 0), At("c", 10, 0, 10, 30)).ToDictionary(b => b.Occurrence.EventId);

        Assert.Equal(0, blocks["a"].Column);
        Assert.Equal(1, blocks["b"].Column);
        Assert.Equal(0, blocks["c"].Column);
        Assert.All(blocks.Values, b => Assert.Equal(2, b.ColumnCount));
    }

    [Fact]
    public void Layout_ThreeAtOnce_ThreeColumns()
    {
        var blocks = Lay(At("a", 9, 0, 10, 0), At("b", 9, 0, 10, 0), At("c", 9, 15, 9, 45));

        Assert.Equal([0, 1, 2], blocks.Select(b => b.Column).Order());
        Assert.All(blocks, b => Assert.Equal(3, b.ColumnCount));
    }

    [Fact]
    public void Layout_TinyAdjacentEvents_GetSeparateColumns()
    {
        var blocks = Lay(At("a", 9, 0, 9, 5), At("b", 9, 10, 9, 15));

        Assert.Equal([0, 1], blocks.Select(b => b.Column).Order());
    }

    [Fact]
    public void Layout_CrossesMidnight_ClippedToDay()
    {
        var b = Assert.Single(Lay(At("late", 22, 0, 2, 0, endDayOffset: 1)));

        Assert.Equal(1320, b.StartMinute);
        Assert.Equal(1440, b.EndMinute);
    }

    [Fact]
    public void Layout_TwentyFourHoursOrMore_LeftToSpanRow()
    {
        Assert.Empty(Lay(At("long", 9, 0, 9, 0, endDayOffset: 1)));
    }

    // The Azores fall back from 1 AM to midnight (Oct 25 2026), so 12:00–1:00 AM happens twice. The day starts at the
    // first midnight: a click in that hour makes its event in the first pass (DragMath), filed under this day
    [Fact]
    public void Layout_RepeatedMidnightHour_DrawsTheFirstPass()
    {
        var azores = TimeZoneInfo.FindSystemTimeZoneById("Atlantic/Azores");
        var day    = new DateOnly(2026, 10, 25);
        var first  = TestOccurrences.Make("first-pass", DragMath.Instant(day, 15, azores), DragMath.Instant(day, 45, azores), false);

        var b = Assert.Single(DayLayout.Layout(day, [first], azores));

        Assert.Equal((15, 45), (b.StartMinute, b.EndMinute));
        Assert.Empty(DayLayout.Layout(day.AddDays(-1), [first], azores));
    }

    [Fact]
    public void Layout_ZeroMinuteAtMidnight_IsOnTheDayItStarts()
    {
        var deadline = At("deadline", 0, 0, 0, 0);

        var b = Assert.Single(Lay(deadline));

        Assert.Equal((0, 0), (b.StartMinute, b.EndMinute));
        Assert.Empty(DayLayout.Layout(Day.AddDays(-1), [deadline], Zone));
    }

    // A card is at least MinVisualMinutes tall, so one in the day's last minutes starts early enough to end at midnight
    [Fact]
    public void Layout_ShortEventAtTheEndOfTheDay_StaysInsideTheDay()
    {
        var blocks = Lay(At("meeting", 23, 30, 23, 45), At("deadline", 23, 59, 23, 59)).ToDictionary(b => b.Occurrence.EventId);

        Assert.Equal((1420, 1439), (blocks["deadline"].StartMinute, blocks["deadline"].EndMinute));
        Assert.Equal((1410, 1425), (blocks["meeting"].StartMinute, blocks["meeting"].EndMinute));
        Assert.Equal((0, 1), (blocks["meeting"].Column, blocks["deadline"].Column));
        Assert.All(blocks.Values, b => Assert.Equal(2, b.ColumnCount));
    }
}
