using LeafCalendar.Core.Events;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class DragMathTests
{
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    static readonly DateOnly Oct1 = new(2026, 10, 1);

    static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    static CalendarOccurrence Event(DateTimeOffset start, DateTimeOffset end, bool allDay = false) =>
        new("acct", "cal", "evt", null, null, start, end, allDay, "Title", EventKind.Default, ResponseStatus.Accepted, "#4285F4", null, false, false);

    [Fact]
    public void InstantAndSnap_RoundToQuarterHourOnTheWallClock()
    {
        var at = DragMath.Instant(Oct1, 10 * 60 + 7, NewYork);

        Assert.Equal(Utc(10, 1, 14, 7), at);
        Assert.Equal(Utc(10, 1, 14), DragMath.Snap(at, NewYork));
    }

    [Fact]
    public void Instant_InDstGap_MovesToFirstRealTime()
    {
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), DragMath.Instant(new DateOnly(2026, 3, 8), 150, NewYork));
    }

    [Fact]
    public void MoveTimed_KeepsDurationAndSnaps()
    {
        var dentist = Event(Utc(10, 1, 13), Utc(10, 1, 14));

        var (start, end) = DragMath.MoveTimed(dentist, Utc(10, 1, 13, 20), Utc(10, 1, 14, 22), NewYork);

        Assert.Equal(Utc(10, 1, 14), start);
        Assert.Equal(Utc(10, 1, 15), end);
    }

    [Fact]
    public void ResizeEnd_NeverShorterThanOneStep()
    {
        var dentist = Event(Utc(10, 1, 13), Utc(10, 1, 14));

        Assert.Equal(Utc(10, 1, 15), DragMath.ResizeEnd(dentist, Utc(10, 1, 14, 58), NewYork));
        Assert.Equal(Utc(10, 1, 13, 15), DragMath.ResizeEnd(dentist, Utc(10, 1, 12), NewYork));
    }

    [Fact]
    public void CreateRange_UpwardDrag_IsOrderedAndSnapped()
    {
        Assert.Equal((Utc(10, 1, 13, 15), Utc(10, 1, 14)), DragMath.CreateRange(Utc(10, 1, 14, 2), Utc(10, 1, 13, 20), NewYork));
        Assert.Equal((Utc(10, 1, 14), Utc(10, 1, 14, 15)), DragMath.CreateRange(Utc(10, 1, 14, 2), Utc(10, 1, 14, 5), NewYork));
    }

    [Fact]
    public void ShiftDays_AcrossDstChange_KeepsWallClockTime()
    {
        // 9 AM EDT on Oct 31 to 9 AM EST on Nov 1
        var (start, end) = DragMath.ShiftDays(Event(Utc(10, 31, 13), Utc(10, 31, 14)), 1, NewYork);

        Assert.Equal(Utc(11, 1, 14), start);
        Assert.Equal(Utc(11, 1, 15), end);
    }

    [Fact]
    public void ShiftDays_AllDay_MovesDates()
    {
        var (start, end) = DragMath.ShiftDays(Event(Utc(10, 12, 0), Utc(10, 13, 0), allDay: true), 2, NewYork);

        Assert.Equal((Utc(10, 14, 0), Utc(10, 15, 0)), (start, end));
    }

    [Fact]
    public void PasteAt_KeepsSpacingBetweenCopiedEvents()
    {
        var first  = Event(Utc(10, 1, 14), Utc(10, 1, 15));
        var second = Event(Utc(10, 1, 15, 30), Utc(10, 1, 16));
        var allDay = Event(Utc(10, 1, 0), Utc(10, 2, 0), allDay: true);

        var pasted = DragMath.PasteAt([second, first, allDay], Utc(10, 2, 18), NewYork);

        Assert.Equal(Utc(10, 2, 19, 30), pasted[0].Start);
        Assert.Equal(Utc(10, 2, 18), pasted[1].Start);
        Assert.Equal(Utc(10, 2, 0), pasted[2].Start);
    }

    [Fact]
    public void NextSlot_RoundsUpToQuarterHour()
    {
        Assert.Equal(Utc(10, 1, 14, 15), DragMath.NextSlot(Utc(10, 1, 14, 7), NewYork));
    }

    [Fact]
    public void Snap_ExactMidpoint_RoundsUp()
    {
        Assert.Equal(Utc(10, 1, 14, 15), DragMath.Snap(Utc(10, 1, 14, 7) + TimeSpan.FromSeconds(30), NewYork));
    }

    [Fact]
    public void RepeatedHour_SnapMoveResizeShiftZero_StayPut()
    {
        // Nov 1 2026: 1:10 AM EDT is 05:10Z, 1:10 AM EST is 06:10Z
        var edt = Event(Utc(11, 1, 5, 10), Utc(11, 1, 5, 40));
        var est = Event(Utc(11, 1, 6, 10), Utc(11, 1, 6, 40));

        Assert.Equal(Utc(11, 1, 5, 15), DragMath.Snap(edt.Start, NewYork));
        Assert.Equal(Utc(11, 1, 6, 15), DragMath.Snap(est.Start, NewYork));
        Assert.Equal((Utc(11, 1, 5, 15), Utc(11, 1, 5, 45)), DragMath.MoveTimed(edt, edt.Start, edt.Start, NewYork));
        Assert.Equal(Utc(11, 1, 5, 45), DragMath.ResizeEnd(edt, edt.End, NewYork));
        Assert.Equal(Utc(11, 1, 5, 15), DragMath.CreateRange(Utc(11, 1, 5, 10), Utc(11, 1, 5, 40), NewYork).Start);
        Assert.Equal((edt.Start, edt.End), DragMath.ShiftDays(edt, 0, NewYork));
        Assert.Equal((est.Start, est.End), DragMath.ShiftDays(est, 0, NewYork));
    }

    [Fact]
    public void ShiftDays_AcrossFallBack_KeepsWallClockAndDefaultsToDaylightInRepeatedHour()
    {
        // 1:30 AM EDT on Oct 31 to the repeated 1:30 AM on Nov 1: earlier (EDT) one
        var (start, _) = DragMath.ShiftDays(Event(Utc(10, 31, 5, 30), Utc(10, 31, 6, 30)), 1, NewYork);

        Assert.Equal(Utc(11, 1, 5, 30), start);
    }

    [Fact]
    public void MoveTimed_OnShortAndLongDays_KeepsDuration()
    {
        // 23-hour day (Mar 8): 6:00 to 7:00 AM EDT
        var spring = Event(Utc(3, 8, 10), Utc(3, 8, 11));
        var (s1, e1) = DragMath.MoveTimed(spring, spring.Start, spring.Start + TimeSpan.FromMinutes(60), NewYork);
        Assert.Equal((Utc(3, 8, 11), Utc(3, 8, 12)), (s1, e1));

        // 25-hour day (Nov 1): midnight to 1 AM EDT, dragged 3 hours
        var fall = Event(Utc(11, 1, 4), Utc(11, 1, 5));
        var (s2, e2) = DragMath.MoveTimed(fall, fall.Start, fall.Start + TimeSpan.FromHours(3), NewYork);
        Assert.Equal((Utc(11, 1, 7), Utc(11, 1, 8)), (s2, e2));
    }

    [Fact]
    public void AllDayRange_OneDay_IsThatDay() =>
        Assert.Equal((Utc(10, 1, 0), Utc(10, 2, 0)), DragMath.AllDayRange(Oct1, Oct1));

    [Fact]
    public void AllDayRange_DraggedRight_CoversEveryDay() =>
        Assert.Equal((Utc(10, 1, 0), Utc(10, 4, 0)), DragMath.AllDayRange(Oct1, Oct1.AddDays(2)));

    [Fact]
    public void AllDayRange_DraggedLeft_CoversEveryDay() =>
        Assert.Equal((Utc(9, 29, 0), Utc(10, 2, 0)), DragMath.AllDayRange(Oct1, Oct1.AddDays(-2)));
}
