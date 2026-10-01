using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Mail;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeafCalendar.Core.Editing;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.People;
using LeafCalendar.Core.Views;
using CalendarRooms = LeafCalendar.Core.People.Rooms;

namespace LeafCalendar.App.ViewModels;

/// <summary>A calendar in the editor's picker (an App type, so WinRT can hold the list). <see cref="IsPrimary"/> marks the account's main calendar.</summary>
public sealed record CalendarChoice(string AccountId, string CalendarId, string Name, string AccountEmail, string Color, bool IsPrimary = false)
{
    /// <summary>The name (what a screen reader says for the picker's item).</summary>
    public override string ToString() => Name;
}

/// <summary>A guest in the editor, with its optional toggle and remove button. A room shows its name, and has no optional toggle.</summary>
public sealed partial class GuestRow(Guest guest, Action<GuestRow> remove, string? roomName = null) : ObservableObject
{
    /// <summary>The guest as loaded (or typed).</summary>
    public Guest Guest { get; } = guest;

    /// <summary>Address.</summary>
    public string Email => Guest.Email;

    /// <summary>A room (Google's resource attendee).</summary>
    public bool IsRoom => Guest.IsResource;

    /// <summary>The optional toggle shows (people only).</summary>
    public bool ShowOptional => !IsRoom;

    /// <summary>The chip's text: the address, or "{name} (room)" for a room (its address until its name is known).</summary>
    public string DisplayName => IsRoom ? $"{RoomName ?? Email} (room)" : Email;

    /// <summary>A room's cleaned name, once known.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    public partial string? RoomName { get; set; } = roomName;

    /// <summary>Optional attendance.</summary>
    [ObservableProperty]
    public partial bool Optional { get; set; } = guest.Optional;

    /// <summary>Automation ID of the chip's text.</summary>
    public string GuestId => $"EditorGuest_{Email}";

    /// <summary>Automation ID of the optional checkbox.</summary>
    public string OptionalId => $"EditorGuestOptional_{Email}";

    /// <summary>Automation ID of the remove button.</summary>
    public string RemoveId => $"EditorGuestRemove_{Email}";

    [RelayCommand]
    void Remove() => remove(this);
}

/// <summary>A contact suggestion for the guest box (an App type, so WinRT can hold the list).</summary>
public sealed class ContactSuggestion(string name, string email)
{
    /// <summary>Address added as the guest.</summary>
    public string Email { get; } = email;

    /// <summary>"Name &lt;email&gt;", or the address alone (plain text; Core already removed control characters).</summary>
    public string Display { get; } = name.Length > 0 ? $"{name} <{email}>" : email;

    /// <summary>The shown text (what a screen reader says for the item).</summary>
    public override string ToString() => Display;
}

/// <summary>A time zone suggestion for the editor's zone box (an App type, so WinRT can hold the list).</summary>
public sealed class ZoneSuggestion(TimeZoneChoice choice)
{
    /// <summary>IANA ID.</summary>
    public string Id { get; } = choice.Id;

    /// <summary>"Tokyo (UTC+9 · Tokyo Standard Time)".</summary>
    public string Display { get; } = choice.ToString();

    /// <summary>The shown text (what a screen reader says for the item).</summary>
    public override string ToString() => Display;
}

/// <summary>A room suggestion for the room box (an App type, so WinRT can hold the list).</summary>
public sealed class RoomSuggestion(Room room)
{
    /// <summary>The room (name already cleaned by Core).</summary>
    public Room Room { get; } = room;

    /// <summary>The room's name.</summary>
    public string Display => Room.Name;

    /// <summary>The shown text (what a screen reader says for the item).</summary>
    public override string ToString() => Display;
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

/// <summary>A custom reminder: a dropdown of times and a remove button.</summary>
public sealed partial class ReminderRow(int index, List<string> choices, int choiceIndex, Action<ReminderRow> remove) : ObservableObject
{
    /// <summary>The times to pick from (shared by every row; a concrete list so WinRT can hold it under AOT).</summary>
    public List<string> Choices { get; } = choices;

    /// <summary>The picked time's position in <see cref="Choices"/>.</summary>
    [ObservableProperty]
    public partial int ChoiceIndex { get; set; } = choiceIndex;

    /// <summary>The row's position (renumbered when a row above is removed).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationId), nameof(RemoveId), nameof(AccessibleName))]
    public partial int Index { get; set; } = index;

