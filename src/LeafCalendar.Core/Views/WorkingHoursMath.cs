using LeafCalendar.Core.Settings;

namespace LeafCalendar.Core.Views;

/// <summary>The stretches of a day outside your working hours, in minutes from local midnight (end exclusive).</summary>
public static class WorkingHoursMath
{
    /// <summary>Nothing when shading is off; the whole day on a day off; before the start and after the end on a workday.</summary>
    /// <param name="hours">Your working hours (Leaf's own setting; Google doesn't expose them).</param>
    /// <param name="day">The weekday drawn.</param>
    /// <returns>Off-hours ranges in minutes from midnight, earliest first; empty ones dropped.</returns>
    public static IReadOnlyList<(int StartMinute, int EndMinute)> OffHours(WorkingHours hours, DayOfWeek day)
    {
        if (!hours.Enabled)
        {
            return [];
        }

        if (!hours.Days.Contains(day))
        {
            return [(0, 1440)];
        }

        return new[] { (0, hours.StartMinute), (hours.EndMinute, 1440) }.Where(r => r.Item2 > r.Item1).ToList();
    }
}
