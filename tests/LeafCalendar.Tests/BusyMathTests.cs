using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;

namespace LeafCalendar.Tests;

public sealed class BusyMathTests
{
    private static BusyRange R(int startHour, int startMinute, int endHour, int endMinute) =>
        new(new DateTimeOffset(2026, 10, 1, startHour, startMinute, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 1, endHour, endMinute, 0, TimeSpan.Zero));

    [Fact]
    public void Merge_JoinsOverlappingAndTouching_DropsEmpty() =>
        Assert.Equal([R(9, 0, 11, 0), R(12, 0, 13, 0)], BusyMath.Merge([R(10, 0, 11, 0), R(9, 0, 10, 0), R(12, 0, 13, 0), R(9, 30, 9, 30), R(14, 0, 13, 0)]));

    [Fact]
    public void Subtract_LeavesTheFreeParts() =>
        Assert.Equal([R(10, 0, 10, 30), R(11, 0, 12, 0)], BusyMath.Subtract([R(9, 0, 12, 0)], [R(8, 0, 10, 0), R(10, 30, 11, 0)]));

    [Fact]
    public void Subtract_AllBusy_IsEmpty() => Assert.Empty(BusyMath.Subtract([R(9, 0, 10, 0)], [R(8, 0, 11, 0)]));

    [Fact]
    public void Subtract_OverlappingWantedSlots_AreMergedFirst() =>
        Assert.Equal([R(9, 0, 12, 0)], BusyMath.Subtract([R(9, 0, 11, 0), R(10, 0, 12, 0)], []));
}
