using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;

namespace LeafCalendar.Tests;

public sealed class ShareSlotEditTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static DateTimeOffset At(int day, int hour) => new(2026, 10, day, hour, 0, 0, TimeSpan.Zero);

    private static TimeSpan H(int hours) => TimeSpan.FromHours(hours);

    [Fact]
    public void LaterEnd_SameDay() =>
        Assert.Equal(new BusyRange(At(1, 10), At(1, 12)), ShareSlotEdit.Apply(new(At(1, 10), At(1, 11)), H(10), H(12), Utc));

    [Fact]
    public void EndBeforeStart_OnADaytimeSlot_IsRejected() =>
        Assert.Null(ShareSlotEdit.Apply(new(At(1, 10), At(1, 11)), H(10), H(9), Utc));

    [Fact]
    public void EndAtStart_IsRejected() =>
        Assert.Null(ShareSlotEdit.Apply(new(At(1, 10), At(1, 11)), H(10), H(10), Utc));

    [Fact]
    public void SlotEndingAtMidnight_KeepsItsNextDayEnd() =>
        Assert.Equal(new BusyRange(At(1, 22), At(2, 0)), ShareSlotEdit.Apply(new(At(1, 21), At(2, 0)), H(22), H(0), Utc));

    [Fact]
    public void SlotPastMidnight_EndsTheNextDay() =>
        Assert.Equal(new BusyRange(At(1, 23), At(2, 2)), ShareSlotEdit.Apply(new(At(1, 23), At(2, 1)), H(23), H(2), Utc));
}
