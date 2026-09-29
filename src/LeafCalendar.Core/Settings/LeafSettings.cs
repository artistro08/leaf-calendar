using System.Globalization;

namespace LeafCalendar.Core.Settings;

/// <summary>Which calendar layout is shown.</summary>
public enum CalendarViewMode
{
    /// <summary>One day.</summary>
    Day,

    /// <summary>One week (5 days when weekends are hidden).</summary>
    Week,

    /// <summary>One month.</summary>
    Month,

    /// <summary>A custom number of days (1-31).</summary>
    Days,
}

/// <summary>App color theme.</summary>
public enum AppTheme
{
    /// <summary>Follow Windows.</summary>
    System,

    /// <summary>Always light.</summary>
    Light,

    /// <summary>Always dark.</summary>
    Dark,
}

/// <summary>An extra time-zone column. <see cref="Id"/> is an IANA ID such as <c>Asia/Tokyo</c>.</summary>
public sealed record ExtraTimeZone(string Id, string? Label);

/// <summary>
/// The user's preferences. Stored as one JSON row by <see cref="SettingsStore"/> and always passed
/// through <see cref="Normalize"/>, so an old or damaged row can never produce unusable values.
/// </summary>
public sealed record LeafSettings
{
    /// <summary>Smallest hour height in the time grid (px).</summary>
    public const double MinHourHeight = 24;

    /// <summary>Largest hour height in the time grid (px).</summary>
    public const double MaxHourHeight = 120;

    /// <summary>Default hour height (px).</summary>
    public const double DefaultHourHeight = 48;

    /// <summary>Most extra time-zone columns.</summary>
    public const int MaxTimeZones = 4;

    const int MaxLabelLength = 24;

    /// <summary>First day of the week. Defaults to the Windows culture's choice.</summary>
    public DayOfWeek WeekStart { get; init; } = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

    /// <summary>Show Saturday and Sunday.</summary>
    public bool ShowWeekends { get; init; } = true;

    /// <summary>Show events you declined.</summary>
    public bool ShowDeclined { get; init; }

    /// <summary>Show ISO week numbers.</summary>
    public bool ShowWeekNumbers { get; init; }

    /// <summary>24-hour clock instead of AM/PM.</summary>
    public bool Use24HourTime { get; init; }

    /// <summary>Current view.</summary>
    public CalendarViewMode ViewMode { get; init; } = CalendarViewMode.Week;

    /// <summary>Day count for <see cref="CalendarViewMode.Days"/> (1-31).</summary>
    public int CustomDayCount { get; init; } = 3;

    /// <summary>Time-grid hour height in px.</summary>
    public double HourHeight { get; init; } = DefaultHourHeight;

    /// <summary>Color theme.</summary>
    public AppTheme Theme { get; init; } = AppTheme.System;

    /// <summary>Sidebar shown.</summary>
    public bool SidebarOpen { get; init; } = true;

    /// <summary>Right details panel shown.</summary>
    public bool DetailsPanelOpen { get; init; } = true;

    /// <summary>Extra time-zone columns, left to right after the local zone.</summary>
    public IReadOnlyList<ExtraTimeZone> TimeZones { get; init; } = [];

    /// <summary>
    /// Returns a copy with every value made safe.
    /// </summary>
    /// <remarks>
    /// Day count is clamped to 1-31 and hour height to its range. Unknown enum values reset to defaults.
    /// Time zones are limited to distinct IDs this PC knows, capped at <see cref="MaxTimeZones"/>, with
    /// labels trimmed (blank becomes null) to at most 24 characters.
    /// </remarks>
    public LeafSettings Normalize()
    {
        var zones = (TimeZones ?? [])
            .Where(z => z is not null && !string.IsNullOrWhiteSpace(z.Id) && TimeZoneInfo.TryFindSystemTimeZoneById(z.Id, out _))
            .DistinctBy(z => z.Id, StringComparer.Ordinal)
            .Take(MaxTimeZones)
            .Select(z => z with { Label = CleanLabel(z.Label) })
            .ToList();

        return this with
        {
            WeekStart      = Enum.IsDefined(WeekStart) ? WeekStart : DayOfWeek.Sunday,
            ViewMode       = Enum.IsDefined(ViewMode) ? ViewMode : CalendarViewMode.Week,
            Theme          = Enum.IsDefined(Theme) ? Theme : AppTheme.System,
            CustomDayCount = Math.Clamp(CustomDayCount, 1, 31),
            HourHeight     = double.IsFinite(HourHeight) ? Math.Clamp(HourHeight, MinHourHeight, MaxHourHeight) : DefaultHourHeight,
            TimeZones      = zones,
        };
    }

    static string? CleanLabel(string? label)
    {
        var trimmed = label?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed[..Math.Min(trimmed.Length, MaxLabelLength)];
    }
}