    /// <summary>Automation ID of the dropdown.</summary>
    public string AutomationId => string.Create(CultureInfo.InvariantCulture, $"EditorReminder_{Index}");

    /// <summary>Automation ID of the remove button.</summary>
    public string RemoveId => string.Create(CultureInfo.InvariantCulture, $"EditorReminderRemove_{Index}");

    /// <summary>What a screen reader calls the dropdown ("Reminder 1", under the "Reminder" heading).</summary>
    public string AccessibleName => string.Create(CultureInfo.InvariantCulture, $"Reminder {Index + 1}");

    // A dropdown swapping its list writes "nothing picked" back; keep the pick
    partial void OnChoiceIndexChanged(int oldValue, int newValue)
    {
        if (newValue < 0)
        {
            ChoiceIndex = oldValue;
        }
    }

    [RelayCommand]
    void Remove() => remove(this);
}

/// <summary>
/// The editor's fields for one event (new or existing). <see cref="ToDraft"/> turns them back into an
/// <see cref="EventDraft"/>; comparing it with <see cref="Before"/> gives the patch, so untouched fields are never sent.
/// </summary>
/// <remarks>
/// Times are shown on the event's own zone's clock (<see cref="TimeZoneId"/>). An all-day event shows its last day (Google stores the day after). Repeat
/// choices: 0 none, 1 daily, 2 weekly, 3 monthly, 4 yearly, 5 a rule the editor can't show (kept exactly as it is).
/// Ends: 0 never, 1 on a date, 2 after a count.
/// </remarks>
public sealed partial class EventEditorViewModel : ObservableObject, IDisposable
{
    const int MaxReminders = 5;

    // Google's visibility values, in the dropdown's order
    static readonly string[] VisibilityValues = ["default", "public", "private"];

    static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    readonly TimeZoneInfo _zone;
    readonly string _localZoneId;
    readonly bool _use24Hour;
    readonly DayOfWeek? _wkst;
    readonly string? _loadedLine;
    readonly (DateOnly? StartDay, TimeSpan StartTime, DateOnly? EndDay, TimeSpan EndTime) _loadedWhen;
    readonly string _loadedZoneId;
    readonly int _loadedVisibility;
    readonly int[] _reminderMinutes;
    readonly LatestSearch<ContactResults> _contactSearch = new();
    TimeZoneInfo _eventZone;
    string? _searchedAccount;
    bool _ready;
    bool _conferenceTouched;
    bool _applyingMeetDefault;

    /// <summary>
    /// Loads the fields from <paramref name="draft"/>. <paramref name="zone"/> is the zone on screen and
    /// <paramref name="localZoneId"/> its IANA ID; the dates and times show in the event's own zone (else that one).
    /// </summary>
    public EventEditorViewModel(EventDraft draft, CalendarOccurrence? occurrence, IReadOnlyList<CalendarChoice> calendars, TimeZoneInfo zone, string localZoneId, bool use24Hour, bool focusEnd = false)
    {
        Before       = draft;
        Occurrence   = occurrence;
        FocusEnd     = focusEnd;
        _zone        = zone;
        _localZoneId = localZoneId;
        _use24Hour   = use24Hour;

        // The Event's Own Zone (one this PC doesn't know shows in the zone on screen)
        TimeZoneId    = draft.TimeZone is { } own && TimeZoneCatalog.IsKnown(own) ? own : localZoneId;
        _eventZone    = FindZone(TimeZoneId) ?? zone;
        _loadedZoneId = TimeZoneId;

        // Show As And Visibility (confidential reads as Private)
        ShowAsIndex       = draft.IsFree ? 1 : 0;
        VisibilityIndex   = draft.Visibility switch { "public" => 1, "private" or "confidential" => 2, _ => 0 };
        _loadedVisibility = VisibilityIndex;

        // Fields
        Calendars           = new ObservableCollection<CalendarChoice>(calendars);
        CalendarIndex       = calendars.ToList().FindIndex(c => c.AccountId == draft.AccountId && c.CalendarId == draft.CalendarId);
        Title               = draft.Title;
        Location            = draft.Location;
        Description         = draft.Description;
        ColorId             = draft.ColorId;
        IsAllDay            = draft.IsAllDay;
        UseDefaultReminders = draft.UseDefaultReminders;
        HasConference       = draft.HasConference;
        Guests              = new ObservableCollection<GuestRow>(draft.Guests.Select(g => new GuestRow(g, RemoveGuest)));

        // Reminders (one row per loaded time)
        _reminderMinutes = [.. ReminderTimes.Choices(draft.ReminderMinutes)];
        ReminderChoices  = _reminderMinutes.Select(ReminderTimes.Label).ToList();
        ReminderRows     = [];
        foreach (var minutes in draft.ReminderMinutes)
        {
            AddReminder(minutes);
        }

        // When (an all-day event shows its last day, inclusive; a timed one shows its own zone's clock)
        var start = draft.IsAllDay ? draft.Start.UtcDateTime : TimeZoneInfo.ConvertTime(draft.Start, _eventZone).DateTime;
        var end   = draft.IsAllDay ? draft.End.UtcDateTime.AddDays(-1) : TimeZoneInfo.ConvertTime(draft.End, _eventZone).DateTime;
        ZoneInput = TimeZoneText;
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

        Guests.CollectionChanged       += (_, _) => OnPropertyChanged(nameof(HasGuests));
        ReminderRows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanAddReminder));
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

