using System.Text.Json;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Google;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Sync;

/// <summary>
/// Pulls Google calendars and events into the local database.
/// </summary>
/// <remarks>
/// For each account, the engine makes the calendar list match Google first. Then each calendar is
/// pulled: incrementally from its sync token, or in full when there is no token or Google answers 410.
/// Every page of a pull is fetched before anything is written. The writes and the new sync token are
/// then applied in one transaction, so a failure part-way leaves the old data and token untouched.
/// A failing calendar is logged and skipped. A rejected refresh token marks the account
/// <see cref="AccountStatus.NeedsSignIn"/> and keeps its data.
/// Only one sync runs at a time: the loop, "Sync now", and the post-sign-in sync all share the
/// engine, and a second caller waits for the running sync to finish, then runs its own.
/// Each account's outbox is sent first (see <see cref="OutboxSender"/>), so Google has the local edits before
/// the pull, and a pull never overwrites an event that still has edits waiting.
/// </remarks>
public sealed class SyncEngine(GoogleCalendarClient google, LeafDatabase database, AppLog log, TimeProvider time) : IDisposable
{
    // ponytail: one gate for every account; per-account gates if parallel account sync is ever wanted
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>How often each account's calendar list is refreshed (event changes are checked every poll).</summary>
    public static readonly TimeSpan CalendarListInterval = TimeSpan.FromMinutes(15);

    // Guarded by _gate
    private readonly Dictionary<string, DateTimeOffset> _calendarListSyncedAt = new(StringComparer.Ordinal);
    private bool _changed;

    // Built on first use: a field initializer that reads the primary-constructor parameters the methods also
    // capture triggers CS9124, which warnings-as-errors turns into a build break
    private OutboxSender? _outboxSender;
    private int _rejected;
    private int _conflicts;
    private readonly List<string> _signInsNeeded = [];
    private bool _reached;
    private bool _unreachable;
    private volatile bool _offline;
    private volatile bool _showsOffline;
    private int _failedInARow;
    private volatile string? _refusal;

    /// <summary>Raised after a sync in which Google refused edits for good (they were undone locally). The argument is how many.</summary>
    public event EventHandler<int>? ChangesRejected;

    /// <summary>Raised after a sync in which Google reported new conflicts ("1 change needs your review"). The argument is how many.</summary>
    public event EventHandler<int>? ConflictsFound;

    /// <summary>Raised once when an account's sign-in stops working (it's marked "needs sign-in" and no longer syncs). The argument is its ID.</summary>
    public event EventHandler<string>? SignInNeeded;

    private OutboxSender Outbox => _outboxSender ??= new OutboxSender(google, database, log, time);

    /// <summary>Raised after a sync that wrote anything. Raised on the syncing thread, after the sync lock is released.</summary>
    public event EventHandler? DataChanged;

    /// <summary>Raised on the syncing thread when <see cref="IsOffline"/> or <see cref="ShowsOffline"/> flips.</summary>
    public event EventHandler? OfflineChanged;

    /// <summary>
    /// True when the last sync couldn't reach Google at all (every call failed to connect or timed out). An error
    /// answer from Google isn't offline: Google was reached.
    /// </summary>
    public bool IsOffline => _offline;

    /// <summary>How many syncs in a row must fail to reach Google before <see cref="ShowsOffline"/> turns on.</summary>
    public const int FailuresBeforeShown = 3;

    /// <summary>
    /// True once <see cref="FailuresBeforeShown"/> syncs in a row couldn't reach Google, until one gets through. Syncing
    /// stays in the background: one dropped sync (a blip, a wake from sleep) shows nothing.
    /// </summary>
    public bool ShowsOffline => _showsOffline;

    /// <summary>
    /// Google's reason for the first error answer in the last sync (for example <c>accessNotConfigured</c> when the
    /// Google Calendar API is off in the user's Cloud project), "" when Google refused without one, or null when Google
    /// refused nothing. Only the machine-readable reason is kept, never the error text.
    /// </summary>
    public string? LastRefusal => _refusal;

    /// <summary>Syncs every account that can sync; the calendar list only when it's due.</summary>
    public Task SyncAllAsync(CancellationToken ct) => SyncAllAsync(false, ct);

