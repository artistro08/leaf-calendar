using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Diagnostics;
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
        Paths    = new LeafPaths(localFolder, options.Profile);
        Log      = new AppLog(Paths.LogDirectory, Time);
        Database = new LeafDatabase(Paths.DatabasePath);
        Database.Migrate();
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

    /// <summary>Clock.</summary>
    public TimeProvider Time { get; } = TimeProvider.System;

    /// <summary>Log.</summary>
    public AppLog Log { get; }

    /// <summary>Database.</summary>
    public LeafDatabase Database { get; }

    /// <summary>Secrets.</summary>
    public ITokenStore Tokens { get; }

    /// <summary>Google services, or null before the OAuth client is set up.</summary>
    public GoogleServices? Google { get; private set; }

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