    /// <summary>A new event's typed title (else "New event"), or "Edit event".</summary>
    public string HeaderText => EditorHeader.Text(IsNew, Title);

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

    /// <summary>The reminder times every dropdown offers ("At start", "10 min", ...).</summary>
    public List<string> ReminderChoices { get; }

    /// <summary>Custom popup reminders, one dropdown each.</summary>
    public ObservableCollection<ReminderRow> ReminderRows { get; }

    /// <summary>Title.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderText))]
    public partial string Title { get; set; }

    /// <summary>All-day event.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTimes), nameof(ShowTimeZone), nameof(ShowLocalTime), nameof(LocalTimeText))]
    public partial bool IsAllDay { get; set; }

    /// <summary>First day.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocalTimeText))]
    public partial DateTimeOffset? StartDate { get; set; }

    /// <summary>Start time (timed events).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocalTimeText))]
    public partial TimeSpan StartTime { get; set; }

    /// <summary>Last day (all-day) or the end's day.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocalTimeText))]
    public partial DateTimeOffset? EndDate { get; set; }

    /// <summary>End time (timed events).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocalTimeText))]
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

    /// <summary>
    /// True when Leaf had to cut the description short when reading it. It's shown read-only and never saved, since saving
    /// it would delete the rest of it on Google.
    /// </summary>
    public bool DescriptionTooLong => Before.DescriptionTooLong;

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

    /// <summary>Custom reminder dropdowns show.</summary>
    public bool ShowReminderChoices => !UseDefaultReminders;

    /// <summary>Another reminder can be added (Google allows 5).</summary>
    public bool CanAddReminder => ReminderRows.Count < MaxReminders;

    /// <summary>There are guests (the quiet save shows).</summary>
    public bool HasGuests => Guests.Count > 0;

    /// <summary>The error line shows.</summary>
    public bool HasError => Error is not null;

    /// <summary>The event has (or gets on save) a Google video call; off for new events.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConferenceText), nameof(CanAddConference))]
    public partial bool HasConference { get; set; }

    /// <summary>"No video call", "Video call: {host}" (the event's own), or "Google Meet link is added when you save".</summary>
    public string ConferenceText => EditorConference.Text(Before.HasConference, HasConference, Before.ConferenceUri);

    /// <summary>"Add Google Meet" shows (no call yet).</summary>
    public bool CanAddConference => !HasConference;

    // =========================================================================
    // EVENT TYPE, SHOW AS, AND VISIBILITY
    // =========================================================================

    /// <summary>0 Event, 1 Focus time, 2 Out of office (new events on a Workspace account's main calendar only).</summary>
    [ObservableProperty]
    public partial int EventTypeIndex { get; set; }

    /// <summary>
    /// The event type dropdown shows: a new timed event on a Workspace account's primary calendar (Google can't change an
    /// event's type later).
    /// </summary>
    public bool ShowEventType => IsNew && !IsAllDay && SelectedCalendar is { IsPrimary: true } calendar && IsWorkspaceAccount?.Invoke(calendar.AccountId) == true;

