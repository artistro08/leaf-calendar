namespace LeafCalendar.Core.Views;

/// <summary>
/// The scrollable run of days behind the time grid: index to date and back, optionally without
/// weekends. Asking for a date that isn't in the strip (a weekend while hidden) gives the next later day.
/// </summary>
public sealed class DayStrip
{
    readonly DateOnly[] _days;

    /// <summary>Builds the strip around <paramref name="origin"/>.</summary>
    public DayStrip(DateOnly origin, int daysBefore, int daysAfter, bool skipWeekends)
    {
        SkipsWeekends = skipWeekends;

        var days = new List<DateOnly>(daysBefore + daysAfter + 1);
        for (var day = origin.AddDays(-daysBefore); day <= origin.AddDays(daysAfter); day = day.AddDays(1))
        {
            if (!skipWeekends || !ViewNavigator.IsWeekend(day))
            {
                days.Add(day);
            }
        }

        _days = [.. days];
    }

    /// <summary>Number of days.</summary>
    public int Count => _days.Length;

    /// <summary>True when weekends are left out.</summary>
    public bool SkipsWeekends { get; }

    /// <summary>First day.</summary>
    public DateOnly First => _days[0];

    /// <summary>Last day.</summary>
    public DateOnly Last => _days[^1];

    /// <summary>The day at <paramref name="index"/> (clamped to the strip).</summary>
    public DateOnly this[int index] => _days[Math.Clamp(index, 0, _days.Length - 1)];

    /// <summary>True when <paramref name="day"/> is within the strip's range.</summary>
    public bool Contains(DateOnly day) => day >= First && day <= Last;

    /// <summary>
    /// The strip's days from <paramref name="a"/> to <paramref name="b"/>, both included, whichever comes first. Days
    /// the strip leaves out (a hidden weekend) aren't in it.
    /// </summary>
    public IReadOnlyList<DateOnly> Between(DateOnly a, DateOnly b)
    {
        var (first, last) = a <= b ? (a, b) : (b, a);

        return [.. _days.Where(day => day >= first && day <= last)];
    }

    /// <summary>Index of <paramref name="day"/>, or of the next later day in the strip (clamped).</summary>
    public int IndexOf(DateOnly day)
    {
        var index = Array.BinarySearch(_days, day);
        return index >= 0 ? index : Math.Min(~index, _days.Length - 1);
    }
}
