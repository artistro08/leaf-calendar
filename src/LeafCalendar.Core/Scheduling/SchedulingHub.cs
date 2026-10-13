using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Settings;

namespace LeafCalendar.Core.Scheduling;

/// <summary>Whether a provider can be used.</summary>
public enum ProviderState
{
    /// <summary>No key is saved (or it couldn't be loaded yet).</summary>
    NotConnected,

    /// <summary>A key is saved and the provider is ready.</summary>
    Connected,

    /// <summary>The provider refused the saved key; no more calls until you connect again.</summary>
    NeedsReconnect,
}

/// <summary>
/// Holds the connected scheduling providers (spec 3.3): built from saved keys at startup, on connect, and dropped on
/// disconnect, with each one's links cached and its rate-limit back-off.
/// </summary>
/// <remarks>
/// Used from the UI thread and the sync thread: one lock guards the providers, states, caches and back-offs, and it is
/// never held across an <c>await</c>. Keys go only through <see cref="ITokenStore"/> and are never logged; the log gets
/// provider IDs, counts and exception type names. A server address must be https (checked here, since
/// <see cref="CalComProvider"/> doesn't); only the UI tests' fake root, when given, may be http.
/// </remarks>
/// <param name="http">The client providers send through.</param>
/// <param name="tokens">Where provider keys are saved.</param>
/// <param name="log">The app log.</param>
/// <param name="time">The clock for the link cache and back-offs.</param>
/// <param name="fakeRoot">The UI tests' fake server; each provider uses its <c>{id}/</c> folder when no server is set.</param>
public sealed class SchedulingHub(HttpClient http, ITokenStore tokens, AppLog log, TimeProvider time, Uri? fakeRoot = null)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(60);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, ISchedulingProvider> _providers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProviderState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CachedLinks> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _waitUntil = new(StringComparer.Ordinal);

    /// <summary>Raised when a provider connects, disconnects or needs reconnecting. Raised on any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>The connected providers (ones that need reconnecting are left out).</summary>
    public IReadOnlyList<ISchedulingProvider> Connected
    {
        get
        {
            lock (_gate)
            {
                return [.. _providers.Values];
            }
        }
    }

    /// <summary>The first invalid field's message, or null when every value is acceptable.</summary>
    /// <remarks>A required field must be non-empty and valid; an optional one is checked only when it isn't empty.</remarks>
    public static string? Check(SchedulingProviderInfo info, IReadOnlyDictionary<string, string> values)
    {
        foreach (var field in info.Fields)
        {
            var value = values.GetValueOrDefault(field.Key) ?? "";
            if (value.Trim().Length == 0 ? field.Required : !field.IsValid(value))
            {
                return field.InvalidMessage;
            }
        }

        return null;
    }

    /// <summary>Whether a provider is connected, needs reconnecting, or isn't connected.</summary>
    public ProviderState StateOf(string providerId)
    {
        lock (_gate)
        {
            return _states.GetValueOrDefault(providerId, ProviderState.NotConnected);
        }
    }

    /// <summary>The connected provider with this ID, or null (also when it needs reconnecting).</summary>
    public ISchedulingProvider? Get(string providerId)
    {
        lock (_gate)
        {
            return _providers.GetValueOrDefault(providerId);
        }
    }

    /// <summary>
    /// Builds every provider with a saved key, at startup and when the secrets become readable again.
    /// </summary>
    /// <remarks>
    /// A provider needs a saved secret for every required secret field; its server comes from <paramref name="settings"/>
    /// (empty means its hosted API, or the fake root's <c>{id}/</c> folder in UI tests). A saved server that isn't a valid
    /// https address leaves it out. When the secrets can't be read right now, that provider is left as it was (logged as
    /// <c>scheduling.load.secrets-unavailable</c>) and nothing is written, so a later call retries. A provider that needs
    /// reconnecting stays that way until you connect again.
    /// </remarks>
    public void Load(IReadOnlyList<ProviderSettings> settings)
    {
        var changed = false;
        foreach (var info in SchedulingProviders.All)
        {
            if (StateOf(info.Id) == ProviderState.NeedsReconnect)
            {
                continue;
            }

            Dictionary<string, string> values;
            try
            {
                values = ReadSecrets(info);
            }
            catch (Exception ex) when (SecretsUnavailable.Is(ex))
            {
                log.Info("scheduling.load.secrets-unavailable", $"provider={info.Id}");
                continue;
            }

            var provider = Build(info, values, settings.FirstOrDefault(s => s.ProviderId == info.Id));
            lock (_gate)
            {
                var before = _states.GetValueOrDefault(info.Id, ProviderState.NotConnected);
                _cache.Remove(info.Id);
                if (provider is null)
                {
                    _providers.Remove(info.Id);
                    _states.Remove(info.Id);
                }
                else
                {
                    _providers[info.Id] = provider;
                    _states[info.Id] = ProviderState.Connected;
                }

                changed |= before != _states.GetValueOrDefault(info.Id, ProviderState.NotConnected);
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Connects a provider: checks the values, proves the key works by reading your links, then saves the secrets and
    /// returns the links. The caller saves the provider's settings (server, default link).
    /// </summary>
    /// <exception cref="ArgumentException">A value is invalid (the message is the field's; the UI calls <see cref="Check"/> first).</exception>
    /// <exception cref="SchedulingAuthException">The provider refused the key; nothing was saved.</exception>
    /// <exception cref="SchedulingUnavailableException">The provider couldn't be reached; nothing was saved.</exception>
    public async Task<IReadOnlyList<SchedulingLink>> ConnectAsync(SchedulingProviderInfo info, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        if (Check(info, values) is { } message)
        {
            throw new ArgumentException(message, nameof(values));
        }

        var provider = info.Create(http, values, BaseFor(info, values.GetValueOrDefault("server") ?? ""));
        IReadOnlyList<SchedulingLink> links = provider is ILinkSource source ? await source.GetLinksAsync(ct) : [];

        foreach (var field in info.Fields.Where(f => f.Secret))
        {
            var value = (values.GetValueOrDefault(field.Key) ?? "").Trim();
            if (value.Length > 0)
            {
                tokens.SetProviderSecret(info.Id, field.Key, value);
            }
        }

        lock (_gate)
        {
            _providers[info.Id] = provider;
            _states[info.Id] = ProviderState.Connected;
            _cache[info.Id] = new CachedLinks(time.GetUtcNow(), links);
            _waitUntil.Remove(info.Id);
        }

        log.Info("scheduling.connected", $"provider={info.Id} links={links.Count}");
        Changed?.Invoke(this, EventArgs.Empty);
        return links;
    }

    /// <summary>
    /// Disconnects a provider: removes its saved secrets and drops it and its cached links. The caller removes its
    /// settings and its saved groups (<see cref="Data.ShareGroupStore.ClearProvider"/>).
    /// </summary>
    public void Disconnect(string providerId)
    {
        tokens.RemoveProviderSecrets(providerId);
        lock (_gate)
        {
            _providers.Remove(providerId);
            _states.Remove(providerId);
            _cache.Remove(providerId);
            _waitUntil.Remove(providerId);
        }

        log.Info("scheduling.disconnected", $"provider={providerId}");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Every connected provider's links, each provider's cached for 5 minutes.
    /// </summary>
    /// <remarks>
    /// A provider that refuses its key is marked as needing reconnecting and adds nothing. One that can't be reached, or is
    /// in a rate-limit back-off, adds its cached links (or nothing); a <c>Retry-After</c> starts a back-off.
    /// </remarks>
    public async Task<IReadOnlyList<SchedulingLink>> GetLinksAsync(CancellationToken ct)
    {
        List<(ISchedulingProvider Provider, ILinkSource Source, CachedLinks? Cached)> work;
        lock (_gate)
        {
            work = [.. _providers.Values.OfType<ILinkSource>().Select(s => ((ISchedulingProvider)s, s, _cache.GetValueOrDefault(((ISchedulingProvider)s).Id)))];
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
                    if (_providers.GetValueOrDefault(provider.Id) == provider)
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
    /// Marks a connected provider as needing reconnecting (it refused its key): it's dropped from <see cref="Connected"/>
    /// and gets no more calls until you connect again. Its saved key and groups stay.
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

    // Marks it only while it's still connected (and, when given, still this instance, so a disconnect or reconnect
    // that happened during a call isn't undone)
    private void MarkNeedsReconnect(string providerId, ISchedulingProvider? expected)
    {
        lock (_gate)
        {
            if (!_providers.TryGetValue(providerId, out var current) || (expected is not null && current != expected))
            {
                return;
            }

            _providers.Remove(providerId);
            _cache.Remove(providerId);
            _states[providerId] = ProviderState.NeedsReconnect;
        }

        log.Info("scheduling.needs-reconnect", $"provider={providerId}");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // The saved secret fields (empty when there's no complete set); throws a SecretsUnavailable exception when they can't be read
    private Dictionary<string, string> ReadSecrets(SchedulingProviderInfo info)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in info.Fields.Where(f => f.Secret))
        {
            var value = tokens.GetProviderSecret(info.Id, field.Key);
            if (string.IsNullOrEmpty(value) && field.Required)
            {
                return [];
            }

            values[field.Key] = value ?? "";
        }

        return values;
    }

    // The provider from its saved secrets and settings, or null when a secret is missing or a value is invalid
    private ISchedulingProvider? Build(SchedulingProviderInfo info, Dictionary<string, string> values, ProviderSettings? saved)
    {
        if (values.Count == 0)
        {
            return null;
        }

        values["server"] = saved?.Server ?? "";
        if (Check(info, values) is not null)
        {
            log.Info("scheduling.load.invalid", $"provider={info.Id}");
            return null;
        }

        return info.Create(http, values, BaseFor(info, values["server"]));
    }

    // The typed server (already checked as https), else the fake root's folder for the provider, else its hosted API
    private Uri BaseFor(SchedulingProviderInfo info, string server) =>
        server.Trim().Length > 0 ? SchedulingProviders.ServerBase(server)
        : fakeRoot is null ? info.DefaultBase
        : new Uri(fakeRoot, info.Id + "/");

    private sealed record CachedLinks(DateTimeOffset At, IReadOnlyList<SchedulingLink> Links);
}