    /// <summary>The type the event is saved as: the picked one for a new event (when offered), else the loaded one.</summary>
    public EventKind EffectiveType => !IsNew
        ? Before.EventType
        : ShowEventType ? EventTypeIndex switch { 1 => EventKind.FocusTime, 2 => EventKind.OutOfOffice, _ => EventKind.Default } : EventKind.Default;

    /// <summary>Guests (and rooms) show: focus time and out of office have none.</summary>
    public bool ShowGuests => EffectiveType == EventKind.Default;

    /// <summary>The location shows (not for focus time or out of office).</summary>
    public bool ShowLocation => EffectiveType == EventKind.Default;

    /// <summary>The video call row shows (not for focus time or out of office).</summary>
    public bool ShowConference => EffectiveType == EventKind.Default;

    /// <summary>Show as and All day can change (focus time and out of office are always busy and timed).</summary>
    public bool IsOrdinaryType => EffectiveType == EventKind.Default;

    /// <summary>0 Busy, 1 Free.</summary>
    [ObservableProperty]
    public partial int ShowAsIndex { get; set; }

    /// <summary>0 Default visibility, 1 Public, 2 Private. A loaded event keeps Google's own value until this changes.</summary>
    [ObservableProperty]
    public partial int VisibilityIndex { get; set; }

    // The picked calendar, or null while the list is swapped
    CalendarChoice? SelectedCalendar => CalendarIndex >= 0 && CalendarIndex < Calendars.Count ? Calendars[CalendarIndex] : null;

    // A type picked with no title names the event; a type other than Event is busy
    partial void OnEventTypeIndexChanged(int value)
    {
        if (value > 0 && string.IsNullOrWhiteSpace(Title))
        {
            Title = value == 1 ? "Focus time" : "Out of office";
        }

        TypeInputsChanged();
    }

    // Everything that hangs on the event type (the rows it hides, the locked switches)
    void TypeInputsChanged()
    {
        if (!IsOrdinaryType)
        {
            ShowAsIndex = 0;
        }

        OnPropertyChanged(nameof(ShowEventType));
        OnPropertyChanged(nameof(EffectiveType));
        OnPropertyChanged(nameof(ShowGuests));
        OnPropertyChanged(nameof(ShowLocation));
        OnPropertyChanged(nameof(ShowConference));
        OnPropertyChanged(nameof(IsOrdinaryType));
        OnPropertyChanged(nameof(ShowRooms));
    }

    // =========================================================================
    // TIME ZONE
    // =========================================================================

    /// <summary>The event's IANA zone; its dates and times show on this zone's clock.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeZoneText), nameof(LocalTimeText), nameof(ShowLocalTime))]
    public partial string TimeZoneId { get; set; }

    /// <summary>"Tokyo (UTC+9)".</summary>
    public string TimeZoneText => $"{TimeZoneCatalog.CityFor(TimeZoneId)} ({TimeZoneCatalog.OffsetLabel(_eventZone.GetUtcOffset(Before.Start))})";

    /// <summary>The zone box's text (the picked zone, or what's being typed).</summary>
    [ObservableProperty]
    public partial string ZoneInput { get; set; } = "";

    /// <summary>Zones matching the typed text.</summary>
    public ObservableCollection<ZoneSuggestion> ZoneSuggestions { get; } = [];

    /// <summary>The zone box shows (timed events).</summary>
    public bool ShowTimeZone => !IsAllDay;

    /// <summary>The "In your time" line shows: a timed event whose zone isn't the one on screen.</summary>
    public bool ShowLocalTime => !IsAllDay && TimeZoneCatalog.IanaId(_eventZone) != TimeZoneCatalog.IanaId(_zone);

    /// <summary>"In your time: Wed, Sep 30, 8:00 PM–9:00 PM": the event's times on the screen's clock, or empty.</summary>
    public string LocalTimeText
    {
        get
        {
            if (!ShowLocalTime || Day(StartDate) is not { } startDay)
            {
                return "";
            }

            var start = TimeZoneInfo.ConvertTime(EditorTimes.ToInstant(startDay, StartTime, _eventZone), _zone);
            var end   = TimeZoneInfo.ConvertTime(EditorTimes.ToInstant(Day(EndDate) ?? startDay, EndTime, _eventZone), _zone);
            var clock = _use24Hour ? "HH:mm" : "h:mm tt";
            return $"In your time: {start.ToString("ddd, MMM d", English)}, {start.ToString(clock, English)}–{end.ToString(clock, English)}";
        }
    }

