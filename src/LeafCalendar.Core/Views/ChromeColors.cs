namespace LeafCalendar.Core.Views;

/// <summary>
/// Colors for chrome the app draws in code (grid lines, secondary text, hover), per theme, as hex (#RRGGBB or
/// #AARRGGBB). The app turns them into brushes; tests check their contrast against the calendar surface.
/// </summary>
public static class ChromeColors
{
    /// <summary>The dark calendar island: LayerFillColorDefault (#3A3A3A at 30%) over dark Mica (#202020).</summary>
    public const string DarkSurface = "#282828";

    /// <summary>The light calendar island: LayerFillColorDefault (#FFFFFF at 50%) over light Mica (#F3F3F3).</summary>
    public const string LightSurface = "#F9F9F9";

    /// <summary>The island surface for a theme.</summary>
    public static string Surface(bool dark) => dark ? DarkSurface : LightSurface;

    /// <summary>Primary text.</summary>
    public static string PrimaryText(bool dark) => dark ? "#FFFFFFFF" : "#E4000000";

    /// <summary>Secondary text (times, zone labels, hour labels, weekday names).</summary>
    public static string SecondaryText(bool dark) => dark ? "#C5FFFFFF" : "#9E000000";

    /// <summary>Dimmed text (days outside the month).</summary>
    public static string DimText(bool dark) => dark ? "#82FFFFFF" : "#91000000";

    /// <summary>Hour and day divider lines.</summary>
    public static string GridLine(bool dark) => dark ? "#1FFFFFFF" : "#1A000000";

    /// <summary>Half-hour lines.</summary>
    public static string HalfHourLine(bool dark) => dark ? "#0DFFFFFF" : "#0A000000";

    /// <summary>Weekend column tint.</summary>
    public static string WeekendFill(bool dark) => dark ? "#08FFFFFF" : "#06000000";

    /// <summary>Hover fill for rows (the theme's SubtleFillColorSecondary).</summary>
    public static string Hover(bool dark) => dark ? "#0FFFFFFF" : "#09000000";

    /// <summary>Opaque fill for the time grid's drag ghost (the theme's SolidBackgroundFillColorTertiary).</summary>
    public static string GhostFill(bool dark) => dark ? "#282828" : "#F9F9F9";

    /// <summary>Differing fields in the conflict dialog (the theme's SystemFillColorCautionBackground).</summary>
    public static string CautionBackground(bool dark) => dark ? "#433519" : "#FFF4CE";

    /// <summary>Text on the accent fill (TextOnAccentFillColorPrimary: black in dark, white in light).</summary>
    public static string OnAccent(bool dark) => dark ? "#FF000000" : "#FFFFFFFF";
}
