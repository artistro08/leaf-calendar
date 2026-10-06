using System.Globalization;
using LeafCalendar.Core.Alerts;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Hosting;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Sync;

namespace LeafCalendar.App.Notifications;

/// <summary>
/// Turns Leaf's alerts into Windows notifications (spec 8.4): reminders at Google's reminder times, the persistent
/// "Join now" at start (withdrawn once its meeting ends, moves, or is declined), new and updated invitations,
/// "1 change needs your review" (spec 5.5), and "Sign in again" (spec 4.2).
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="AlertScheduler"/> decides what's due; every local edit, conflict answer, and sync that wrote something
/// makes it plan again, and a minute before each alert it asks for a sync (spec 5.3). Each sync that wrote something
/// also looks for invitations. Settings are read for every notification, so a change in Settings › Notifications
/// applies to the next one. Logs carry the kind and the tag only.
/// </para>
/// <para>
/// The scheduler raises its events on its own thread while it holds its pass lock, and the sync engine on the syncing
/// thread, so these handlers never wait on the UI thread and never throw. The PC time zone comes from the App's watcher; the App calls <see cref="Invalidate"/>
/// when it changes (all-day reminders count from local midnight).
/// </para>
/// </remarks>
internal sealed class AlertCenter : IDisposable
{
    private readonly LeafServices _services;
    private readonly Notifier _notifier;
    private readonly Func<TimeZoneInfo> _zone;
    private readonly AlertScheduler _scheduler;
    private readonly Lock _syncGate = new();
    private readonly Lock _inviteGate = new();
    private SyncEngine? _sync;
    private GoogleServices? _google;
    private bool _disposed;

    /// <summary>Wires the scheduler to the notifier and to every source of changed events.</summary>
    /// <param name="services">The app services (database, clock, edits, Google).</param>
    /// <param name="notifier">Shows and withdraws the notifications.</param>
    /// <param name="zone">The PC time zone (the App's <see cref="Core.Views.LocalZoneWatcher"/>).</param>
    public AlertCenter(LeafServices services, Notifier notifier, Func<TimeZoneInfo> zone)
    {
        _services = services;
        _notifier = notifier;
        _zone = zone;
        _scheduler = new AlertScheduler(services.Database, services.Time, zone) { IsEnabled = IsEnabled };

        // Scheduler
        _scheduler.AlertDue += OnAlertDue;
        _scheduler.AlertRetracted += OnAlertRetracted;
        _scheduler.SyncSoon += OnSyncSoon;
        _scheduler.Failed += OnSchedulerFailed;
        _scheduler.Planned += OnPlanned;

        // Changed Events Re-Plan
        services.Editor.Changed += OnDataChanged;
        services.Conflicts.Changed += OnConflictsChanged;
        services.GoogleChanged += OnGoogleChanged;
        AttachSync();
    }

    /// <summary>
    /// Starts the 15-second passes (the first one right away, on the scheduler's thread), and, off the UI thread, looks
    /// for invitations and for accounts that already need signing in again.
    /// </summary>
    public void Start()
    {
        _scheduler.Start();
        _ = Task.Run(() =>
        {
            ShowInvites();
            ShowSignInsNeeded();
        });
    }

    /// <summary>Plans again on the next pass, which runs right away (the PC time zone changed).</summary>
    public void Invalidate() => _scheduler.Invalidate();

