using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Editing;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Sync;
using LeafCalendar.Core.Views;
using Microsoft.UI.Dispatching;

namespace LeafCalendar.App.ViewModels;

/// <summary>
/// An upcoming event in the details panel; <see cref="CanJoin"/> shows its Join button. The buttons x:Bind their
/// clicks to <see cref="Open"/> and <see cref="Join"/>, so nothing is read back from a control's Tag.
/// </summary>
public sealed record UpcomingItem(CalendarOccurrence Occurrence, string Title, string When, string Relative, string Color, bool CanJoin, Action<CalendarOccurrence> OnOpen, Action<CalendarOccurrence> OnJoin)
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
    bool CanRespond);

/// <summary>A guest in the details panel: address and a summary such as "Maybe · Optional · “Late”".</summary>
public sealed record GuestItem(string Email, string Detail);

/// <summary>The bar at the bottom of the calendar ("Event deleted · Undo", or a short message).</summary>
public sealed record NoticeInfo(string Text, bool CanUndo);

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
    /// <summary>How far ahead the upcoming list looks.</summary>
    public static readonly TimeSpan UpcomingWindow = TimeSpan.FromHours(8);

    readonly LeafServices _services;
    readonly DispatcherQueue _dispatcher;
    readonly DispatcherQueueTimer _minuteTimer;
    readonly CancellationTokenSource _life = new();
    readonly LocalZoneWatcher _zones = new();
    (DateOnly First, DateOnly Last) _ensuredMonths;
    SyncEngine? _attachedSync;
    readonly List<CalendarOccurrence> _selection = [];
    (DateOnly Day, Func<CalendarOccurrence, bool> Match)? _reselect;
    DeleteReceipt? _lastDelete;

    /// <summary>Loads settings and calendars and starts the minute clock.</summary>
    public CalendarViewModel(LeafServices services, DispatcherQueue dispatcher)
    {
        _services   = services;
        _dispatcher = dispatcher;

        using (var conn = services.Database.Open())
        {
            Settings  = SettingsStore.Load(conn);
            Calendars     = CalendarStore.GetAll(conn);
            AccountEmails = AccountStore.GetAll(conn).ToDictionary(a => a.Id, a => a.Email);
        }

        Today = services.Options.StartDate ?? DateOnly.FromDateTime(DateTime.Now);
        Cache = new EventWindowCache(LoadAsync, Zone);
        Cache.Changed += (_, _) =>
        {
            RefreshUpcoming();
            OccurrencesChanged?.Invoke(this, EventArgs.Empty);
        };

        PeriodStart = ViewNavigator.PeriodStart(Settings.ViewMode, Today, Settings.WeekStart);
        PeriodTitle = ViewNavigator.MonthTitle(PeriodStart);

        services.GoogleChanged += OnGoogleChanged;
        AttachSync();

        // Local Edits Reload The Views
        services.Editor.LocalZoneId = IanaZoneId(Zone);
        services.Editor.Changed    += OnEditsChanged;

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

    /// <summary>The zone the grid is drawn in: the PC's, followed while Leaf runs (see <see cref="CheckTimeZone"/>).</summary>
    public TimeZoneInfo Zone => _zones.Zone;

    /// <summary>The user's view settings.</summary>
    public LeafSettings Settings { get; private set; }

    /// <summary>The ±3-month event cache.</summary>
    public EventWindowCache Cache { get; }

    /// <summary>Every calendar, grouped by account.</summary>
    public IReadOnlyList<CalendarInfo> Calendars { get; private set; }

    /// <summary>Account ID → email, for sidebar headers.</summary>
    public IReadOnlyDictionary<string, string> AccountEmails { get; private set; } = new Dictionary<string, string>();

    /// <summary>Events in the next <see cref="UpcomingWindow"/>.</summary>
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

    /// <summary>The selected event, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial SelectedEventInfo? SelectedInfo { get; set; }

    /// <summary>True when an event is selected.</summary>
    public bool HasSelection => SelectedInfo is not null;

    /// <summary>Every selected event (one or more; the details panel shows the first when it's the only one).</summary>
    public IReadOnlyList<CalendarOccurrence> Selection => _selection;

    /// <summary>Asks which events of a series an edit applies to; the flag offers "This and following". Set by the page.</summary>
    public Func<bool, Task<EditScope?>>? AskScope { get; set; }

    /// <summary>The bar at the bottom of the calendar, or null.</summary>
    [ObservableProperty]
    public partial NoticeInfo? Notice { get; set; }

    /// <summary>True while a reload updates the selection (not the user picking an event), so the details panel isn't opened for it.</summary>
    internal bool IsRefreshingSelection { get; private set; }

    /// <summary>True when <paramref name="occurrence"/> is selected.</summary>
    public bool IsSelected(CalendarOccurrence occurrence) => _selection.Exists(s => s.Key == occurrence.Key);

    /// <summary>The cached events changed (redraw).</summary>
    public event EventHandler? OccurrencesChanged;

    /// <summary>A layout setting changed (mode, weekends, hour height, zones, week start, clock).</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>The calendar list or its colors or visibility changed.</summary>
    public event EventHandler? CalendarsChanged;

    /// <summary>The view should scroll to this period start (animated).</summary>
    public event EventHandler<DateOnly>? NavigateRequested;

    /// <summary>The time grid should scroll this instant into view.</summary>
    public event EventHandler<DateTimeOffset>? ScrollToTimeRequested;

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
        NavigateRequested?.Invoke(this, PeriodStart);
    }

    /// <summary>Switches view (and day count for <see cref="CalendarViewMode.Days"/>), keeping the selected event's day, else today when it's showing, else the period start.</summary>
    public void SetMode(CalendarViewMode mode, int? days = null)
    {
        var todayShown = Mode == CalendarViewMode.Month
            ? ViewNavigator.MonthStartOf(Today) == PeriodStart
            : Today >= PeriodStart && Today < PeriodStart.AddDays(VisibleColumns);
        var anchor = SelectedInfo?.Occurrence is { } selected ? LocalDate(selected.Start) : todayShown ? Today : PeriodStart;
        Update(s => s with { ViewMode = mode, CustomDayCount = days ?? s.CustomDayCount });
        NavigateTo(anchor);
    }

    /// <summary>
    /// Called by a view for each new first day while it scrolls, and when it lands. Sets the period and
    /// title, and makes sure the months around the visible days are loaded. The cache only works in whole
    /// months, so the load is skipped while the visible days stay in the same months.
    /// </summary>
    public void OnViewScrolled(DateOnly first, DateOnly lastExclusive, DateOnly? focus = null)
    {
        PeriodStart = Mode == CalendarViewMode.Month ? ViewNavigator.MonthStartOf(focus ?? first) : first;
        PeriodTitle = ViewNavigator.MonthTitle(Mode == CalendarViewMode.Month ? focus ?? first : first);

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
        var before = Settings;
        Settings = change(Settings).Normalize();
        using (var conn = _services.Database.Open())
        {
            SettingsStore.Save(conn, Settings);
        }

        // Pane Flags Only
        var panesOnly = Settings with { SidebarOpen = before.SidebarOpen, DetailsPanelOpen = before.DetailsPanelOpen, TimeZones = before.TimeZones } == before
            && Settings.TimeZones.SequenceEqual(before.TimeZones);
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

    /// <summary>Shows or hides Saturday and Sunday.</summary>
    public void ToggleWeekends() => Update(s => s with { ShowWeekends = !s.ShowWeekends });

    /// <summary>Shows or hides declined events.</summary>
    public void ToggleDeclined() => Update(s => s with { ShowDeclined = !s.ShowDeclined }, reloadData: true);

    /// <summary>Makes the grid taller (positive) or shorter (negative).</summary>
    public void ZoomBy(double delta) => Update(s => s with { HourHeight = s.HourHeight + delta });

    /// <summary>Default grid height.</summary>
    public void ZoomReset() => Update(s => s with { HourHeight = LeafSettings.DefaultHourHeight });

    // =========================================================================
    // SELECTION
    // =========================================================================

    /// <summary>Selects an event and loads its details.</summary>
    public void Select(CalendarOccurrence occurrence)
    {
        try
        {
            using var conn = _services.Database.Open();
            var stored     = EventStore.Get(conn, occurrence.AccountId, occurrence.CalendarId, occurrence.EventId);
            var details    = stored is null ? null : EventDetailsParser.Parse(stored.RawJson);
            var calendar   = Calendars.FirstOrDefault(c => c.AccountId == occurrence.AccountId && c.Id == occurrence.CalendarId);
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

    /// <summary>Clears the selection.</summary>
    public void ClearSelection()
    {
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
        var anchor = SelectedInfo?.Occurrence;
        var from   = anchor?.Start ?? Now;
        var day    = LocalDate(from);

        for (var i = 0; i <= 90; i++, day = day.AddDays(direction))
        {
            if (OccurrenceOrder.Adjacent(Cache.ForDay(day), from, anchor?.Key, direction) is { } next)
            {
                Select(next);
                NavigateTo(LocalDate(next.Start));
                ScrollToTimeRequested?.Invoke(this, next.Start);
                return;
            }
        }
    }

    // =========================================================================
    // CALENDARS
    // =========================================================================

    /// <summary>Shows or hides a calendar in Leaf.</summary>
    public void SetCalendarHidden(CalendarInfo calendar, bool hidden)
    {
        using (var conn = _services.Database.Open())
        {
            CalendarStore.SetHidden(conn, calendar.AccountId, calendar.Id, hidden);
        }

        ReloadCalendars();
    }

    /// <summary>Sets Leaf's color for a calendar (null restores Google's).</summary>
    public void SetCalendarColor(CalendarInfo calendar, string? color)
    {
        using (var conn = _services.Database.Open())
        {
            CalendarStore.SetColor(conn, calendar.AccountId, calendar.Id, color);
        }

        ReloadCalendars();
    }

    /// <summary>Saves the order of an account's calendars.</summary>
    public void ReorderCalendars(string accountId, IReadOnlyList<string> calendarIds)
    {
        using (var conn = _services.Database.Open())
        {
            CalendarStore.Reorder(conn, accountId, calendarIds);
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
            Calendars     = CalendarStore.GetAll(conn);
            AccountEmails = AccountStore.GetAll(conn).ToDictionary(a => a.Id, a => a.Email);
        }

        CalendarsChanged?.Invoke(this, EventArgs.Empty);
        Run(RefreshAsync);
    }

    /// <summary>
    /// Reloads the cached events from the database. An edit that asked for it gets its event selected again (a moved
    /// event has a new key); otherwise deleted events leave the selection and edited ones are picked up.
    /// </summary>
    public async Task RefreshAsync()
    {
        await Cache.RefreshAsync(_life.Token);

        // An Edit Asked To Select Its Event Once It's Reloaded
        if (_reselect is { } reselect)
        {
            _reselect = null;
            if (Cache.ForDay(reselect.Day).FirstOrDefault(reselect.Match) is { } edited)
            {
                Select(edited);
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

    /// <summary>
    /// Follows the PC's time zone. When it changed since the last check, the events are sorted into the new
    /// local days and the views redraw (hour labels, now line, today, the selected event's time, and the
    /// upcoming list). Runs every minute and whenever the window is activated.
    /// </summary>
    public void CheckTimeZone()
    {
        var before = Zone;
        if (!_zones.Check())
        {
            return;
        }

        _services.Log.Info("calendar.timezone.changed", $"{before.Id} -> {Zone.Id}");
        Cache.Zone = Zone;
        _services.Editor.LocalZoneId = IanaZoneId(Zone);
        Today      = _services.Options.StartDate ?? DateOnly.FromDateTime(DateTime.Now);
        if (SelectedInfo is { } selected)
        {
            SelectedInfo = selected with { When = WhenText(selected.Occurrence) };
        }

        LayoutChanged?.Invoke(this, EventArgs.Empty);
        Run(RefreshAsync);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _minuteTimer.Stop();
        _services.GoogleChanged -= OnGoogleChanged;
        _services.Editor.Changed -= OnEditsChanged;
        if (_attachedSync is not null)
        {
            _attachedSync.DataChanged -= OnSyncDataChanged;
        }

        _life.Cancel();
        _life.Dispose();
        Cache.Dispose();
    }

    // =========================================================================
    // EDITING
    // =========================================================================

    /// <summary>
    /// Deletes events you can change (asking about repeating ones), then shows "Event deleted · Undo". The delete
    /// waits in the outbox for <see cref="EventEditor.UndoWindow"/>. Invites and read-only calendars are skipped
    /// with a hint. A failure shows "Couldn't delete" and is logged as <c>event.delete.failed</c>; it never throws.
    /// </summary>
    public async Task DeleteAsync(IReadOnlyList<CalendarOccurrence> items, bool sendUpdates)
    {
        try
        {
            // Only Events You Can Change
            var permissions = items.Select(o => (Occurrence: o, Rights: _services.Editor.Permissions(o))).ToList();
            var deletable   = permissions.Where(p => p.Rights.CanEdit).Select(p => p.Occurrence).ToList();
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

            // Offer Undo Until The Delete Is Sent
            _lastDelete = receipt;
            ClearSelection();
            Say(deletable.Count == 1 ? "Event deleted" : string.Create(CultureInfo.InvariantCulture, $"{deletable.Count} events deleted"), canUndo: true);
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
    /// when it has a link, else the first upcoming one that does. Meet links get the event's account (spec 8.5).
    /// </summary>
    public async Task JoinAsync(CalendarOccurrence? occurrence = null)
    {
        var target = occurrence
            ?? (SelectedInfo is { Details.ConferenceUri: not null } selected ? selected.Occurrence : null)
            ?? Upcoming.FirstOrDefault(u => u.CanJoin)?.Occurrence;

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

    /// <summary>Opens a description link (checked against the allowlist again).</summary>
    public Task OpenLinkAsync(Uri link) => _services.LaunchAsync(link);

    /// <summary>Opens the selected event's location in Google Maps.</summary>
    public async Task OpenLocationAsync()
    {
        if (SelectedInfo?.Details.Location is { Length: > 0 } location)
        {
            await _services.LaunchAsync(LinkSafety.MapsSearch(location));
        }
    }

    /// <summary>Opens an email to every other guest, with the title as the subject (E then E).</summary>
    public async Task EmailGuestsAsync()
    {
        if (SelectedInfo is { } info && GuestsMailto(info) is { } mailto)
        {
            await _services.LaunchAsync(mailto);
        }
    }

    /// <summary>The "Email guests" address for an event (every guest but you), or null when no usable address remains.</summary>
    public static Uri? GuestsMailto(SelectedEventInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var emails = info.Draft.Guests.Where(g => !g.IsSelf).Select(g => g.Email).ToList();
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

            var o     = info.Occurrence;
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

    /// <summary>Undoes the last delete if it hasn't reached Google yet.</summary>
    public void Undo()
    {
        if (_lastDelete is not { } receipt)
        {
            return;
        }

        _lastDelete = null;
        try
        {
            if (_services.Editor.Undo(receipt))
            {
                Notice = null;
                return;
            }

            Say("Already sent to Google, so it can't be undone.", canUndo: false);
        }
        catch (Exception ex) when (IsEditFailure(ex))
        {
            Fail("event.undo.failed", ex);
        }
    }

    /// <summary>Hides the notice bar.</summary>
    public void DismissNotice() => Notice = null;

    /// <summary>Runs work started from a click or key; failures are logged under <paramref name="eventName"/>, never thrown into the dispatcher.</summary>
    public void Fire(Func<Task> work, string eventName = "calendar.action.failed") => Run(work, eventName);

    /// <summary>Logs a failure a view caught (internal IDs only).</summary>
    public void LogError(string eventName, Exception exception) => _services.Log.Error(eventName, exception);

    // "This event" for single events; otherwise the page's dialog (null means the user canceled)
    async Task<EditScope?> ScopeForAsync(IReadOnlyList<CalendarOccurrence> items, bool includeFollowing)
    {
        if (!items.Any(o => o.RecurringEventId is not null))
        {
            return EditScope.This;
        }

        return AskScope is { } ask ? await ask(includeFollowing) : EditScope.This;
    }

    // After an edit the event may have a new key (moved) or a new ID (one instance of a series): find it by its start
    void ReselectAfterRefresh(CalendarOccurrence occurrence, DateTimeOffset start, bool isAllDay)
    {
        var day    = isAllDay ? DateOnly.FromDateTime(start.UtcDateTime) : LocalDate(start);
        var series = occurrence.RecurringEventId ?? occurrence.EventId;
        _reselect  = (day, o => o.AccountId == occurrence.AccountId && o.Start == start && (o.EventId == occurrence.EventId || o.RecurringEventId == series));
    }

    // One selected event shows its details; zero or several show the list or the selection summary
    void PublishSelection()
    {
        if (_selection.Count == 1)
        {
            Select(_selection[0]);
            return;
        }

        SelectedInfo = null;
        OnPropertyChanged(nameof(Selection));
        OccurrencesChanged?.Invoke(this, EventArgs.Empty);
    }

    void Fail(string eventName, Exception exception)
    {
        _reselect = null;
        _services.Log.Error(eventName, exception);
        Say("Something went wrong saving that change. Try again.", canUndo: false);
    }

    // Every notice counts as new (a repeat of the same text restarts the bar's timer), so it's cleared first
    void Say(string text, bool canUndo)
    {
        Notice = null;
        Notice = new NoticeInfo(text, canUndo);
    }

    static bool IsEditFailure(Exception ex) =>
        ex is Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException or InvalidOperationException;

    // Deletes wait out the undo window; nudge the sync loop once it has passed so they go out right away
    async Task NudgeAfterUndoWindowAsync()
    {
        await Task.Delay(EventEditor.UndoWindow + TimeSpan.FromSeconds(0.5));
        _services.Google?.Loop.TriggerNow();
    }

    EventDetails? LoadDetails(CalendarOccurrence o)
    {
        using var conn = _services.Database.Open();
        return EventStore.Get(conn, o.AccountId, o.CalendarId, o.EventId) is { } stored ? EventDetailsParser.Parse(stored.RawJson) : null;
    }

    void OnEditsChanged(object? sender, EventArgs e) => Run(RefreshAsync);

    DateOnly DayOf(CalendarOccurrence o) => o.IsAllDay ? o.AllDayStart : LocalDate(o.Start);

    // Google wants IANA zone IDs; Windows reports its own
    static string IanaZoneId(TimeZoneInfo zone) =>
        TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : zone.Id;

    // =========================================================================
    // INTERNALS
    // =========================================================================

    Task<IReadOnlyList<CalendarOccurrence>> LoadAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var includeDeclined = Settings.ShowDeclined;
        return Task.Run<IReadOnlyList<CalendarOccurrence>>(
            () =>
            {
                using var conn = _services.Database.Open();
                return OccurrenceQuery.Load(conn, from, to, Zone, includeDeclined);
            },
            ct);
    }

    void AttachSync()
    {
        if (_attachedSync is not null)
        {
            _attachedSync.DataChanged -= OnSyncDataChanged;
        }

        _attachedSync = _services.Google?.Sync;
        if (_attachedSync is not null)
        {
            _attachedSync.DataChanged += OnSyncDataChanged;
        }
    }

    void OnGoogleChanged(object? sender, EventArgs e) => _dispatcher.TryEnqueue(AttachSync);

    // Sync runs on a background thread; hop to the UI thread before touching the cache
    void OnSyncDataChanged(object? sender, EventArgs e) => _dispatcher.TryEnqueue(ReloadCalendars);

    void OnMinute()
    {
        CheckTimeZone();
        var today = _services.Options.StartDate ?? DateOnly.FromDateTime(DateTime.Now);
        if (today != Today)
        {
            Today = today;
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        RefreshUpcoming();
    }

    void RefreshUpcoming()
    {
        var now   = Now;
        var until = now + UpcomingWindow;
        var items = Enumerable.Range(0, 2)
            .SelectMany(i => Cache.ForDay(LocalDate(now).AddDays(i)))
            .DistinctBy(o => o.Key)
            .Where(o => !o.IsAllDay && o.End > now && o.Start < until)
            .OrderBy(o => o.Start)
            .Take(20)
            .Select(o => new UpcomingItem(o, o.Title, TimeLabels.Range(o.Start, o.End, Zone, Settings.Use24HourTime), TimeLabels.Relative(o.Start, o.End, now), EventColors.ResolveAccent(o.ColorId, o.CalendarColor), o.HasConference, Select, occurrence => Fire(() => JoinAsync(occurrence), "calendar.join.failed")))
            .ToList();

        Upcoming.Clear();
        foreach (var item in items)
        {
            Upcoming.Add(item);
        }
    }

    string WhenText(CalendarOccurrence o)
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

    DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Zone).DateTime);

    // Background work started from UI events: failures are logged, never thrown into the dispatcher
    async void Run(Func<Task> work, string eventName = "calendar.load.failed")
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
public sealed partial class CalendarRow(CalendarInfo info) : ObservableObject
{
    /// <summary>The stored calendar.</summary>
    public CalendarInfo Info { get; } = info;

    /// <summary>Display name.</summary>
    public string Name => Info.Summary;

    /// <summary>Checked when shown.</summary>
    public bool IsVisible => Info.IsVisible;

    /// <summary>Hex color.</summary>
    public string Color => Info.DisplayColor;
}

/// <summary>An account's calendars in the sidebar.</summary>
public sealed class AccountGroup(string email, IEnumerable<CalendarRow> calendars)
{
    /// <summary>Account email (header).</summary>
    public string Email { get; } = email;

    /// <summary>Calendars in Leaf's order (drag to reorder).</summary>
    public ObservableCollection<CalendarRow> Calendars { get; } = new(calendars);
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
}
