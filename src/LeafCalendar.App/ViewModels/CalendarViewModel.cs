using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Editing;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Sync;
using LeafCalendar.Core.Views;
using Microsoft.UI.Dispatching;

namespace LeafCalendar.App.ViewModels;

/// <summary>
/// An upcoming event in the details panel; <see cref="CanJoin"/> shows its Join button. The buttons x:Bind their
/// clicks to <see cref="Open"/> and <see cref="Join"/>, so nothing is read back from a control's Tag.
/// </summary>
public sealed record UpcomingItem(CalendarOccurrence Occurrence, string Title, string When, string Relative, string Color, bool CanJoin, MeetingProvider? Provider, Action<CalendarOccurrence> OnOpen, Action<CalendarOccurrence> OnJoin)
{
    /// <summary>Automation ID of the Join button.</summary>
    public string JoinId => $"UpcomingJoin_{Occurrence.EventId}";

    /// <summary>Row click: shows the event.</summary>
    public void Open() => OnOpen(Occurrence);

    /// <summary>Join click: opens the call.</summary>
    public void Join() => OnJoin(Occurrence);
}

/// <summary>The selected event, ready to show: details, the editor's fields, styled description, and what you may do.</summary>
public sealed record SelectedEventInfo(
    CalendarOccurrence Occurrence,
    EventDetails Details,
    string When,
    string CalendarName,
    string CalendarColor,
    EventDraft Draft,
    IReadOnlyList<DescriptionRun> DescriptionRuns,
    bool CanEdit,
    bool CanRespond)
{
    /// <summary>"Busy · Default visibility": whether the event blocks your time, and who can see it (confidential shows as Private).</summary>
    public string StatusText => $"{(Details.IsFree ? "Free" : "Busy")} · {Details.Visibility switch
    {
        "public" => "Public",
        "private" => "Private",
        "confidential" => "Private",
        _ => "Default visibility",
    }}";
}

/// <summary>
/// A guest in the details panel: address, a summary such as "Maybe · Optional · “Late”", and Google's display name if
/// known. Like the editor's chip, a named guest shows the name with the address under it.
/// </summary>
public sealed record GuestItem(string Email, string Detail, string? Name = null)
{
    /// <summary>The first line: the name, else the address.</summary>
    public string Primary => Name ?? Email;

    /// <summary>A named guest's address line (empty otherwise).</summary>
    public string Address => Name is null ? "" : Email;

    /// <summary>The address line shows.</summary>
    public bool ShowAddress => Name is not null;
}

/// <summary>The bar at the bottom of the calendar ("Event deleted · Undo", or a short message).</summary>
public sealed record NoticeInfo(string Text, bool CanUndo);

/// <summary>A page of the Settings window.</summary>
public enum SettingsSection
{
    /// <summary>Theme, view, and time settings.</summary>
    General,

    /// <summary>Calendar colors and visibility.</summary>
    Calendars,

    /// <summary>Extra time-zone columns.</summary>
    TimeZones,

    /// <summary>Reminder, Join now, invitation, and sound switches.</summary>
    Notifications,

    /// <summary>The tray flyout's agenda and lookahead.</summary>
    Tray,

    /// <summary>Global shortcuts.</summary>
    Shortcuts,

    /// <summary>Google accounts, sync, and the OAuth client.</summary>
    Accounts,

    /// <summary>Version and links.</summary>
    About,
}

/// <summary>
/// State and commands for the calendar views. It owns the sliding event cache, the user's view
/// settings (saved on every change), selection, and the upcoming list.
/// </summary>
/// <remarks>
/// Views listen to the events here and redraw; they never touch the database. Sync notifications
/// arrive on a background thread and are marshaled to the UI thread before the cache refreshes.
/// </remarks>
public sealed partial class CalendarViewModel : ObservableObject, IDisposable
{
    private readonly LeafServices _services;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _minuteTimer;
    private readonly CancellationTokenSource _life = new();
    private (DateOnly First, DateOnly Last) _ensuredMonths;
    private SyncEngine? _attachedSync;
    private readonly List<CalendarOccurrence> _selection = [];
    private (DateOnly Day, Func<CalendarOccurrence, bool> Match)? _reselect;
    private readonly List<DeleteReceipt> _deletes = [];

    // ponytail: session-only and capped, since older deletes are rarely wanted
    private const int UndoDepth = 50;
    private readonly List<(CalendarOccurrence Occurrence, EventCopy Copy)> _clipboard = [];

    // Marks Leaf's own events on the Windows clipboard, so Ctrl+V pastes them only while nothing else was copied since
    private const string ClipboardFormat = "LeafCalendar.Events";

    // Places visited, for back and forward; _restoring keeps a back or forward step from being recorded again
    private readonly NavigationHistory _history = new();
    private bool _restoring;

    /// <summary>Loads settings and calendars and starts the minute clock.</summary>
    public CalendarViewModel(LeafServices services, DispatcherQueue dispatcher)
    {
        _services = services;
        _dispatcher = dispatcher;

        using (var conn = services.Database.Open())
        {
            Settings = SettingsStore.Load(conn);
            Calendars = CalendarStore.GetAll(conn);
            ReadAccounts(conn);
        }

        // "Today" Is The Date In The Zone On Screen, Not The PC Clock's
        _applied = Zone;
        Today = services.Options.StartDate ?? LocalDate(Now);
        Cache = new EventWindowCache(LoadAsync, Zone, DrawnFirst);
        Cache.Changed += (_, _) =>
        {
            // The Data Changed: one calendar's upcoming list is read again, meeting links looked up again, and the
            // command menu's search index read again when it's next needed
            ForgetCalendarSoon();
            _providers.Clear();
            _searchIndex = null;
            _searchIndexGeneration++;
            RefreshUpcoming();
            OccurrencesChanged?.Invoke(this, EventArgs.Empty);
        };

        PeriodStart = ViewNavigator.PeriodStart(Settings.ViewMode, Today, Settings.WeekStart);
        PeriodTitle = ViewNavigator.MonthTitle(PeriodStart);

        // Seed History With The Opening Place
        _history.Visit(new ViewPlace(Mode, Settings.CustomDayCount, PeriodStart));

        services.GoogleChanged += OnGoogleChanged;
        AttachSync();

        // Local Edits Reload The Views
        services.Editor.LocalZoneId = TimeZoneCatalog.IanaId(UserZone);
        services.Editor.Changed += OnEditsChanged;
        services.Conflicts.Changed += OnEditsChanged;
        RefreshSyncState();

        _minuteTimer = dispatcher.CreateTimer();
        _minuteTimer.Interval = TimeSpan.FromMinutes(1);
        _minuteTimer.Tick += (_, _) => OnMinute();
        _minuteTimer.Start();
    }

    /// <summary>"Today": the real date, or <c>--start-date</c> in test mode.</summary>
    public DateOnly Today { get; private set; }

    /// <summary>The current instant (in test mode, 8:00 local on the start date, so tests are stable).</summary>
    public DateTimeOffset Now => _services.Options.StartDate is { } d
        ? OccurrenceQuery.LocalMidnight(d, Zone).AddHours(8)
        : _services.Time.GetUtcNow();

    /// <summary>True once <paramref name="o"/> is over (drawn faded).</summary>
    public bool IsPast(CalendarOccurrence o) => o.HasEndedBy(Now, Zone);