    /// <summary>Lists the zones matching the typed text.</summary>
    public void RefreshZoneSuggestions(DateTimeOffset now)
    {
        ZoneSuggestions.Clear();
        foreach (var choice in TimeZoneCatalog.Search(ZoneInput, now, 8))
        {
            ZoneSuggestions.Add(new ZoneSuggestion(choice));
        }
    }

    /// <summary>Makes a picked zone the event's own: the clock fields stay, so the event moves to that clock.</summary>
    public void PickZone(ZoneSuggestion zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        if (FindZone(zone.Id) is { } picked)
        {
            _eventZone = picked;
            TimeZoneId = zone.Id;
        }

        ResetZoneInput();
    }

    /// <summary>Puts the picked zone back in the box (typing without a pick changes nothing).</summary>
    public void ResetZoneInput()
    {
        ZoneInput = TimeZoneText;
        ZoneSuggestions.Clear();
    }

    static TimeZoneInfo? FindZone(string id) => TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;

    // =========================================================================
    // MEET BY DEFAULT
    // =========================================================================

    /// <summary>
    /// Whether an account adds Google Meet to new events (set by <see cref="CalendarViewModel"/>). A new event starts
    /// from its account's choice, and follows the picked calendar's account until the call buttons are used.
    /// </summary>
    public Func<string, bool>? MeetByDefault
    {
        get;
        set
        {
            field = value;
            ApplyMeetDefault();
        }
    }

    void ApplyMeetDefault()
    {
        if (!IsNew || _conferenceTouched || MeetByDefault is not { } meet)
        {
            return;
        }

        _applyingMeetDefault = true;
        HasConference        = meet(ContactsAccountId);
        _applyingMeetDefault = false;
    }

    // The call buttons were used: the account's default no longer applies
    partial void OnHasConferenceChanged(bool value)
    {
        if (_ready && !_applyingMeetDefault)
        {
            _conferenceTouched = true;
        }
    }

    // =========================================================================
    // CONTACT SUGGESTIONS
    // =========================================================================

    /// <summary>
    /// Searches the picked calendar's account for the typed text (set by <see cref="CalendarViewModel"/>); null leaves
    /// suggestions off.
    /// </summary>
    public Func<string, CancellationToken, Task<ContactResults>>? SearchContacts { get; set; }

    /// <summary>Contacts matching the typed text (kept only while typing; cleared on pick, empty text, and close).</summary>
    public ObservableCollection<ContactSuggestion> Suggestions { get; } = [];

    /// <summary>Whether the last search could run, or what the user can fix.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAllowContacts), nameof(ShowContactsApiOff))]
    public partial ContactAccess ContactsAccess { get; set; } = ContactAccess.Allowed;

    /// <summary>"Allow contact suggestions" shows (the account's sign-in lacks the contacts permission).</summary>
    public bool ShowAllowContacts => ContactsAccess == ContactAccess.NeedsConsent;

    /// <summary>The "turn on the People API" line shows.</summary>
    public bool ShowContactsApiOff => ContactsAccess == ContactAccess.ApiDisabled;

    /// <summary>The account suggestions come from (the picked calendar's).</summary>
    public string ContactsAccountId => CalendarIndex >= 0 && CalendarIndex < Calendars.Count ? Calendars[CalendarIndex].AccountId : Before.AccountId;

    /// <summary>
    /// Searches contacts for the typed text, replacing <see cref="Suggestions"/>. A newer search cancels this one, and
    /// a result that arrives late is dropped. The text is never logged.
    /// </summary>
    public async Task RefreshSuggestionsAsync()
    {
        var text = GuestInput.Trim();
        if (text.Length == 0)
        {
            ClearSuggestions();
            return;
        }

        // People You Meet Often Show At Once
        var account = ContactsAccountId;
        ShowSuggestions(LocalPeople?.Invoke(account, text) ?? []);
        if (SearchContacts is not { } search)
        {
            return;
        }

        // Stale (the user kept typing) or closed
        if (await _contactSearch.RunAsync(ct => search(text, ct)) is not { } results)
        {
            return;
        }

        // Google's Answers Go After Them (read again: they may have loaded meanwhile)
        _searchedAccount = account;
        ContactsAccess   = results.Access;
        ShowSuggestions([.. LocalPeople?.Invoke(account, text) ?? [], .. results.Contacts]);
    }

