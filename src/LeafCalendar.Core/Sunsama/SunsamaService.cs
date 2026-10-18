using System.Globalization;
using System.Text.Json;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Scheduling;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Sunsama;

/// <summary>Whether Leaf Calendar can read Sunsama.</summary>
public enum SunsamaState
{
    /// <summary>No Sunsama sign-in is saved.</summary>
    SignedOut,

    /// <summary>Signed in; the days on screen are fetched after each Google sync pass.</summary>
    SignedIn,

    /// <summary>Sunsama refused the saved sign-in; saved blocks stay until you sign in again or sign out.</summary>
    NeedsSignIn,
}

/// <summary>
/// Signs in to Sunsama, signs out, and fetches the task times for the days on screen into <c>sunsama_block</c>
/// (spec 2026-10-16, Sunsama projections, sections 4 and 5).
/// </summary>
/// <remarks>
/// Leaf Calendar only reads Sunsama (scopes <c>read offline_access</c>) and never writes to it or to Google. The client
/// ID Leaf Calendar registers, the refresh token and the account email are provider secrets under
/// <see cref="ProviderId"/> in <c>secrets.bin</c>; the access token stays in memory. Nothing from Sunsama is logged:
/// only event names, counts and exception type names. Events are raised on whatever thread the work ran on.
/// </remarks>
/// <param name="http">The shared HTTP client.</param>
/// <param name="tokens">Where the provider secrets are kept.</param>
/// <param name="database">The local database the blocks are saved in.</param>
/// <param name="log">The app log.</param>
/// <param name="time">The clock.</param>
/// <param name="endpoints">Sunsama's servers (or the UI tests' fake).</param>
public sealed class SunsamaService(HttpClient http, ITokenStore tokens, LeafDatabase database, AppLog log, TimeProvider time, SunsamaEndpoints endpoints) : IDisposable
{
    /// <summary>The provider ID Sunsama's secrets are saved under.</summary>
    public const string ProviderId = "sunsama";

    /// <summary>Sunsama's loopback redirect port (Cal.com keeps 53817).</summary>
    public const int RedirectPort = SunsamaEndpoints.DefaultRedirectPort;

    /// <summary>The loopback redirect path.</summary>
    public const string RedirectPath = "/sunsama/callback";

    private const string ClientIdKey = "client_id";
    private const string RefreshTokenKey = "refresh_token";
    private const string EmailKey = "email";

    // ponytail: fetches every visible day each pass; cache by day + lastModified if Sunsama rate-limits
    private const int MaxDays = 42;

    // Passes in a row that couldn't reach Sunsama before the sync status says so (Google's offline icon waits for 3 too)
    private const int UnreachableAfter = 3;

    private readonly Lock _gate = new();
    private readonly SchedulingOAuth _oauth = new(
        endpoints.Authorize,
        endpoints.Token,
        ["read", "offline_access"],
        endpoints.RedirectPort,
        RedirectPath,
        new Uri("https://help.sunsama.com/docs/integrations/mcp"),
        UsePkce: true,
        Resource: endpoints.Mcp);

    private Session? _session;
    private SunsamaProfile? _profile;
    private DateOnly _from;
    private DateOnly _to;
    private int _endings;
    private int _running;
    private int _failures;
    private DateTimeOffset _waitUntil;
    private bool _registerAgain;

    /// <summary>Raised when <see cref="State"/> or <see cref="Email"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when saved blocks were replaced or cleared.</summary>
    public event EventHandler? DataChanged;

    /// <summary>Whether Leaf Calendar can read Sunsama.</summary>
    public SunsamaState State { get; private set; }

    /// <summary>The signed-in account's email (shown in Settings, never logged), or null when signed out.</summary>
    public string? Email { get; private set; }

    /// <summary>
    /// True once 3 passes in a row couldn't reach Sunsama (offline, 429, 5xx), until one works: the sync status's quiet
    /// "Couldn't reach Sunsama" line. Changes raise <see cref="Changed"/>.
    /// </summary>
    public bool Unreachable => Volatile.Read(ref _failures) >= UnreachableAfter;

