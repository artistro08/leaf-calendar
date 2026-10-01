using LeafCalendar.Core.Events;
using LeafCalendar.Core.Views;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public class BoxSelectionTests
{
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    static readonly DateOnly Mon = new(2026, 10, 5);

    static CalendarOccurrence At(string id, DateOnly day, int startHour, int endHour, bool allDay = false) =>
        TestOccurrences.Make(id, DragMath.Instant(day, startHour * 60, NewYork), DragMath.Instant(day, endHour * 60, NewYork), allDay);

    [Fact]
    public void InTimeBox_PicksOverlappingTimedEventsOnCoveredDays()
    {
        CalendarOccurrence[] all =
        [
            At("in-mon", Mon, 13, 14),
            At("in-wed", Mon.AddDays(2), 14, 15),
            At("too-late", Mon.AddDays(1), 17, 18),
            At("other-day", Mon.AddDays(4), 13, 14),
            At("all-day", Mon, 0, 24, allDay: true),
        ];

        var hits = BoxSelection.InTimeBox(all, Mon, Mon.AddDays(2), 12 * 60, 16 * 60, NewYork);

        Assert.Equal(["in-mon", "in-wed"], hits.Select(o => o.EventId));
    }

    [Fact]
    public void InTimeBox_DraggedBackward_SameAsForward()
    {
        CalendarOccurrence[] all = [At("a", Mon, 13, 14), At("b", Mon.AddDays(1), 15, 16)];

        Assert.Equal(
            BoxSelection.InTimeBox(all, Mon, Mon.AddDays(1), 12 * 60, 16 * 60, NewYork).Select(o => o.EventId),
            BoxSelection.InTimeBox(all, Mon.AddDays(1), Mon, 16 * 60, 12 * 60, NewYork).Select(o => o.EventId));
    }

    [Fact]
    public void InTimeBox_ZeroMinuteEventInsideBand_IsPicked()
    {
        var reminder = TestOccurrences.Make("zero", DragMath.Instant(Mon, 13 * 60, NewYork), DragMath.Instant(Mon, 13 * 60, NewYork), false);

        Assert.Single(BoxSelection.InTimeBox([reminder], Mon, Mon, 12 * 60, 14 * 60, NewYork));
    }

    // Fall-back Sunday (Nov 1 2026): 1:00–2:00 happens twice; the band is wall-clock time in the view's zone
    [Fact]
    public void InTimeBox_FallBackDay_UsesWallClockBand()
    {
        var sunday = new DateOnly(2026, 11, 1);
        CalendarOccurrence[] all = [At("three-am", sunday, 3, 4), At("noon", sunday, 12, 13)];

        Assert.Equal(["three-am"], BoxSelection.InTimeBox(all, sunday, sunday, 2.5 * 60, 4 * 60, NewYork).Select(o => o.EventId));
    }

    // Fall-back Sunday: a band inside the repeated 1:00–2:00 hour covers both passes, since the grid draws both there
    [Fact]
    public void InTimeBox_FallBackDay_BandInsideRepeatedHour_PicksBothPasses()
    {
        var sunday = new DateOnly(2026, 11, 1);
        var edt    = TimeSpan.FromHours(-4);
        var est    = TimeSpan.FromHours(-5);
        CalendarOccurrence[] all =
        [
            TestOccurrences.Make("first-pass", new DateTimeOffset(2026, 11, 1, 1, 20, 0, edt), new DateTimeOffset(2026, 11, 1, 1, 40, 0, edt), false),
            TestOccurrences.Make("second-pass", new DateTimeOffset(2026, 11, 1, 1, 20, 0, est), new DateTimeOffset(2026, 11, 1, 1, 40, 0, est), false),
            At("two-am", sunday, 2, 3),
        ];

        Assert.Equal(["first-pass", "second-pass"], BoxSelection.InTimeBox(all, sunday, sunday, 75, 105, NewYork).Select(o => o.EventId));
    }

    // Spring-forward Sunday (Mar 8 2026): 2:00–3:00 doesn't happen; a band drawn there covers what the grid draws there
    [Fact]
    public void InTimeBox_SpringForwardGap_PicksWhatSpansTheGapOnly()
    {
        var sunday = new DateOnly(2026, 3, 8);
        CalendarOccurrence[] all =
        [
            TestOccurrences.Make("across-gap", new DateTimeOffset(2026, 3, 8, 1, 30, 0, TimeSpan.FromHours(-5)), new DateTimeOffset(2026, 3, 8, 3, 30, 0, TimeSpan.FromHours(-4)), false),
            At("three-am", sunday, 3, 4),
        ];

        Assert.Equal(["across-gap"], BoxSelection.InTimeBox(all, sunday, sunday, 2 * 60, 2.75 * 60, NewYork).Select(o => o.EventId));
    }

    [Fact]
    public void InTimeBox_EventCrossingMidnight_PickedFromEitherDay()
    {
        var late = TestOccurrences.Make("late", DragMath.Instant(Mon, 23 * 60, NewYork), DragMath.Instant(Mon.AddDays(1), 60, NewYork), false);

        Assert.Single(BoxSelection.InTimeBox([late], Mon.AddDays(1), Mon.AddDays(1), 0, 30, NewYork));
    }
}
