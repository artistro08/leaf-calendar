using System.Globalization;
using System.Net;
using LeafCalendar.App.Interop;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Editing;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.Hosting;
using LeafCalendar.Core.Places;
using LeafCalendar.Core.Scheduling;
using LeafCalendar.Core.Sunsama;
using Microsoft.Windows.System.Power;
using Windows.Networking.Connectivity;
using Windows.System;

namespace LeafCalendar.App;

/// <summary>
/// App composition root: paths, log, database, secrets, HTTP, and (once an OAuth client is saved)
/// the Google services and sync loop.
/// </summary>
public sealed class LeafServices : IAsyncDisposable
{
    // Test Mode "Browser": follows the fake Google's sign-in redirect itself
    private static readonly HttpClient FakeBrowser = new();

    // How often secrets that can't be read right now are tried again
    private static readonly TimeSpan SecretsRetry = TimeSpan.FromSeconds(30);

    // The longest one scheduling provider call may hold up a Google sync pass
    private static readonly TimeSpan SchedulingTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _http;
    private readonly HttpClient _schedulingHttp;
    private ITimer? _secretsRetry;

    /// <summary>Creates services for a profile under the package's local folder.</summary>
    public LeafServices(LaunchOptions options, string localFolder)
    {
        Options = options;
        Time = options.Now is { } now ? new ShiftedTimeProvider(TimeProvider.System, now) : TimeProvider.System;
        Paths = new LeafPaths(localFolder, options.Profile);
        Log = new AppLog(Paths.LogDirectory, Time);
        Database = new LeafDatabase(Paths.DatabasePath);
        Database.Migrate();

        // Local Edits (work offline; every edit and conflict answer nudges the sync loop to send it, and so do deletes once their undo window passes)
        Editor = new EventEditor(Database, Time);
        Conflicts = new ConflictResolver(Database, Time);
        Editor.Changed += (_, _) => Google?.Loop.TriggerNow();
        Editor.DeletesDue += (_, _) => Google?.Loop.TriggerNow();
        Conflicts.Changed += (_, _) => Google?.Loop.TriggerNow();

        // Global Shortcuts (registered by the tray once its window exists)
        Shortcuts = new GlobalShortcuts(Log);

        // Secrets Live In The Profile Folder (removed with the app); the first start after the update moves them out of the Credential Locker
        Tokens = TokenStoreMigration.Open(new ProtectedFileTokenStore(Paths.ProfileDirectory), () => new CredentialLockerTokenStore(options.Profile), Log);
        _http = new HttpClient(new GoogleRetryHandler(Time) { InnerHandler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All } });

        // Google only compresses its answers when the user agent says "gzip" too
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LeafCalendar (gzip)");

