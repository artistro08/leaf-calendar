using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Sync;
using LeafCalendar.Core.Views;
using Microsoft.UI.Dispatching;

namespace LeafCalendar.App.ViewModels;

/// <summary>An upcoming event in the details panel.</summary>
public sealed record UpcomingItem(CalendarOccurrence Occurrence, string Title, string When, string Relative, string Color);

/// <summary>The selected event, ready to show.</summary>
public sealed record SelectedEventInfo(CalendarOccurrence Occurrence, EventDetails Details, string When, string CalendarName, string CalendarColor);

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
    SyncEngine? _attachedSync;

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
        PeriodTitle = TitleFor(PeriodStart, PeriodStart.AddDays(VisibleColumns));

        services.GoogleChanged += OnGoogleChanged;
        AttachSync();

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

    /// <summary>The zone the grid is drawn in.</summary>
    [SuppressMessage("Performance", "CA1822", Justification = "View-model state that views read per instance.")]
    public TimeZoneInfo Zone => TimeZoneInfo.Local;

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

    /// <summary>Title-bar text such as "October 2026".</summary>
    [ObservableProperty]
    public partial string PeriodTitle { get; set; }

    /// <summary>The selected event, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial SelectedEventInfo? SelectedInfo { get; set; }

    /// <summary>True when an event is selected.</summary>
    public bool HasSelection => SelectedInfo is not null;

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
    public void Previous() => NavigateTo(ViewNavigator.Step(Mode, PeriodStart, -1, Settings.CustomDayCount));

    /// <summary>Next period.</summary>
    public void Next() => NavigateTo(ViewNavigator.Step(Mode, PeriodStart, 1, Settings.CustomDayCount));

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

    /// <summary>Called by a view when scrolling settles.</summary>
    public void OnViewScrolled(DateOnly first, DateOnly lastExclusive, DateOnly? focus = null)
    {
        PeriodStart = Mode == CalendarViewMode.Month ? ViewNavigator.MonthStartOf(focus ?? first) : first;
        PeriodTitle = Mode == CalendarViewMode.Month ? ViewNavigator.MonthTitle(focus ?? first) : TitleFor(first, lastExclusive);
        Run(() => Cache.EnsureAsync(first, lastExclusive, _life.Token));
    }

    // =========================================================================
    // SETTINGS
    // =========================================================================

    /// <summary>Changes and saves settings, then tells views to relayout (and reloads data when filters changed).</summary>
    public void Update(Func<LeafSettings, LeafSettings> change, bool reloadData = false)
    {
        Settings = change(Settings).Normalize();
        using (var conn = _services.Database.Open())
        {
            SettingsStore.Save(conn, Settings);
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
            if (details is null)
            {
                return;
            }

            SelectedInfo = new SelectedEventInfo(occurrence, details, WhenText(occurrence), calendar?.Summary ?? "", EventColors.ResolveAccent(occurrence.ColorId, occurrence.CalendarColor));
            OccurrencesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            _services.Log.Error("calendar.select.failed", ex);
        }
    }

    /// <summary>Clears the selection.</summary>
    public void ClearSelection()
    {
        if (SelectedInfo is null)
        {
            return;
        }

        SelectedInfo = null;
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
        Run(RefreshAsync);
    }

    /// <summary>Sets Leaf's color for a calendar (null restores Google's).</summary>
    public void SetCalendarColor(CalendarInfo calendar, string? color)
    {
        using (var conn = _services.Database.Open())
        {
            CalendarStore.SetColor(conn, calendar.AccountId, calendar.Id, color);
        }

        ReloadCalendars();
        Run(RefreshAsync);
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

    /// <summary>Re-reads the calendar list (after sync, sidebar edits, or account changes).</summary>
    public void ReloadCalendars()
    {
        using (var conn = _services.Database.Open())
        {
            Calendars     = CalendarStore.GetAll(conn);
            AccountEmails = AccountStore.GetAll(conn).ToDictionary(a => a.Id, a => a.Email);
        }

        CalendarsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reloads the cached events from the database.</summary>
    public Task RefreshAsync() => Cache.RefreshAsync(_life.Token);

    /// <inheritdoc />
    public void Dispose()
    {
        _minuteTimer.Stop();
        _services.GoogleChanged -= OnGoogleChanged;
        if (_attachedSync is not null)
        {
            _attachedSync.DataChanged -= OnSyncDataChanged;
        }

        _life.Cancel();
        _life.Dispose();
        Cache.Dispose();
    }

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
    void OnSyncDataChanged(object? sender, EventArgs e) => _dispatcher.TryEnqueue(() =>
    {
        ReloadCalendars();
        Run(RefreshAsync);
    });

    void OnMinute()
    {
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
            .Select(o => new UpcomingItem(o, o.Title, TimeLabels.Range(o.Start, o.End, Zone, Settings.Use24HourTime), TimeLabels.Relative(o.Start, o.End, now), EventColors.ResolveAccent(o.ColorId, o.CalendarColor)))
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

    static string TitleFor(DateOnly first, DateOnly lastExclusive) => ViewNavigator.PeriodTitle(first, lastExclusive.AddDays(-1) < first ? first : lastExclusive.AddDays(-1));

    DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Zone).DateTime);

    // Background work started from UI events: failures are logged, never thrown into the dispatcher
    async void Run(Func<Task> work)
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
            _services.Log.Error("calendar.load.failed", ex);
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
