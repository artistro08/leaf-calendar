using LeafCalendar.Core.Sunsama;

namespace LeafCalendar.Tests;

public sealed class SunsamaTrackedTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 15, 50, 0, TimeSpan.Zero);

    private static SunsamaBlock Block(DateTimeOffset start, int minutes, int? tracked) =>
        new(new DateOnly(2026, 10, 16), "t1", "x", "c", start, start.AddMinutes(minutes), 120, tracked, false, SunsamaBlockKind.Projected, null, null, null);

    [Fact]
    public void Fill_OneBlock_IsTrackedOverLength() =>
        Assert.Equal([0.7], SunsamaTracked.Fill([Block(Start, 120, 84)]));

    [Fact]
    public void Fill_SplitBlocks_CarryOverInTimeOrder()
    {
        // 40 m then 80 m, 84 m tracked: the first full, the second 44/80
        var fill = SunsamaTracked.Fill([Block(Start.AddMinutes(100), 80, 84), Block(Start, 40, 84)]);

        Assert.Equal(0.55, fill[0], 3);
        Assert.Equal(1.0, fill[1], 3);
    }

    [Fact]
    public void Fill_MoreThanPlanned_FillsEveryBlock() =>
        Assert.Equal([1.0, 1.0], SunsamaTracked.Fill([Block(Start, 30, 500), Block(Start.AddHours(1), 30, 500)]));

    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public void Fill_NoTrackedTime_IsZero(int? tracked) => Assert.Equal([0.0], SunsamaTracked.Fill([Block(Start, 60, tracked)]));

    [Fact]
    public void Fill_NoEstimate_StillFillsByBlockLength()
    {
        var block = Block(Start, 60, 30) with { EstimateMinutes = null };

        Assert.Equal([0.5], SunsamaTracked.Fill([block]));
    }

    [Fact]
    public void Fill_BlockNotStartedYet_HasNoTrackedTime()
    {
        // 175 m tracked so far; tomorrow's block can't hold any of it
        var fill = SunsamaTracked.Fill([Block(Start, 60, 175), Block(Start.AddDays(1), 75, 175)], now: Start.AddHours(2));

        Assert.Equal([1.0, 0.0], fill);
    }

    [Fact]
    public void Fill_Empty_IsEmpty() => Assert.Empty(SunsamaTracked.Fill([]));
}