    /// <summary>
    /// "People you meet often" for an account and the typed text (set by <see cref="CalendarViewModel"/>); listed before
    /// Google's suggestions. Null leaves them out.
    /// </summary>
    public Func<string, string, IReadOnlyList<Contact>>? LocalPeople { get; set; }

    // One row per address (ignoring case), at most as many as a Google search gives
    void ShowSuggestions(IEnumerable<Contact> contacts)
    {
        Suggestions.Clear();
        foreach (var contact in contacts.DistinctBy(c => c.Email, StringComparer.OrdinalIgnoreCase).Take(ContactSearch.MaxResults))
        {
            Suggestions.Add(new ContactSuggestion(contact.Name, contact.Email));
        }
    }

    /// <summary>Adds a picked suggestion as a guest; the box empties (dropping the suggestions) only when it's added.</summary>
    public void PickSuggestion(ContactSuggestion suggestion)
    {
        GuestInput = suggestion.Email;
        AddGuest();
    }

    /// <summary>Stops any search and lets go of the suggestions (contacts stay in memory only while typing).</summary>
    public void ClearSuggestions()
    {
        _contactSearch.Cancel();
        Suggestions.Clear();
    }

    // Another account's contacts: what the last account allowed no longer counts (and its rooms are its own)
    partial void OnCalendarIndexChanged(int value)
    {
        TypeInputsChanged();
        ApplyMeetDefault();
        if (_ready && Rooms.Count > 0)
        {
            Rooms = [];
            RoomSuggestions.Clear();
        }

        if (_searchedAccount is not null && _searchedAccount != ContactsAccountId)
        {
            _searchedAccount = null;
            ContactsAccess   = ContactAccess.Allowed;
            ClearSuggestions();
        }
    }

    partial void OnGuestInputChanged(string value)
    {
        if (value.Trim().Length == 0)
        {
            ClearSuggestions();
            return;
        }

        // The First Letter Starts Loading People You Meet Often, So They're Ready When The Search Runs
        _ = LocalPeople?.Invoke(ContactsAccountId, "");
    }

    /// <summary>The editor closed: stops any search and lets go of the suggestions.</summary>
    public void Dispose()
    {
        ClearSuggestions();
        RoomSuggestions.Clear();
        Rooms = [];
        _contactSearch.Dispose();
    }

    // =========================================================================
    // ROOMS
    // =========================================================================

    /// <summary>Whether an account is a Google Workspace one (set by <see cref="CalendarViewModel"/>); rooms show only there.</summary>
    public Func<string, bool>? IsWorkspaceAccount
    {
        get;
        set
        {
            field = value;
            TypeInputsChanged();
        }
    }

    /// <summary>Loads the rooms an account booked before (set by <see cref="CalendarViewModel"/>, cached per editor).</summary>
    public Func<string, Task<IReadOnlyList<Room>>>? LoadRooms { get; set; }

    /// <summary>The picked calendar's account's rooms (empty until loaded).</summary>
    public IReadOnlyList<Room> Rooms { get; private set; } = [];

    /// <summary>The room box shows (a Workspace account's calendar is picked, and the event takes guests).</summary>
    public bool ShowRooms => ShowGuests && IsWorkspaceAccount?.Invoke(ContactsAccountId) == true;

    /// <summary>Leaf learned which accounts are Workspace ones (an older account's first lookup): the rows that depend on it update.</summary>
    public void AccountsChanged() => TypeInputsChanged();

    /// <summary>The room name being typed.</summary>
    [ObservableProperty]
    public partial string RoomInput { get; set; } = "";

    /// <summary>Rooms matching the typed text.</summary>
    public ObservableCollection<RoomSuggestion> RoomSuggestions { get; } = [];

    /// <summary>Loads the picked account's rooms (once per editor), names the room chips, and lists the rooms matching the typed text.</summary>
    public async Task RefreshRoomSuggestionsAsync()
    {
        await EnsureRoomsAsync();

        RoomSuggestions.Clear();
        foreach (var room in CalendarRooms.Match(Rooms, RoomInput))
        {
            RoomSuggestions.Add(new RoomSuggestion(room));
        }
    }