    /// <summary>Whether either sidebar item is on; when off, <see cref="SyncAsync"/> fetches nothing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Reads the saved sign-in at startup and when the secrets become readable again. Leaves a signed-in or
    /// needs-sign-in state as it is; when the secrets can't be read right now, changes nothing (logged as
    /// <c>sunsama.load.secrets-unavailable</c>) so a later call retries. No network call is made.
    /// </summary>
    public void Load()
    {
        if (State != SunsamaState.SignedOut)
        {
            return;
        }

        int endings;
        lock (_gate)
        {
            endings = _endings;
        }

        string? clientId;
        string? refreshToken;
        string? email;
        try
        {
            clientId = NonEmpty(tokens.GetProviderSecret(ProviderId, ClientIdKey));
            refreshToken = NonEmpty(tokens.GetProviderSecret(ProviderId, RefreshTokenKey));
            email = tokens.GetProviderSecret(ProviderId, EmailKey);
        }
        catch (Exception ex) when (SecretsUnavailable.Is(ex))
        {
            log.Info("sunsama.load.secrets-unavailable");
            return;
        }

        if (clientId is null || refreshToken is null)
        {
            return;
        }

        lock (_gate)
        {
            // A sign-in or a sign-out while the secrets were read wins
            if (State != SunsamaState.SignedOut || _endings != endings)
            {
                return;
            }

            _session = StartSession(clientId, refreshToken);
            State = SunsamaState.SignedIn;
            Email = email;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Signs in through the browser: registers Leaf Calendar's client the first time, runs the PKCE sign-in, proves the
    /// tokens work by reading <c>sunsama://me</c>, and only then saves the refresh token and the email.
    /// </summary>
    /// <param name="openBrowser">Opens Sunsama's consent page.</param>
    /// <param name="ct">Cancels the sign-in.</param>
    /// <exception cref="InvalidDataException">The secrets can't be read right now (<see cref="SecretsUnavailable.Is"/>).</exception>
    /// <exception cref="SchedulingPortBusyException">Another app holds the redirect port.</exception>
    /// <exception cref="SchedulingAuthException">The sign-in was canceled or refused; a refused client is forgotten so the next sign-in registers again.</exception>
    /// <exception cref="SchedulingUnavailableException">Sunsama couldn't be reached.</exception>
    /// <exception cref="TimeoutException">The browser didn't come back in time.</exception>
    public async Task SignInAsync(Func<Uri, Task> openBrowser, CancellationToken ct)
    {
        int endings;
        lock (_gate)
        {
            endings = _endings;
        }

        // Register Once, Then Reuse the Client (unless a sign-in while signed out by Sunsama was refused)
        var clientId = Volatile.Read(ref _registerAgain) ? null : NonEmpty(tokens.GetProviderSecret(ProviderId, ClientIdKey));
        if (clientId is null)
        {
            clientId = await SunsamaRegistration.RegisterAsync(http, endpoints.Register, $"http://127.0.0.1:{endpoints.RedirectPort.ToString(CultureInfo.InvariantCulture)}{RedirectPath}", ct);
            tokens.SetProviderSecret(ProviderId, ClientIdKey, clientId);
            Volatile.Write(ref _registerAgain, false);
            log.Info("sunsama.registered");
        }

        string refreshToken;
        try
        {
            refreshToken = await new ProviderSignIn(http, _oauth, new ProviderClient(clientId, ""), openBrowser, time).RunAsync(ct);
        }
        catch (SchedulingAuthException)
        {
            // A refused or lost client registers again next time (only this service writes Sunsama's secrets). Signed out
            // by Sunsama, the saved sign-in stays until a new one replaces it, so only the client is replaced
            if (State == SunsamaState.SignedOut)
            {
                tokens.RemoveProviderSecrets(ProviderId);
            }
            else if (State == SunsamaState.NeedsSignIn)
            {
                Volatile.Write(ref _registerAgain, true);
            }

            throw;
        }

        // Prove the Tokens Work Before Saving Them
        var session = StartSession(clientId, refreshToken);
        SunsamaProfile? profile;
        string email;
        Session? old;
        try
        {
            using (var me = await session.Mcp.ReadResourceAsync("sunsama://me", ct))
            {
                profile = SunsamaParser.Profile(me.RootElement);
                email = profile?.Email ?? "";
            }

            lock (_gate)
            {
                if (_endings != endings)
                {
                    throw new SchedulingAuthException("The sign-in was canceled by a sign-out.");
                }

                tokens.SetProviderSecret(ProviderId, RefreshTokenKey, session.RefreshToken);
                tokens.SetProviderSecret(ProviderId, EmailKey, email);

                // A New Sign-In Starts Clean: rows kept after a refusal (or another account's) go, and the next pass refetches
                using (var conn = database.Open())
                {
                    SunsamaStore.Clear(conn, null);
                }

                old = _session;
                _session = session;
                _profile = profile;
                State = SunsamaState.SignedIn;
                Email = email;
                _failures = 0;
                _waitUntil = default;
            }
        }
        catch
        {
            session.Dispose();
            throw;
        }

        old?.Dispose();
        log.Info("sunsama.signed-in");
        Changed?.Invoke(this, EventArgs.Empty);
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Signs out: removes every saved block and the saved sign-in, and ends any sign-in or sync on its way. The blocks go
    /// first, and <see cref="Changed"/> and <see cref="DataChanged"/> are raised even when removing the secrets fails.
    /// </summary>
    /// <exception cref="IOException">The secrets couldn't be removed (signed out for this session; the next start may load them).</exception>
    public void SignOut()
    {
        Session? old;
        lock (_gate)
        {
            _endings++;
            old = _session;
            _session = null;
            _profile = null;
            State = SunsamaState.SignedOut;
            Email = null;
            _failures = 0;
            _waitUntil = default;
            _registerAgain = false;
        }

        try
        {
            using (var conn = database.Open())
            {
                SunsamaStore.Clear(conn, null);
            }

            tokens.RemoveProviderSecrets(ProviderId);
        }
        finally
        {
            old?.Dispose();
            log.Info("sunsama.signed-out");
            Changed?.Invoke(this, EventArgs.Empty);
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Sets the local days on screen (at most 42 are fetched). Safe from any thread.</summary>
    /// <param name="from">The first day on screen.</param>
    /// <param name="toExclusive">The day after the last.</param>
    public void SetDays(DateOnly from, DateOnly toExclusive)
    {
        lock (_gate)
        {
            _from = from;
            _to = toExclusive.DayNumber - from.DayNumber > MaxDays ? from.AddDays(MaxDays) : toExclusive;
        }
    }

    /// <summary>
    /// One pass, run after each Google sync pass: fetches each day on screen and replaces its saved rows. Does nothing
    /// unless signed in and <see cref="Enabled"/>. Offline, 429, 5xx and unreadable answers keep the saved rows and log
    /// <c>sunsama.sync.failed</c>; a refused sign-in moves to <see cref="SunsamaState.NeedsSignIn"/>. Never throws, except
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> is canceled. One pass runs at a time (a call
    /// while one runs returns at once), and a 429's <c>Retry-After</c> skips passes until it ends.
    /// </summary>
    /// <param name="ct">Cancels the pass.</param>
    public async Task SyncAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            await SyncOnceAsync(ct);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task SyncOnceAsync(CancellationToken ct)
    {
        Session session;
        DateOnly from;
        DateOnly to;
        int endings;
        lock (_gate)
        {
            if (State != SunsamaState.SignedIn || !Enabled || _session is null || _to <= _from || time.GetUtcNow() < _waitUntil)
            {
                return;
            }

            (session, from, to, endings) = (_session, _from, _to, _endings);
        }

        var days = 0;
        var blocks = 0;
        var changed = 0;
        try
        {
            // The Zone Is Read Once Per Sign-In, and Again While It's Missing
            SunsamaProfile? profile;
            lock (_gate)
            {
                profile = _profile;
            }

            if (profile is null)
            {
                using var me = await session.Mcp.ReadResourceAsync("sunsama://me", ct);
                profile = SunsamaParser.Profile(me.RootElement);
                if (profile is null)
                {
                    log.Info("sunsama.sync.no-zone");
                    return;
                }

                lock (_gate)
                {
                    // Another sign-in's zone isn't overwritten by this older pass
                    if (_endings != endings)
                    {
                        return;
                    }

                    _profile = profile;
                }
            }

            for (var day = from; day < to; day = day.AddDays(1))
            {
                var text = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                using var tasks = await session.Mcp.ReadResourceAsync($"sunsama://tasks/{text}", ct);
                using var events = await session.Mcp.ReadResourceAsync($"sunsama://calendar/events/{text}", ct);
                var parsed = SunsamaParser.Day(tasks.RootElement, events.RootElement, profile);

                using var conn = database.Open();
                var cached = OccurrenceQuery.Load(conn, day, day.AddDays(1), profile.Zone, includeDeclined: false, keepSharedCopies: false);
                var rows = SunsamaMatch.Blocks(day, parsed, cached);
                lock (_gate)
                {
                    // A sign-out meanwhile wins: its cleared table stays cleared
                    if (_endings != endings)
                    {
                        return;
                    }

                    // A day saved as it was raises nothing, so an unchanged plan doesn't reload the calendar every pass
                    if (!Same(SunsamaStore.Range(conn, null, day, day.AddDays(1)), rows))
                    {
                        SunsamaStore.ReplaceDay(conn, null, day, rows);
                        changed++;
                    }
                }

                days++;
                blocks += rows.Count;
            }

            log.Info("sunsama.sync.done", string.Create(CultureInfo.InvariantCulture, $"days={days} changed={changed} blocks={blocks}"));
            SetFailures(0, endings);
        }
        catch (SchedulingAuthException)
        {
            var refused = false;
            lock (_gate)
            {
                if (_endings == endings && State == SunsamaState.SignedIn)
                {
                    State = SunsamaState.NeedsSignIn;
                    refused = true;
                }
            }

            log.Info("sunsama.sync.needs-sign-in");
            if (refused)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex) when (ex is SchedulingUnavailableException or JsonException or SqliteException)
        {
            // Sunsama Asked Leaf Calendar To Slow Down: no passes until it says
            if (ex is SchedulingUnavailableException { RetryAfter: { } wait })
            {
                lock (_gate)
                {
                    _waitUntil = time.GetUtcNow() + wait;
                }
            }

            log.Info("sunsama.sync.failed", $"type={ex.GetType().Name} days={days.ToString(CultureInfo.InvariantCulture)}");
            if (ex is SchedulingUnavailableException)
            {
                SetFailures(Volatile.Read(ref _failures) + 1, endings);
            }
        }
        finally
        {
            if (changed > 0)
            {
                DataChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
        }
    }

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    // Counts passes in a row that couldn't reach Sunsama (for this sign-in only), and says so when Unreachable flips
    private void SetFailures(int failures, int endings)
    {
        bool before;
        lock (_gate)
        {
            if (_endings != endings)
            {
                return;
            }

            before = Unreachable;
            _failures = failures;
        }

        if (before != Unreachable)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // The same rows in any order (the store reads them back by start)
    private static bool Same(IReadOnlyList<SunsamaBlock> saved, IReadOnlyList<SunsamaBlock> fresh) =>
        saved.Count == fresh.Count && saved.OrderBy(Order).SequenceEqual(fresh.OrderBy(Order));

    private static (DateTimeOffset, string, SunsamaBlockKind, DateTimeOffset, string?) Order(SunsamaBlock b) => (b.Start, b.TaskId, b.Kind, b.End, b.MatchedEventId);

    // Tokens and an MCP client for one sign-in. A rotated refresh token is saved only while this is the current session:
    // not before a sign-in proves it (the sign-in saves it then), and not after a sign-out or a newer sign-in replaced it
    private Session StartSession(string clientId, string refreshToken)
    {
        var session = new Session(refreshToken);
        session.Tokens = new ProviderTokens(http, _oauth, new ProviderClient(clientId, ""), refreshToken, time, rotated =>
        {
            lock (_gate)
            {
                session.RefreshToken = rotated;
                if (!ReferenceEquals(_session, session))
                {
                    return;
                }

                // A save that fails keeps this session working; the type only (the next start may need a new sign-in)
                try
                {
                    tokens.SetProviderSecret(ProviderId, RefreshTokenKey, rotated);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    log.Info("sunsama.token.save-failed", $"error={ex.GetType().Name}");
                }
            }
        });
        session.Mcp = new SunsamaMcpClient(http, endpoints.Mcp, session.Tokens.GetAccessTokenAsync, session.Tokens.ForgetAccessToken);
        return session;
    }

    private sealed class Session(string refreshToken) : IDisposable
    {
        public ProviderTokens Tokens { get; set; } = null!;

        public SunsamaMcpClient Mcp { get; set; } = null!;

        public string RefreshToken { get; set; } = refreshToken;

        // Ends both, so calls still on their way fail as unavailable
        public void Dispose()
        {
            Tokens.Dispose();
            Mcp.Dispose();
        }
    }
}
