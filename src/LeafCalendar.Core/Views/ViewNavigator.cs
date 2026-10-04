using System.Globalization;
using LeafCalendar.Core.Settings;

namespace LeafCalendar.Core.Views;

/// <summary>Date math for the calendar views. English-only formatting (localization is deferred).</summary>
public static class ViewNavigator
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    /// <summary>How many day columns the view shows.</summary>
    public static int VisibleColumnCount(CalendarViewMode mode, int customDays, bool showWeekends) => mode switch
    {
        CalendarViewMode.Day => 1,
        CalendarViewMode.Days => Math.Clamp(customDays, 1, 31),
        _ => showWeekends ? 7 : 5,
    };

    /// <summary>
    /// The day the mini month should show: the period start in Month view; otherwise today when it is in the
    /// visible span, else the middle of the span. With weekends hidden, the span counts shown days only.
    /// </summary>
    public static DateOnly MiniMonthAnchor(CalendarViewMode mode, DateOnly periodStart, int visibleColumns, DateOnly today, bool showWeekends = true)
    {
        if (mode == CalendarViewMode.Month)
        {
            return periodStart;
        }

        var shown = Enumerable.Range(0, 62).Select(periodStart.AddDays).Where(d => showWeekends || !IsWeekend(d)).Take(visibleColumns).ToList();

        return shown.Contains(today) ? today : shown[visibleColumns / 2];
    }

    /// <summary>The first day of the week containing <paramref name="date"/>.</summary>
    public static DateOnly WeekStartOf(DateOnly date, DayOfWeek weekStart) =>
        date.AddDays(-(((int)date.DayOfWeek - (int)weekStart + 7) % 7));

    /// <summary>The first day of the month.</summary>
    public static DateOnly MonthStartOf(DateOnly date) => new(date.Year, date.Month, 1);

    /// <summary>Where the period containing <paramref name="anchor"/> starts.</summary>
    public static DateOnly PeriodStart(CalendarViewMode mode, DateOnly anchor, DayOfWeek weekStart) => mode switch
    {
        CalendarViewMode.Week => WeekStartOf(anchor, weekStart),
        CalendarViewMode.Month => MonthStartOf(anchor),
        _ => anchor,
    };

    /// <summary>
    /// The start of the next (<c>+1</c>) or previous (<c>-1</c>) period. With weekends hidden, Day and
    /// Days views step by shown days, so paging back from Monday lands on Friday.
    /// </summary>
    public static DateOnly Step(CalendarViewMode mode, DateOnly periodStart, int direction, int customDays, bool showWeekends = true) => mode switch
    {
        CalendarViewMode.Day => StepDays(periodStart, 1, direction, showWeekends),
        CalendarViewMode.Week => periodStart.AddDays(7 * direction),
        CalendarViewMode.Month => MonthStartOf(periodStart).AddMonths(direction),
        _ => StepDays(periodStart, Math.Clamp(customDays, 1, 31), direction, showWeekends),
    };

    // Moves count shown days, skipping Saturday and Sunday when weekends are hidden
    private static DateOnly StepDays(DateOnly start, int count, int direction, bool showWeekends)
    {
        if (showWeekends)
        {
            return start.AddDays(count * direction);
        }

        var day = start;
        for (var moved = 0; moved < count;)
        {
            day = day.AddDays(direction);
            if (!IsWeekend(day))
            {
                moved++;
            }
        }

        return day;
    }

    /// <summary>Title for a visible range: "October 2026", "Sep – Oct 2026", or "Dec 2026 – Jan 2027".</summary>
    public static string PeriodTitle(DateOnly first, DateOnly lastInclusive)
    {
        if (first.Year == lastInclusive.Year && first.Month == lastInclusive.Month)
        {
            return MonthTitle(first);
        }

        return first.Year == lastInclusive.Year
            ? $"{first.ToString("MMM", English)} – {lastInclusive.ToString("MMM yyyy", English)}"
            : $"{first.ToString("MMM yyyy", English)} – {lastInclusive.ToString("MMM yyyy", English)}";
    }

    /// <summary>"October 2026".</summary>
    public static string MonthTitle(DateOnly anyDay) => anyDay.ToString("MMMM yyyy", English);

    /// <summary>ISO 8601 week number.</summary>
    public static int WeekNumber(DateOnly date) => ISOWeek.GetWeekOfYear(date.ToDateTime(TimeOnly.MinValue));

    /// <summary>Saturday or Sunday.</summary>
    public static bool IsWeekend(DateOnly date) => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// <summary>The first grid day and number of week rows needed to show the whole month.</summary>
    public static (DateOnly GridStart, int Weeks) MonthGrid(DateOnly anyDayInMonth, DayOfWeek weekStart)
    {
        var first = MonthStartOf(anyDayInMonth);
        var gridStart = WeekStartOf(first, weekStart);
        var last = first.AddMonths(1).AddDays(-1);

        return (gridStart, (last.DayNumber - gridStart.DayNumber) / 7 + 1);
    }
}
