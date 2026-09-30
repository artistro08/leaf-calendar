using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Alerts;

/// <summary>
/// Decides, every 15 seconds on <see cref="TimeProvider"/>, which notifications are due (spec 8.4).
/// </summary>
/// <remarks>
/// <para>
/// Each pass looks at what came due in the last hour, so an alert missed while the PC slept, while Leaf wasn't running,
/// or before a sync brought the event still shows, as long as its meeting isn't over. Per event instance only the latest
/// alert that came due counts, so waking in the middle of a meeting shows one "Join now" and not three stale reminders.
/// Every shown alert goes into <see cref="AlertLedger"/> first, so the next pass, a restart, or a full resync never
/// shows it again.
/// </para>
/// <para>
/// A "Join now" stays on screen until clicked, so once its meeting ends, moves, is declined, or is deleted, the pass
/// raises <see cref="AlertRetracted"/> with its tag (see <see cref="WithdrawsStaleJoinNow"/>). A minute before any
/// alert, <see cref="SyncSoon"/> asks for a sync, so a last-minute change on Google is caught first (spec 5.3).
/// </para>
/// <para>
/// The plan (a day back to a day ahead) is cached and rebuilt after <see cref="Invalidate"/> (data changed) or when it
/// runs out. <see cref="IsEnabled"/> is read once per kind per pass. Events are raised outside the lock, on the timer's
/// thread or the caller's.
/// </para>
/// </remarks>
public sealed class AlertScheduler(LeafDatabase database, TimeProvider time, Func<TimeZoneInfo> zone) : IDisposable
{
    /// <summary>How often a pass runs.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

    /// <summary>How far each pass looks back for alerts that came due.</summary>
    public static readonly TimeSpan LookBack = TimeSpan.FromHours(1);

    /// <summary>How long before an alert the sync is asked for.</summary>
    public static readonly TimeSpan SyncLead = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Whether a shown "Join now" is withdrawn once its meeting ends, moves, is declined, or is deleted. False keeps it
    /// on screen until Join or Dismiss (the owner's call is pending; this is the one switch).
    /// </summary>
    static bool WithdrawsStaleJoinNow => true;

    static readonly TimeSpan PlanSpan   = TimeSpan.FromDays(1);
    static readonly TimeSpan LedgerKeep = TimeSpan.FromDays(2);

    readonly Lock _gate = new();
    ITimer? _timer;
    IReadOnlyList<Alert>? _plan;
    DateTimeOffset _planTo;
    DateTimeOffset _syncedUntil;
    bool _disposed;

    /// <summary>Whether a kind may show (the Notifications settings). Read once per kind on every pass.</summary>
    public Func<AlertKind, bool> IsEnabled { get; set; } = _ => true;

    /// <summary>An alert is due; show it.</summary>
    public event EventHandler<Alert>? AlertDue;

    /// <summary>A shown "Join now" no longer applies; the argument is its tag.</summary>
    public event EventHandler<string>? AlertRetracted;

    /// <summary>An alert fires within a minute; sync now.</summary>
    public event EventHandler? SyncSoon;

    /// <summary>A timer pass failed (database or data error); the next pass tries again.</summary>
    public event EventHandler<Exception>? Failed;

