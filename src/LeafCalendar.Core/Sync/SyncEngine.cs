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
/// </remarks>
public sealed class SyncEngine(GoogleCalendarClient google, LeafDatabase database, AppLog log, TimeProvider time) : IDisposable
{
    // ponytail: one gate for every account; per-account gates if parallel account sync is ever wanted
    readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>How often each account's calendar list is refreshed (event changes are checked every poll).</summary>
    public static readonly TimeSpan CalendarListInterval = TimeSpan.FromMinutes(15);

    // Guarded by _gate
    readonly Dictionary<string, DateTimeOffset> _calendarListSyncedAt = new(StringComparer.Ordinal);
    bool _changed;

    /// <summary>Raised after a sync that wrote anything. Raised on the syncing thread, after the sync lock is released.</summary>
    public event EventHandler? DataChanged;

    /// <summary>Syncs every account that can sync; the calendar list only when it's due.</summary>
    public Task SyncAllAsync(CancellationToken ct) => SyncAllAsync(false, ct);

    /// <summary>Syncs every account that can sync. <paramref name="refreshCalendarLists"/> forces a calendar-list refresh ("Sync now").</summary>
    public async Task SyncAllAsync(bool refreshCalendarLists, CancellationToken ct)
    {
        bool changed;
        await _gate.WaitAsync(ct);
        try
        {
            _changed = false;

            IReadOnlyList<Account> accounts;
            using (var conn = database.Open())
            {
                accounts = AccountStore.GetAll(conn);
            }

            foreach (var account in accounts.Where(a => a.Status == AccountStatus.Ok))
            {
                await SyncAccountCoreAsync(account.Id, refreshCalendarLists, ct);
            }

            changed = _changed;
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Syncs one account now, including its calendar list (used right after sign-in).</summary>
    public async Task SyncAccountAsync(string accountId, CancellationToken ct)
    {
        bool changed;
        await _gate.WaitAsync(ct);
        try
        {
            _changed = false;
            await SyncAccountCoreAsync(accountId, refreshCalendarList: true, ct);
            changed = _changed;
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    async Task SyncAccountCoreAsync(string accountId, bool refreshCalendarList, CancellationToken ct)
    {
        try
        {
            // Calendar List (When Due)
            var now = time.GetUtcNow();
            var due = refreshCalendarList
                || !_calendarListSyncedAt.TryGetValue(accountId, out var last)
                || now - last >= CalendarListInterval;

            if (due)
            {
                var entries = await google.ListCalendarsAsync(accountId, ct);
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

            // Events Per Calendar
            foreach (var calendar in calendars)
            {
                await SyncCalendarAsync(calendar, ct);
            }
        }
        catch (AccountNeedsSignInException)
        {
            using var conn = database.Open();
            AccountStore.SetStatus(conn, accountId, AccountStatus.NeedsSignIn);
            log.Info("sync.account.needs-sign-in", $"account={accountId}");
        }
        catch (Exception ex) when (IsSyncFailure(ex, ct))
        {
            log.Error("sync.account.failed", ex);
        }
    }

    async Task SyncCalendarAsync(CalendarInfo calendar, CancellationToken ct)
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
            log.Error("sync.calendar.failed", ex);
        }
    }

    async Task PullAsync(CalendarInfo calendar, string? syncToken, CancellationToken ct)
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

        // Apply Atomically (events with edits waiting in the outbox keep the local version until they're sent)
        using var conn = database.Open();
        using var tx   = conn.BeginTransaction();
        var pending    = OutboxStore.EventIdsFor(conn, tx, calendar.AccountId, calendar.Id);

        if (syncToken is null)
        {
            EventStore.DeleteAllForCalendar(conn, tx, calendar.AccountId, calendar.Id);
        }

        foreach (var item in items)
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && pending.Contains(id.GetString()!))
            {
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

        CalendarStore.SetSyncToken(conn, tx, calendar.AccountId, calendar.Id, page.NextSyncToken);
        tx.Commit();

        if (items.Count > 0 || syncToken is null)
        {
            _changed = true;
        }

        log.Info("sync.calendar.done", $"account={calendar.AccountId} changes={items.Count} full={syncToken is null}");
    }

    static bool IsSyncFailure(Exception ex, CancellationToken ct) =>
        ex is GoogleApiException or HttpRequestException or JsonException or InvalidDataException or SqliteException ||
        (ex is TaskCanceledException && !ct.IsCancellationRequested);
}