    /// <summary>Syncs every account that can sync. <paramref name="refreshCalendarLists"/> forces a calendar-list refresh ("Sync now").</summary>
    public async Task SyncAllAsync(bool refreshCalendarLists, CancellationToken ct)
    {
        bool changed;
        int rejected;
        bool? offline;
        int conflicts;
        string[] signIns;
        await _gate.WaitAsync(ct);
        try
        {
            Begin();

            IReadOnlyList<Account> accounts;
            using (var conn = database.Open())
            {
                accounts = AccountStore.GetAll(conn);
            }

            foreach (var account in accounts.Where(a => a.Status == AccountStatus.Ok))
            {
                await SyncAccountCoreAsync(account.Id, refreshCalendarLists, ct);
            }

            (changed, rejected, offline) = End();
            conflicts = _conflicts;
            signIns = [.. _signInsNeeded];
        }
        finally
        {
            _gate.Release();
        }

        Raise(changed, rejected, offline, conflicts, signIns);
    }

    /// <summary>Syncs one account now, including its calendar list (used right after sign-in).</summary>
    public async Task SyncAccountAsync(string accountId, CancellationToken ct)
    {
        bool changed;
        int rejected;
        bool? offline;
        int conflicts;
        string[] signIns;
        await _gate.WaitAsync(ct);
        try
        {
            Begin();
            await SyncAccountCoreAsync(accountId, refreshCalendarList: true, ct);
            (changed, rejected, offline) = End();
            conflicts = _conflicts;
            signIns = [.. _signInsNeeded];
        }
        finally
        {
            _gate.Release();
        }

        Raise(changed, rejected, offline, conflicts, signIns);
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    // Guarded by _gate: a pass starts with nothing changed, refused, reached, or unreachable
    private void Begin()
    {
        _changed = false;
        _rejected = 0;
        _reached = false;
        _unreachable = false;
        _conflicts = 0;
        _refusal = null;
        _signInsNeeded.Clear();
    }

    // Guarded by _gate: what the pass did, and the new offline state when it flipped (null when it didn't)
    private (bool Changed, int Rejected, bool? Offline) End()
    {
        var offline = _unreachable && !_reached;
        _failedInARow = offline ? _failedInARow + 1 : 0;
        var shows = _failedInARow >= FailuresBeforeShown;
        if (offline == _offline && shows == _showsOffline)
        {
            return (_changed, _rejected, null);
        }

        if (offline != _offline)
        {
            log.Info("sync.offline", $"offline={offline}");
        }

        _offline = offline;
        _showsOffline = shows;
        return (_changed, _rejected, offline);
    }

    // Outside the lock, so handlers may start another sync
    private void Raise(bool changed, int rejected, bool? offline, int conflicts, string[] signIns)
    {
        if (changed)
        {
            DataChanged?.Invoke(this, EventArgs.Empty);
        }

        if (rejected > 0)
        {
            ChangesRejected?.Invoke(this, rejected);
        }

        if (conflicts > 0)
        {
            ConflictsFound?.Invoke(this, conflicts);
        }

        foreach (var account in signIns)
        {
            SignInNeeded?.Invoke(this, account);
        }

        if (offline is not null)
        {
            OfflineChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task SyncAccountCoreAsync(string accountId, bool refreshCalendarList, CancellationToken ct)
    {
        try
        {
            // Local Edits First (in order; a pull never overwrites an event that still has some waiting)
            var sent = await Outbox.SendAsync(accountId, ct);
            _changed |= sent.Changed;
            _rejected += sent.Rejected;
            _conflicts += sent.Conflicts;

            // Calendar List (When Due)
            var now = time.GetUtcNow();
            var due = refreshCalendarList
                || !_calendarListSyncedAt.TryGetValue(accountId, out var last)
                || now - last >= CalendarListInterval;

            if (due)
            {
                var entries = await google.ListCalendarsAsync(accountId, ct);
                _reached = true;
                using var conn = database.Open();
                CalendarStore.ReplaceForAccount(conn, accountId, entries);
                _calendarListSyncedAt[accountId] = now;
                _changed = true;
            }

            IReadOnlyList<CalendarInfo> calendars;
            using (var conn = database.Open())
            {
                calendars = CalendarStore.GetForAccount(conn, accountId);
            }

            // Events Per Calendar (not those hidden from Google Calendar's list: they appear nowhere in Leaf; their sync
            // token picks up where it left off once they're shown again)
            foreach (var calendar in calendars.Where(c => !c.Hidden))
            {
                await SyncCalendarAsync(calendar, ct);
            }
        }
        catch (AccountNeedsSignInException)
        {
            using var conn = database.Open();
            AccountStore.SetStatus(conn, accountId, AccountStatus.NeedsSignIn);
            _signInsNeeded.Add(accountId);
            log.Info("sync.account.needs-sign-in", $"account={accountId}");
        }
        catch (Exception ex) when (IsSyncFailure(ex, ct))
        {
            NoteFailure(ex, ct);
            log.Error("sync.account.failed", ex);
        }
    }

    private async Task SyncCalendarAsync(CalendarInfo calendar, CancellationToken ct)
    {
        try
        {
            try
            {
                await PullAsync(calendar, calendar.SyncToken, ct);
            }
            catch (SyncTokenExpiredException)
            {
                log.Info("sync.calendar.full-resync", $"account={calendar.AccountId}");
                await PullAsync(calendar, null, ct);
            }
        }
        catch (Exception ex) when (IsSyncFailure(ex, ct))
        {
            NoteFailure(ex, ct);
            log.Error("sync.calendar.failed", ex);
        }
    }

    // Guarded by _gate: a connection failure counts toward offline; Google's first refusal keeps its reason
    private void NoteFailure(Exception ex, CancellationToken ct)
    {
        _unreachable |= IsNoConnection(ex, ct);
        if (ex is GoogleApiException api)
        {
            _refusal ??= api.Reason ?? "";
        }
    }

    private async Task PullAsync(CalendarInfo calendar, string? syncToken, CancellationToken ct)
    {
        // Fetch Every Page First
        var items = new List<JsonElement>();
        string? pageToken = null;
        EventsPage page;

        do
        {
            page = await google.ListEventsAsync(calendar.AccountId, calendar.Id, syncToken, pageToken, ct);
            items.AddRange(page.Items);
            pageToken = page.NextPageToken;
        }
        while (pageToken is not null);

        _reached = true;

        // Apply Atomically (events with edits waiting in the outbox keep the local version until they're sent)
        using var conn = database.Open();
        using var tx = conn.BeginTransaction();
        var pending = OutboxStore.EventIdsFor(conn, tx, calendar.AccountId, calendar.Id);

        if (syncToken is null)
        {
            EventStore.DeleteAllForCalendar(conn, tx, calendar.AccountId, calendar.Id);
        }

        bool IsQueued(JsonElement item, string property) =>
            item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && pending.Contains(value.GetString()!);

        var skipped = false;
        foreach (var item in items)
        {
            if (item.ValueKind == JsonValueKind.Object && (IsQueued(item, "id") || IsQueued(item, "recurringEventId")))
            {
                skipped = true;
                continue;
            }

            // One Bad Event Must Not Block The Calendar
            try
            {
                EventStore.Apply(conn, tx, calendar.AccountId, calendar.Id, item);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                log.Info("sync.event.skipped", $"account={calendar.AccountId}");
            }
        }

        // Skipped Changes Are Fetched Again: the token only moves on when nothing was skipped (an undone or later
        // accepted edit would otherwise leave Google's change unseen), and never over a token forgotten meanwhile
        CalendarStore.ReplaceSyncToken(conn, tx, calendar.AccountId, calendar.Id, calendar.SyncToken, skipped ? syncToken : page.NextSyncToken);
        tx.Commit();

        if (items.Count > 0 || syncToken is null)
        {
            _changed = true;
        }

        log.Info("sync.calendar.done", $"account={calendar.AccountId} changes={items.Count} full={syncToken is null}");
    }

    // Google never answered: the connection failed or timed out (an HTTP error status means it was reached)
    private static bool IsNoConnection(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException { StatusCode: null } || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    private static bool IsSyncFailure(Exception ex, CancellationToken ct) =>
        ex is GoogleApiException or HttpRequestException or JsonException or InvalidDataException or SqliteException or SyncTokenExpiredException ||
        (ex is TaskCanceledException && !ct.IsCancellationRequested);
}