        // Scheduling Providers (built from saved keys now and when unreadable secrets recover; under --fake-google they talk to the fake too).
        // Their own client: no Google retries and a short timeout, since the booking check runs inside every Google sync pass.
        _schedulingHttp = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = SchedulingTimeout };
        _schedulingHttp.DefaultRequestHeaders.UserAgent.ParseAdd("LeafCalendar");
        Scheduling = new SchedulingHub(_schedulingHttp, Tokens, Log, Time, options.FakeGoogle);
        Bookings = new BookingWatcher(Scheduling, Database, Log, Time);

        // Sunsama (read-only task times; under --fake-google it talks to the fake under sunsama/)
        Sunsama = new SunsamaService(_schedulingHttp, Tokens, Database, Log, Time, options.FakeGoogle is { } root ? SunsamaEndpoints.ForFake(root) : SunsamaEndpoints.Default);
        LoadScheduling();

        // Sync Triggers
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        PowerManager.SystemSuspendStatusChanged += OnSuspendStatusChanged;

        Endpoints = options.FakeGoogle is { } fake ? GoogleEndpoints.ForFake(fake) : GoogleEndpoints.Default;
        Google = CreateGoogle();

        // Location Suggestions (the user's own Maps API key; not tied to the OAuth client, so it outlives a client change)
        Places = new PlaceSearch(_http, Tokens, Log, Endpoints);
    }

    /// <summary>Real Google, or the fake Google from <c>--fake-google</c>.</summary>
    public GoogleEndpoints Endpoints { get; }

    /// <summary>Launch options.</summary>
    public LaunchOptions Options { get; }

    /// <summary>Profile folders.</summary>
    public LeafPaths Paths { get; }

    /// <summary>Clock (in fake-Google mode, <c>--now</c> starts it at a chosen instant).</summary>
    public TimeProvider Time { get; }

    /// <summary>Log.</summary>
    public AppLog Log { get; }

    /// <summary>Database.</summary>
    public LeafDatabase Database { get; }

    /// <summary>Secrets.</summary>
    public ITokenStore Tokens { get; }

    /// <summary>Every change to events (local first, then the outbox).</summary>
    public EventEditor Editor { get; }

    /// <summary>Conflict answers and outbox counts.</summary>
    public ConflictResolver Conflicts { get; }

    /// <summary>Global shortcuts (registered once the tray icon exists).</summary>
    public GlobalShortcuts Shortcuts { get; }

    /// <summary>The connected scheduling providers (Cal.com and others) and their cached links.</summary>
    public SchedulingHub Scheduling { get; }

    /// <summary>Checks saved times for bookings after each sync pass.</summary>
    public BookingWatcher Bookings { get; }

    /// <summary>The Sunsama sign-in and the task times fetched after each sync pass.</summary>
    public SunsamaService Sunsama { get; }

    /// <summary>Google Maps place search for the editor's Location box and <c>places.search</c> (on only while <see cref="PlaceSearch.IsOn"/>).</summary>
    public PlaceSearch Places { get; }

    /// <summary>Google services, or null before the OAuth client is set up.</summary>
    public GoogleServices? Google { get; private set; }

    /// <summary>
    /// True while Settings › Connected apps is on but another program holds the local API's pipe name, so no app can
    /// connect (local API spec 3.3). Set by the App, read by Settings.
    /// </summary>
    public bool ApiNameTaken
    {
        get;
        set
        {
            field = value;
            ApiStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised when <see cref="ApiNameTaken"/> is set, on the thread that set it.</summary>
    public event EventHandler? ApiStateChanged;

    /// <summary>
    /// True while the saved secrets couldn't be read at startup (or a reload) and a timer is trying again, so
    /// <see cref="Google"/> is null for that reason, not because Leaf Calendar is offline or not set up.
    /// </summary>
    public bool SecretsUnavailable => Volatile.Read(ref _secretsRetry) is not null;

    /// <summary>Raised after <see cref="ReloadGoogleAsync"/> replaces <see cref="Google"/>.</summary>
    public event EventHandler? GoogleChanged;

    /// <summary>
    /// The browser came back from a sign-in (Google signed in, or answered with a refusal or an error), so the window that
    /// asked can come back to the front. Not raised when Leaf Calendar canceled it, when it timed out (the user has moved on), or
    /// when Google couldn't be reached. Raised on whatever thread the sign-in finished on.
    /// </summary>
    public event EventHandler? SignInReturned;

    /// <summary>
    /// Signs a Google account in through the browser (<see cref="SignInFlow.RunAsync(string?, string?, CancellationToken)"/>),
    /// then raises <see cref="SignInReturned"/> when the browser came back.
    /// </summary>
    public async Task<Account> SignInAsync(GoogleServices google, string? loginHint, string? expectedAccountId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(google);

        var returned = false;
        try
        {
            var account = await google.CreateSignIn(OpenSignInPageAsync).RunAsync(loginHint, expectedAccountId, ct);
            returned = true;
            return account;
        }
        catch (SignInException ex) when (ex is not SignInTimeoutException && !ct.IsCancellationRequested)
        {
            returned = true;
            throw;
        }
        finally
        {
            if (returned)
            {
                SignInReturned?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Rebuilds Google services after the OAuth client changes.</summary>
    public async Task ReloadGoogleAsync()
    {
        try
        {
            if (Google is { } old)
            {
                await old.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            // A failed teardown must not leave the app without Google services
            Log.Error("google.dispose.failed", ex);
        }

        Google = CreateGoogle();
        GoogleChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>True when the profile has a Google account saved (reads the database).</summary>
    public bool HasAccount()
    {
        using var conn = Database.Open();
        return AccountStore.GetAll(conn).Count > 0;
    }

    /// <summary>
    /// True when an OAuth client is saved. When the secrets can't be read right now (<see cref="InvalidDataException"/>),
    /// it's taken as saved, so a passing failure never sends a signed-in user back to setup.
    /// </summary>
    public bool HasOAuthClient()
    {
        try
        {
            return Tokens.GetClientCredentials() is not null;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
        PowerManager.SystemSuspendStatusChanged -= OnSuspendStatusChanged;
        Interlocked.Exchange(ref _secretsRetry, null)?.Dispose();

        if (Google is { } google)
        {
            await google.DisposeAsync();
        }

        Sunsama.Dispose();
        _http.Dispose();
        _schedulingHttp.Dispose();
    }

    /// <summary>
    /// Opens Google's sign-in page. In fake-Google mode Leaf Calendar plays the browser itself. It doesn't wait,
    /// because the redirect only completes once sign-in is listening for it.
    /// </summary>
    /// <exception cref="SignInException">The browser couldn't be opened.</exception>
    public async Task OpenSignInPageAsync(Uri uri)
    {
        if (Options.FakeGoogle is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var response = await FakeBrowser.GetAsync(uri);
                }
                catch (HttpRequestException ex)
                {
                    Log.Error("signin.fake-browser.failed", ex);
                }
            });
            return;
        }

        if (!await Launcher.LaunchUriAsync(uri))
        {
            throw new SignInException("Couldn't open your browser. Try again.");
        }
    }

    /// <summary>
    /// Opens a link from an event, a Join button, "Email guests", or a location. Only <c>https</c>, the meeting app
    /// schemes, <c>mailto</c>, and a plain <c>tel</c> number (a location's phone number) pass (spec 4.5); anything else
    /// is logged by scheme and dropped. In fake-Google mode nothing opens: the address is appended to <c>launched.txt</c> in the profile folder, where UI tests read it. A launch that
    /// fails is logged (scheme and error only) and reported as false, never thrown.
    /// </summary>
    public async Task<bool> LaunchAsync(Uri uri)
    {
        if (!uri.IsAbsoluteUri || (!LinkSafety.CanLaunch(uri) && uri.Scheme != Uri.UriSchemeMailto && !LinkSafety.IsCallLink(uri)))
        {
            Log.Info("link.blocked", $"scheme={(uri.IsAbsoluteUri ? uri.Scheme : "relative")}");
            return false;
        }

        return await OpenAsync(uri);
    }

    /// <summary>
    /// Opens the link an app sent to bring you back to it after you allowed it (approval window spec 4). The link was
    /// checked by <see cref="Core.Api.ApiHello.ReadReturnUrl"/> (an app's own scheme only), so <see cref="LinkSafety"/>,
    /// which allows only web and meeting links, doesn't apply. Recorded, logged and reported like <see cref="LaunchAsync"/>.
    /// </summary>
    public Task<bool> LaunchReturnLinkAsync(Uri link) => OpenAsync(link);

    /// <summary>
    /// Opens a Sunsama task in the Sunsama app (<c>sunsama://action/details/&lt;id&gt;</c>), or Sunsama's home page in the browser
    /// when no app handles <c>sunsama://</c> (spec 2026-10-16, Open in Sunsama). The link is built only by
    /// <see cref="SunsamaLinks.TaskLink"/>, so <see cref="LinkSafety"/> doesn't apply. In fake-Google mode the link is appended
    /// to <c>launched.txt</c>. Never throws; false when nothing opened.
    /// </summary>
    public async Task<bool> LaunchSunsamaAsync(string taskId)
    {
        if (SunsamaLinks.TaskLink(taskId) is not { } link)
        {
            Log.Info("sunsama.link.blocked");
            return false;
        }

        if (Options.FakeGoogle is not null)
        {
            return await OpenAsync(link);
        }

        try
        {
            return await Launcher.LaunchUriAsync(link, new LauncherOptions { FallbackUri = SunsamaLinks.Fallback });
        }
        catch (Exception ex)
        {
            // A launch failure is never thrown; the type only
            Log.Info("sunsama.link.failed", string.Create(CultureInfo.InvariantCulture, $"error={ex.GetType().Name} hresult=0x{ex.HResult:X8}"));
            return false;
        }
    }

    /// <summary>
    /// Raises <see cref="SignInReturned"/> for a sign-in that isn't Google's (Sunsama's), so the window that asked comes
    /// back to the front when the browser came back.
    /// </summary>
    public void NotifySignInReturned() => SignInReturned?.Invoke(this, EventArgs.Empty);

    // Opens a checked link (its canonical address); in fake-Google mode it's appended to launched.txt instead. Never throws.
    private async Task<bool> OpenAsync(Uri uri)
    {
        try
        {
            if (Options.FakeGoogle is not null)
            {
                await File.AppendAllTextAsync(Path.Combine(Paths.ProfileDirectory, "launched.txt"), uri.OriginalString + Environment.NewLine);
                return true;
            }

            // Open the canonical address (what the details panel shows), not the event's own spelling of it
            return await Launcher.LaunchUriAsync(new Uri(uri.AbsoluteUri));
        }
        catch (Exception ex)
        {
            // The address and message may hold event content, so only the scheme and the error type are logged
            Log.Info("link.launch.failed", string.Create(CultureInfo.InvariantCulture, $"scheme={uri.Scheme} error={ex.GetType().Name} hresult=0x{ex.HResult:X8}"));
            return false;
        }
    }

    /// <summary>
    /// Opens one of Leaf Calendar's own folders (the log folder, from Settings › About) in File Explorer. It's Leaf Calendar's only folder
    /// launch, and never takes a path from event, contact, or calendar content. In fake-Google mode nothing opens:
    /// <c>folder:&lt;path&gt;</c> is appended to <c>launched.txt</c>, as <see cref="LaunchAsync"/> does for links. A launch that
    /// fails is logged by error type and reported as false, never thrown.
    /// </summary>
    public async Task<bool> OpenFolderAsync(string path)
    {
        try
        {
            if (Options.FakeGoogle is not null)
            {
                await File.AppendAllTextAsync(Path.Combine(Paths.ProfileDirectory, "launched.txt"), "folder:" + path + Environment.NewLine);
                return true;
            }

            return await Launcher.LaunchFolderPathAsync(path);
        }
        catch (Exception ex)
        {
            Log.Info("folder.launch.failed", string.Create(CultureInfo.InvariantCulture, $"error={ex.GetType().Name} hresult=0x{ex.HResult:X8}"));
            return false;
        }
    }

    /// <summary>
    /// Opens a file shipped in Leaf Calendar's package (the third-party notices, from Settings › About) in its default app.
    /// <paramref name="appUri"/> is a fixed <c>ms-appx:///</c> address, never one from event, contact, or calendar
    /// content. In fake-Google mode nothing opens: <c>file:&lt;uri&gt;</c> is appended to <c>launched.txt</c>, as
    /// <see cref="LaunchAsync"/> does for links. A launch that fails is logged by error type and reported as false, never thrown.
    /// </summary>
    public async Task<bool> OpenPackageFileAsync(Uri appUri)
    {
        try
        {
            if (Options.FakeGoogle is not null)
            {
                await File.AppendAllTextAsync(Path.Combine(Paths.ProfileDirectory, "launched.txt"), "file:" + appUri.OriginalString + Environment.NewLine);
                return true;
            }

            var file = await Windows.Storage.StorageFile.GetFileFromApplicationUriAsync(appUri);
            return await Launcher.LaunchFileAsync(file);
        }
        catch (Exception ex)
        {
            Log.Info("file.launch.failed", string.Create(CultureInfo.InvariantCulture, $"error={ex.GetType().Name} hresult=0x{ex.HResult:X8}"));
            return false;
        }
    }

    private GoogleServices? CreateGoogle()
    {
        OAuthClientCredentials? credentials;
        try
        {
            credentials = Tokens.GetClientCredentials();
        }
        catch (InvalidDataException ex)
        {
            // Secrets Can't Be Read Right Now (DPAPI failing for a passing reason): no Google until they can, never
            // "signed out"; a timer tries again. The error type only.
            Log.Info("auth.secrets.unavailable", $"error={ex.InnerException?.GetType().Name}");
            _secretsRetry ??= Time.CreateTimer(_ => _ = RetryGoogleAsync(), null, SecretsRetry, SecretsRetry);
            return null;
        }

        if (credentials is null)
        {
            return null;
        }

        var google = new GoogleServices(_http, credentials, Tokens, Database, Log, Time, Endpoints, afterSync: AfterSyncAsync);
        google.Loop.Start();
        return google;
    }

    // After Each Google Sync Pass: the booking check, and the Sunsama fetch started beside it, so both follow SyncLoop's
    // pace but a slow Sunsama (a month is 84 reads) never holds up the next Google pass or the edits it sends
    private async Task AfterSyncAsync(CancellationToken ct)
    {
        _ = SyncSunsamaAsync(ct);
        await Bookings.CheckAsync(ct);
    }

    // Started and not awaited, so nothing may escape; the type only
    private async Task SyncSunsamaAsync(CancellationToken ct)
    {
        try
        {
            await Sunsama.SyncAsync(ct);
        }
        catch (Exception ex)
        {
            Log.Info("sunsama.sync.stopped", $"error={ex.GetType().Name}");
        }
    }

    // Secrets Readable Again: Google services are built, and everything listening reattaches (GoogleChanged, any thread)
    private async Task RetryGoogleAsync()
    {
        try
        {
            Tokens.GetClientCredentials();
        }
        catch (InvalidDataException)
        {
            return;
        }

        // Only one tick goes on
        if (Interlocked.Exchange(ref _secretsRetry, null) is not { } timer)
        {
            return;
        }

        timer.Dispose();
        LoadScheduling();
        try
        {
            if (Google is null)
            {
                await ReloadGoogleAsync();
            }
        }
        catch (Exception ex)
        {
            // A timer callback, so nothing may escape
            Log.Error("google.retry.failed", ex);
        }
    }

    // Builds the signed-in scheduling providers and Sunsama from their saved secrets; at startup and from a timer, so nothing may escape
    private void LoadScheduling()
    {
        try
        {
            Scheduling.Load();
        }
        catch (Exception ex)
        {
            Log.Info("scheduling.load.failed", $"error={ex.GetType().Name}");
        }

        try
        {
            Sunsama.Load();
        }
        catch (Exception ex)
        {
            Log.Info("sunsama.load.failed", $"error={ex.GetType().Name}");
        }
    }

    private void OnNetworkStatusChanged(object sender) => RetryNow();

    private void OnSuspendStatusChanged(object? sender, object e)
    {
        if (PowerManager.SystemSuspendStatus is SystemSuspendStatus.AutoResume or SystemSuspendStatus.ManualResume)
        {
            RetryNow();
        }
    }

    // A Reconnect Or Resume: edits backing off after a failed try go on this sync
    private void RetryNow()
    {
        // A system event handler, so nothing may escape; the type only, never content
        try
        {
            using var conn = Database.Open();
            OutboxStore.ClearBackoff(conn);
        }
        catch (Exception ex)
        {
            Log.Info("outbox.backoff.clear.failed", $"error={ex.GetType().Name}");
        }

        Google?.Loop.TriggerNow();
    }
}