    /// <summary>
    /// An event card's hover tooltip: the title, the time, and the location when there is one, one cleaned line each.
    /// </summary>
    /// <remarks>The location isn't on the occurrence, so it's read from the stored event (once per hover, not per card drawn).</remarks>
    public string HoverText(CalendarOccurrence o, string timeText)
    {
        var lines = new List<string> { Core.Tray.DisplayText.Clean(o.Title, 200), timeText };
        try
        {
            using var conn = _services.Database.Open();
            if (EventStore.Get(conn, o.AccountId, o.CalendarId, o.EventId) is { } stored)
            {
                lines.Add(Core.Tray.DisplayText.Clean(EventDetailsParser.Parse(stored.RawJson)?.Location, 200));
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            _services.Log.Error("calendar.hover.failed", ex);
        }

        return string.Join('\n', lines.Where(l => l.Length > 0));
    }

    /// <summary>What Narrator reads for an event card: "Title, time, calendar name", then ", past" and ", declined" when they apply (never color alone).</summary>
    public string CardName(CalendarOccurrence o, string timeText)
    {
        var calendar = Calendars.FirstOrDefault(c => c.AccountId == o.AccountId && c.Id == o.CalendarId)?.Summary;
        var parts = new List<string> { o.Title, timeText };
        if (!string.IsNullOrEmpty(calendar))
        {
            parts.Add(calendar);
        }

        if (IsPast(o))
        {
            parts.Add("past");
        }

        if (o.SelfResponse == ResponseStatus.Declined)
        {
            parts.Add("declined");
        }

        return string.Join(", ", parts);
    }

    /// <summary>The user's view settings.</summary>
    public LeafSettings Settings { get; private set; }

    /// <summary>The ±3-month event cache.</summary>
    public EventWindowCache Cache { get; }

    /// <summary>Every calendar, grouped by account.</summary>
    public IReadOnlyList<CalendarInfo> Calendars { get; private set; }

    /// <summary>Account ID → email, for sidebar headers.</summary>
    public IReadOnlyDictionary<string, string> AccountEmails { get; private set; } = new Dictionary<string, string>();

    /// <summary>True for a Google Workspace account (Google's <c>hd</c> sign-in claim); rooms and event types show only there.</summary>
    public bool IsWorkspace(string accountId) => _workspace.Contains(accountId);

    // The accounts Leaf knows are Workspace ones
    private HashSet<string> _workspace = [];

    // Workspace domains are looked up once per session, for accounts that signed in before Leaf stored them
    private bool _domainsChecked;

    private void ReadAccounts(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        var accounts = AccountStore.GetAll(conn);
        AccountEmails = accounts.ToDictionary(a => a.Id, a => a.Email);
        _workspace = [.. accounts.Where(AccountStore.IsWorkspace).Select(a => a.Id)];
    }

    /// <summary>Events in the next <see cref="LeafSettings.UpcomingHours"/> hours (or one calendar's next 30 days, see <see cref="UpcomingCalendar"/>).</summary>
    public ObservableCollection<UpcomingItem> Upcoming { get; } = [];

    /// <summary>Current view mode.</summary>
    public CalendarViewMode Mode => Settings.ViewMode;

    /// <summary>Day columns the current view shows.</summary>
    public int VisibleColumns => ViewNavigator.VisibleColumnCount(Settings.ViewMode, Settings.CustomDayCount, Settings.ShowWeekends);

    /// <summary>Start of the visible period.</summary>
    [ObservableProperty]
    public partial DateOnly PeriodStart { get; set; }

    /// <summary>The island's title: the month and year of the first visible day (month view: the focused month), such as "October 2026".</summary>
    [ObservableProperty]
    public partial string PeriodTitle { get; set; }

    /// <summary>The window title: the days on screen ("Oct 5 – 11, 2026"; month view: "October 2026"), which the taskbar shows.</summary>
    [ObservableProperty]
    public partial string WindowTitle { get; set; } = "Leaf Calendar";

    /// <summary>The selected event, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial SelectedEventInfo? SelectedInfo { get; set; }

    /// <summary>True when an event is selected.</summary>
    public bool HasSelection => SelectedInfo is not null;

    /// <summary>Every selected event (one or more; the details panel shows the first when it's the only one).</summary>
    public IReadOnlyList<CalendarOccurrence> Selection => _selection;

    /// <summary>
    /// Asks which events of a series an edit applies to. The flags offer "This and following" and "This event" (a repeat
    /// change can't apply to one event). Set by the page.
    /// </summary>
    public Func<bool, bool, Task<EditScope?>>? AskScope { get; set; }

    /// <summary>Opens the Settings window on a page (the sidebar's settings button, the grid corner's time zones button). Set by the main window.</summary>
    public Action<SettingsSection>? OpenSettings { get; set; }

    /// <summary>Quits Leaf (the command menu's Quit; set by the App). The way out when the tray icon is hidden.</summary>
    public Action? QuitApp { get; set; }

    /// <summary>The bar at the bottom of the calendar, or null.</summary>
    [ObservableProperty]
    public partial NoticeInfo? Notice { get; set; }

    /// <summary>Changes Google had a different version of (the title bar badge).</summary>
    [ObservableProperty]
    public partial int ConflictCount { get; set; }

    /// <summary>Changes waiting to reach Google (offline, or held for undo).</summary>
    [ObservableProperty]
    public partial int PendingCount { get; set; }

    /// <summary>True when the last sync couldn't reach Google (no connection); edits wait until it can.</summary>
    [ObservableProperty]
    public partial bool IsOffline { get; set; }

    /// <summary>True once 3 syncs in a row couldn't reach Google (the title bar's offline and waiting icons show only then).</summary>
    [ObservableProperty]
    public partial bool ShowsOffline { get; set; }

    /// <summary>True while a sync you asked for (the command menu, the sync status button, the tray) is running. Nothing shows it: syncing stays in the background.</summary>
    [ObservableProperty]
    public partial bool IsSyncing { get; private set; }

    // Syncs you asked for that haven't finished
    private int _syncsRunning;

    /// <summary>
    /// Syncs every account with Google now, the calendar lists included, with <see cref="IsSyncing"/> set while it runs,
    /// then reloads what's on screen. Without Google services it only reloads.
    /// </summary>
    public async Task SyncNowAsync()
    {
        if (_services.Google is not { } google)
        {
            await RefreshAsync();
            return;
        }

        // One Sync At A Time: a second ask while one runs is the same sync
        if (IsSyncing)
        {
            return;
        }

        _syncsRunning++;
        IsSyncing = true;
        try
        {
            // Off The UI Thread
            await Task.Run(() => google.Sync.SyncAllAsync(refreshCalendarLists: true, _life.Token));
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested)
        {
            // Leaf Is Closing: not a failed sync, and nothing left to reload
            return;
        }
        finally
        {
            _syncsRunning--;
            IsSyncing = _syncsRunning > 0;
        }

        await RefreshAsync();
    }

    // Which copy of a shared event is drawn: one you can edit, then one on the main account (lower is drawn first)
    private int DrawnFirst(CalendarOccurrence copy)
    {
        var editable = Calendars.Any(c => c.AccountId == copy.AccountId && c.Id == copy.CalendarId && c.AccessRole is "owner" or "writer");
        return (editable ? 0 : 2) + (copy.AccountId == Settings.MainAccountId ? 0 : 1);
    }

    /// <summary>True while a reload updates the selection (not the user picking an event), so the details panel isn't opened for it.</summary>
    internal bool IsRefreshingSelection { get; private set; }

    /// <summary>True when at least one selected event is one you can change (and so delete).</summary>
    public bool CanDeleteSelection => _selection.Exists(o => _services.Editor.Permissions(o).CanEdit);

    /// <summary>True when <paramref name="occurrence"/> is selected.</summary>
    public bool IsSelected(CalendarOccurrence occurrence) => _selection.Exists(s => s.Key == occurrence.Key);

    /// <summary>The cached events changed (redraw).</summary>
    public event EventHandler? OccurrencesChanged;

    /// <summary>A layout setting changed (mode, weekends, hour height, zones, week start, clock).</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>The calendar list or its colors or visibility changed.</summary>
    public event EventHandler? CalendarsChanged;

    /// <summary>An account's calendars were folded away or shown again (the calendar lists follow; nothing else changes).</summary>
    public event EventHandler? AccountFoldingChanged;

    /// <summary>The view should scroll to this period start (animated).</summary>
    public event EventHandler<DateOnly>? NavigateRequested;

    /// <summary>The time grid should scroll this instant into view.</summary>
    public event EventHandler<DateTimeOffset>? ScrollToTimeRequested;

    /// <summary>The details panel should open (editing again brought back an editor hidden by closing the panel).</summary>
    public event EventHandler? DetailsOpenRequested;

    // =========================================================================
    // NAVIGATION
    // =========================================================================

    /// <summary>Jumps to today.</summary>
    public void GoToToday() => NavigateTo(Today);

    /// <summary>Previous period.</summary>
    public void Previous() => NavigateTo(ViewNavigator.Step(Mode, PeriodStart, -1, Settings.CustomDayCount, Settings.ShowWeekends));

    /// <summary>Next period.</summary>
    public void Next() => NavigateTo(ViewNavigator.Step(Mode, PeriodStart, 1, Settings.CustomDayCount, Settings.ShowWeekends));

    /// <summary>Shows the period containing <paramref name="date"/>.</summary>
    public void NavigateTo(DateOnly date)
    {
        PeriodStart = ViewNavigator.PeriodStart(Mode, date, Settings.WeekStart);
        CursorTime = null;
        NavigateRequested?.Invoke(this, PeriodStart);

        // Record The Place
        if (!_restoring)
        {
            _history.Visit(new ViewPlace(Mode, Settings.CustomDayCount, PeriodStart));
        }
    }

    /// <summary>Goes back to the previously shown view and date, if any.</summary>
    /// <returns>True when a place was restored.</returns>
    public bool GoBack() => Restore(_history.Back());

    /// <summary>Goes forward again after going back, if possible.</summary>
    /// <returns>True when a place was restored.</returns>
    public bool GoForward() => Restore(_history.Forward());

    private bool Restore(ViewPlace? place)
    {
        if (place is null)
        {
            return false;
        }

        _restoring = true;
        try
        {
            var days = place.Mode == CalendarViewMode.Days ? place.CustomDayCount : Settings.CustomDayCount;
            if (Mode != place.Mode || Settings.CustomDayCount != days)
            {
                Update(s => s with { ViewMode = place.Mode, CustomDayCount = days });
            }

            NavigateTo(place.PeriodStart);
            return true;
        }
        finally
        {
            _restoring = false;
        }
    }

    /// <summary>
    /// The period start a view switch lands on, while the switch is under way (null otherwise). Views lay their new
    /// layout out there directly, so the switch never shows the old first day and then scrolls across to the new one.
    /// </summary>
    public DateOnly? SwitchingTo { get; private set; }

    /// <summary>Switches view (and day count for <see cref="CalendarViewMode.Days"/>), keeping the selected event's day, else today when it's showing, else the period start.</summary>
    public void SetMode(CalendarViewMode mode, int? days = null)
    {
        var todayShown = Mode == CalendarViewMode.Month
            ? ViewNavigator.MonthStartOf(Today) == PeriodStart
            : Today >= PeriodStart && Today < PeriodStart.AddDays(VisibleColumns);
        var anchor = SelectedInfo?.Occurrence is { } selected ? DayOf(selected) : todayShown ? Today : PeriodStart;

        // The Views Relayout Straight Onto The New Period (the navigation that follows finds them already there)
        SwitchingTo = ViewNavigator.PeriodStart(mode, anchor, Settings.WeekStart);
        try
        {
            Update(s => s with { ViewMode = mode, CustomDayCount = days ?? s.CustomDayCount });
            NavigateTo(anchor);
        }
        finally
        {
            SwitchingTo = null;
        }
    }

    /// <summary>
    /// Called by a view for each new first day while it scrolls, and when it lands. Sets the period and
    /// title, and makes sure the months around the visible days are loaded. The cache only works in whole
    /// months, so the load is skipped while the visible days stay in the same months.
    /// </summary>
    public void OnViewScrolled(DateOnly first, DateOnly lastExclusive, DateOnly? focus = null)
    {
        // A picked time scrolled out of view isn't where paste or C should go any more
        if (CursorTime is { } picked && (LocalDate(picked) < first || LocalDate(picked) >= lastExclusive))
        {
            CursorTime = null;
        }

        PeriodStart = Mode == CalendarViewMode.Month ? ViewNavigator.MonthStartOf(focus ?? first) : first;
        PeriodTitle = ViewNavigator.MonthTitle(Mode == CalendarViewMode.Month ? focus ?? first : first);
        WindowTitle = Mode == CalendarViewMode.Month ? PeriodTitle : ViewNavigator.DateRangeTitle(first, lastExclusive.AddDays(-1));

        var months = (ViewNavigator.MonthStartOf(first), ViewNavigator.MonthStartOf(lastExclusive.AddDays(-1)));
        if (months == _ensuredMonths)
        {
            return;
        }

        _ensuredMonths = months;
        Run(async () =>
        {
            try
            {
                await Cache.EnsureAsync(first, lastExclusive, _life.Token);
            }
            catch
            {
                // Try again on the next report
                _ensuredMonths = default;
                throw;
            }
        });
    }

    // =========================================================================
    // SETTINGS
    // =========================================================================

    /// <summary>
    /// Changes and saves settings, then tells views to relayout (and reloads data when filters changed).
    /// Opening or closing the sidebar or details panel is only saved: the page already moved the panes,
    /// and a relayout of the calendar would land on the first frame of the pane's slide.
    /// </summary>
    public void Update(Func<LeafSettings, LeafSettings> change, bool reloadData = false)
    {
        // Memory takes the new value only after the disk does, so a failed save leaves both as they were
        var before = Settings;
        var next = change(Settings).Normalize();
        lock (_settingsWrite)
        {
            using var conn = _services.Database.Open();
            SettingsStore.Save(conn, next);
            Settings = next;
        }

        _services.Log.Detailed = next.DetailedLogging;
        if (before.MapProvider != next.MapProvider)
        {
            OnPropertyChanged(nameof(MapButtonText));
        }

        // A New Lookahead Or Main Account Shows Right Away
        if (before.UpcomingHours != next.UpcomingHours)
        {
            RefreshUpcoming();
            OnPropertyChanged(nameof(UpcomingHours));
        }

        if (before.MainAccountId != next.MainAccountId)
        {
            CalendarsChanged?.Invoke(this, EventArgs.Empty);
        }

        // A hidden editor keeps its fields, not contact suggestions
        if (before.DetailsPanelOpen && !Settings.DetailsPanelOpen)
        {
            Editing?.ClearSuggestions();
        }

        // A Primary Zone Change Re-Sorts The Events (no-op when the zone on screen stayed)
        SyncZone();

        // Pane Flags Only (Normalize keeps unchanged lists, so record equality holds)
        var panesOnly = Settings with { SidebarOpen = before.SidebarOpen, DetailsPanelOpen = before.DetailsPanelOpen } == before;
        if (panesOnly && !reloadData)
        {
            return;
        }

        LayoutChanged?.Invoke(this, EventArgs.Empty);
        if (reloadData)
        {
            Run(RefreshAsync);
        }
    }

    /// <summary>
    /// Remembers something nothing has to relayout for: a pane opening or closing (the page has already moved it) or
    /// a window's size on close. Memory changes at once. In the background (the pane toggles), the database write runs
    /// off the UI thread: a synchronous write here waited on a sync that held the database, and the pane's slide waited
    /// with it. Either way the disk gets the latest settings, in order with every other save.
    /// </summary>
    public void Remember(Func<LeafSettings, LeafSettings> change, bool inBackground = true)
    {
        var before = Settings;
        Settings = change(Settings).Normalize();

        // A hidden editor keeps its fields, not contact suggestions
        if (before.DetailsPanelOpen && !Settings.DetailsPanelOpen)
        {
            Editing?.ClearSuggestions();
        }

        if (inBackground)
        {
            _ = Task.Run(SaveLatestQuietly);
            return;
        }

        SaveLatestQuietly();
    }

    private void SaveLatestQuietly()
    {
        try
        {
            SaveLatestSettings();
        }
        catch (Exception ex)
        {
            _services.Log.Error("settings.remember.save-failed", ex);
        }
    }
    // Every settings write goes through here, one at a time, and writes what's in memory when its turn comes, so an
    // older snapshot never lands after a newer one
    private readonly Lock _settingsWrite = new();

    private void SaveLatestSettings()
    {
        lock (_settingsWrite)
        {
            using var conn = _services.Database.Open();
            SettingsStore.Save(conn, Settings);
        }
    }

    /// <summary>Shows or hides Saturday and Sunday.</summary>
    public void ToggleWeekends() => Update(s => s with { ShowWeekends = !s.ShowWeekends });

    /// <summary>Shows or hides declined events.</summary>
    public void ToggleDeclined() => Update(s => s with { ShowDeclined = !s.ShowDeclined }, reloadData: true);

    /// <summary>Makes the grid taller (positive) or shorter (negative).</summary>
    public void ZoomBy(double delta) => Zoom(s => s with { HourHeight = s.HourHeight + delta });

    /// <summary>Default grid height.</summary>
    public void ZoomReset() => Zoom(s => s with { HourHeight = LeafSettings.DefaultHourHeight });

    // Every wheel notch relayouts at once and saves off the UI thread, so a sync holding the database never stalls the wheel
    private void Zoom(Func<LeafSettings, LeafSettings> change)
    {
        Remember(change);
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    // =========================================================================
    // SELECTION
    // =========================================================================

    /// <summary>
    /// Selects an event and loads its details. Picking an event other than the one being edited (or any event while
    /// creating one) ends the edit without saving, like Esc, so the panel shows the picked event's details.
    /// </summary>
    public void Select(CalendarOccurrence occurrence)
    {
        // A Shared Event's Other Copies Select The One Drawn, So Its Card Highlights
        occurrence = Cache.Drawn(occurrence);
        if (Editing is { } editing && editing.Occurrence?.Key != occurrence.Key)
        {
            CancelEdit();
        }

        ShowSelected(occurrence);
    }

    // Selects and loads without touching the editor (a refresh re-selects the event behind an open editor)
    private void ShowSelected(CalendarOccurrence occurrence)
    {
        try
        {
            using var conn = _services.Database.Open();
            var stored = EventStore.Get(conn, occurrence.AccountId, occurrence.CalendarId, occurrence.EventId);
            var details = stored is null ? null : EventDetailsParser.Parse(stored.RawJson);
            var calendar = Calendars.FirstOrDefault(c => c.AccountId == occurrence.AccountId && c.Id == occurrence.CalendarId);
            if (details is null || stored is null)
            {
                return;
            }

            _selection.Clear();
            _selection.Add(occurrence);

            var (canEdit, canRespond) = _services.Editor.Permissions(occurrence);
            SelectedInfo = new SelectedEventInfo(
                occurrence,
                details,
                WhenText(occurrence),
                calendar?.Summary ?? "",
                EventColors.ResolveAccent(occurrence.ColorId, occurrence.CalendarColor),
                _services.Editor.Load(occurrence),
                DescriptionFormatter.FormatEvent(stored.RawJson),
                canEdit,
                canRespond);
            OnPropertyChanged(nameof(Selection));
            OccurrencesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            _services.Log.Error("calendar.select.failed", ex);
        }
    }

    /// <summary>Clears the selection and ends any edit without saving (clicking off shows the upcoming list).</summary>
    public void ClearSelection()
    {
        // A New Event Has No Selection, So The Edit Ends Before The Nothing-Selected Guard
        CancelEdit();

        if (SelectedInfo is null && _selection.Count == 0)
        {
            return;
        }

        _selection.Clear();
        SelectedInfo = null;
        OnPropertyChanged(nameof(Selection));
        OccurrencesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects the next (+1) or previous (-1) event after the selection (or now), up to 90 days away.</summary>
    public void SelectAdjacent(int direction)
    {
        // An All-Day Anchor Counts From Local Midnight Of Its Own Date, Not Its UTC Midnight
        var anchor = SelectedInfo?.Occurrence;
        var from = anchor?.StartIn(Zone) ?? Now;
        var day = anchor is null ? LocalDate(from) : DayOf(anchor);

        for (var i = 0; i <= 90; i++, day = day.AddDays(direction))
        {
            if (OccurrenceOrder.Adjacent(Cache.ForDay(day), from, anchor?.Key, direction, Zone) is { } next)
            {
                Select(next);
                NavigateTo(DayOf(next));
                ScrollToTimeRequested?.Invoke(this, next.Start);
                return;
            }
        }
    }

    /// <summary>Shows an event picked outside the window (the tray flyout, a notification): jumps to its day, selects it, and scrolls to it.</summary>
    public void Reveal(CalendarOccurrence occurrence)
    {
        // An All-Day Event's Own Date (its UTC midnight would be the day before west of UTC), As The Views Draw It
        NavigateTo(DayOf(occurrence));
        Select(occurrence);
        ScrollToTimeRequested?.Invoke(this, occurrence.Start);
    }

    // =========================================================================
    // CALENDARS
    // =========================================================================

    /// <summary>Shows or hides a calendar in Leaf.</summary>
    public void SetCalendarHidden(CalendarInfo calendar, bool hidden)
    {
        // A Full Or Busy Database Shows A Notice; The Reload Puts The List Back As Stored
        try
        {
            using var conn = _services.Database.Open();
            CalendarStore.SetHidden(conn, calendar.AccountId, calendar.Id, hidden);
        }
        catch (Exception ex) when (IsEditFailure(ex))
        {
            Fail("calendar.hide.failed", ex);
        }

        ReloadCalendars();
    }

    /// <summary>Sets Leaf's color for a calendar (null restores Google's).</summary>
    public void SetCalendarColor(CalendarInfo calendar, string? color)
    {
        try
        {
            using var conn = _services.Database.Open();
            CalendarStore.SetColor(conn, calendar.AccountId, calendar.Id, color);
        }
        catch (Exception ex) when (IsEditFailure(ex))
        {
            Fail("calendar.color.failed", ex);
        }

        ReloadCalendars();
    }

    /// <summary>The calendars grouped by account (the main account first, then in Leaf's order), for the sidebar and Settings › Calendars.</summary>
    public List<AccountGroup> CalendarGroups() =>
        [.. Calendars
            .GroupBy(c => c.AccountId)
            .OrderBy(g => g.Key == Settings.MainAccountId ? 0 : 1)
            .Select(g => new AccountGroup(g.Key, AccountEmails.GetValueOrDefault(g.Key, g.Key), g.Select(c => new CalendarRow(c)), isExpanded: !Settings.CollapsedAccounts.Contains(g.Key)))];

    /// <summary>
    /// Folds an account's calendars away under its header, or shows them again, in the sidebar and Settings › Calendars
    /// alike. Only remembered: nothing on the calendar changes.
    /// </summary>
    public void SetAccountExpanded(string accountId, bool expanded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        // Already That Way: nothing to save or redraw
        if (Settings.CollapsedAccounts.Contains(accountId) == !expanded)
        {
            return;
        }

        Remember(s => s.WithAccountCollapsed(accountId, collapsed: !expanded));
        AccountFoldingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Saves the order of an account's calendars.</summary>
    public void ReorderCalendars(string accountId, IReadOnlyList<string> calendarIds)
    {
        try
        {
            using var conn = _services.Database.Open();
            CalendarStore.Reorder(conn, accountId, calendarIds);
        }
        catch (Exception ex) when (IsEditFailure(ex))
        {
            Fail("calendar.reorder.failed", ex);
        }

        ReloadCalendars();
    }

    /// <summary>
    /// Re-reads the calendar list (after sync, sidebar edits, or account changes) and reloads the cached
    /// events, so a disconnected account's events leave the calendar.
    /// </summary>
    public void ReloadCalendars()
    {
        using (var conn = _services.Database.Open())
        {
            Calendars = CalendarStore.GetAll(conn);
            ReadAccounts(conn);
        }

        Editing?.AccountsChanged();

        // The Upcoming List's Calendar Takes Its New Name (or goes, with its calendar)
        if (UpcomingCalendar is { } upcoming)
        {
            UpcomingCalendar = Calendars.FirstOrDefault(c => c.AccountId == upcoming.AccountId && c.Id == upcoming.Id);
        }

        // A Disconnected Account's Choices Go With It (main account, Meet by default, tray-excluded calendars)
        var pruned = Settings.ForAccounts([.. AccountEmails.Keys]);
        if (pruned != Settings)
        {
            try
            {
                Update(_ => pruned);
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
            {
                _services.Log.Error("calendar.prune.failed", ex);
            }
        }

        CalendarsChanged?.Invoke(this, EventArgs.Empty);
        Run(RefreshAsync);
    }

    // =========================================================================
    // CALENDAR MANAGEMENT
    // =========================================================================

    /// <summary>The calendar the upcoming list is limited to (its next 30 days), or null for every calendar.</summary>
    [ObservableProperty]
    public partial CalendarInfo? UpcomingCalendar { get; private set; }

    /// <summary>
    /// Renames a calendar on Google (blank, or nothing but hidden characters, goes back to Google's name), then shows the
    /// name Google accepted. It needs a connection: offline or refused, it says so and changes nothing. The log carries
    /// the account and the result, never the name.
    /// </summary>
    /// <returns>True when Google took the new name.</returns>
    public Task<bool> RenameCalendarAsync(CalendarInfo calendar, string? text)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        var name = CalendarEdits.CleanName(text);
        return PatchCalendarAsync(calendar, CalendarEdits.RenamePatch(name), "calendar.rename", "Connect to the internet to rename a calendar.", "Google didn't accept that name.",
            conn => CalendarStore.SetSummaryOverride(conn, calendar.AccountId, calendar.Id, name));
    }

    /// <summary>
    /// Sets a calendar's default reminders on Google (popups, at most five), then mirrors them locally, so reminders
    /// follow from the alert planner's next pass. Like a rename, it needs a connection and isn't queued.
    /// </summary>
    /// <returns>True when Google took them.</returns>
    public Task<bool> SetCalendarRemindersAsync(CalendarInfo calendar, IReadOnlyList<int> minutes)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        // Google's Email Reminders Ride Along Unchanged (Leaf edits popups only)
        string? stored;
        using (var conn = _services.Database.Open())
        {
            stored = CalendarStore.DefaultRemindersJson(conn, calendar.AccountId, calendar.Id);
        }

        var patch = CalendarEdits.RemindersPatch(minutes, stored);
        return PatchCalendarAsync(calendar, patch, "calendar.reminders", "Connect to the internet to change default reminders.", "Google didn't accept those reminders.",
            conn => CalendarStore.SetDefaultReminders(conn, calendar.AccountId, calendar.Id, System.Text.Json.Nodes.JsonNode.Parse(patch)!["defaultReminders"]!.ToJsonString()));
    }

    /// <summary>A calendar's default popup reminders, in minutes, as Google last gave them.</summary>
    public IReadOnlyList<int> DefaultRemindersOf(CalendarInfo calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        using var conn = _services.Database.Open();
        return CalendarStore.PopupDefaults(conn).GetValueOrDefault((calendar.AccountId, calendar.Id)) ?? [];
    }

    // Sends a calendar-list change; only after Google accepts it is the local copy changed
    private async Task<bool> PatchCalendarAsync(CalendarInfo calendar, string patch, string logName, string offline, string refused, Action<Microsoft.Data.Sqlite.SqliteConnection> mirror)
    {
        if (_services.Google is not { } google || IsOffline)
        {
            Say(offline, canUndo: false);
            return false;
        }

        string? problem = null;
        try
        {
            await google.Calendar.PatchCalendarListAsync(calendar.AccountId, calendar.Id, patch, _life.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !_life.IsCancellationRequested))
        {
            problem = offline;
        }
        catch (GoogleApiException)
        {
            problem = refused;
        }
        catch (AccountNeedsSignInException)
        {
            problem = "Sign in again to change this calendar.";
        }

        _services.Log.Info(logName, $"account={calendar.AccountId} result={(problem is null ? "ok" : "failed")}");
        if (problem is not null)
        {
            Say(problem, canUndo: false);
            return false;
        }

        using (var conn = _services.Database.Open())
        {
            mirror(conn);
        }

        ReloadCalendars();
        return true;
    }

    /// <summary>Moves a calendar up (-1) or down (+1) among its account's calendars (Leaf's order only).</summary>
    public void MoveCalendar(CalendarInfo calendar, int delta)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        var ids = Calendars.Where(c => c.AccountId == calendar.AccountId).Select(c => c.Id).ToList();
        var index = ids.IndexOf(calendar.Id);
        var to = index + delta;
        if (index < 0 || to < 0 || to >= ids.Count)
        {
            return;
        }

        (ids[index], ids[to]) = (ids[to], ids[index]);
        ReorderCalendars(calendar.AccountId, ids);
    }

    /// <summary>
    /// Limits the upcoming list to one calendar's next 30 days (at most 50 events) and shows it in the details panel, or
    /// (null) goes back to every calendar.
    /// </summary>
    public void ShowUpcomingFor(CalendarInfo? calendar)
    {
        UpcomingCalendar = calendar;
        ForgetCalendarSoon();
        Upcoming.Clear();
        if (calendar is not null)
        {
            ClearSelection();
            DetailsOpenRequested?.Invoke(this, EventArgs.Empty);
        }

        RefreshUpcoming();
    }

    // One calendar's next 31 days, read off the UI thread once, and again only when the data or the day changes (the
    // minute tick only re-filters); the key says which calendar and day they were read for
    private IReadOnlyList<CalendarOccurrence>? _calendarSoon;
    private (string AccountId, string Id, DateOnly Day)? _calendarSoonKey;
    private int _calendarSoonLoads;

    private void ForgetCalendarSoon()
    {
        _calendarSoon = null;
        _calendarSoonKey = null;
    }

    // One calendar's events from now to 30 days out, all-day ones too, each with its day; null while they're being read
    private List<UpcomingItem>? UpcomingIn(CalendarInfo calendar, DateTimeOffset now)
    {
        var key = (calendar.AccountId, calendar.Id, LocalDate(now));
        if (_calendarSoonKey != key)
        {
            LoadCalendarSoon(key);
            return null;
        }

        if (_calendarSoon is not { } occurrences)
        {
            return null;
        }

        return [.. occurrences
            .Where(o => o.EndIn(Zone) > now && DayOf(o) <= LocalDate(now).AddDays(30))
            .OrderBy(o => o.StartIn(Zone))
            .Take(50)
            .Select(o => new UpcomingItem(
                o,
                o.Title,
                DayOf(o).ToString("ddd, MMM d", CultureInfo.GetCultureInfo("en-US")) + (o.IsAllDay ? "" : $" · {TimeLabels.Range(o.Start, o.End, Zone, Settings.Use24HourTime)}"),
                o.IsAllDay ? "All day" : TimeLabels.Relative(o.Start, o.End, now),
                EventColors.ResolveAccent(o.ColorId, o.CalendarColor),
                o.HasConference,
                ProviderOf(o),
                Select,
                occurrence => Fire(() => JoinAsync(occurrence), "calendar.join.failed")))];
    }

    // Reads the calendar's events for the key on a background thread; a newer read wins
    private void LoadCalendarSoon((string AccountId, string Id, DateOnly Day) key)
    {
        var load = ++_calendarSoonLoads;
        var zone = Zone;
        var declined = Settings.ShowDeclined;
        _calendarSoonKey = key;
        _calendarSoon = null;

        Run(async () =>
        {
            var found = await Task.Run(() =>
            {
                using var conn = _services.Database.Open();
                return OccurrenceQuery.Load(conn, key.Day, key.Day.AddDays(31), zone, declined)
                    .Where(o => o.AccountId == key.AccountId && o.CalendarId == key.Id)
                    .DistinctBy(o => o.Key)
                    .ToList();
            });

            if (load != _calendarSoonLoads)
            {
                return;
            }

            _calendarSoon = found;
            RefreshUpcoming();
        }, "calendar.upcoming.failed");
    }

    /// <summary>
    /// Reloads the cached events from the database. An edit that asked for it gets its event selected again (a moved
    /// event has a new key); otherwise deleted events leave the selection and edited ones are picked up.
    /// </summary>
    public async Task RefreshAsync()
    {
        // A request made while this read runs may not be in it, so it waits for the refresh its edit started
        var pending = _reselect?.Match;
        await Cache.RefreshAsync(_life.Token);

        // An Edit Asked To Select Its Event Once It's Reloaded
        if (_reselect is { } reselect && ReferenceEquals(reselect.Match, pending))
        {
            _reselect = null;
            if (Cache.ForDay(reselect.Day).FirstOrDefault(reselect.Match) is { } edited)
            {
                ShowSelected(edited);
                return;
            }
        }

        // Keep The Selection In Step With The Data
        var changed = false;
        for (var i = _selection.Count - 1; i >= 0; i--)
        {
            var day = DayOf(_selection[i]);
            if (!Cache.LoadedMonths.Contains(ViewNavigator.MonthStartOf(day)))
            {
                continue;
            }

            var fresh = Cache.ForDay(day).FirstOrDefault(o => o.Key == _selection[i].Key);
            if (fresh is null)
            {
                _selection.RemoveAt(i);
                changed = true;
            }
            else if (fresh != _selection[i])
            {
                _selection[i] = fresh;
                changed = true;
            }
        }

        if (changed)
        {
            // A sync or edit refreshed the selection; the page leaves a closed details panel closed
            IsRefreshingSelection = true;
            try
            {
                PublishSelection();
            }
            finally
            {
                IsRefreshingSelection = false;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _minuteTimer.Stop();
        _services.GoogleChanged -= OnGoogleChanged;
        _services.Editor.Changed -= OnEditsChanged;
        _services.Conflicts.Changed -= OnEditsChanged;
        if (_attachedSync is not null)
        {
            _attachedSync.DataChanged -= OnSyncDataChanged;
            _attachedSync.ChangesRejected -= OnChangesRejected;
            _attachedSync.OfflineChanged -= OnOfflineChanged;
        }

        _life.Cancel();
        _life.Dispose();
        Cache.Dispose();
    }

    // =========================================================================
    // EDITING
    // =========================================================================

    /// <summary>The event being edited or created in the details panel, or null.</summary>
    [ObservableProperty]
    public partial EventEditorViewModel? Editing { get; set; }

    /// <summary>The time a click on empty grid space picked (C and paste go there), or null.</summary>
    public DateTimeOffset? CursorTime { get; set; }

    /// <summary>True when you may change the event (drags check this before they start).</summary>
    public bool CanEdit(CalendarOccurrence occurrence) => _services.Editor.Permissions(occurrence).CanEdit;

    /// <summary>Opens the editor on the selected event (E; "E then U" focuses the end time).</summary>
    public void BeginEdit(bool focusEnd = false)
    {
        if (ReopenHiddenEditor())
        {
            return;
        }

        if (SelectedInfo is not { CanEdit: true } info)
        {
            return;
        }

        Editing = new EventEditorViewModel(info.Draft, info.Occurrence, WritableCalendars(info.Occurrence), Zone, TimeZoneCatalog.IanaId(UserZone), Settings.Use24HourTime, focusEnd);
    }

    /// <summary>Opens the editor on a new event in your default calendar (your chosen one, else primary, else the first you can write to).</summary>
    public void BeginCreate(DateTimeOffset start, DateTimeOffset end, bool isAllDay)
    {
        if (HomeCalendar() is not { } home)
        {
            Notice = new NoticeInfo("Add a Google account with a calendar you can edit first.", CanUndo: false);
            return;
        }

        var draft = new EventDraft
        {
            AccountId = home.AccountId,
            CalendarId = home.Id,
            Start = start,
            End = end,
            IsAllDay = isAllDay,
            TimeZone = isAllDay ? null : TimeZoneCatalog.IanaId(UserZone),
        };

        ClearSelection();
        Editing = new EventEditorViewModel(draft, null, WritableCalendars(), Zone, TimeZoneCatalog.IanaId(UserZone), Settings.Use24HourTime);
    }

    /// <summary>A new one-hour event at the picked time, or the next quarter hour (C).</summary>
    public void BeginCreateNow()
    {
        var start = CursorTime ?? DragMath.NextSlot(Now, Zone);
        BeginCreate(start, start + DragMath.DefaultLength, isAllDay: false);
    }

    /// <summary>Closes the editor without saving.</summary>
    public void CancelEdit() => Editing = null;

    // True while "Allow contact suggestions" waits for the browser
    private bool _allowingContacts;

    // A closed editor drops its contact suggestions; a new one searches the account of the calendar it has picked
    partial void OnEditingChanged(EventEditorViewModel? oldValue, EventEditorViewModel? newValue)
    {
        oldValue?.Dispose();
        if (newValue is null)
        {
            return;
        }

        // Google's Suggestions (a Workspace account without the directory permission is offered it)
        newValue.SearchContacts = (query, ct) => _services.Google is { } google
            ? google.Contacts.SearchAsync(newValue.ContactsAccountId, query, IsWorkspace(newValue.ContactsAccountId), ct)
            : Task.FromResult(new ContactResults([], ContactAccess.Allowed));

        // People You Meet Often And Rooms: read from local events on a background thread once per account per editor,
        // kept in memory only while it's open
        var people = new Dictionary<string, Task<IReadOnlyList<Contact>>>(StringComparer.Ordinal);
        var rooms = new Dictionary<string, Task<IReadOnlyList<Room>>>(StringComparer.Ordinal);
        var now = Now;

        newValue.IsWorkspaceAccount = IsWorkspace;
        newValue.MeetByDefault = id => Settings.MeetByDefaultAccounts.Contains(id);
        newValue.LocalPeople = (account, query) =>
        {
            if (!people.TryGetValue(account, out var load))
            {
                people[account] = load = Task.Run(() => ReadLocal(conn => FrequentPeople.Load(conn, account, now)));
            }

            return load.IsCompletedSuccessfully ? FrequentPeople.Match(load.Result, query) : [];
        };
        newValue.LoadRooms = account =>
        {
            if (!rooms.TryGetValue(account, out var load))
            {
                rooms[account] = load = Task.Run(() => ReadLocal(conn => Rooms.Load(conn, account)));
            }

            return load;
        };

        // Room Chips Of A Loaded Event Get Their Names
        Fire(newValue.EnsureRoomsAsync, "rooms.load.failed");
    }

    // A local read for suggestions; a failure is logged and suggests nothing
    private IReadOnlyList<T> ReadLocal<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, IReadOnlyList<T>> read)
    {
        try
        {
            using var conn = _services.Database.Open();
            return read(conn);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            _services.Log.Error("suggestions.load.failed", ex);
            return [];
        }
    }

    /// <summary>
    /// Signs the account in again (the Accounts page's sign-in, with its email as the hint) so Google asks for the
    /// contacts permission, then searches the guest box again.
    /// </summary>
    public async Task AllowContactsAsync(string accountId)
    {
        // One Sign-In At A Time; Answers Go To The Editor That Asked (if it's still open)
        if (_services.Google is not { } google || Editing is not { } editor || _allowingContacts)
        {
            return;
        }

        string? email;
        using (var conn = _services.Database.Open())
        {
            email = AccountStore.GetAll(conn).FirstOrDefault(a => a.Id == accountId)?.Email;
        }

        string? problem = null;
        _allowingContacts = true;
        try
        {
            // Only this account may come back; another Google user is never saved
            await _services.SignInAsync(google, email, accountId, CancellationToken.None);
        }
        catch (WrongAccountException ex)
        {
            problem = $"You signed in as {ex.SignedInEmail}. Sign in as {ex.ExpectedEmail} to allow suggestions.";
        }
        catch (SignInException ex)
        {
            problem = ex.Message;
        }
        catch (HttpRequestException)
        {
            problem = "Couldn't reach Google. Check your connection and try again.";
        }
        finally
        {
            _allowingContacts = false;
        }

        if (!ReferenceEquals(Editing, editor))
        {
            return;
        }

        if (problem is not null)
        {
            editor.Error = problem;
            return;
        }

        // Allowed Now (the link goes even with an empty box)
        editor.Error = null;
        editor.ContactsAccess = ContactAccess.Allowed;
        await editor.RefreshSuggestionsAsync();
    }

    // Closing the panel only hides an editor; editing again (the toolbar's Edit, a double-click) brings that one back
    private bool ReopenHiddenEditor()
    {
        if (Editing is null || Settings.DetailsPanelOpen)
        {
            return false;
        }

        DetailsOpenRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Saves the editor. A new event gets the birthday rule (spec 7.2). A repeating event asks "this / following / all"
    /// (a calendar change always moves the whole series: Google can't move one instance).
    /// </summary>
    public async Task SaveEditorAsync(bool sendUpdates)
    {
        if (Editing is not { } editor)
        {
            return;
        }

        if (editor.Validate() is { } problem)
        {
            editor.Error = problem;
            return;
        }

        var after = editor.ToDraft();
        try
        {
            if (editor.Occurrence is not { } o)
            {
                var draft = EventJson.ApplyBirthdayRule(after, Zone);
                string? id = null;
                _reselect = (draft.IsAllDay ? DateOnly.FromDateTime(draft.Start.UtcDateTime) : LocalDate(draft.Start), x => x.EventId == id);
                id = _services.Editor.Create(draft, sendUpdates);
            }
            else
            {
                var scope = EditScope.This;
                if (o.RecurringEventId is not null)
                {
                    var moved = after.CalendarId != editor.Before.CalendarId || after.AccountId != editor.Before.AccountId;
                    if (moved)
                    {
                        scope = EditScope.All;
                    }
                    else if (await ScopeForAsync([o], includeFollowing: true, includeThis: after.Recurrence.SequenceEqual(editor.Before.Recurrence)) is { } asked)
                    {
                        scope = asked;
                    }
                    else
                    {
                        return;
                    }
                }

                ReselectAfterRefresh(o, after.Start, after.IsAllDay);
                _services.Editor.Save(o, editor.Before, after, scope, sendUpdates);
            }
        }
        catch (Exception ex) when (IsEditFailure(ex) || ex is ArgumentException)
        {
            _reselect = null;
            _services.Log.Error("event.save.failed", ex);
            editor.Error = "Couldn't save that. Try again.";
            return;
        }

        Editing = null;
    }

    /// <summary>
    /// Moves or resizes a dragged event (asking about a repeating one), then selects it where it landed. A dragged event
    /// that is part of a bigger selection moves the whole selection (events you can't change stay, with a hint).
    /// </summary>
    public async Task MoveAsync(CalendarOccurrence occurrence, DateTimeOffset start, DateTimeOffset end, bool isAllDay)
    {
        var moves = BulkMoves(occurrence, start, end, isAllDay);
        var skipped = _selection.Count > 1 && IsSelected(occurrence) ? _selection.Count - moves.Count : 0;
        var scope = await ScopeForAsync([.. moves.Select(m => m.Occurrence)], includeFollowing: true);
        if (scope is null)
        {
            // Canceled: redraw the event where it was
            OccurrencesChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        ReselectAfterRefresh(occurrence, start, isAllDay);
        try
        {
            _services.Editor.Move(moves, scope.Value, sendUpdates: true);
            SaySkipped(skipped);
        }
        catch (Exception ex) when (IsEditFailure(ex) || ex is ArgumentException)
        {
            Fail("calendar.move.failed", ex);
        }
    }

    /// <summary>Copies an event to a new time (Alt+drag) and selects the copy (in a calendar you can write to, like paste).</summary>
    public void Duplicate(CalendarOccurrence occurrence, DateTimeOffset start, DateTimeOffset end, bool isAllDay)
    {
        string? id = null;
        _reselect = (isAllDay ? DateOnly.FromDateTime(start.UtcDateTime) : LocalDate(start), o => o.EventId == id);
        try
        {
            if (Writable(_services.Editor.CopyOf(occurrence)) is not { } copy)
            {
                _reselect = null;
                SaySkipped(1);
                return;
            }

            id = _services.Editor.Paste(copy, start, end, isAllDay);
        }
        catch (Exception ex) when (IsEditFailure(ex) || ex is ArgumentException)
        {
            Fail("calendar.duplicate.failed", ex);
        }
    }

    /// <summary>The event under the mouse (X adds it to or takes it out of the selection), or null.</summary>
    public CalendarOccurrence? PointerEvent { get; set; }

    /// <summary>Adds an event to the selection, or takes it out (Ctrl+click, Shift+click), ending any edit like a plain click does.</summary>
    public void ToggleSelect(CalendarOccurrence occurrence)
    {
        CancelEdit();

        var index = _selection.FindIndex(s => s.Key == occurrence.Key);
        if (index >= 0)
        {
            _selection.RemoveAt(index);
        }
        else
        {
            _selection.Add(occurrence);
        }

        PublishSelection();
    }

    /// <summary>X: toggles the event under the mouse, else the selected one.</summary>
    public void ToggleFocused()
    {
        // The event must still be on screen (a sync or edit may have moved or removed it since the mouse got there)
        if ((PointerEvent ?? SelectedInfo?.Occurrence) is { } target && Cache.ForDay(DayOf(target)).FirstOrDefault(o => o.Key == target.Key) is { } current)
        {
            ToggleSelect(current);
        }
    }

    /// <summary>Selects every event on the days showing (Ctrl+A).</summary>
    public void SelectAllVisible()
    {
        _selection.Clear();
        _selection.AddRange(VisibleDays().SelectMany(Cache.ForDay).DistinctBy(o => o.Key));
        PublishSelection();
    }

    /// <summary>
    /// Shift+drag box: selects exactly <paramref name="hits"/>, or adds them when <paramref name="add"/> (Ctrl held too).
    /// Ends any edit, like Ctrl+click does. An empty box that doesn't add clears the selection, like clicking empty space.
    /// </summary>
    public void SelectBox(IReadOnlyList<CalendarOccurrence> hits, bool add)
    {
        ArgumentNullException.ThrowIfNull(hits);

        CancelEdit();

        if (!add)
        {
            _selection.Clear();
        }

        foreach (var hit in hits.Where(h => !_selection.Exists(s => s.Key == h.Key)))
        {
            _selection.Add(hit);
        }

        PublishSelection();
    }

    /// <summary>Every event on these days (the month view's box, and the time grid's candidates).</summary>
    public IReadOnlyList<CalendarOccurrence> OnDays(IEnumerable<DateOnly> days) => [.. days.SelectMany(Cache.ForDay).DistinctBy(o => o.Key)];

    /// <summary>
    /// Copies the selection (Ctrl+C). The copies keep the events' data, so a cut event can still be pasted. The Windows
    /// clipboard gets a readable summary (one line per event) plus Leaf's marker; the events themselves stay in Leaf.
    /// Leaf's copies change only once the clipboard took the new content.
    /// </summary>
    /// <returns>True when the selection was copied.</returns>
    public bool CopySelection()
    {
        if (_selection.Count == 0)
        {
            return false;
        }

        try
        {
            var copies = _selection.Select(o => (o, _services.Editor.CopyOf(o))).ToList();

            // Readable Text, And Leaf's Marker For Paste
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(string.Join(Environment.NewLine, copies.Select(c => $"{c.o.Title} · {WhenText(c.o)}")));
            package.SetData(ClipboardFormat, "1");
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);

            _clipboard.Clear();
            _clipboard.AddRange(copies);
            Say(copies.Count == 1 ? "Copied" : string.Create(CultureInfo.InvariantCulture, $"Copied {copies.Count} events"), canUndo: false);
            return true;
        }
        catch (Exception ex) when (IsEditFailure(ex) || ex is System.Runtime.InteropServices.COMException)
        {
            Fail("calendar.copy.failed", ex);
            return false;
        }
    }

    /// <summary>Copies, then deletes, the selection (Ctrl+X). Nothing is deleted when the copy failed.</summary>
    public async Task CutSelectionAsync()
    {
        if (!CopySelection())
        {
            return;
        }

        await DeleteAsync([.. _selection], sendUpdates: true);
    }

    /// <summary>
    /// Pastes the copied events at the picked time (or the next quarter hour), keeping their spacing (Ctrl+V). Only
    /// Leaf's own copies are pasted, from Leaf's memory; nothing on the clipboard is ever read as an event or run. A copy
    /// from a calendar you can't write to goes to your main calendar in that account (else your main calendar); with
    /// none, it's skipped with a hint.
    /// </summary>
    public void Paste()
    {
        if (_clipboard.Count == 0 || !ClipboardIsOurs())
        {
            return;
        }

        var placed = DragMath.PasteAt([.. _clipboard.Select(c => c.Occurrence)], CursorTime ?? DragMath.NextSlot(Now, Zone), Zone);
        string? id = null;
        if (placed.Count == 1)
        {
            _reselect = (placed[0].Occurrence.IsAllDay ? DateOnly.FromDateTime(placed[0].Start.UtcDateTime) : LocalDate(placed[0].Start), o => o.EventId == id);
        }

        try
        {
            var skipped = 0;
            for (var i = 0; i < placed.Count; i++)
            {
                if (Writable(_clipboard[i].Copy) is not { } copy)
                {
                    skipped++;
                    continue;
                }

                id = _services.Editor.Paste(copy, placed[i].Start, placed[i].End, placed[i].Occurrence.IsAllDay);
            }

            if (skipped > 0)
            {
                _reselect = id is null ? null : _reselect;
                SaySkipped(skipped);
            }
        }
        catch (Exception ex) when (IsEditFailure(ex))
        {
            Fail("calendar.paste.failed", ex);
        }
    }

    /// <summary>Sets the color of the selected events you can change (null for the calendar's color); the rest are skipped with a hint.</summary>
    public async Task RecolorAsync(string? colorId)
    {
        var items = _selection.Where(CanEdit).ToList();
        var skipped = _selection.Count - items.Count;
        if (items.Count == 0)
        {
            SaySkipped(skipped);
            return;
        }

        // Canceled: nothing changed, so nothing to report
        if (await ScopeForAsync(items, includeFollowing: true) is not { } scope)
        {
            return;
        }

        try
        {
            _services.Editor.Recolor(items, colorId, scope);
            SaySkipped(skipped);
        }
        catch (Exception ex) when (IsEditFailure(ex))
        {
            Fail("calendar.recolor.failed", ex);
        }
    }

    // Something else copied since Leaf did (another app, or text in Leaf) wins; an unreadable clipboard doesn't block Leaf's paste
    private static bool ClipboardIsOurs()
    {
        try
        {
            return Windows.ApplicationModel.DataTransfer.Clipboard.GetContent().Contains(ClipboardFormat);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return true;
        }
    }

    // A mixed selection changes what it can and says how many it left alone
    private void SaySkipped(int skipped)
    {
        if (skipped > 0)
        {
            Say(skipped == 1 ? "1 event couldn't be changed" : string.Create(CultureInfo.InvariantCulture, $"{skipped} events couldn't be changed"), canUndo: false);
        }
    }

    // Where new events go: your chosen default calendar, else your main one you can write to (in one account when given), or null
    private CalendarInfo? HomeCalendar(string? accountId = null) => DefaultCalendar.Pick(Calendars, AccountEmails.Keys.ToHashSet(), Settings.DefaultCalendar, accountId, Settings.MainAccountId);

    // Where a copy is created: its own calendar when you can write to it, else the account's main one, else your main one
    private EventCopy? Writable(EventCopy copy)
    {
        if (Calendars.Any(c => c.AccountId == copy.AccountId && c.Id == copy.CalendarId && c.AccessRole is "owner" or "writer"))
        {
            return copy;
        }

        return (HomeCalendar(copy.AccountId) ?? HomeCalendar()) is { } home ? copy with { AccountId = home.AccountId, CalendarId = home.Id } : null;
    }

    // A dragged event that's part of a bigger selection takes the others you can change along (same time shift; all-day by
    // days; when the dragged one switches between timed and all-day, the others move by whole days only)
    private IReadOnlyList<EventMove> BulkMoves(CalendarOccurrence dragged, DateTimeOffset start, DateTimeOffset end, bool isAllDay)
    {
        if (_selection.Count < 2 || !IsSelected(dragged))
        {
            return [new EventMove(dragged, start, end, isAllDay)];
        }

        var switched = isAllDay != dragged.IsAllDay;
        var days = (isAllDay ? DateOnly.FromDateTime(start.UtcDateTime) : LocalDate(start)).DayNumber - DayOf(dragged).DayNumber;
        return [.. _selection.Where(CanEdit).Select(o => o.Key == dragged.Key ? new EventMove(o, start, end, isAllDay) : Shifted(o))];

        EventMove Shifted(CalendarOccurrence o)
        {
            if (!o.IsAllDay && !switched)
            {
                // Same Shift On The Wall Clock (keeps the time of day across a DST change)
                var (timedStart, timedEnd) = DragMath.ShiftWith(o, dragged.Start, start, Zone);
                return new EventMove(o, timedStart, timedEnd, false);
            }

            var (s, e) = DragMath.ShiftDays(o, days, Zone);
            return new EventMove(o, s, e, o.IsAllDay);
        }
    }

    /// <summary>The days on screen (hidden weekends skipped), for select-all and the mini month's band.</summary>
    public IEnumerable<DateOnly> VisibleDays()
    {
        if (Mode == CalendarViewMode.Month)
        {
            var (gridStart, weeks) = ViewNavigator.MonthGrid(PeriodStart, Settings.WeekStart);
            return Enumerable.Range(0, weeks * 7).Select(gridStart.AddDays).Where(d => Settings.ShowWeekends || !ViewNavigator.IsWeekend(d));
        }

        return Enumerable.Range(0, 62).Select(PeriodStart.AddDays).Where(d => Settings.ShowWeekends || !ViewNavigator.IsWeekend(d)).Take(VisibleColumns);
    }

    // Calendars you can add events to; an edited event's own calendar is always there (a guest who may edit an
    // invite on a calendar you can only read), so leaving the picker alone never moves the event
    private IReadOnlyList<CalendarChoice> WritableCalendars(CalendarOccurrence? source = null) =>
        [.. Calendars
            .Where(c => (c.AccessRole is "owner" or "writer" || (c.AccountId == source?.AccountId && c.Id == source.CalendarId)) && AccountEmails.ContainsKey(c.AccountId))
            .Select(c => new CalendarChoice(c.AccountId, c.Id, c.Summary, AccountEmails[c.AccountId], c.DisplayColor, c.IsPrimary))];

    /// <summary>
    /// Deletes events you can change (asking about repeating ones), then shows "Event deleted · Undo". The delete
    /// waits in the outbox for <see cref="EventEditor.UndoWindow"/>. Invites and read-only calendars are skipped
    /// with a hint (in a mixed selection, the undo notice counts them: "2 events deleted · 1 couldn't be changed"). A failure shows "Couldn't delete" and is logged as <c>event.delete.failed</c>; it never throws.
    /// </summary>
    public async Task DeleteAsync(IReadOnlyList<CalendarOccurrence> items, bool sendUpdates)
    {
        try
        {
            // Only Events You Can Change
            var permissions = items.Select(o => (Occurrence: o, Rights: _services.Editor.Permissions(o))).ToList();
            var deletable = permissions.Where(p => p.Rights.CanEdit).Select(p => p.Occurrence).ToList();
            if (deletable.Count == 0)
            {
                if (permissions.Count > 0)
                {
                    Say(permissions.Exists(p => p.Rights.CanRespond)
                        ? "You can't delete an event you were invited to. Reply \"Not going\" instead."
                        : "You can't change events on this calendar.", canUndo: false);
                }

                return;
            }

            var scope = await ScopeForAsync(deletable, includeFollowing: true);
            if (scope is null)
            {
                return;
            }

            var receipt = _services.Editor.Delete(deletable, scope.Value, sendUpdates);
            if (receipt.Seqs.Count == 0)
            {
                return;
            }

            // Keep The Receipt For Undo (Newest Last, Oldest Dropped Past The Cap)
            _deletes.Add(receipt);
            if (_deletes.Count > UndoDepth)
            {
                _deletes.RemoveAt(0);
            }

            // Offer Undo Until The Delete Is Sent
            ClearSelection();
            var deleted = deletable.Count == 1 ? "Event deleted" : string.Create(CultureInfo.InvariantCulture, $"{deletable.Count} events deleted");
            var skipped = permissions.Count - deletable.Count;
            Say(skipped > 0 ? string.Create(CultureInfo.InvariantCulture, $"{deleted} · {skipped} couldn't be changed") : deleted, canUndo: true);
            _ = NudgeAfterUndoWindowAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _reselect = null;
            _services.Log.Error("event.delete.failed", ex);
            Say("Couldn't delete. Try again.", canUndo: false);
        }
    }

    /// <summary>
    /// Joins a meeting (Ctrl+J and the Join buttons): <paramref name="occurrence"/> when given, else the selected event
    /// when it has a link, else the first upcoming one on a known meeting host (opened blind, like the tray's). Meet links
    /// get the event's account (spec 8.5).
    /// </summary>
    public async Task JoinAsync(CalendarOccurrence? occurrence = null)
    {
        var target = occurrence
            ?? (SelectedInfo is { Details.ConferenceUri: not null } selected ? selected.Occurrence : null)
            ?? Upcoming.FirstOrDefault(u => u.CanJoin && u.Provider is not null)?.Occurrence;

        if (target is null || LoadDetails(target)?.ConferenceUri is not { } link)
        {
            Say("No meeting to join.", canUndo: false);
            return;
        }

        await _services.LaunchAsync(LinkSafety.JoinUri(link, AccountEmails.GetValueOrDefault(target.AccountId) ?? ""));
    }

    /// <summary>Opens the selected event's video link exactly as it is (V).</summary>
    public async Task OpenMeetingLinkAsync()
    {
        if (SelectedInfo?.Details.ConferenceUri is { } link)
        {
            await _services.LaunchAsync(link);
        }
    }

    /// <summary>
    /// Copies the selected event's video link to the clipboard (the Join button's menu) and says so. It's the address
    /// the Join tooltip shows, <see cref="LinkSafety.DisplayForm"/>; a link with no safe form isn't copied.
    /// </summary>
    public void CopyMeetingLink()
    {
        if (SelectedInfo?.Details.ConferenceUri is not { } link || LinkSafety.DisplayForm(link) is not { } text)
        {
            return;
        }

        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            Say("Link copied", canUndo: false);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            // Another app holds the clipboard (a remote session, a clipboard manager): not a crash
            Fail("calendar.copy-link.failed", ex);
        }
    }

    /// <summary>Opens a description link (checked against the allowlist again).</summary>
    public Task OpenLinkAsync(Uri link) => _services.LaunchAsync(link);

    /// <summary>Opens the selected event's location in the map service picked in Settings › General.</summary>
    public async Task OpenLocationAsync()
    {
        if (SelectedInfo?.Details.Location is { Length: > 0 } location)
        {
            await _services.LaunchAsync(LinkSafety.MapsSearch(location, Settings.MapProvider));
        }
    }

    /// <summary>The details panel's location button: "Open in Google Maps" or "Open in Bing Maps".</summary>
    public string MapButtonText => Settings.MapProvider == MapProvider.Bing ? "Open in Bing Maps" : "Open in Google Maps";

    /// <summary>How many hours ahead the upcoming list looks (the details panel's empty text says so).</summary>
    public int UpcomingHours => Settings.UpcomingHours;

    /// <summary>Opens an email to every other guest, with the title as the subject (E then E).</summary>
    public async Task EmailGuestsAsync()
    {
        if (SelectedInfo is { } info && GuestsMailto(info) is { } mailto)
        {
            await _services.LaunchAsync(mailto);
        }
    }

    /// <summary>The "Email guests" address for an event (every guest but you and the rooms), or null when no usable address remains.</summary>
    public static Uri? GuestsMailto(SelectedEventInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var emails = info.Draft.Guests.Where(g => !g.IsSelf && !g.IsResource).Select(g => g.Email).ToList();
        return emails.Count > 0 ? LinkSafety.MailtoGuests(emails, info.Details.Title) : null;
    }

    /// <summary>
    /// Replies to the selected invite, asking "this event or all events" for a repeating one. Returns true once the reply
    /// is saved (it waits in the outbox for Google). A failure shows "Couldn't save your reply" and is logged as
    /// <c>event.rsvp.failed</c>; it never throws.
    /// </summary>
    public async Task<bool> RespondAsync(ResponseStatus response, string? note, bool emailOrganizer)
    {
        try
        {
            if (SelectedInfo is not { CanRespond: true } info)
            {
                return false;
            }

            var o = info.Occurrence;
            var scope = await ScopeForAsync([o], includeFollowing: false);
            if (scope is null)
            {
                return false;
            }

            ReselectAfterRefresh(o, o.Start, o.IsAllDay);
            _services.Editor.Respond(o, response, note, emailOrganizer, scope.Value);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _reselect = null;
            _services.Log.Error("event.rsvp.failed", ex);
            Say("Couldn't save your reply. Try again.", canUndo: false);
            return false;
        }
    }

    /// <summary>
    /// Undoes the newest delete not yet undone this session (Ctrl+Z or the notice's Undo). Inside the hold window the
    /// delete never leaves; after it, the event comes back as a quiet copy, or a repeating day is restored (no emails).
    /// </summary>
    public void Undo()
    {
        if (_deletes.Count == 0)
        {
            return;
        }

        var receipt = _deletes[^1];
        _deletes.RemoveAt(_deletes.Count - 1);
        try
        {
            switch (_services.Editor.Undo(receipt, out var restored))
            {
                case UndoResult.Restored:
                    Notice = null;
                    break;
                case UndoResult.Recreated:
                    Say(restored == 1 ? "Event restored" : string.Create(CultureInfo.InvariantCulture, $"{restored} events restored"), canUndo: false);
                    break;
                default:
                    Say("Nothing to undo", canUndo: false);
                    break;
            }
        }
        catch (InvalidOperationException ex)
        {
            // The calendar became read-only: Core's message says so (the receipt goes back, so undo can be tried again)
            _deletes.Add(receipt);
            _services.Log.Error("event.undo.failed", ex);
            Say(ex.Message, canUndo: false);
        }
        catch (Exception ex) when (IsEditFailure(ex))
        {
            // Nothing Was Saved: the receipt goes back, so undo can be tried again
            _deletes.Add(receipt);
            _services.Log.Error("event.undo.failed", ex);
            Say("Couldn't restore that event.", canUndo: false);
        }
    }

    /// <summary>Hides the notice bar.</summary>
    public void DismissNotice() => Notice = null;

    /// <summary>Shows a short message in the notice bar (no Undo).</summary>
    public void ShowMessage(string text) => Say(text, canUndo: false);

    /// <summary>Open conflicts, oldest first.</summary>
    public IReadOnlyList<ConflictInfo> Conflicts() => _services.Conflicts.GetAll();

    /// <summary>The side-by-side rows for a conflict, in this calendar's zone and clock.</summary>
    public IReadOnlyList<FieldComparison> Compare(ConflictInfo conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        return ConflictDiff.Compare(conflict.LocalJson, conflict.GoogleJson, Zone, Settings.Use24HourTime);
    }

    /// <summary>"Keep mine": re-sends the local change on top of Google's version.</summary>
    public void KeepMine(ConflictInfo conflict)
    {
        try
        {
            _services.Conflicts.KeepMine(conflict);
        }
        catch (Exception ex) when (IsEditFailure(ex))
        {
            Fail("conflict.keep-mine.failed", ex);
        }
    }

    /// <summary>"Keep Google's": drops the local change.</summary>
    public void KeepGoogles(ConflictInfo conflict)
    {
        try
        {
            _services.Conflicts.KeepGoogles(conflict);
        }
        catch (Exception ex) when (IsEditFailure(ex))
        {
            Fail("conflict.keep-googles.failed", ex);
        }
    }

    /// <summary>Runs work started from a click or key; failures are logged under <paramref name="eventName"/>, never thrown into the dispatcher.</summary>
    public void Fire(Func<Task> work, string eventName = "calendar.action.failed") => Run(work, eventName);

    /// <summary>Logs a failure a view caught (internal IDs only).</summary>
    public void LogError(string eventName, Exception exception) => _services.Log.Error(eventName, exception);

    /// <summary>A breadcrumb for Detailed logging (internal names only, never event content).</summary>
    public void Trace(string eventName, string? detail = null) => _services.Log.Trace(eventName, detail);

    /// <inheritdoc />
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Breadcrumbs: where you went, what you picked, and the editor opening or closing (property names only)
        if (e.PropertyName is nameof(PeriodStart) or nameof(SelectedInfo) or nameof(Selection) or nameof(Editing))
        {
            _services?.Log.Trace("vm.changed", e.PropertyName);
        }

        base.OnPropertyChanged(e);
    }

    // "This event" for single events; otherwise the page's dialog (null means the user canceled)
    private async Task<EditScope?> ScopeForAsync(IReadOnlyList<CalendarOccurrence> items, bool includeFollowing, bool includeThis = true)
    {
        if (!items.Any(o => o.RecurringEventId is not null))
        {
            return EditScope.This;
        }

        return AskScope is { } ask ? await ask(includeFollowing, includeThis) : includeThis ? EditScope.This : EditScope.All;
    }

    // After an edit the event may have a new key (moved) or a new ID (one instance of a series): find it by its start
    private void ReselectAfterRefresh(CalendarOccurrence occurrence, DateTimeOffset start, bool isAllDay)
    {
        var day = isAllDay ? DateOnly.FromDateTime(start.UtcDateTime) : LocalDate(start);
        var series = occurrence.RecurringEventId ?? occurrence.EventId;
        _reselect = (day, o => o.AccountId == occurrence.AccountId && o.Start == start && (o.EventId == occurrence.EventId || o.RecurringEventId == series));
    }

    // One selected event shows its details; zero or several show the list or the selection summary
    private void PublishSelection()
    {
        if (_selection.Count == 1)
        {
            ShowSelected(_selection[0]);
            return;
        }

        SelectedInfo = null;
        OnPropertyChanged(nameof(Selection));
        OccurrencesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Fail(string eventName, Exception exception)
    {
        _reselect = null;
        _services.Log.Error(eventName, exception);
        Say("Something went wrong saving that change. Try again.", canUndo: false);
    }

    /// <summary>A read-only event was dragged: the notice says it can't be changed, and why.</summary>
    public void ExplainReadOnly(CalendarOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);

        string? reason = null;
        try
        {
            reason = _services.Editor.ReadOnlyReason(occurrence);
        }
        catch (Exception ex) when (IsEditFailure(ex))
        {
            _services.Log.Error("calendar.readonly.reason.failed", ex);
        }

        Say($"This event can't be changed: {reason ?? "it's read-only"}.", canUndo: false);
    }

    // Every notice counts as new (a repeat of the same text restarts the bar's timer), so it's cleared first
    private void Say(string text, bool canUndo)
    {
        Notice = null;
        Notice = new NoticeInfo(text, canUndo);
    }

    private static bool IsEditFailure(Exception ex) =>
        ex is Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException or InvalidOperationException;

    // Deletes wait out the undo window; nudge the sync loop once it has passed so they go out right away, and count
    // them as waiting from then on (offline, no sync event would say so)
    private async Task NudgeAfterUndoWindowAsync()
    {
        await Task.Delay(EventEditor.UndoWindow + TimeSpan.FromSeconds(0.5));
        _dispatcher.TryEnqueue(RefreshSyncState);
        _services.Google?.Loop.TriggerNow();
    }

    private EventDetails? LoadDetails(CalendarOccurrence o)
    {
        using var conn = _services.Database.Open();
        return EventStore.Get(conn, o.AccountId, o.CalendarId, o.EventId) is { } stored ? EventDetailsParser.Parse(stored.RawJson) : null;
    }

    // Local edits and conflict answers arrive on the UI thread
    private void OnEditsChanged(object? sender, EventArgs e)
    {
        RefreshSyncState();
        Run(RefreshAsync);
    }

    private void RefreshSyncState()
    {
        try
        {
            var (conflicts, pending) = _services.Conflicts.Counts();
            ConflictCount = conflicts;
            PendingCount = pending;
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            // A busy database skips this refresh; the next edit or sync tries again
            _services.Log.Error("calendar.sync-state.failed", ex);
        }

        IsOffline = _attachedSync?.IsOffline ?? false;
        ShowsOffline = _attachedSync?.ShowsOffline ?? false;
    }

    private DateOnly DayOf(CalendarOccurrence o) => o.IsAllDay ? o.AllDayStart : LocalDate(o.Start);

    // =========================================================================
    // INTERNALS
    // =========================================================================

    private Task<IReadOnlyList<CalendarOccurrence>> LoadAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var includeDeclined = Settings.ShowDeclined;
        return Task.Run<IReadOnlyList<CalendarOccurrence>>(
            () =>
            {
                using var conn = _services.Database.Open();
                // Every copy of a shared event: the cache merges them (SharedEvents) and draws the preferred one
                return OccurrenceQuery.Load(conn, from, to, Zone, includeDeclined, keepSharedCopies: true);
            },
            ct);
    }

    private void AttachSync()
    {
        if (_attachedSync is not null)
        {
            _attachedSync.DataChanged -= OnSyncDataChanged;
            _attachedSync.ChangesRejected -= OnChangesRejected;
            _attachedSync.OfflineChanged -= OnOfflineChanged;
        }

        _attachedSync = _services.Google?.Sync;
        if (_attachedSync is not null)
        {
            _attachedSync.DataChanged += OnSyncDataChanged;
            _attachedSync.ChangesRejected += OnChangesRejected;
            _attachedSync.OfflineChanged += OnOfflineChanged;
        }

        IsOffline = _attachedSync?.IsOffline ?? false;
        ShowsOffline = _attachedSync?.ShowsOffline ?? false;

        // Once Google Is Ready, Older Accounts Learn Whether They're Workspace Ones (once a session; then the editor sees it)
        if (_services.Google is { } google && !_domainsChecked)
        {
            _domainsChecked = true;
            Fire(async () =>
            {
                await google.RefreshHostedDomainsAsync(_life.Token);
                ReloadCalendars();
            }, "account.domain.failed");
        }
    }

    private void OnGoogleChanged(object? sender, EventArgs e) => _dispatcher.TryEnqueue(AttachSync);

    // Sync runs on a background thread; hop to the UI thread before touching the cache
    private void OnSyncDataChanged(object? sender, EventArgs e) => _dispatcher.TryEnqueue(() =>
    {
        RefreshSyncState();
        ReloadCalendars();
    });

    // Google refused edits for good (no permission): they were undone, so say so
    private void OnChangesRejected(object? sender, int count) => _dispatcher.TryEnqueue(() =>
    {
        Say(count == 1 ? "Google didn't accept a change, so it was undone." : string.Create(CultureInfo.InvariantCulture, $"Google didn't accept {count} changes, so they were undone."), canUndo: false);
        RefreshSyncState();
    });

    // Reaching Google again (or losing it) also changes what's waiting to be sent
    private void OnOfflineChanged(object? sender, EventArgs e) => _dispatcher.TryEnqueue(RefreshSyncState);

    private void OnMinute()
    {
        CheckTimeZone();
        var today = _services.Options.StartDate ?? LocalDate(Now);
        if (today != Today)
        {
            Today = today;
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        RefreshUpcoming();
    }

    // Each event's meeting service for its Join button's logo, looked up once per event (forgotten when the data changes)
    private readonly Dictionary<(string, string, string), MeetingProvider?> _providers = [];

    private MeetingProvider? ProviderOf(CalendarOccurrence o)
    {
        if (!o.HasConference)
        {
            return null;
        }

        var key = (o.AccountId, o.CalendarId, o.EventId);
        if (!_providers.TryGetValue(key, out var provider))
        {
            try
            {
                using var conn = _services.Database.Open();
                provider = Core.Alerts.JoinPicker.MeetingLink(conn, o) is { } link ? LinkSafety.ProviderOf(link) : null;
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
            {
                // A busy database (runs from the minute clock and syncs): no logo this time, looked up again next refresh
                _services.Log.Error("calendar.upcoming.provider.failed", ex);
                return null;
            }

            _providers[key] = provider;
        }

        return provider;
    }

    private void RefreshUpcoming()
    {
        var now = Now;
        var items = UpcomingCalendar is { } calendar ? UpcomingIn(calendar, now) : Enumerable.Range(0, 2)
            .SelectMany(i => Cache.ForDay(LocalDate(now).AddDays(i)))
            .DistinctBy(o => o.Key)
            .Where(o => !o.IsAllDay && o.End > now && o.Start < now + TimeSpan.FromHours(Settings.UpcomingHours))
            .OrderBy(o => o.Start)
            .Take(20)
            .Select(o => new UpcomingItem(o, o.Title, TimeLabels.Range(o.Start, o.End, Zone, Settings.Use24HourTime), TimeLabels.Relative(o.Start, o.End, now), EventColors.ResolveAccent(o.ColorId, o.CalendarColor), o.HasConference, ProviderOf(o), Select, occurrence => Fire(() => JoinAsync(occurrence), "calendar.join.failed")))
            .ToList();

        // One Calendar's Events Still Being Read: the list waits for them
        if (items is null)
        {
            return;
        }

        Upcoming.Clear();
        foreach (var item in items)
        {
            Upcoming.Add(item);
        }
    }

    private string WhenText(CalendarOccurrence o)
    {
        if (o.IsAllDay)
        {
            var last = o.AllDayEnd.AddDays(-1);
            return last > o.AllDayStart
                ? $"{TimeLabels.LongDate(o.AllDayStart)} – {TimeLabels.LongDate(last)} · All day"
                : $"{TimeLabels.LongDate(o.AllDayStart)} · All day";
        }

        return $"{TimeLabels.LongDate(LocalDate(o.Start))} · {TimeLabels.Range(o.Start, o.End, Zone, Settings.Use24HourTime)}";
    }

    private DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Zone).DateTime);

    // Background work started from UI events: failures are logged, never thrown into the dispatcher
    private async void Run(Func<Task> work, string eventName = "calendar.load.failed")
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _services.Log.Error(eventName, ex);
        }
    }
}

