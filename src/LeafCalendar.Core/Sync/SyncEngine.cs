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
public sealed class SyncEngine(GoogleCalendarClient google, LeafDatabase database, AppLog log) : IDisposable
{
    // ponytail: one gate for every account; per-account gates if parallel account sync is ever wanted
    readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Syncs every account that can sync.</summary>
    public async Task SyncAllAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            IReadOnlyList<Account> accounts;
            using (var conn = database.Open())
            {
                accounts = AccountStore.GetAll(conn);
            }

            foreach (var account in accounts.Where(a => a.Status == AccountStatus.Ok))
            {
                await SyncAccountCoreAsync(account.Id, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Syncs one account's calendar list and every calendar's events.</summary>
    public async Task SyncAccountAsync(string accountId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await SyncAccountCoreAsync(accountId, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    async Task SyncAccountCoreAsync(string accountId, CancellationToken ct)
    {
        try
        {
            // Calendar List
            var entries = await google.ListCalendarsAsync(accountId, ct);

            IReadOnlyList<CalendarInfo> calendars;
            using (var conn = database.Open())
            {
                CalendarStore.ReplaceForAccount(conn, accountId, entries);
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

        // Apply Atomically
        using var conn = database.Open();
        using var tx   = conn.BeginTransaction();

        if (syncToken is null)
        {
            EventStore.DeleteAllForCalendar(conn, tx, calendar.AccountId, calendar.Id);
        }

        foreach (var item in items)
        {
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

        log.Info("sync.calendar.done", $"account={calendar.AccountId} changes={items.Count} full={syncToken is null}");
    }

    static bool IsSyncFailure(Exception ex, CancellationToken ct) =>
        ex is GoogleApiException or HttpRequestException or JsonException or InvalidDataException or SqliteException ||
        (ex is TaskCanceledException && !ct.IsCancellationRequested);
}