    /// <summary>Loads the picked account's rooms when it's a Workspace one, and gives room chips their names.</summary>
    public async Task EnsureRoomsAsync()
    {
        if (!ShowRooms || LoadRooms is not { } load)
        {
            return;
        }

        // Another calendar picked meanwhile: its own load will run
        var account = ContactsAccountId;
        var rooms   = await load(account);
        if (account != ContactsAccountId)
        {
            return;
        }

        Rooms = rooms;
        foreach (var row in Guests.Where(g => g.IsRoom))
        {
            row.RoomName = rooms.FirstOrDefault(r => string.Equals(r.Email, row.Email, StringComparison.OrdinalIgnoreCase))?.Name ?? row.RoomName;
        }
    }

    /// <summary>Adds a picked room as a resource guest (once), and empties the room box.</summary>
    public void PickRoom(RoomSuggestion room)
    {
        ArgumentNullException.ThrowIfNull(room);

        if (!Guests.Any(g => string.Equals(g.Email, room.Room.Email, StringComparison.OrdinalIgnoreCase)))
        {
            Guests.Add(new GuestRow(new Guest(room.Room.Email, IsResource: true), RemoveGuest, room.Room.Name));
        }

        RoomInput = "";
        RoomSuggestions.Clear();
    }

    partial void OnRoomInputChanged(string value)
    {
        if (value.Trim().Length == 0)
        {
            RoomSuggestions.Clear();
        }
    }

    // =========================================================================
    // GUESTS AND SAVING
    // =========================================================================

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

        // Untouched times keep the loaded instants and zone exactly (no round trip through the pickers); another zone keeps
        // the clock and moves the instants
        var zoneChanged = !IsAllDay && TimeZoneId != _loadedZoneId;
        var untouched   = !zoneChanged && IsAllDay == Before.IsAllDay && (Day(StartDate), StartTime, Day(EndDate), EndTime) == _loadedWhen;
        var (start, end) = untouched
            ? (Before.Start, Before.End)
            : IsAllDay
                ? (Midnight(startDay), Midnight(endDay.AddDays(1)))
                : (EditorTimes.ToInstant(startDay, StartTime, _eventZone), EditorTimes.ToInstant(endDay, EndTime, _eventZone));
        var reminders = ReminderRows.Select(r => _reminderMinutes[r.ChoiceIndex]).Distinct().ToList();
        var kind      = EffectiveType;

        return Before with
        {
            AccountId           = calendar?.AccountId ?? Before.AccountId,
            CalendarId          = calendar?.CalendarId ?? Before.CalendarId,
            Title               = Title,
            Start               = start,
            End                 = end,
            IsAllDay            = IsAllDay,
            TimeZone            = zoneChanged ? TimeZoneId : untouched || IsAllDay ? Before.TimeZone : Before.TimeZone ?? TimeZoneId,
            Location            = Location,
            Description         = Description,
            ColorId             = ColorId,
            Guests              = [.. Guests.Select(g => g.Guest with { Optional = g.Optional })],
            UseDefaultReminders = UseDefaultReminders,
            ReminderMinutes     = UseDefaultReminders || reminders.Order().SequenceEqual(Before.ReminderMinutes.Order()) ? Before.ReminderMinutes : reminders,
            Recurrence          = Recurrence(),
            HasConference       = HasConference,
            EventType           = kind,
            IsFree              = kind == EventKind.Default ? ShowAsIndex == 1 : !IsNew && Before.IsFree,
            Visibility          = VisibilityIndex == _loadedVisibility ? Before.Visibility : VisibilityValues[Math.Clamp(VisibilityIndex, 0, 2)],
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

    /// <summary>Adds a 10 minute reminder row (none past Google's 5).</summary>
    public void AddReminder() => AddReminder(10);

    void AddReminder(int minutes)
    {
        if (!CanAddReminder)
        {
            return;
        }

        ReminderRows.Add(new ReminderRow(ReminderRows.Count, ReminderChoices, Array.IndexOf(_reminderMinutes, minutes), RemoveReminder));
    }

    // The rows below move up a place
    void RemoveReminder(ReminderRow row)
    {
        ReminderRows.Remove(row);
        for (var i = 0; i < ReminderRows.Count; i++)
        {
            ReminderRows[i].Index = i;
        }
    }

    // Turning off the default with no custom times starts with one
    partial void OnUseDefaultRemindersChanged(bool value)
    {
        if (_ready && !value && ReminderRows.Count == 0)
        {
            AddReminder();
        }
    }

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
        // The Event Type Is Offered For Timed Events Only
        TypeInputsChanged();

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