/// <summary>One calendar in the sidebar.</summary>
public sealed partial class CalendarRow : ObservableObject
{
    /// <summary>Creates a row for <paramref name="info"/>.</summary>
    public CalendarRow(CalendarInfo info) => Info = info;

    /// <summary>The stored calendar (replaced in place when its color or visibility changes, so the row keeps its place and focus).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(IsVisible), nameof(Color))]
    public partial CalendarInfo Info { get; set; }

    /// <summary>Display name.</summary>
    public string Name => Info.Summary;

    /// <summary>Checked when shown.</summary>
    public bool IsVisible => Info.IsVisible;

    /// <summary>Hex color.</summary>
    public string Color => Info.DisplayColor;
}

/// <summary>An account's calendars in the sidebar.</summary>
public sealed partial class AccountGroup(string accountId, string email, IEnumerable<CalendarRow> calendars, bool isExpanded = true) : ObservableObject
{
    /// <summary>Google account ID (stable: the list matches groups by it).</summary>
    public string AccountId { get; } = accountId;

    /// <summary>Automation ID of the header that folds the account's calendars away.</summary>
    public string HeaderId => $"AccountHeader_{AccountId}";

    /// <summary>Account email (header; the account ID until the email is known).</summary>
    [ObservableProperty]
    public partial string Email { get; set; } = email;

