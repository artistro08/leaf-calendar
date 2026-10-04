namespace LeafCalendar.Core.Views;

/// <summary>
/// The month view's heights at a Windows text size. Today's numbers at 100%, grown with the text so 12 px labels never
/// clip (design standard section 14: text follows the system text scale).
/// </summary>
/// <param name="ChipHeight">One event chip's lane, including the 2 DIP gap below it.</param>
/// <param name="DayNumberHeight">The band at the top of a day holding its number button.</param>
/// <param name="DayButtonHeight">The day number button.</param>
/// <param name="MinRowHeight">The smallest week row.</param>
public sealed record MonthMetrics(double ChipHeight, double DayNumberHeight, double DayButtonHeight, double MinRowHeight)
{
    /// <summary>The heights for a text scale factor (Windows' UISettings.TextScaleFactor, 1 to 2.25).</summary>
    public static MonthMetrics For(double textScale)
    {
        var s = Math.Max(1, textScale);
        var chip = Math.Max(20, Math.Ceiling(16 * s) + 4);
        var button = Math.Max(22, Math.Ceiling(16 * s) + 6);
        var band = Math.Max(26, button + 4);
        return new MonthMetrics(chip, band, button, Math.Max(96, band + 3 * chip));
    }
}
