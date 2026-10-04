using LeafCalendar.Core.Google;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Core.People;

/// <summary>The share panel's start and end pickers turned back into a picked time.</summary>
public static class ShareSlotEdit
{
    /// <summary>
    /// The time <paramref name="slot"/> becomes with new start and end clock times on its first day, in
    /// <paramref name="zone"/>. An end at or before the start means the next day only for a slot that already ran past
    /// midnight or ends at it (one dragged to the day's end); otherwise the pick makes no sense and the answer is null, so the panel
    /// puts the pickers back.
    /// </summary>
    public static BusyRange? Apply(BusyRange slot, TimeSpan start, TimeSpan end, TimeZoneInfo zone)
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(slot.Start, zone).DateTime);
        var endsOn = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(slot.End, zone).DateTime);
        var crosses = endsOn > day;
        if (end <= start && !crosses)
        {
            return null;
        }

        var endDay = end <= start ? day.AddDays(1) : day;
        return new BusyRange(DragMath.Instant(day, start.TotalMinutes, zone), DragMath.Instant(endDay, end.TotalMinutes, zone));
    }
}
