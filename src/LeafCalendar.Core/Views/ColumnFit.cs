namespace LeafCalendar.Core.Views;

/// <summary>
/// Whole-pixel day columns for the day and week grid. The available width is rounded to whole pixels first, so the float
/// noise WinUI's widths carry (809.599976 for 809.6) can't change the answer, and the spare is a whole number of pixels
/// (exactly 0 when the pixels divide evenly). A spare of a few millionths of a DIP got rounded up to a pixel by layout,
/// which narrowed the grid, which changed the columns, which gave the pixel back, until WinUI stopped with a
/// LayoutCycleException.
/// </summary>
public static class ColumnFit
{
    /// <summary>The narrowest a day column gets, in DIPs (rounded up to a whole pixel).</summary>
    public const double MinWidth = 48;

    /// <summary>
    /// Fits <paramref name="columns"/> day columns into <paramref name="available"/> DIPs at the monitor's
    /// <paramref name="scale"/>: each column a whole number of pixels, and the pixels left over as the spare at the right edge.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scale"/> or <paramref name="columns"/> isn't positive.</exception>
    public static (double Width, double Spare) Fit(double available, double scale, int columns)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);

        // (half a pixel rounds up, as layout rounding does)
        var pixels = Math.Round(available * scale, MidpointRounding.AwayFromZero);
        var column = Math.Max(Math.Ceiling(MinWidth * scale), Math.Floor(pixels / columns));
        return (column / scale, Math.Max(0, pixels - (column * columns)) / scale);
    }
}