    /// <summary>The calendars show under the header (false: folded away).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron), nameof(FoldTip))]
    public partial bool IsExpanded { get; set; } = isExpanded;

    /// <summary>The header's chevron: down while open, right while folded.</summary>
    public string Chevron => IsExpanded ? "\uE70D" : "\uE76C";

    /// <summary>The header's tooltip.</summary>
    public string FoldTip => IsExpanded ? "Collapse" : "Expand";

    /// <summary>Calendars in Leaf's order (drag to reorder).</summary>
    public ObservableCollection<CalendarRow> Calendars { get; } = new(calendars);

    /// <summary>
    /// Brings the shown groups in line with a freshly built list without rebuilding them.
    /// </summary>
    /// <remarks>
    /// Accounts and calendars are matched by ID (<see cref="ListSync"/>). Kept groups take the fresh email and folding, and kept rows
    /// the fresh <see cref="CalendarRow.Info"/>; only calendars or accounts that came or went are inserted or removed, so a
    /// list control animates just those rows and leaves the rest alone.
    /// </remarks>
    /// <param name="shown">The groups the list is bound to (changed in place).</param>
    /// <param name="fresh">The groups just built by <see cref="CalendarViewModel.CalendarGroups"/>.</param>
    public static void Sync(ObservableCollection<AccountGroup> shown, List<AccountGroup> fresh)
    {
        ListSync.Apply(shown, fresh, g => g.AccountId, (group, from) =>
        {
            group.Email = from.Email;
            group.IsExpanded = from.IsExpanded;
            ListSync.Apply(group.Calendars, from.Calendars, r => r.Info.Id, (row, freshRow) => row.Info = freshRow.Info);
        });
    }
}

/// <summary>An extra time zone in the time-zone panel.</summary>
public sealed partial class ZoneRow(string id, string city, string detail) : ObservableObject
{
    /// <summary>IANA ID.</summary>
    public string Id { get; } = id;

    /// <summary>City name (placeholder when no label).</summary>
    public string City { get; } = city;

    /// <summary>"UTC+9 · Tokyo Standard Time".</summary>
    public string Detail { get; } = detail;

    /// <summary>Custom column label (blank means the city).</summary>
    [ObservableProperty]
    public partial string Label { get; set; } = "";

    /// <summary>Automation ID of the label box.</summary>
    public string LabelBoxId => $"ZoneLabelBox_{Id}";

    /// <summary>Automation ID of the remove button.</summary>
    public string RemoveId => $"ZoneRemove_{Id}";

    /// <summary>Automation ID of the "More options" button (Move up, Move down).</summary>
    public string MoreId => $"ZoneMore_{Id}";
}
