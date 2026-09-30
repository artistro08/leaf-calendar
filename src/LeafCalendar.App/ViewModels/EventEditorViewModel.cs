using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Mail;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeafCalendar.Core.Editing;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Views;

namespace LeafCalendar.App.ViewModels;

/// <summary>A calendar in the editor's picker (an App type, so WinRT can hold the list).</summary>
public sealed record CalendarChoice(string AccountId, string CalendarId, string Name, string AccountEmail, string Color)
{
    /// <summary>The name (what a screen reader says for the picker's item).</summary>
    public override string ToString() => Name;
}

/// <summary>A guest in the editor, with its optional toggle and remove button.</summary>
public sealed partial class GuestRow(Guest guest, Action<GuestRow> remove) : ObservableObject
{
    /// <summary>The guest as loaded (or typed).</summary>
    public Guest Guest { get; } = guest;

    /// <summary>Address.</summary>
    public string Email => Guest.Email;

    /// <summary>Optional attendance.</summary>
    [ObservableProperty]
    public partial bool Optional { get; set; } = guest.Optional;

    /// <summary>Automation ID of the optional checkbox.</summary>
    public string OptionalId => $"EditorGuestOptional_{Email}";

    /// <summary>Automation ID of the remove button.</summary>
    public string RemoveId => $"EditorGuestRemove_{Email}";

    [RelayCommand]
    void Remove() => remove(this);
}

/// <summary>A weekday toggle for weekly repeats.</summary>
public sealed partial class WeekdayToggle(DayOfWeek day, bool isOn) : ObservableObject
{
    /// <summary>The weekday.</summary>
    public DayOfWeek Day { get; } = day;

    /// <summary>"Mo".</summary>
    public string Label => CultureInfo.GetCultureInfo("en-US").DateTimeFormat.GetShortestDayName(Day);

    /// <summary>Automation ID.</summary>
    public string AutomationId => $"EditorWeekday_{Day}";

    /// <summary>Repeats on this day.</summary>
    [ObservableProperty]
    public partial bool IsOn { get; set; } = isOn;
}

/// <summary>A popup reminder time.</summary>
public sealed partial class ReminderToggle(int minutes, bool isOn) : ObservableObject
{
    /// <summary>Minutes before the start.</summary>
    public int Minutes { get; } = minutes;

    /// <summary>"10 min", "1 hr", "1 day", or "At start".</summary>
    public string Label => Minutes switch
    {
        0                         => "At start",
        < 60                      => string.Create(CultureInfo.InvariantCulture, $"{Minutes} min"),
        _ when Minutes % 1440 == 0 => Minutes == 1440 ? "1 day" : string.Create(CultureInfo.InvariantCulture, $"{Minutes / 1440} days"),
        _ when Minutes % 60 == 0   => string.Create(CultureInfo.InvariantCulture, $"{Minutes / 60} hr"),
        _                         => string.Create(CultureInfo.InvariantCulture, $"{Minutes} min"),
    };

    /// <summary>Automation ID.</summary>
    public string AutomationId => string.Create(CultureInfo.InvariantCulture, $"EditorReminder_{Minutes}");

    /// <summary>Reminder on.</summary>
    [ObservableProperty]
    public partial bool IsOn { get; set; } = isOn;
}

/// <summary>
/// The editor's fields for one event (new or existing). <see cref="ToDraft"/> turns them back into an
/// <see cref="EventDraft"/>; comparing it with <see cref="Before"/> gives the patch, so untouched fields are never sent.
/// </summary>
/// <remarks>
/// Times are shown in the calendar's zone. An all-day event shows its last day (Google stores the day after). Repeat
/// choices: 0 none, 1 daily, 2 weekly, 3 monthly, 4 yearly, 5 a rule the editor can't show (kept exactly as it is).
/// Ends: 0 never, 1 on a date, 2 after a count.
/// </remarks>
public sealed partial class EventEditorViewModel : ObservableObject
{
    static readonly int[] ReminderPresets = [0, 5, 10, 15, 30, 60, 1440];

    readonly TimeZoneInfo _zone;
    readonly string _localZoneId;
    readonly bool _use24Hour;
    readonly DayOfWeek? _wkst;
    readonly string? _loadedLine;
    readonly (DateOnly? StartDay, TimeSpan StartTime, DateOnly? EndDay, TimeSpan EndTime) _loadedWhen;
    bool _ready;

