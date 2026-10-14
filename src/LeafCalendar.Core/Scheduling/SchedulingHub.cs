using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Diagnostics;

namespace LeafCalendar.Core.Scheduling;

/// <summary>Whether a provider can be used.</summary>
public enum ProviderState
{
    /// <summary>No OAuth client is saved (or the secrets couldn't be read yet).</summary>
    NoClient,

    /// <summary>Your OAuth client is saved, but you aren't signed in.</summary>
    NotSignedIn,

    /// <summary>Signed in; the provider is ready.</summary>
    SignedIn,

    /// <summary>The provider refused the sign-in; no more calls until you sign in again.</summary>
    NeedsReconnect,
}

/// <summary>
/// Holds the signed-in scheduling providers (spec 3.3): your OAuth client per provider, the sign-in, sign-out and quiet
/// token refresh, each provider's cached links and its rate-limit back-off.
/// </summary>
/// <remarks>
/// Used from the UI thread and the sync thread: one lock guards the sessions, states, caches and back-offs, and it is
/// never held across an <c>await</c>. The client ID, client secret and refresh token go only through
/// <see cref="ITokenStore"/> (keys <c>client_id</c>, <c>client_secret</c>, <c>refresh_token</c>) and are never logged;
/// the log gets provider IDs, counts and exception type names. A provider whose token refresh or API call is refused is
/// marked <see cref="ProviderState.NeedsReconnect"/>.
/// </remarks>
/// <param name="http">The client providers send through.</param>
/// <param name="tokens">Where the OAuth client and refresh token are saved.</param>
/// <param name="log">The app log.</param>
/// <param name="time">The clock for tokens, the link cache and back-offs.</param>
/// <param name="fakeRoot">The UI tests' fake server: each provider's API, authorize and token addresses move under its <c>{id}/</c> folder.</param>
public sealed class SchedulingHub(HttpClient http, ITokenStore tokens, AppLog log, TimeProvider time, Uri? fakeRoot = null)
{
    private const string ClientIdKey = "client_id";
    private const string ClientSecretKey = "client_secret";
    private const string RefreshTokenKey = "refresh_token";

    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(60);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProviderState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CachedLinks> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _waitUntil = new(StringComparer.Ordinal);

    // Sign-outs and client removals per provider
    private readonly Dictionary<string, int> _endings = new(StringComparer.Ordinal);

    /// <summary>Raised when a provider's state changes (client saved or removed, signed in or out, needs reconnecting). Raised on any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>The signed-in providers (ones that need reconnecting are left out).</summary>
    public IReadOnlyList<ISchedulingProvider> Connected
    {
        get
        {
            lock (_gate)
            {
                return [.. _sessions.Values.Select(s => s.Provider)];
            }
        }
    }

    // The providers this hub knows; logic tests swap in one with a free redirect port
    internal IReadOnlyList<SchedulingProviderInfo> Providers { get; init; } = SchedulingProviders.All;

    /// <summary>Whether a provider has no client, isn't signed in, is signed in, or needs reconnecting.</summary>
    public ProviderState StateOf(string providerId)
    {
        lock (_gate)
        {
            return _states.GetValueOrDefault(providerId, ProviderState.NoClient);
        }
    }

    /// <summary>The signed-in provider with this ID, or null (also when it needs reconnecting).</summary>
    public ISchedulingProvider? Get(string providerId)
    {
        lock (_gate)
        {
            return _sessions.GetValueOrDefault(providerId)?.Provider;
        }
    }

