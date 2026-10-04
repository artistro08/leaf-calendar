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
/// or before a sync brought the event still shows, as long as its meeting isn't over. A "Join now" shows for as long as
/// its meeting runs, even past the hour. Per event instance only the latest alert that came due counts, so waking in the
/// middle of a meeting shows one "Join now" and not three stale reminders. Every shown alert goes into
/// <see cref="AlertLedger"/> first, so the next pass, a restart, or a full resync never shows it again.
/// </para>
/// <para>
/// A "Join now" stays on screen until clicked, so once its meeting ends, moves, is declined, or is deleted, the pass
/// raises <see cref="AlertRetracted"/> with its tag and forgets it, so it shows again if the meeting comes back.
/// A minute before any alert, <see cref="SyncSoon"/> asks for a sync, so a
/// last-minute change on Google is caught first (spec 5.3).
/// </para>
/// <para>
/// The plan (a day back, or back to the start of the longest running meeting with a link, to a day ahead) is cached and rebuilt
/// after <see cref="Invalidate"/> (data changed), when it runs out, when the clock is set back past it, or when the time
/// zone changes. <see cref="IsEnabled"/> is read once per kind per pass. Events are raised outside the state lock, in
/// pass order, on the timer's thread or the caller's; a handler that throws is reported through <see cref="Failed"/>
/// and never stops the rest of the pass.
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

    static readonly TimeSpan PlanSpan   = TimeSpan.FromDays(1);
    static readonly TimeSpan LedgerKeep = TimeSpan.FromDays(2);

    readonly Lock _gate  = new();
    readonly Lock _raise = new();
    ITimer? _timer;
    IReadOnlyList<Alert>? _plan;
    DateTimeOffset _planTo;
    string? _planZone;
    DateTimeOffset _syncedUntil;
    bool _stale;
    bool _disposed;

    /// <summary>Whether a kind may show (the Notifications settings). Read once per kind on every pass.</summary>
    public Func<AlertKind, bool> IsEnabled { get; set; } = _ => true;

    /// <summary>An alert is due; show it.</summary>
    public event EventHandler<Alert>? AlertDue;

    /// <summary>A shown "Join now" no longer applies; the argument is its tag.</summary>
    public event EventHandler<string>? AlertRetracted;

    /// <summary>An alert fires within a minute; sync now.</summary>
    public event EventHandler? SyncSoon;

    /// <summary>The plan was rebuilt; the argument holds what it found from now on (for the diagnostic log).</summary>
    public event EventHandler<PlanSummary>? Planned;

    /// <summary>A pass or one of its handlers failed; the rest of the pass goes on, and the next pass tries again.</summary>
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

    /// <summary>
    /// Events changed: the next pass re-plans, and runs right away when the timer is going. Never waits for a running
    /// pass (it's called on the UI thread after every edit).
    /// </summary>
    public void Invalidate()
    {
        // No State Lock: a pass holds it through all its database work
        Volatile.Write(ref _stale, true);

        try
        {
            Volatile.Read(ref _timer)?.Change(TimeSpan.Zero, Tick);
        }
        catch (ObjectDisposedException)
        {
            // Disposed meanwhile (Quit); nothing left to plan
        }
    }

    /// <summary>Runs one pass now.</summary>
    public void Check()
    {
        // One Pass At A Time, So A Later Pass's Retract Never Overtakes An Earlier Pass's Show
        lock (_raise)
        {
            // Disposed: Don't Even Read The Settings
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
            }

            // The Settings, Read Once Per Kind
            var isEnabled = IsEnabled;
            var enabled   = Enum.GetValues<AlertKind>().Where(isEnabled).ToHashSet();

            var due       = new List<Alert>();
            var retracted = new List<string>();
            var syncSoon  = false;
            PlanSummary? planned = null;

            try
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    // Events Changed (read before the events are, so a change during this pass re-plans the next one)
                    if (Interlocked.Exchange(ref _stale, false))
                    {
                        _plan = null;
                    }

                    var now       = time.GetUtcNow();
                    var tz        = zone();
                    var from      = now - LookBack;
                    var replanned = false;

                    using var conn = database.Open();

                    // Plan (a day each side, and back to the start of any meeting still running, so its "Join now" is known)
                    if (_plan is null || now + SyncLead + Tick > _planTo || now < _planTo - 2 * PlanSpan || tz.Id != _planZone)
                    {
                        // The Old Plan Goes First, so a plan that fails is made again on the next pass
                        _plan     = null;
                        _planTo   = now + PlanSpan;
                        _planZone = tz.Id;
                        _plan     = AlertPlanner.Plan(conn, PlanFrom(conn, now, tz), _planTo, tz);
                        replanned = true;
                        var ahead = _plan.Where(a => a.FireAt > now).ToList();
                        planned   = new PlanSummary(ahead.Count, ahead.Count > 0 ? ahead[0].FireAt : null);
                    }

                    // Clock Set Back: forget the sync look-ahead so the next minute's alerts still ask for one
                    if (_syncedUntil > now + SyncLead)
                    {
                        _syncedUntil = now;
                    }

                    Due(conn, _plan, enabled, from, now, tz, due);
                    Retract(conn, _plan, now, tz, retracted);
                    syncSoon = SyncDue(_plan, enabled, now);

                    // Pruned After Retract, so a "Join now" left open (Leaf off for days) is withdrawn before its row, its only handle, goes
                    if (replanned)
                    {
                        AlertLedger.Prune(conn, now - LedgerKeep);
                    }
                }
            }
            finally
            {
                // Raised Outside The State Lock, Each On Its Own (handlers show notifications, which can fail). Also when
                // the pass failed part-way: what the ledger already recorded or forgot would otherwise never show or go.
                if (planned is not null)
                {
                    Raise(() => Planned?.Invoke(this, planned));
                }

                foreach (var alert in due)
                {
                    Raise(() => AlertDue?.Invoke(this, alert));
                }

                foreach (var tag in retracted)
                {
                    Raise(() => AlertRetracted?.Invoke(this, tag));
                }

                if (syncSoon)
                {
                    Raise(() => SyncSoon?.Invoke(this, EventArgs.Empty));
                }
            }
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
#pragma warning disable CA1031 // A timer callback that throws ends the process; the pass is reported and retried
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Failed?.Invoke(this, ex);
        }
    }

    // One handler call; whatever it throws (a COMException from a toast, say) is reported so the rest of the pass still raises
    void Raise(Action raise)
    {
        try
        {
            raise();
        }
        catch (Exception ex)
        {
            Failed?.Invoke(this, ex);
        }
    }

    // A day back, or earlier when a timed meeting that's still running started before that. Only one with a meeting link
    // counts (its "Join now" is at its start), so a months-long block without one doesn't make every plan load months.
    internal static DateTimeOffset PlanFrom(SqliteConnection conn, DateTimeOffset now, TimeZoneInfo tz)
    {
        var from  = now - PlanSpan;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, tz).DateTime);
        foreach (var o in OccurrenceQuery.Load(conn, today, today.AddDays(1), tz, includeDeclined: false))
        {
            if (!o.IsAllDay && o.Start <= from && !IsOver(o, now, tz) && JoinPicker.MeetingLink(conn, o) is not null)
            {
                from = o.Start - TimeSpan.FromTicks(1);
            }
        }

        return from;
    }

    // Per instance, the latest enabled alert that came due (the plan is sorted by fire time, then kind, so "Join now"
    // wins a tie), unless the instance is over or it was shown before. A "Join now" counts past the look-back while its
    // meeting runs. Each goes into the caller's list as soon as the ledger has it, so a later failure can't lose it.
    static void Due(SqliteConnection conn, IReadOnlyList<Alert> plan, HashSet<AlertKind> enabled, DateTimeOffset from, DateTimeOffset now, TimeZoneInfo tz, List<Alert> result)
    {
        var came = plan.Where(a => (a.FireAt > from || a.Kind == AlertKind.JoinNow) && a.FireAt <= now && enabled.Contains(a.Kind));
        foreach (var group in came.GroupBy(a => a.Occurrence.Key, StringComparer.Ordinal))
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
    }

    // A shown "Join now" that isn't in the plan as a running meeting any more (ended, moved, declined, deleted). Its row
    // goes too, so the meeting shows "Join now" again if it comes back (re-accepted, moved back, clock corrected). Each
    // tag goes into the caller's list as soon as its row is gone, so a later failure can't leave its toast up.
    static void Retract(SqliteConnection conn, IReadOnlyList<Alert> plan, DateTimeOffset now, TimeZoneInfo tz, List<string> tags)
    {
        var running = plan
            .Where(a => a.Kind == AlertKind.JoinNow && a.FireAt <= now && !IsOver(a.Occurrence, now, tz))
            .Select(a => a.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var entry in AlertLedger.OpenJoinNow(conn))
        {
            if (!running.Contains(entry.Key))
            {
                AlertLedger.Remove(conn, entry.Key);
                tags.Add(entry.Tag);
            }
        }
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

/// <summary>A rebuilt plan: how many alerts are still ahead in it, and when the first of them fires.</summary>
public sealed record PlanSummary(int Ahead, DateTimeOffset? Next);
