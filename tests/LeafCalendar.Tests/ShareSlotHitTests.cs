using LeafCalendar.Core.Google;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public sealed class ShareSlotHitTests
{
    private static readonly DateTimeOffset Nine = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Edge = TimeSpan.FromMinutes(5);

    private static BusyRange Hours(double from, double to) => new(Nine.AddHours(from), Nine.AddHours(to));

    [Fact]
    public void At_NearTheTop_IsTheTopEdge() =>
        Assert.Equal(new SlotHitResult(0, SlotEdge.Top), ShareSlotHit.At([Hours(0, 1)], Nine.AddMinutes(3), Edge));

    [Fact]
    public void At_NearTheBottom_IsTheBottomEdge() =>
        Assert.Equal(new SlotHitResult(0, SlotEdge.Bottom), ShareSlotHit.At([Hours(0, 1)], Nine.AddMinutes(57), Edge));

    [Fact]
    public void At_InTheMiddle_IsInside() =>
        Assert.Equal(new SlotHitResult(0, SlotEdge.Inside), ShareSlotHit.At([Hours(0, 1)], Nine.AddMinutes(30), Edge));

    [Fact]
    public void At_JustOutside_IsTheEdgeStill() =>
        Assert.Equal(new SlotHitResult(0, SlotEdge.Bottom), ShareSlotHit.At([Hours(0, 1)], Nine.AddMinutes(63), Edge));

    [Fact]
    public void At_FarOutside_IsNothing() =>
        Assert.Null(ShareSlotHit.At([Hours(0, 1)], Nine.AddHours(2), Edge));

    [Fact]
    public void At_Overlapping_TheLastInTheListWins() =>
        Assert.Equal(new SlotHitResult(1, SlotEdge.Inside), ShareSlotHit.At([Hours(0, 3), Hours(1, 2)], Nine.AddMinutes(90), Edge));

    [Fact]
    public void At_ShortSlot_SplitsTheEdgesAtTheMiddle()
    {
        // Six minutes is shorter than two edges, so the halves split at the three-minute mark
        Assert.Equal(new SlotHitResult(0, SlotEdge.Top), ShareSlotHit.At([Hours(0, 0.1)], Nine.AddMinutes(2), Edge));
        Assert.Equal(new SlotHitResult(0, SlotEdge.Bottom), ShareSlotHit.At([Hours(0, 0.1)], Nine.AddMinutes(4), Edge));
    }
}