    /// <summary>Starts the 15-second passes, the first one right away (no-op once started or disposed).</summary>
    public void Start()
    {
        // The timer is created stopped and started outside the lock, since a test clock may run its first pass at once
        ITimer timer;
        lock (_gate)
        {
            if (_timer is not null || _disposed)
            {
                return;
            }

            _timer = timer = time.CreateTimer(_ => SafeCheck(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        timer.Change(TimeSpan.Zero, Tick);
    }

    /// <summary>Events changed: the next pass re-plans, and runs right away when the timer is going.</summary>
    public void Invalidate()
    {
        ITimer? timer;
        lock (_gate)
        {
            _plan = null;
            timer = _timer;
        }

        try
        {
            timer?.Change(TimeSpan.Zero, Tick);
        }
        catch (ObjectDisposedException)
        {
            // Disposed meanwhile (Quit); nothing left to plan
        }
    }

    /// <summary>Runs one pass now.</summary>
    public void Check()
    {
        // The Settings, Read Once Per Kind
        var isEnabled = IsEnabled;
        var enabled   = Enum.GetValues<AlertKind>().Where(isEnabled).ToHashSet();

        List<Alert> due;
        List<string> retracted;
        bool syncSoon;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var now       = time.GetUtcNow();
            var tz        = zone();
            var from      = now - LookBack;
            var replanned = false;

            using var conn = database.Open();

            // Plan (a day each side, so a long meeting's "Join now" is still known while it runs)
            if (_plan is null || now + SyncLead + Tick > _planTo)
            {
                _planTo   = now + PlanSpan;
                _plan     = AlertPlanner.Plan(conn, now - PlanSpan, _planTo, tz);
                replanned = true;
            }

            due       = Due(conn, _plan, enabled, from, now, tz);
            retracted = WithdrawsStaleJoinNow ? Retract(conn, _plan, now, tz) : [];
            syncSoon  = SyncDue(_plan, enabled, now);

            // Pruned After Retract, so a "Join now" left open (Leaf off for days) is withdrawn before its row, its only handle, goes
            if (replanned)
            {
                AlertLedger.Prune(conn, now - LedgerKeep);
            }
        }

        // Raised Outside The Lock (handlers show notifications)
        foreach (var alert in due)
        {
            AlertDue?.Invoke(this, alert);
        }

        foreach (var tag in retracted)
        {
            AlertRetracted?.Invoke(this, tag);
        }

        if (syncSoon)
        {
            SyncSoon?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    // A timer callback must never throw (it would end the process), so failures are reported and the next pass retries
    void SafeCheck()
    {
        try
        {
            Check();
        }
        catch (Exception ex) when (ex is SqliteException or JsonException or InvalidOperationException or IOException)
        {
            Failed?.Invoke(this, ex);
        }
    }

    // Per instance, the latest enabled alert that came due (the plan is sorted by fire time, then kind, so "Join now"
    // wins a tie), unless the instance is over or it was shown before
    static List<Alert> Due(SqliteConnection conn, IReadOnlyList<Alert> plan, HashSet<AlertKind> enabled, DateTimeOffset from, DateTimeOffset now, TimeZoneInfo tz)
    {
        var result = new List<Alert>();
        foreach (var group in plan.Where(a => a.FireAt > from && a.FireAt <= now && enabled.Contains(a.Kind)).GroupBy(a => a.Occurrence.Key, StringComparer.Ordinal))
        {
            var latest = group.Last();
            if (IsOver(latest.Occurrence, now, tz))
            {
                continue;
            }

            if (AlertLedger.TryAdd(conn, latest.Key, latest.Kind, latest.Tag, latest.Occurrence.EndIn(tz), now))
            {
                result.Add(latest);
            }
        }

        return result;
    }

    // A shown "Join now" that isn't in the plan as a running meeting any more (ended, moved, declined, deleted)
    static List<string> Retract(SqliteConnection conn, IReadOnlyList<Alert> plan, DateTimeOffset now, TimeZoneInfo tz)
    {
        var running = plan
            .Where(a => a.Kind == AlertKind.JoinNow && a.FireAt <= now && !IsOver(a.Occurrence, now, tz))
            .Select(a => a.Key)
            .ToHashSet(StringComparer.Ordinal);

        var tags = new List<string>();
        foreach (var entry in AlertLedger.OpenJoinNow(conn))
        {
            if (!running.Contains(entry.Key))
            {
                AlertLedger.MarkRetracted(conn, entry.Key);
                tags.Add(entry.Tag);
            }
        }

        return tags;
    }

    // Once per alert: something enabled fires between the last look-ahead and a minute from now
    bool SyncDue(IReadOnlyList<Alert> plan, HashSet<AlertKind> enabled, DateTimeOffset now)
    {
        var ahead = now + SyncLead;
        var after = _syncedUntil > now ? _syncedUntil : now;
        var soon  = plan.Any(a => a.FireAt > after && a.FireAt <= ahead && enabled.Contains(a.Kind));
        _syncedUntil = ahead;
        return soon;
    }

    // Over once it has ended (a zero-length event counts as a one-minute one)
    static bool IsOver(CalendarOccurrence o, DateTimeOffset now, TimeZoneInfo tz)
    {
        var start = o.StartIn(tz);
        var end   = o.EndIn(tz);
        return now >= (end > start ? end : start + TimeSpan.FromMinutes(1));
    }
}
