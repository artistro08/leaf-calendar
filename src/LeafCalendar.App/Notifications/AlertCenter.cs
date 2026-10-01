using LeafCalendar.Core.Alerts;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Sync;

namespace LeafCalendar.App.Notifications;

/// <summary>
/// Turns Leaf's alerts into Windows notifications (spec 8.4): reminders at Google's reminder times, and the persistent
/// "Join now" at start, withdrawn once its meeting ends, moves, or is declined.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="AlertScheduler"/> decides what's due; every local edit, conflict answer, and sync that wrote something
/// makes it plan again, and a minute before each alert it asks for a sync (spec 5.3). Settings are read on every pass,
/// so a change in Settings › Notifications applies to the next alert. Logs carry the alert's kind and tag only.
/// </para>
/// <para>
/// The scheduler raises its events on its own thread while it holds its pass lock, so these handlers never wait on the
/// UI thread and never throw. The PC time zone comes from the App's watcher; the App calls <see cref="Invalidate"/>
/// when it changes (all-day reminders count from local midnight).
/// </para>
/// </remarks>
internal sealed class AlertCenter : IDisposable
{
    readonly LeafServices _services;
    readonly Notifier _notifier;
    readonly Func<TimeZoneInfo> _zone;
    readonly AlertScheduler _scheduler;
    readonly Lock _syncGate = new();
    SyncEngine? _sync;
    bool _disposed;

    /// <summary>Wires the scheduler to the notifier and to every source of changed events.</summary>
    /// <param name="services">The app services (database, clock, edits, Google).</param>
    /// <param name="notifier">Shows and withdraws the notifications.</param>
    /// <param name="zone">The PC time zone (the App's <see cref="Core.Views.LocalZoneWatcher"/>).</param>
    public AlertCenter(LeafServices services, Notifier notifier, Func<TimeZoneInfo> zone)
    {
        _services  = services;
        _notifier  = notifier;
        _zone      = zone;
        _scheduler = new AlertScheduler(services.Database, services.Time, zone) { IsEnabled = IsEnabled };

        // Scheduler
        _scheduler.AlertDue       += OnAlertDue;
        _scheduler.AlertRetracted += OnAlertRetracted;
        _scheduler.SyncSoon       += OnSyncSoon;
        _scheduler.Failed         += OnSchedulerFailed;

        // Changed Events Re-Plan
        services.Editor.Changed    += OnDataChanged;
        services.Conflicts.Changed += OnDataChanged;
        services.GoogleChanged     += OnGoogleChanged;
        AttachSync();
    }

    /// <summary>Starts the 15-second passes (the first one right away, on the scheduler's thread).</summary>
    public void Start() => _scheduler.Start();

    /// <summary>Plans again on the next pass, which runs right away (the PC time zone changed).</summary>
    public void Invalidate() => _scheduler.Invalidate();

    /// <inheritdoc />
    public void Dispose()
    {
        _services.Editor.Changed    -= OnDataChanged;
        _services.Conflicts.Changed -= OnDataChanged;
        _services.GoogleChanged     -= OnGoogleChanged;
        lock (_syncGate)
        {
            _disposed = true;
            DetachSync();
        }

        _scheduler.Dispose();
    }

    // =========================================================================
    // SCHEDULER EVENTS (raised on the scheduler's thread, under its pass lock)
    // =========================================================================

    bool IsEnabled(AlertKind kind)
    {
        using var conn = _services.Database.Open();
        var settings   = SettingsStore.Load(conn);
        return kind switch
        {
            AlertKind.Reminder => settings.ReminderNotifications,
            AlertKind.JoinNow  => settings.JoinNowNotifications,
            _                  => settings.InviteNotifications,
        };
    }

    void OnAlertDue(object? sender, Alert alert)
    {
        try
        {
            using var conn = _services.Database.Open();
            var o          = alert.Occurrence;
            if (EventStore.Get(conn, o.AccountId, o.CalendarId, o.EventId) is not { } stored)
            {
                return;
            }

            // Content (description links count, the same parse the planner used for the meeting link)
            var settings = SettingsStore.Load(conn);
            var details  = EventDetailsParser.Parse(stored.RawJson, includeDescription: true);
            var when     = ToastContent.When(o, _zone(), settings.Use24HourTime, _services.Time.GetUtcNow());
            var profile  = _services.Options.Profile;
            _notifier.Show(alert.Kind == AlertKind.JoinNow
                ? ToastContent.JoinNow(alert, details, when, profile, settings.NotificationSound)
                : ToastContent.Reminder(alert, details, when, profile, settings.NotificationSound));
            _services.Log.Info("alert.shown", $"kind={alert.Kind} tag={alert.Tag}");
        }
        catch (Exception ex)
        {
            // Raised under the scheduler's pass lock, so nothing may escape
            _services.Log.Info("alert.show.failed", $"error={ex.GetType().Name}");
        }
    }

    void OnAlertRetracted(object? sender, string tag)
    {
        _services.Log.Info("alert.withdrawn", $"tag={tag}");
        _ = _notifier.RemoveAsync(tag, ToastContent.JoinGroup);
    }

    void OnSyncSoon(object? sender, EventArgs e) => _services.Google?.Loop.TriggerNow();

    // Logging never throws, so a failure report can't fail in turn
    void OnSchedulerFailed(object? sender, Exception ex) => _services.Log.Info("alert.check.failed", $"error={ex.GetType().Name}");

    // =========================================================================
    // CHANGED EVENTS
    // =========================================================================

    // Edits, conflict answers, and syncs (on the sync thread); Invalidate only nudges the timer, never runs a pass here
    void OnDataChanged(object? sender, EventArgs e) => _scheduler.Invalidate();

    void OnGoogleChanged(object? sender, EventArgs e) => AttachSync();

    // GoogleChanged may arrive on any thread, even while Quit disposes this, so attaching and detaching share a lock
    void AttachSync()
    {
        lock (_syncGate)
        {
            if (_disposed)
            {
                return;
            }

            DetachSync();
            _sync = _services.Google?.Sync;
            if (_sync is not null)
            {
                _sync.DataChanged += OnDataChanged;
            }
        }
    }

    void DetachSync()
    {
        if (_sync is not null)
        {
            _sync.DataChanged -= OnDataChanged;
        }

        _sync = null;
    }
}