    /// <inheritdoc />
    public void Dispose()
    {
        _services.Editor.Changed -= OnDataChanged;
        _services.Conflicts.Changed -= OnConflictsChanged;
        _services.GoogleChanged -= OnGoogleChanged;
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

    private bool IsEnabled(AlertKind kind)
    {
        using var conn = _services.Database.Open();
        var settings = SettingsStore.Load(conn);
        return kind switch
        {
            AlertKind.Reminder => settings.ReminderNotifications,
            AlertKind.JoinNow => settings.JoinNowNotifications,
            _ => settings.InviteNotifications,
        };
    }

    private void OnAlertDue(object? sender, Alert alert)
    {
        try
        {
            using var conn = _services.Database.Open();
            var o = alert.Occurrence;
            if (EventStore.Get(conn, o.AccountId, o.CalendarId, o.EventId) is not { } stored)
            {
                _services.Log.Info("alert.skipped", $"kind={alert.Kind} tag={alert.Tag} reason=gone");
                return;
            }

            // Content (description links count, the same parse the planner used for the meeting link)
            var settings = SettingsStore.Load(conn);
            var details = EventDetailsParser.Parse(stored.RawJson, includeDescription: true);
            var when = ToastContent.When(o, _zone(), settings.Use24HourTime, _services.Time.GetUtcNow());
            var profile = _services.Options.Profile;
            var persistent = ToastContent.StaysOnScreen(settings, details, alert.MeetingLink);
            var shown = _notifier.Show(alert.Kind == AlertKind.JoinNow
                ? ToastContent.JoinNow(alert, details, when, profile, settings.NotificationSound, persistent)
                : ToastContent.Reminder(alert, details, when, profile, settings.NotificationSound, persistent));
            if (!shown)
            {
                // Not Shown: forgotten, so the next pass tries again while it's still due
                AlertLedger.Remove(conn, alert.Key);
                return;
            }

            _services.Log.Info("alert.shown", $"kind={alert.Kind} tag={alert.Tag}");
        }
        catch (Exception ex)
        {
            // Raised under the scheduler's pass lock, so nothing may escape
            _services.Log.Info("alert.show.failed", $"error={ex.GetType().Name}");
        }
    }

    private void OnAlertRetracted(object? sender, string tag)
    {
        _services.Log.Info("alert.withdrawn", $"tag={tag}");
        _ = _notifier.RemoveAsync(tag, ToastContent.JoinGroup);
    }

    // Detailed logging only: counts and a time, never event content
    private void OnPlanned(object? sender, PlanSummary plan) =>
        _services.Log.Trace("alert.plan", string.Create(CultureInfo.InvariantCulture, $"ahead={plan.Ahead} next={plan.Next?.UtcDateTime:O}"));

    private void OnSyncSoon(object? sender, EventArgs e) => _services.Google?.Loop.TriggerNow();

    // Logging never throws, so a failure report can't fail in turn
    private void OnSchedulerFailed(object? sender, Exception ex) => _services.Log.Info("alert.check.failed", $"error={ex.GetType().Name}");

    // =========================================================================
    // SYNC SIGNALS (raised on the syncing thread; nothing may escape)
    // =========================================================================

    // Edits; Invalidate only nudges the timer, never runs a pass here
    private void OnDataChanged(object? sender, EventArgs e) => _scheduler.Invalidate();

    // A sync wrote something: plan again, then look for invitations
    private void OnSyncDataChanged(object? sender, EventArgs e)
    {
        _scheduler.Invalidate();
        ShowInvites();
    }

    // One look at a time, so the startup look and a sync's can't both show the same invitation
    private void ShowInvites()
    {
        try
        {
            lock (_inviteGate)
            {
                using var conn = _services.Database.Open();
                var settings = SettingsStore.Load(conn);
                var now = _services.Time.GetUtcNow();
                var zone = _zone();
                foreach (var invite in InviteWatcher.TakeNew(conn, now, zone))
                {
                    // Recorded Either Way, So Turning Invitations Back On Doesn't Bring Old Ones
                    if (!settings.InviteNotifications)
                    {
                        continue;
                    }

                    var when = ToastContent.When(invite.Occurrence, zone, settings.Use24HourTime, now);
                    _notifier.Show(ToastContent.Invite(invite.Occurrence, invite.Details, invite.IsUpdate, invite.Tag, when, _services.Options.Profile, settings.NotificationSound));
                    _services.Log.Info("alert.shown", $"kind=Invite tag={invite.Tag}");
                }
            }
        }
        catch (Exception ex)
        {
            _services.Log.Info("alert.invites.failed", $"error={ex.GetType().Name}");
        }
    }

    // New conflicts: one notification with the total, replacing any earlier one
    private void OnConflictsFound(object? sender, int found)
    {
        try
        {
            using var conn = _services.Database.Open();
            var count = ConflictStore.Count(conn);
            if (count > 0)
            {
                _notifier.Show(ToastContent.Conflicts(count, _services.Options.Profile, SettingsStore.Load(conn).NotificationSound));
                _services.Log.Info("alert.shown", $"kind=Conflicts count={count}");
            }
        }
        catch (Exception ex)
        {
            _services.Log.Info("alert.conflicts.failed", $"error={ex.GetType().Name}");
        }
    }

    // A conflict was answered: plan again, and withdraw the notification once none are left
    private void OnConflictsChanged(object? sender, EventArgs e)
    {
        _scheduler.Invalidate();
        try
        {
            using var conn = _services.Database.Open();
            if (ConflictStore.Count(conn) == 0)
            {
                _ = _notifier.RemoveAsync(ToastContent.ConflictTag, ToastContent.ConflictGroup);
            }
        }
        catch (Exception ex)
        {
            _services.Log.Info("alert.conflicts.failed", $"error={ex.GetType().Name}");
        }
    }

    private void OnSignInNeeded(object? sender, string accountId) => ShowSignIn(a => a.Id == accountId);

    // At Start: accounts whose sign-in stopped working while Leaf wasn't running (each toast replaces its earlier one)
    private void ShowSignInsNeeded() => ShowSignIn(a => a.Status == AccountStatus.NeedsSignIn);

    private void ShowSignIn(Func<Account, bool> which)
    {
        try
        {
            using var conn = _services.Database.Open();
            var sound = SettingsStore.Load(conn).NotificationSound;
            foreach (var account in AccountStore.GetAll(conn).Where(which))
            {
                _notifier.Show(ToastContent.SignIn(account.Id, account.Email, _services.Options.Profile, sound));
                _services.Log.Info("alert.shown", $"kind=SignIn account={account.Id}");
            }
        }
        catch (Exception ex)
        {
            _services.Log.Info("alert.signin.failed", $"error={ex.GetType().Name}");
        }
    }

    private void OnGoogleChanged(object? sender, EventArgs e) => AttachSync();

    // GoogleChanged may arrive on any thread, even while Quit disposes this, so attaching and detaching share a lock
    private void AttachSync()
    {
        lock (_syncGate)
        {
            if (_disposed)
            {
                return;
            }

            DetachSync();
            _google = _services.Google;
            _sync = _google?.Sync;
            if (_google is not null)
            {
                _google.JoinNowWithdrawn += OnAlertRetracted;
            }

            if (_sync is not null)
            {
                _sync.DataChanged += OnSyncDataChanged;
                _sync.ConflictsFound += OnConflictsFound;
                _sync.SignInNeeded += OnSignInNeeded;
            }
        }
    }

    private void DetachSync()
    {
        if (_sync is not null)
        {
            _sync.DataChanged -= OnSyncDataChanged;
            _sync.ConflictsFound -= OnConflictsFound;
            _sync.SignInNeeded -= OnSignInNeeded;
        }

        _sync = null;

        if (_google is not null)
        {
            _google.JoinNowWithdrawn -= OnAlertRetracted;
        }

        _google = null;
    }
}
