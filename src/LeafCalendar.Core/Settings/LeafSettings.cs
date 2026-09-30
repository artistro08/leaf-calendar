using System.Globalization;
using LeafCalendar.Core.Tray;

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

/// <summary>A calendar by account and calendar ID.</summary>
public sealed record CalendarRef(string AccountId, string CalendarId);

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

    /// <summary>Most days the tray flyout's agenda lists.</summary>
    public const int MaxFlyoutDays = 14;

    /// <summary>The join shortcut out of the box (spec 8.6).</summary>
    public const string DefaultJoinShortcut = "Ctrl+Alt+J";

    /// <summary>The flyout shortcut out of the box (spec 8.6).</summary>
    public const string DefaultFlyoutShortcut = "Ctrl+Alt+K";

    /// <summary>The flyout header's and tooltip's lookahead choices in minutes: 15, 30, 60 minutes, 2, 4, 8 hours (spec 9).</summary>
    public static IReadOnlyList<int> LookaheadChoices { get; } = [15, 30, 60, 120, 240, 480];

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

    /// <summary>Where new events go; null uses your main Google calendar (primary, else the first you can write to).</summary>
    public CalendarRef? DefaultCalendar { get; init; }

    /// <summary>Extra time-zone columns, left to right after the local zone.</summary>
    public IReadOnlyList<ExtraTimeZone> TimeZones { get; init; } = [];

    /// <summary>Show a notification at each reminder time.</summary>
    public bool ReminderNotifications { get; set; } = true;

    /// <summary>Show the persistent "Join now" notification when a meeting with a link starts.</summary>
    public bool JoinNowNotifications { get; set; } = true;

    /// <summary>Show new and updated invitations.</summary>
    public bool InviteNotifications { get; set; } = true;

    /// <summary>Notifications play the Windows sound.</summary>
    public bool NotificationSound { get; set; } = true;

    /// <summary>Days the tray flyout's agenda lists, starting today (1-14).</summary>
    public int FlyoutDays { get; set; } = 3;

    /// <summary>The tray flyout lists all-day events.</summary>
    public bool FlyoutAllDay { get; set; } = true;

    /// <summary>How far ahead the flyout header and the tray tooltip look for the next event, in minutes.</summary>
    public int TrayLookaheadMinutes { get; set; } = 60;

    /// <summary>Global shortcut that joins the next meeting, such as "Ctrl+Alt+J"; empty for none.</summary>
    public string JoinShortcut { get; set; } = DefaultJoinShortcut;

    /// <summary>Global shortcut that shows or hides the tray flyout; empty for none.</summary>
    public string FlyoutShortcut { get; set; } = DefaultFlyoutShortcut;

    /// <summary>
    /// Returns a copy with every value made safe.
    /// </summary>
    /// <remarks>
    /// Day count is clamped to 1-31 and hour height to its range. Unknown enum values reset to defaults.
    /// Time zones are limited to distinct IDs this PC knows, capped at <see cref="MaxTimeZones"/>, with
    /// labels trimmed (blank becomes null) to at most 24 characters. Flyout days are clamped to 1-14, a lookahead
    /// that isn't one of <see cref="LookaheadChoices"/> becomes 60 minutes, shortcuts are rewritten in
    /// <see cref="Hotkey"/>'s order (unreadable ones return to their defaults, empty stays empty), and a flyout
    /// shortcut that repeats the join shortcut is turned off.
    /// </remarks>
    public LeafSettings Normalize()
    {
        var zones = (TimeZones ?? [])
            .Where(z => z is not null && !string.IsNullOrWhiteSpace(z.Id) && TimeZoneInfo.TryFindSystemTimeZoneById(z.Id, out _))
            .DistinctBy(z => z.Id, StringComparer.Ordinal)
            .Take(MaxTimeZones)
            .Select(z => z with { Label = CleanLabel(z.Label) })
            .ToList();

        // Shortcuts (one combination can't do two things)
        var join   = CleanShortcut(JoinShortcut, DefaultJoinShortcut);
        var flyout = CleanShortcut(FlyoutShortcut, DefaultFlyoutShortcut);
        if (flyout.Length > 0 && flyout == join)
        {
            flyout = "";
        }

        return this with
        {
            WeekStart            = Enum.IsDefined(WeekStart) ? WeekStart : DayOfWeek.Sunday,
            ViewMode             = Enum.IsDefined(ViewMode) ? ViewMode : CalendarViewMode.Week,
            Theme                = Enum.IsDefined(Theme) ? Theme : AppTheme.System,
            CustomDayCount       = Math.Clamp(CustomDayCount, 1, 31),
            HourHeight           = double.IsFinite(HourHeight) ? Math.Clamp(HourHeight, MinHourHeight, MaxHourHeight) : DefaultHourHeight,
            TimeZones            = zones,
            DefaultCalendar      = DefaultCalendar is { } d && !string.IsNullOrWhiteSpace(d.AccountId) && !string.IsNullOrWhiteSpace(d.CalendarId) ? d : null,
            FlyoutDays           = Math.Clamp(FlyoutDays, 1, MaxFlyoutDays),
            TrayLookaheadMinutes = LookaheadChoices.Contains(TrayLookaheadMinutes) ? TrayLookaheadMinutes : 60,
            JoinShortcut         = join,
            FlyoutShortcut       = flyout,
        };
    }

    // Empty means "no shortcut"; null (a row saved before the field existed) or unreadable text means the default
    static string CleanShortcut(string? text, string fallback)
    {
        if (text is { Length: 0 })
        {
            return "";
        }

        return Hotkey.TryParse(text, out var hotkey) ? hotkey.ToString() : fallback;
    }

    static string? CleanLabel(string? label)
    {
        var trimmed = label?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed[..Math.Min(trimmed.Length, MaxLabelLength)];
    }
}
