using System.Globalization;

namespace LeafCalendar.Core.Tray;

/// <summary>
/// Picks the tray icon's file: today's date, white in dark mode and black in light mode, drawn at the taskbar's
/// small-icon size (Assets/Tray, rendered by tools/make-icons.ps1).
/// </summary>
/// <remarks>
/// Each file is drawn at its exact pixel size, so Windows never scales it (scaling is what blurs a small icon). An
/// in-between size uses the next one up, else the largest.
/// </remarks>
public static class TrayGlyph
{
    /// <summary>The drawn sizes: the small-icon size at 100, 125, 150, 175, 200, 225, 250, 300, 350, and 400% scale.</summary>
    public static IReadOnlyList<int> Sizes { get; } = [16, 20, 24, 28, 32, 36, 40, 48, 56, 64];

    /// <summary>The drawn size for a small-icon size: that size when drawn, else the next one up, else the largest.</summary>
    public static int SizeFor(int iconSize) => Sizes.FirstOrDefault(s => s >= iconSize, Sizes[^1]);

    /// <summary>The file name for a day of the month (1–31) in dark or light mode at a small-icon size.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="day"/> isn't 1–31.</exception>
    public static string FileName(int day, bool lightMode, int iconSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(day, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(day, 31);

        return string.Create(CultureInfo.InvariantCulture, $"tray-{(lightMode ? "light" : "dark")}-{day}-{SizeFor(iconSize)}.png");
    }
}
