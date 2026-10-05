namespace LeafCalendar.Core.Views;

/// <summary>
/// The month view's heights at a Windows text size. Today's numbers at 100%, grown with the text so 12 px labels never
/// clip (design standard section 14: text follows the system text scale).
/// </summary>
/// <param name="ChipHeight">One event chip's lane, including the 2 DIP gap below it.</param>
/// <param name="DayNumberHeight">The band at the top of a day holding its number button.</param>
/// <param name="DayButtonHeight">The day number button.</param>
/// <param name="MinRowHeight">The smallest week row.</param>
/// <param name="WeekdayHeaderHeight">The weekday names above the grid, with their line.</param>
public sealed record MonthMetrics(double ChipHeight, double DayNumberHeight, double DayButtonHeight, double MinRowHeight, double WeekdayHeaderHeight)
{
    /// <summary>The heights for a text scale factor (Windows' UISettings.TextScaleFactor, 1 to 2.25).</summary>
    public static MonthMetrics For(double textScale)
    {
        var s = Math.Max(1, textScale);
        var chip = Math.Max(20, Math.Ceiling(16 * s) + 4);
        var button = Math.Max(22, Math.Ceiling(16 * s) + 6);
        var band = Math.Max(26, button + 4);

        // Weekday Names: 8 above and 8 below to their line, as at 100%
        var header = Math.Max(32, Math.Ceiling(16 * s) + 16);
        return new MonthMetrics(chip, band, button, Math.Max(96, band + 3 * chip), header);
    }
}