    /// <summary>Saves your OAuth client for a provider (trimmed). Without a client it becomes ready to sign in.</summary>
    /// <exception cref="ArgumentException">The client ID or secret is empty, or Leaf Calendar doesn't know the provider.</exception>
    public void SaveClient(string providerId, ProviderClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        Find(providerId);
        var clientId = client.ClientId.Trim();
        var clientSecret = client.ClientSecret.Trim();
        if (clientId.Length == 0 || clientSecret.Length == 0)
        {
            throw new ArgumentException("An OAuth client needs a client ID and a client secret.", nameof(client));
        }

        tokens.SetProviderSecret(providerId, ClientIdKey, clientId);
        tokens.SetProviderSecret(providerId, ClientSecretKey, clientSecret);

        bool changed;
        lock (_gate)
        {
            changed = _states.GetValueOrDefault(providerId, ProviderState.NoClient) == ProviderState.NoClient;
            if (changed)
            {
                _states[providerId] = ProviderState.NotSignedIn;
            }
        }

        log.Info("scheduling.client.saved", $"provider={providerId}");
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Removes your OAuth client and sign-in for a provider (all three secrets) and drops it. The caller removes its
    /// settings and its saved groups (<see cref="Data.ShareGroupStore.ClearProvider"/>). A failure to remove the secrets
    /// throws and changes nothing.
    /// </summary>
    public void RemoveClient(string providerId)
    {
        EndSession(providerId, () => tokens.RemoveProviderSecrets(providerId), ProviderState.NoClient);
        log.Info("scheduling.client.removed", $"provider={providerId}");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Signs out of a provider: removes the saved refresh token only (your OAuth client stays) and drops it. A failure to
    /// remove the token throws and changes nothing.
    /// </summary>
    public void SignOut(string providerId)
    {
        // ITokenStore has no single-key remove; an empty value reads as no refresh token
        EndSession(providerId, () => tokens.SetProviderSecret(providerId, RefreshTokenKey, ""), ProviderState.NotSignedIn);
        log.Info("scheduling.signed-out", $"provider={providerId}");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Reads every provider's saved secrets, at startup and when the secrets become readable again, and builds the ones
    /// with a saved refresh token. No network call is made until a provider is used.
    /// </summary>
    /// <remarks>
    /// A provider already signed in, or needing reconnecting, is left as it is (only this hub writes its secrets). When the
    /// secrets can't be read right now, that provider is left as it was (logged as <c>scheduling.load.secrets-unavailable</c>)
    /// and nothing is written, so a later call retries.
    /// </remarks>
    public void Load()
    {
        var changed = false;
        foreach (var info in Providers)
        {
            if (StateOf(info.Id) is ProviderState.SignedIn or ProviderState.NeedsReconnect)
            {
                continue;
            }

            ProviderClient? client;
            string? refreshToken;
            try
            {
                client = ReadClient(info.Id);
                refreshToken = client is null ? null : NonEmpty(tokens.GetProviderSecret(info.Id, RefreshTokenKey));
            }
            catch (Exception ex) when (SecretsUnavailable.Is(ex))
            {
                log.Info("scheduling.load.secrets-unavailable", $"provider={info.Id}");
                continue;
            }

            var session = client is not null && refreshToken is not null ? StartSession(info, client, refreshToken, saved: true) : null;
            var state = session is not null ? ProviderState.SignedIn : client is not null ? ProviderState.NotSignedIn : ProviderState.NoClient;
            lock (_gate)
            {
                var before = _states.GetValueOrDefault(info.Id, ProviderState.NoClient);

                // A sign-in that finished meanwhile wins
                if (before is ProviderState.SignedIn or ProviderState.NeedsReconnect)
                {
                    session?.End();
                    continue;
                }

                if (session is not null)
                {
                    _sessions[info.Id] = session;
                    _cache.Remove(info.Id);
                }

                _states[info.Id] = state;
                changed |= before != state;
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Signs in to a provider with your saved OAuth client through the browser, proves the tokens work by reading your
    /// links, and only then saves the refresh token. Returns the links; the caller saves the default link.
    /// </summary>
    /// <param name="providerId">The provider to sign in to.</param>
    /// <param name="openBrowser">Opens the provider's consent page.</param>
    /// <param name="ct">Cancels the sign-in.</param>
    /// <exception cref="InvalidOperationException">No OAuth client is saved for the provider.</exception>
    /// <exception cref="InvalidDataException">The secrets can't be read right now (<see cref="SecretsUnavailable.Is"/>); nothing was changed.</exception>
    /// <exception cref="SchedulingPortBusyException">Another app holds the redirect port.</exception>
    /// <exception cref="SchedulingAuthException">The sign-in was canceled or refused; nothing was saved.</exception>
    /// <exception cref="SchedulingUnavailableException">The provider couldn't be reached; nothing was saved.</exception>
    /// <exception cref="TimeoutException">The browser didn't come back within <see cref="ProviderSignIn.Timeout"/>.</exception>
    public async Task<IReadOnlyList<SchedulingLink>> SignInAsync(string providerId, Func<Uri, Task> openBrowser, CancellationToken ct)
    {
        var info = Find(providerId);
        int endings;
        lock (_gate)
        {
            endings = _endings.GetValueOrDefault(providerId);
        }

        var client = ReadClient(providerId) ?? throw new InvalidOperationException("No OAuth client is saved for this provider.");
        var refreshToken = await new ProviderSignIn(http, OAuthFor(info), client, openBrowser, time).RunAsync(ct);

        var session = StartSession(info, client, refreshToken, saved: false);
        IReadOnlyList<SchedulingLink> links;
        Session? old;
        try
        {
            links = session.Provider is ILinkSource source ? await source.GetLinksAsync(ct) : [];

            // Saved and installed together, so a sign-out or client removal either comes after (and ends it) or wins
            lock (_gate)
            {
                if (_endings.GetValueOrDefault(providerId) != endings)
                {
                    throw new SchedulingAuthException("The sign-in was canceled by a sign-out.");
                }

                session.Saver.Commit();
                old = _sessions.GetValueOrDefault(providerId);
                _sessions[providerId] = session;
                _states[providerId] = ProviderState.SignedIn;
                _cache[providerId] = new CachedLinks(time.GetUtcNow(), links);
                _waitUntil.Remove(providerId);
            }
        }
        catch
        {
            session.End();
            throw;
        }

        old?.End();
        log.Info("scheduling.signed-in", $"provider={providerId} links={links.Count}");
        Changed?.Invoke(this, EventArgs.Empty);
        return links;
    }

    /// <summary>
    /// Every signed-in provider's links, each provider's cached for 5 minutes.
    /// </summary>
    /// <remarks>
    /// A provider that refuses its sign-in is marked as needing reconnecting and adds nothing. One that can't be reached,
    /// or is in a rate-limit back-off, adds its cached links (or nothing); a <c>Retry-After</c> starts a back-off.
    /// </remarks>
    public async Task<IReadOnlyList<SchedulingLink>> GetLinksAsync(CancellationToken ct)
    {
        List<(ISchedulingProvider Provider, ILinkSource Source, CachedLinks? Cached)> work;
        lock (_gate)
        {
            work = [.. _sessions.Values.Where(s => s.Provider is ILinkSource).Select(s => (s.Provider, (ILinkSource)s.Provider, _cache.GetValueOrDefault(s.Provider.Id)))];
        }

        var all = new List<SchedulingLink>();
        foreach (var (provider, source, cached) in work)
        {
            if ((cached is not null && time.GetUtcNow() - cached.At < CacheFor) || IsWaiting(provider.Id))
            {
                all.AddRange(cached?.Links ?? []);
                continue;
            }

            try
            {
                var links = await source.GetLinksAsync(ct);
                lock (_gate)
                {
                    if (_sessions.GetValueOrDefault(provider.Id)?.Provider == provider)
                    {
                        _cache[provider.Id] = new CachedLinks(time.GetUtcNow(), links);
                    }
                }

                all.AddRange(links);
            }
            catch (SchedulingAuthException)
            {
                log.Info("scheduling.links.refused", $"provider={provider.Id}");
                MarkNeedsReconnect(provider.Id, provider);
            }
            catch (SchedulingUnavailableException ex)
            {
                log.Info("scheduling.links.unavailable", $"provider={provider.Id} error={ex.InnerException?.GetType().Name ?? ex.GetType().Name}");
                if (ex.RetryAfter is { } retryAfter)
                {
                    WaitFor(provider.Id, retryAfter);
                }

                all.AddRange(cached?.Links ?? []);
            }
        }

        return all;
    }

    /// <summary>
    /// Marks a signed-in provider as needing reconnecting (it refused its token or its refresh): it's dropped from
    /// <see cref="Connected"/> and gets no more calls until you sign in again. Its saved secrets and groups stay.
    /// </summary>
    public void MarkNeedsReconnect(string providerId) => MarkNeedsReconnect(providerId, null);

    /// <summary>Whether a provider is in a rate-limit back-off.</summary>
    public bool IsWaiting(string providerId)
    {
        lock (_gate)
        {
            return _waitUntil.TryGetValue(providerId, out var until) && time.GetUtcNow() < until;
        }
    }

    /// <summary>Starts a rate-limit back-off: <paramref name="retryAfter"/> when the provider gave a positive one, else 60 seconds.</summary>
    public void WaitFor(string providerId, TimeSpan? retryAfter)
    {
        var wait = retryAfter is { } r && r > TimeSpan.Zero ? r : DefaultWait;
        lock (_gate)
        {
            _waitUntil[providerId] = time.GetUtcNow() + wait;
        }
    }

    // Marks it only while it's still signed in (and, when given, still this instance, so a sign-out or new sign-in that
    // happened during a call isn't undone)
    private void MarkNeedsReconnect(string providerId, ISchedulingProvider? expected)
    {
        Session? session;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(providerId, out session) || (expected is not null && session.Provider != expected))
            {
                return;
            }

            _sessions.Remove(providerId);
            _cache.Remove(providerId);
            _states[providerId] = ProviderState.NeedsReconnect;
        }

        session.End();
        log.Info("scheduling.needs-reconnect", $"provider={providerId}");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Writes the secret change (with the session's rotated-token saving stopped first, so a refresh can't write its token
    // back afterwards), then drops the session, cache and back-off and sets the state; a failed write throws and changes nothing
    private void EndSession(string providerId, Action write, ProviderState state)
    {
        Session? session;
        lock (_gate)
        {
            // Counted before the write, so a sign-in still in the browser can't save its token after it
            _endings[providerId] = _endings.GetValueOrDefault(providerId) + 1;
            session = _sessions.GetValueOrDefault(providerId);
        }

        if (session is null)
        {
            write();
        }
        else
        {
            session.Saver.Retire(write);
        }

        lock (_gate)
        {
            if (_sessions.GetValueOrDefault(providerId) == session)
            {
                // Signing out with no client saved leaves it without a client
                var before = _states.GetValueOrDefault(providerId, ProviderState.NoClient);
                _sessions.Remove(providerId);
                _states[providerId] = before == ProviderState.NoClient ? ProviderState.NoClient : state;
            }

            _cache.Remove(providerId);
            _waitUntil.Remove(providerId);
        }

        session?.End();
    }

    // A provider with its own token source; a sign-in's refresh token isn't saved until it's committed
    private Session StartSession(SchedulingProviderInfo info, ProviderClient client, string refreshToken, bool saved)
    {
        var saver = new RefreshTokenSaver(tokens, log, info.Id, refreshToken, saved);
        var source = new ProviderTokens(http, OAuthFor(info), client, refreshToken, time, saver.Save);
        return new Session(info.Create(http, source.GetAccessTokenAsync, BaseFor(info)), source, saver);
    }

    // The saved client, or null when either value is missing; throws a SecretsUnavailable exception when they can't be read
    private ProviderClient? ReadClient(string providerId) =>
        NonEmpty(tokens.GetProviderSecret(providerId, ClientIdKey)) is { } clientId
        && NonEmpty(tokens.GetProviderSecret(providerId, ClientSecretKey)) is { } clientSecret
            ? new ProviderClient(clientId, clientSecret)
            : null;

    private SchedulingProviderInfo Find(string providerId) =>
        Providers.FirstOrDefault(p => p.Id == providerId) ?? throw new ArgumentException("Leaf Calendar doesn't know this scheduling provider.", nameof(providerId));

    // The provider's hosted API, or the fake root's folder for it
    private Uri BaseFor(SchedulingProviderInfo info) => fakeRoot is null ? info.DefaultBase : new Uri(fakeRoot, info.Id + "/");

    // The provider's OAuth data; under the fake root the authorize and token paths move into its folder (the redirect stays)
    private SchedulingOAuth OAuthFor(SchedulingProviderInfo info)
    {
        if (fakeRoot is null)
        {
            return info.OAuth;
        }

        var folder = BaseFor(info);
        return info.OAuth with
        {
            Authorize = new Uri(folder, info.OAuth.Authorize.AbsolutePath.TrimStart('/')),
            Token = new Uri(folder, info.OAuth.Token.AbsolutePath.TrimStart('/')),
        };
    }

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private sealed record CachedLinks(DateTimeOffset At, IReadOnlyList<SchedulingLink> Links);

    // One signed-in provider and the token source and saver behind it
    private sealed record Session(ISchedulingProvider Provider, ProviderTokens Tokens, RefreshTokenSaver Saver)
    {
        // Stops saving rotated tokens and frees the token source
        public void End()
        {
            Saver.Retire(null);
            Tokens.Dispose();
        }
    }

    // Saves a provider's rotated refresh tokens. During a sign-in it only remembers the newest until Commit; after Retire
    // it saves nothing. A failed rotated save is logged by type and never thrown into the API call.
    private sealed class RefreshTokenSaver(ITokenStore tokens, AppLog log, string providerId, string refreshToken, bool saved)
    {
        private readonly Lock _gate = new();
        private string _latest = refreshToken;
        private bool _saving = saved;
        private bool _retired;

        public void Save(string rotated)
        {
            lock (_gate)
            {
                _latest = rotated;
                if (!_saving || _retired)
                {
                    return;
                }

                try
                {
                    tokens.SetProviderSecret(providerId, RefreshTokenKey, rotated);
                }
                catch (Exception ex)
                {
                    log.Info("scheduling.token.save-failed", $"provider={providerId} error={ex.GetType().Name}");
                }
            }
        }

        // Saves the newest refresh token (throws when it can't) and saves later rotations as they come
        public void Commit()
        {
            lock (_gate)
            {
                tokens.SetProviderSecret(providerId, RefreshTokenKey, _latest);
                _saving = true;
            }
        }

        // Runs the secret change, when given, then stops saving; a failed change throws and leaves saving on
        public void Retire(Action? write)
        {
            lock (_gate)
            {
                write?.Invoke();
                _retired = true;
            }
        }
    }
}
