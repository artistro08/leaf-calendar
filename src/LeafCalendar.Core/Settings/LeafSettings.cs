using System.Globalization;
using LeafCalendar.Core.People;
using LeafCalendar.Core.Tray;
using LeafCalendar.Core.Views;

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

/// <summary>Which map site "Open in maps" uses (Settings › General).</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(MapProviderConverter))]
public enum MapProvider
{
    /// <summary>Google Maps.</summary>
    Google,

    /// <summary>Bing Maps.</summary>
    Bing,
}

/// <summary>
/// Your working hours. Google's Calendar API doesn't expose the ones set in Google Calendar, so Leaf keeps its own.
/// Minutes from local midnight; the end is exclusive.
/// </summary>
public sealed record WorkingHours
{
    /// <summary>Weekdays, Monday to Friday.</summary>
    public static IReadOnlyList<DayOfWeek> Weekdays { get; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    /// <summary>Shade the hours outside these.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Start, minutes from midnight (9 AM).</summary>
    public int StartMinute { get; init; } = 9 * 60;

    /// <summary>End, minutes from midnight (5 PM).</summary>
    public int EndMinute { get; init; } = 17 * 60;

    /// <summary>Days you work.</summary>
    // TODO: An unknown day name (DayOfWeek is a BCL enum, so it can't carry a lenient converter, and a list element has no safe fallback) still resets all settings to defaults.
    public IReadOnlyList<DayOfWeek> Days { get; init; } = Weekdays;
}

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

    /// <summary>
    /// The last view that shows the time grid (Day, Week, or a number of days), kept as <see cref="ViewMode"/> changes,
    /// so scheduling from Month view can go back to it. Null only in a row saved before it existed (Normalize makes it Week).
    /// </summary>
    public CalendarViewMode? LastGridView { get; init; } = CalendarViewMode.Week;

    /// <summary>Time-grid hour height in px.</summary>
    public double HourHeight { get; init; } = DefaultHourHeight;

    /// <summary>Color theme.</summary>
    public AppTheme Theme { get; init; } = AppTheme.System;

    /// <summary>Sidebar shown.</summary>
    public bool SidebarOpen { get; init; } = true;

    /// <summary>Right details panel shown.</summary>
    public bool DetailsPanelOpen { get; init; } = true;

    /// <summary>The main window's size when it last closed; null until then (it opens at <see cref="WindowSize.MainDefault"/>).</summary>
    public WindowSize? MainWindowSize { get; init; }

    /// <summary>The Settings window's size when it last closed; null until then.</summary>
    public WindowSize? SettingsWindowSize { get; init; }

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

    /// <summary>Upcoming-list lookahead choices in hours (details panel, nothing selected).</summary>
    public static IReadOnlyList<int> UpcomingChoices { get; } = [2, 4, 8, 12, 24];

    /// <summary>Your working hours (shading outside them).</summary>
    public WorkingHours WorkingHours { get; init; } = new();

    /// <summary>The all-day row starts expanded.</summary>
    public bool AllDayExpanded { get; init; }

    /// <summary>Map site for locations.</summary>
    public MapProvider MapProvider { get; init; } = MapProvider.Google;

    /// <summary>How many hours ahead the details panel's upcoming list looks.</summary>
    public int UpcomingHours { get; init; } = 8;

    /// <summary>Leaf's own time zone (IANA ID); null follows Windows.</summary>
    public string? PrimaryTimeZone { get; init; }

    /// <summary>With <see cref="PrimaryTimeZone"/> set, offer to switch when Windows' time zone changes.</summary>
    public bool PromptOnZoneChange { get; init; } = true;

    /// <summary>The main account: listed first, and used for people overlays and free/busy. Null: the first account.</summary>
    public string? MainAccountId { get; init; }

    /// <summary>Accounts whose new events get a Google Meet link by default.</summary>
    public IReadOnlyList<string> MeetByDefaultAccounts { get; init; } = [];

    /// <summary>
    /// The message Copy wraps your free times in while scheduling (Share availability); <c>{times}</c> marks where they
    /// go. Empty copies the times alone.
    /// </summary>
    public string ShareMessage { get; init; } = AvailabilityText.DefaultMessage;

