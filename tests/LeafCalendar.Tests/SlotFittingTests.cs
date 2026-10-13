using LeafCalendar.Core.Google;
using LeafCalendar.Core.Scheduling;

namespace LeafCalendar.Tests;

public class SlotFittingTests
{
    private static readonly DateTimeOffset Day = new(2026, 10, 13, 0, 0, 0, TimeSpan.FromHours(-5));

    private static DateTimeOffset At(int hour, int minute = 0) => Day.AddHours(hour).AddMinutes(minute);

    private static BusyRange R(int h1, int m1, int h2, int m2) => new(At(h1, m1), At(h2, m2));

    // 30-minute openings every 30 minutes from 9:00 to 11:00
    private static readonly IReadOnlyList<BusyRange> Every30 =
        [R(9, 0, 9, 30), R(9, 30, 10, 0), R(10, 0, 10, 30), R(10, 30, 11, 0)];

    [Fact]
    public void Fit_FullyCovered_KeepsEach()
    {
        var result = SlotFitting.Fit(R(9, 0, 10, 0), Every30);

        Assert.Equal([R(9, 0, 10, 0)], result.Ranges);
        Assert.Equal(new FitSummary(2, 30, 0, 0, 0), result.Summary);
    }

    [Fact]
    public void Fit_SpecExample_StretchesMoreThanHalfAndRemovesLess()
    {
        // 9:10–10:10: 9:00 covered 20 of 30 (stretched), 9:30 whole, 10:00 covered 10 of 30 (removed)
        var result = SlotFitting.Fit(R(9, 10, 10, 10), Every30);

        Assert.Equal([R(9, 0, 10, 0)], result.Ranges);
        Assert.Equal(new FitSummary(2, 30, 1, 1, 0), result.Summary);
    }

    [Fact]
    public void Fit_ExactlyHalf_IsRemoved()
    {
        var result = SlotFitting.Fit(R(9, 15, 9, 30), Every30);

        Assert.Empty(result.Ranges);
        Assert.Equal(new FitSummary(0, 0, 0, 1, 0), result.Summary);
    }

    [Fact]
    public void Fit_NoOpenTime_CountsNotOpenMinutes()
    {
        var result = SlotFitting.Fit(R(12, 0, 13, 0), Every30);

        Assert.Empty(result.Ranges);
        Assert.Equal(new FitSummary(0, 0, 0, 0, 60), result.Summary);
    }

    [Fact]
    public void Fit_PartlyOutsideOpenings_CountsTheGap()
    {
        // 10:30–11:30: 10:30 kept, 11:00–11:30 has no opening
        var result = SlotFitting.Fit(R(10, 30, 11, 30), Every30);

        Assert.Equal([R(10, 30, 11, 0)], result.Ranges);
        Assert.Equal(new FitSummary(1, 30, 0, 0, 30), result.Summary);
    }

    [Fact]
    public void Fit_IntervalShorterThanLength_DoesNotDoubleUp()
    {
        // 30-minute openings every 15 minutes: 9:00, 9:15, 9:30, 9:45, 10:00
        IReadOnlyList<BusyRange> every15 = [R(9, 0, 9, 30), R(9, 15, 9, 45), R(9, 30, 10, 0), R(9, 45, 10, 15), R(10, 0, 10, 30)];

        var result = SlotFitting.Fit(R(9, 0, 10, 0), every15);

        Assert.Equal([R(9, 0, 10, 0)], result.Ranges);
        Assert.Equal(2, result.Summary.Kept);
        Assert.Equal(0, result.Summary.Removed);
    }

    [Fact]
    public void Fit_DstFallBack_KeepsInstantLengths()
    {
        // 2026-11-01 in Chicago: 1:00–2:00 AM happens twice. Two 30-minute openings across the repeat, by instant
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var first = new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero);
        IReadOnlyList<BusyRange> open = [new(first, first.AddMinutes(30)), new(first.AddMinutes(30), first.AddMinutes(60))];

        var result = SlotFitting.Fit(new BusyRange(first, first.AddMinutes(60)), open);

        Assert.Equal([new BusyRange(first, first.AddMinutes(60))], result.Ranges);
        Assert.Equal(30, result.Summary.Minutes);
        Assert.NotNull(zone);
    }

    [Fact]
    public void FitAll_AddsSummariesAndMerges()
    {
        var result = SlotFitting.FitAll([R(9, 10, 9, 40), R(10, 0, 10, 30)], Every30);

        Assert.Equal([R(9, 0, 9, 30), R(10, 0, 10, 30)], result.Ranges);
        Assert.Equal(2, result.Summary.Kept);
        Assert.Equal(1, result.Summary.Stretched);
        Assert.Equal(1, result.Summary.Removed);
    }

    [Fact]
    public void Split_ByOpenTimes_TakesWholeOnesEarliestFirst()
    {
        var pieces = SlotFitting.Split(R(9, 0, 10, 0), Every30, 30);

        Assert.Equal([R(9, 0, 9, 30), R(9, 30, 10, 0)], pieces);
    }

    [Fact]
    public void Split_Offline_StepsByLength()
    {
        var pieces = SlotFitting.Split(R(9, 0, 10, 15), null, 30);

        Assert.Equal([R(9, 0, 9, 30), R(9, 30, 10, 0)], pieces);
    }

    [Fact]
    public void Split_CrossMidnight_UsesInstants()
    {
        var start = Day.AddHours(23).AddMinutes(30);
        var pieces = SlotFitting.Split(new BusyRange(start, start.AddHours(1)), null, 30);

        Assert.Equal([new BusyRange(start, start.AddMinutes(30)), new BusyRange(start.AddMinutes(30), start.AddHours(1))], pieces);
    }
}