    /// <summary>Loads the fields from <paramref name="draft"/>.</summary>
    public EventEditorViewModel(EventDraft draft, CalendarOccurrence? occurrence, IReadOnlyList<CalendarChoice> calendars, TimeZoneInfo zone, string localZoneId, bool use24Hour, bool focusEnd = false)
    {
        Before       = draft;
        Occurrence   = occurrence;
        FocusEnd     = focusEnd;
        _zone        = zone;
        _localZoneId = localZoneId;
        _use24Hour   = use24Hour;

        // Fields
        Calendars           = new ObservableCollection<CalendarChoice>(calendars);
        CalendarIndex       = calendars.ToList().FindIndex(c => c.AccountId == draft.AccountId && c.CalendarId == draft.CalendarId);
        Title               = draft.Title;
        Location            = draft.Location;
        Description         = draft.Description;
        ColorId             = draft.ColorId;
        IsAllDay            = draft.IsAllDay;
        UseDefaultReminders = draft.UseDefaultReminders;
        Guests              = new ObservableCollection<GuestRow>(draft.Guests.Select(g => new GuestRow(g, RemoveGuest)));
        Reminders           = new ObservableCollection<ReminderToggle>(ReminderPresets.Union(draft.ReminderMinutes).Order().Select(m => new ReminderToggle(m, draft.ReminderMinutes.Contains(m))));

        // When (an all-day event shows its last day, inclusive)
        var start = draft.IsAllDay ? draft.Start.UtcDateTime : TimeZoneInfo.ConvertTime(draft.Start, zone).DateTime;
        var end   = draft.IsAllDay ? draft.End.UtcDateTime.AddDays(-1) : TimeZoneInfo.ConvertTime(draft.End, zone).DateTime;
        StartDate = Picker(start);
        StartTime = start.TimeOfDay;
        EndDate   = Picker(end);
        EndTime   = end.TimeOfDay;
        _loadedWhen = (Day(StartDate), StartTime, Day(EndDate), EndTime);

        // Repeat
        var line  = draft.Recurrence.FirstOrDefault(l => l.StartsWith("RRULE:", StringComparison.Ordinal));
        var rule  = line is null ? null : RepeatRule.Parse(line, zone);
        RepeatIndex    = line is null ? 0 : rule is null ? 5 : (int)rule.Frequency + 1;
        HasCustomRule  = RepeatIndex == 5;
        RepeatInterval = rule?.Interval ?? 1;
        EndsIndex      = rule?.Count is not null ? 2 : rule?.Until is not null ? 1 : 0;
        EndsOn         = rule?.Until is { } until ? Picker(until.ToDateTime(TimeOnly.MinValue)) : Picker(start.AddMonths(3));
        EndsAfter      = rule?.Count ?? 10;
        Weekdays       = new ObservableCollection<WeekdayToggle>(Enumerable.Range(0, 7).Select(i => (DayOfWeek)i)
            .Select(d => new WeekdayToggle(d, rule?.Weekdays?.Contains(d) ?? d == start.DayOfWeek)));
        _wkst          = rule?.Wkst;

        // The rule as the untouched fields write it, so saving without a repeat change keeps Google's exact lines
        _loadedLine = rule is null ? null : RepeatLine();

        Guests.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasGuests));
        _ready = true;
    }

    /// <summary>The event as loaded (new events: the starting values).</summary>
    public EventDraft Before { get; }

    /// <summary>The on-screen event being edited, or null for a new one.</summary>
    public CalendarOccurrence? Occurrence { get; }

    /// <summary>True for a new event.</summary>
    public bool IsNew => Occurrence is null;

    /// <summary>Focus the end time first ("E then U").</summary>
    public bool FocusEnd { get; }

    /// <summary>"New event" or "Edit event".</summary>
    public string HeaderText => IsNew ? "New event" : "Edit event";

    /// <summary>The event loaded with a repeat rule the editor can't show ("Custom rule (kept as is)" is offered only then).</summary>
    public bool HasCustomRule { get; }

    /// <summary>The time pickers' clock.</summary>
    public string Clock => _use24Hour ? "24HourClock" : "12HourClock";

    /// <summary>Calendars you can add events to.</summary>
    public ObservableCollection<CalendarChoice> Calendars { get; }

    /// <summary>Guests.</summary>
    public ObservableCollection<GuestRow> Guests { get; }

    /// <summary>Weekday toggles (Sunday first).</summary>
    public ObservableCollection<WeekdayToggle> Weekdays { get; }

    /// <summary>Popup reminder choices.</summary>
    public ObservableCollection<ReminderToggle> Reminders { get; }

    /// <summary>Title.</summary>
    [ObservableProperty]
    public partial string Title { get; set; }

    /// <summary>All-day event.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTimes))]
    public partial bool IsAllDay { get; set; }

    /// <summary>First day.</summary>
    [ObservableProperty]
    public partial DateTimeOffset? StartDate { get; set; }

    /// <summary>Start time (timed events).</summary>
    [ObservableProperty]
    public partial TimeSpan StartTime { get; set; }

    /// <summary>Last day (all-day) or the end's day.</summary>
    [ObservableProperty]
    public partial DateTimeOffset? EndDate { get; set; }

    /// <summary>End time (timed events).</summary>
    [ObservableProperty]
    public partial TimeSpan EndTime { get; set; }

    /// <summary>Picked calendar.</summary>
    [ObservableProperty]
    public partial int CalendarIndex { get; set; }

    /// <summary>Location.</summary>
    [ObservableProperty]
    public partial string Location { get; set; }

    /// <summary>Description (plain text).</summary>
    [ObservableProperty]
    public partial string Description { get; set; }

    /// <summary>Event color 1-11, or null for the calendar's.</summary>
    [ObservableProperty]
    public partial string? ColorId { get; set; }

    /// <summary>Use the calendar's default reminders.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowReminderChoices))]
    public partial bool UseDefaultReminders { get; set; }

    /// <summary>Repeat choice (see the class remarks).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRepeatOptions), nameof(ShowWeekdays), nameof(ShowEndsOn), nameof(ShowEndsAfter))]
    public partial int RepeatIndex { get; set; }

    /// <summary>Every N days/weeks/months/years.</summary>
    [ObservableProperty]
    public partial double RepeatInterval { get; set; }

    /// <summary>Ends choice (see the class remarks).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEndsOn), nameof(ShowEndsAfter))]
    public partial int EndsIndex { get; set; }

    /// <summary>Last date of the repeat.</summary>
    [ObservableProperty]
    public partial DateTimeOffset? EndsOn { get; set; }

    /// <summary>Number of times.</summary>
    [ObservableProperty]
    public partial double EndsAfter { get; set; }

    /// <summary>The address being typed.</summary>
    [ObservableProperty]
    public partial string GuestInput { get; set; } = "";

    /// <summary>A problem to fix before saving, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    /// <summary>Time pickers show (timed events).</summary>
    public bool ShowTimes => !IsAllDay;

    /// <summary>Interval and end choices show.</summary>
    public bool ShowRepeatOptions => RepeatIndex is >= 1 and <= 4;

    /// <summary>Weekday toggles show.</summary>
    public bool ShowWeekdays => RepeatIndex == 2;

    /// <summary>End date shows.</summary>
    public bool ShowEndsOn => ShowRepeatOptions && EndsIndex == 1;

    /// <summary>Count shows.</summary>
    public bool ShowEndsAfter => ShowRepeatOptions && EndsIndex == 2;

    /// <summary>Custom reminder toggles show.</summary>
    public bool ShowReminderChoices => !UseDefaultReminders;

    /// <summary>There are guests (the quiet save shows).</summary>
    public bool HasGuests => Guests.Count > 0;

    /// <summary>The error line shows.</summary>
    public bool HasError => Error is not null;

    /// <summary>The video link (read-only), or empty.</summary>
    public string ConferenceText => Before.ConferenceUri is { } uri ? $"Video call: {uri.Host}" : "";

    /// <summary>The video link row shows.</summary>
    public bool HasConference => Before.ConferenceUri is not null;

    /// <summary>Adds the typed address as a guest; false (with <see cref="Error"/>) when it isn't a valid address.</summary>
    public bool AddGuest()
    {
        var email = GuestInput.Trim();
        if (!MailAddress.TryCreate(email, out var address) || address.Address != email)
        {
            Error = "Enter a valid email address.";
            return false;
        }

        if (!Guests.Any(g => string.Equals(g.Email, email, StringComparison.OrdinalIgnoreCase)))
        {
            Guests.Add(new GuestRow(new Guest(email), RemoveGuest));
        }

        GuestInput = "";
        Error      = null;
        return true;
    }

    /// <summary>A problem that blocks saving, or null.</summary>
    public string? Validate()
    {
        if (ShowEndsOn && EndsOn is null)
        {
            return "Pick the day the repeat ends.";
        }

        // A cleared number box holds NaN
        if (ShowRepeatOptions && double.IsNaN(RepeatInterval))
        {
            return "Enter how often the event repeats.";
        }

        if (ShowEndsAfter && double.IsNaN(EndsAfter))
        {
            return "Enter how many times the event repeats.";
        }

        var draft = ToDraft();
        return draft.End < draft.Start ? "The event can't end before it starts." : null;
    }

    /// <summary>The fields as a draft (the calendar picker decides the account and calendar).</summary>
    public EventDraft ToDraft()
    {
        // No pick (the list is being swapped) keeps the event where it is
        var calendar = CalendarIndex >= 0 && CalendarIndex < Calendars.Count ? Calendars[CalendarIndex] : null;
        var startDay = Day(StartDate) ?? DateOnly.FromDateTime(Before.Start.UtcDateTime);
        var endDay   = Day(EndDate) ?? startDay;

        // Untouched times keep the loaded instants and zone exactly (no round trip through the pickers)
        var untouched = IsAllDay == Before.IsAllDay && (Day(StartDate), StartTime, Day(EndDate), EndTime) == _loadedWhen;
        var (start, end) = untouched
            ? (Before.Start, Before.End)
            : IsAllDay
                ? (Midnight(startDay), Midnight(endDay.AddDays(1)))
                : (DragMath.ToInstant(startDay.ToDateTime(TimeOnly.FromTimeSpan(StartTime)), _zone), DragMath.ToInstant(endDay.ToDateTime(TimeOnly.FromTimeSpan(EndTime)), _zone));
        var reminders = Reminders.Where(r => r.IsOn).Select(r => r.Minutes).ToList();

        return Before with
        {
            AccountId           = calendar?.AccountId ?? Before.AccountId,
            CalendarId          = calendar?.CalendarId ?? Before.CalendarId,
            Title               = Title,
            Start               = start,
            End                 = end,
            IsAllDay            = IsAllDay,
            TimeZone            = untouched || IsAllDay ? Before.TimeZone : Before.TimeZone ?? _localZoneId,
            Location            = Location,
            Description         = Description.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'),
            ColorId             = ColorId,
            Guests              = [.. Guests.Select(g => g.Guest with { Optional = g.Optional })],
            UseDefaultReminders = UseDefaultReminders,
            ReminderMinutes     = UseDefaultReminders || reminders.Order().SequenceEqual(Before.ReminderMinutes.Order()) ? Before.ReminderMinutes : reminders,
            Recurrence          = Recurrence(),
        };
    }

    // The repeat lines; an untouched rule keeps Google's exact lines (Core spots an unchanged repeat by them), and
    // EXDATE lines always stay
    IReadOnlyList<string> Recurrence()
    {
        if (RepeatIndex == 0)
        {
            return [];
        }

        if (RepeatIndex == 5)
        {
            return Before.Recurrence;
        }

        var line = RepeatLine();
        if (line == _loadedLine)
        {
            return Before.Recurrence;
        }

        return [line, .. Before.Recurrence.Where(l => !l.StartsWith("RRULE:", StringComparison.Ordinal))];
    }

    // The RRULE line the repeat fields describe (the week start is kept from Google's rule)
    string RepeatLine() => new RepeatRule(
        (RepeatFrequency)Math.Clamp(RepeatIndex - 1, 0, 3),
        Math.Max(1, (int)RepeatInterval),
        RepeatIndex == 2 ? [.. Weekdays.Where(w => w.IsOn).Select(w => w.Day)] : null,
        EndsIndex == 1 ? Day(EndsOn) : null,
        EndsIndex == 2 ? Math.Max(1, (int)EndsAfter) : null,
        _wkst).ToRRule(IsAllDay, _zone);

    void RemoveGuest(GuestRow row) => Guests.Remove(row);

    // =========================================================================
    // END FOLLOWS START
    // =========================================================================

    // Moving the start moves the end with it, keeping the event's length (like Google)
    partial void OnStartDateChanged(DateTimeOffset? oldValue, DateTimeOffset? newValue)
    {
        if (_ready && Day(oldValue) is { } from && Day(newValue) is { } to && Day(EndDate) is { } endDay)
        {
            EndDate = Picker(endDay.AddDays(to.DayNumber - from.DayNumber).ToDateTime(TimeOnly.MinValue));
        }
    }

    partial void OnStartTimeChanged(TimeSpan oldValue, TimeSpan newValue)
    {
        if (_ready && Day(EndDate) is { } endDay)
        {
            var end = endDay.ToDateTime(TimeOnly.FromTimeSpan(EndTime)) + (newValue - oldValue);
            EndDate = Picker(end);
            EndTime = end.TimeOfDay;
        }
    }

    // An all-day event turned timed would run midnight to midnight; give it 9 to 10 instead
    partial void OnIsAllDayChanged(bool value)
    {
        if (!_ready || value || StartTime != TimeSpan.Zero || EndTime != TimeSpan.Zero)
        {
            return;
        }

        _ready = false;
        StartTime = TimeSpan.FromHours(9);
        EndTime   = TimeSpan.FromHours(10);
        _ready    = true;
    }

    // Date pickers hold a local wall-clock date; only its date part is read back
    static DateTimeOffset Picker(DateTime value) => new(DateTime.SpecifyKind(value.Date, DateTimeKind.Unspecified));

    static DateOnly? Day(DateTimeOffset? value) => value is { } v ? DateOnly.FromDateTime(v.DateTime) : null;

    static DateTimeOffset Midnight(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}