    /// <summary>Accounts whose calendars are folded away under their header (the sidebar and Settings › Calendars).</summary>
    public IReadOnlyList<string> CollapsedAccounts { get; init; } = [];

    /// <summary>
    /// When Windows starts Leaf at sign-in, open its window too; false (the default, and what a row saved before this
    /// existed reads as) starts it minimized to the tray. Settings shows it as "Start minimized", the other way round.
    /// </summary>
    public bool OpenWindowAtSignIn { get; init; }

    /// <summary>Detailed logging (Settings › About): a breadcrumb trail and crash dumps in the log folder. Off by default.</summary>
    public bool DetailedLogging { get; init; }

    /// <summary>
    /// Returns a copy with every value made safe.
    /// </summary>
    /// <remarks>
    /// Day count is clamped to 1-31 and hour height to its range. Unknown enum values reset to defaults. The last
    /// time-grid view follows the view mode whenever it isn't Month.
    /// Time zones are limited to distinct IDs this PC knows, capped at <see cref="MaxTimeZones"/>, with
    /// labels trimmed (blank becomes null) to at most 24 characters. Flyout days are clamped to 1-14, a lookahead
    /// that isn't one of <see cref="LookaheadChoices"/> becomes 60 minutes, shortcuts are rewritten in
    /// <see cref="Hotkey"/>'s order (unreadable ones return to their defaults, empty stays empty), and a flyout
    /// shortcut that repeats the join shortcut is turned off.
    /// Working hours that are backwards or outside the day return to 9 AM-5 PM, and unknown or repeated days are dropped.
    /// An unknown map provider becomes Google.
    /// An upcoming lookahead that isn't one of <see cref="UpcomingChoices"/> becomes 8 hours.
    /// A primary time zone this PC doesn't know becomes null (follow Windows).
    /// A blank main account becomes null, and so does a window size that isn't positive and finite.
    /// Blank and repeated Meet-by-default and collapsed accounts are dropped. A share message saved before it existed
    /// (null) is the default one, and a long one is cut to its first 2,000 characters. (The old tray-excluded calendars are no longer read: the
    /// tray follows what's shown in Leaf, so a saved row that still has them loads without them.)
    /// A list whose contents didn't change keeps its instance, so normalizing twice gives an equal record.
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
            WeekStart             = Enum.IsDefined(WeekStart) ? WeekStart : DayOfWeek.Sunday,
            ViewMode              = Enum.IsDefined(ViewMode) ? ViewMode : CalendarViewMode.Week,
            LastGridView          = GridView(ViewMode) ?? (LastGridView is { } last ? GridView(last) : null) ?? CalendarViewMode.Week,
            Theme                 = Enum.IsDefined(Theme) ? Theme : AppTheme.System,
            CustomDayCount        = Math.Clamp(CustomDayCount, 1, 31),
            HourHeight            = double.IsFinite(HourHeight) ? Math.Clamp(HourHeight, MinHourHeight, MaxHourHeight) : DefaultHourHeight,
            TimeZones             = Keep(TimeZones, zones),
            DefaultCalendar       = DefaultCalendar is { } d && !string.IsNullOrWhiteSpace(d.AccountId) && !string.IsNullOrWhiteSpace(d.CalendarId) ? d : null,
            FlyoutDays            = Math.Clamp(FlyoutDays, 1, MaxFlyoutDays),
            TrayLookaheadMinutes  = LookaheadChoices.Contains(TrayLookaheadMinutes) ? TrayLookaheadMinutes : 60,
            JoinShortcut          = join,
            FlyoutShortcut        = flyout,
            WorkingHours          = CleanHours(WorkingHours),
            MapProvider           = Enum.IsDefined(MapProvider) ? MapProvider : MapProvider.Google,
            UpcomingHours         = UpcomingChoices.Contains(UpcomingHours) ? UpcomingHours : 8,
            PrimaryTimeZone       = PrimaryTimeZone is { } z && TimeZoneInfo.TryFindSystemTimeZoneById(z, out _) ? z : null,
            MainAccountId         = string.IsNullOrWhiteSpace(MainAccountId) ? null : MainAccountId,
            MainWindowSize        = MainWindowSize?.Clean(),
            SettingsWindowSize    = SettingsWindowSize?.Clean(),
            MeetByDefaultAccounts = Keep(MeetByDefaultAccounts, CleanAccounts(MeetByDefaultAccounts)),
            CollapsedAccounts     = Keep(CollapsedAccounts, CleanAccounts(CollapsedAccounts)),
            ShareMessage          = ShareMessage is null ? AvailabilityText.DefaultMessage : Clip(ShareMessage, AvailabilityText.MaxMessageLength),
        };
    }

    // Cuts text to a length without splitting a surrogate pair (an emoji at the limit is dropped whole)
    static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..(char.IsHighSurrogate(text[max - 1]) ? max - 1 : max)];

    /// <summary>
    /// Returns a copy without the per-account choices of accounts no longer connected (the main account, Meet by
    /// default, collapsed in the calendar lists), so adding the same Google account again starts fresh. Equal to this
    /// record when every account they name is still in <paramref name="accountIds"/>.
    /// </summary>
    public LeafSettings ForAccounts(IReadOnlyCollection<string> accountIds) => this with
    {
        MainAccountId         = MainAccountId is { } main && accountIds.Contains(main) ? main : null,
        MeetByDefaultAccounts = Keep(MeetByDefaultAccounts, [.. MeetByDefaultAccounts.Where(accountIds.Contains)]),
        CollapsedAccounts     = Keep(CollapsedAccounts, [.. CollapsedAccounts.Where(accountIds.Contains)]),
    };

    /// <summary>Returns a copy with <paramref name="accountId"/>'s calendars folded away (or shown again).</summary>
    public LeafSettings WithAccountCollapsed(string accountId, bool collapsed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        if (CollapsedAccounts.Contains(accountId) == collapsed)
        {
            return this;
        }

        return this with { CollapsedAccounts = collapsed ? [.. CollapsedAccounts, accountId] : [.. CollapsedAccounts.Where(a => a != accountId)] };
    }

    // A view that shows the time grid (not Month, not an unknown value), or null
    static CalendarViewMode? GridView(CalendarViewMode mode) =>
        mode != CalendarViewMode.Month && Enum.IsDefined(mode) ? mode : null;

    // Account IDs without blanks or repeats, in their first order
    static List<string> CleanAccounts(IReadOnlyList<string>? accounts) =>
        [.. (accounts ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.Ordinal)];

    // Records compare lists by reference, so an unchanged list keeps its instance and a normalized copy still equals the original
    static IReadOnlyList<T> Keep<T>(IReadOnlyList<T>? original, List<T> cleaned) =>
        original is not null && original.SequenceEqual(cleaned) ? original : cleaned;

    // Out of range or backwards falls back to 9-5; unknown and repeated days are dropped
    static WorkingHours CleanHours(WorkingHours? hours)
    {
        var h     = hours ?? new WorkingHours();
        var valid = h.StartMinute is >= 0 and < 1440 && h.EndMinute is > 0 and <= 1440 && h.StartMinute < h.EndMinute;
        return h with
        {
            StartMinute = valid ? h.StartMinute : 9 * 60,
            EndMinute   = valid ? h.EndMinute : 17 * 60,
            Days        = Keep(h.Days, [.. (h.Days ?? WorkingHours.Weekdays).Where(d => Enum.IsDefined(d)).Distinct()]),
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

/// <summary>Reads a map provider by name; a name this build doesn't know (say, from a newer version) becomes Google instead of resetting every setting.</summary>
sealed class MapProviderConverter : System.Text.Json.Serialization.JsonConverter<MapProvider>
{
    public override MapProvider Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) =>
        reader.TokenType == System.Text.Json.JsonTokenType.String && Enum.TryParse<MapProvider>(reader.GetString(), true, out var value) && Enum.IsDefined(value)
            ? value
            : MapProvider.Google;

    public override void Write(System.Text.Json.Utf8JsonWriter writer, MapProvider value, System.Text.Json.JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
