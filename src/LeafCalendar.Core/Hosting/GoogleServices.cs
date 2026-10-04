using LeafCalendar.Core.Alerts;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;
using LeafCalendar.Core.Sync;

namespace LeafCalendar.Core.Hosting;

/// <summary>
/// Everything that talks to Google, wired for one OAuth client.
/// </summary>
/// <remarks>
/// It lives in Core, with no UI, so a future background helper can reuse it unchanged.
/// When the user changes the OAuth client, the app disposes this object and builds a new one.
/// </remarks>
public sealed class GoogleServices : IAsyncDisposable
{
    private readonly ITokenStore _tokenStore;
    private readonly LeafDatabase _database;
    private readonly AppLog _log;
    private readonly TimeProvider _time;

    /// <summary>Wires the Google services.</summary>
    public GoogleServices(HttpClient http, OAuthClientCredentials credentials, ITokenStore tokenStore, LeafDatabase database, AppLog log, TimeProvider time, GoogleEndpoints? endpoints = null)
    {
        _tokenStore = tokenStore;
        _database = database;
        _log = log;
        _time = time;

        OAuth = new GoogleOAuthClient(http, credentials, time, endpoints);
        AccessTokens = new AccessTokenProvider(OAuth, tokenStore, time);
        Calendar = new GoogleCalendarClient(http, AccessTokens, endpoints);
        Contacts = new ContactSearch(http, AccessTokens, log, endpoints);
        Sync = new SyncEngine(Calendar, database, log, time);
        Loop = new SyncLoop(Sync.SyncAllAsync, time, log);
    }

    /// <summary>An open "Join now" belonged to an account being disconnected; the argument is its tag, to withdraw from screen.</summary>
    public event EventHandler<string>? JoinNowWithdrawn;

    /// <summary>OAuth calls.</summary>
    public GoogleOAuthClient OAuth { get; }

    /// <summary>Access tokens per account.</summary>
    public AccessTokenProvider AccessTokens { get; }

    /// <summary>Calendar REST client.</summary>
    public GoogleCalendarClient Calendar { get; }

    /// <summary>Guest autocomplete from Google contacts (People API, read-only).</summary>
    public ContactSearch Contacts { get; }

    /// <summary>Sync engine.</summary>
    public SyncEngine Sync { get; }

    /// <summary>Polling loop (not started until <see cref="SyncLoop.Start"/>).</summary>
    public SyncLoop Loop { get; }

    /// <summary>Creates a sign-in flow that opens pages with <paramref name="openBrowser"/>.</summary>
    public SignInFlow CreateSignIn(Func<Uri, Task> openBrowser) =>
        new(OAuth, _tokenStore, AccessTokens, _database, openBrowser, _time, _log);

    /// <summary>
    /// Disconnects an account. Leaf asks Google to revoke the token (best effort), then deletes the
    /// token and the account's local data. Google Calendar itself is not changed.
    /// </summary>
    public async Task DisconnectAsync(string accountId, CancellationToken ct)
    {
        // Revoke (Best Effort)
        if (_tokenStore.GetRefreshToken(accountId) is { } refreshToken)
        {
            try
            {
                await OAuth.RevokeAsync(refreshToken, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or GoogleApiException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                _log.Error("account.revoke.failed", ex);
            }
        }

        // Remove Locally
        _tokenStore.RemoveRefreshToken(accountId);
        AccessTokens.Forget(accountId);

        // Its Alerts Go Too, So Adding It Again Starts With A Quiet First Look (after its calendars, so an invite pass
        // running now can't mark them seeded again; one transaction, so a failure can't leave half of it behind)
        using var conn = _database.Open();

        // Its Open "Join now" Notifications Are Withdrawn, Since The Ledger Row Is Their Only Handle (keys read Kind|account|...)
        var open = AlertLedger.OpenJoinNow(conn).Where(e => e.Key.Split('|').ElementAtOrDefault(1) == accountId).ToList();

        using (var tx = conn.BeginTransaction())
        {
            AccountStore.Delete(conn, accountId, tx);
            InviteWatcher.Forget(conn, accountId, tx);
            tx.Commit();
        }

        foreach (var entry in open)
        {
            JoinNowWithdrawn?.Invoke(this, entry.Tag);
        }

        _log.Info("account.disconnected", $"account={accountId}");
    }

    /// <summary>
    /// Looks up the Workspace domain (Google's <c>hd</c>) of every account that signed in before Leaf stored it, so
    /// rooms and event types know which accounts are Workspace ones. A failure is logged with the account ID and
    /// error type only, and the account is tried again on the next launch.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task RefreshHostedDomainsAsync(CancellationToken ct)
    {
        List<Account> unknown;
        using (var conn = _database.Open())
        {
            unknown = [.. AccountStore.GetAll(conn).Where(a => a.HostedDomain is null)];
        }

        foreach (var account in unknown)
        {
            try
            {
                var user = await OAuth.GetUserInfoAsync(await AccessTokens.GetAccessTokenAsync(account.Id, ct), ct);
                using var conn = _database.Open();
                AccountStore.SetHostedDomain(conn, account.Id, user.Hd ?? "");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.Info("account.domain.failed", $"account={account.Id} error={ex.GetType().Name}");
            }
        }
    }

    /// <summary>Stops the polling loop, then disposes the sync engine and access tokens.</summary>
    public async ValueTask DisposeAsync()
    {
        await Loop.DisposeAsync();
        Sync.Dispose();
        AccessTokens.Dispose();
    }
}
