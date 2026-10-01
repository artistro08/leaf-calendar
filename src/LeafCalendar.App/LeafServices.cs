using System.Globalization;
using LeafCalendar.App.Interop;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Editing;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.Hosting;
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
    static readonly HttpClient FakeBrowser = new();

    readonly HttpClient _http;

    /// <summary>Creates services for a profile under the package's local folder.</summary>
    public LeafServices(LaunchOptions options, string localFolder)
    {
        Options  = options;
        Time     = options.Now is { } now ? new ShiftedTimeProvider(TimeProvider.System, now) : TimeProvider.System;
        Paths    = new LeafPaths(localFolder, options.Profile);
        Log      = new AppLog(Paths.LogDirectory, Time);
        Database = new LeafDatabase(Paths.DatabasePath);
        Database.Migrate();

        // Local Edits (work offline; every edit and conflict answer nudges the sync loop to send it)
        Editor    = new EventEditor(Database, Time);
        Conflicts = new ConflictResolver(Database, Time);
        Editor.Changed    += (_, _) => Google?.Loop.TriggerNow();
        Conflicts.Changed += (_, _) => Google?.Loop.TriggerNow();

        // Global Shortcuts (registered by the tray once its window exists)
        Shortcuts = new GlobalShortcuts(Log);

        Tokens = new CredentialLockerTokenStore(options.Profile);
        _http  = new HttpClient(new GoogleRetryHandler(Time) { InnerHandler = new SocketsHttpHandler() });

        // Sync Triggers
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        PowerManager.SystemSuspendStatusChanged += OnSuspendStatusChanged;

        Endpoints = options.FakeGoogle is { } fake ? GoogleEndpoints.ForFake(fake) : GoogleEndpoints.Default;
        Google    = CreateGoogle();
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

    /// <summary>Google services, or null before the OAuth client is set up.</summary>
    public GoogleServices? Google { get; private set; }

    /// <summary>Raised after <see cref="ReloadGoogleAsync"/> replaces <see cref="Google"/>.</summary>
    public event EventHandler? GoogleChanged;

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

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
        PowerManager.SystemSuspendStatusChanged -= OnSuspendStatusChanged;

        if (Google is { } google)
        {
            await google.DisposeAsync();
        }

        _http.Dispose();
    }

    /// <summary>
    /// Opens Google's sign-in page. In fake-Google mode Leaf plays the browser itself. It doesn't wait,
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
    /// Opens a link from an event, a Join button, or "Email guests". Only <c>https</c>, the meeting app schemes, and
    /// <c>mailto</c> pass (spec 4.5); anything else is logged by scheme and dropped. In fake-Google mode nothing opens:
    /// the address is appended to <c>launched.txt</c> in the profile folder, where UI tests read it. A launch that
    /// fails is logged (scheme and error only) and reported as false, never thrown.
    /// </summary>
    public async Task<bool> LaunchAsync(Uri uri)
    {
        if (!uri.IsAbsoluteUri || (!LinkSafety.CanLaunch(uri) && uri.Scheme != Uri.UriSchemeMailto))
        {
            Log.Info("link.blocked", $"scheme={(uri.IsAbsoluteUri ? uri.Scheme : "relative")}");
            return false;
        }

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
    /// Opens one of Leaf's own folders (the log folder, from Settings › About) in File Explorer. It's Leaf's only folder
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

    GoogleServices? CreateGoogle()
    {
        if (Tokens.GetClientCredentials() is not { } credentials)
        {
            return null;
        }

        var google = new GoogleServices(_http, credentials, Tokens, Database, Log, Time, Endpoints);
        google.Loop.Start();
        return google;
    }

    void OnNetworkStatusChanged(object sender) => Google?.Loop.TriggerNow();

    void OnSuspendStatusChanged(object? sender, object e)
    {
        if (PowerManager.SystemSuspendStatus is SystemSuspendStatus.AutoResume or SystemSuspendStatus.ManualResume)
        {
            Google?.Loop.TriggerNow();
        }
    }
}
