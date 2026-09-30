using LeafCalendar.App.Interop;
using LeafCalendar.App.Views.Onboarding;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Hosting;
using LeafCalendar.Core.Sync;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Storage;

namespace LeafCalendar.App;

/// <summary>Leaf Calendar application entry.</summary>
public partial class App : Application
{
    MainWindow? _window;
    OnboardingWindow? _onboarding;
    LeafServices? _services;
    DispatcherQueueTimer? _probeTimer;
    AppLog? _log;

    /// <summary>Loads XAML resources and hooks crash logging.</summary>
    public App()
    {
        InitializeComponent();

        // Crash Logging
        UnhandledException                    += (_, e) => _log?.Error("app.unhandled", e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => _log?.Error("app.task.unobserved", e.Exception);
    }

    /// <inheritdoc />
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var options     = LaunchOptions.Parse([.. Environment.GetCommandLineArgs().Skip(1)]);
        var localFolder = ApplicationData.Current.LocalFolder.Path;

        // Log First, So A Startup Failure Is Recorded Before The Process Ends
        _log = new AppLog(new LeafPaths(localFolder, options.Profile).LogDirectory, TimeProvider.System);

        LeafServices services;
        try
        {
            services = new LeafServices(options, localFolder);
        }
        catch (Exception ex)
        {
            _log.Error("app.start.failed", ex);
            throw;
        }

        _log      = services.Log;
        _services = services;

        // First Run: onboarding shows instead of the main window until there's an OAuth client and an account
        if (OnboardingFlow.IsNeeded(services.Tokens.GetClientCredentials() is not null, services.HasAccount()))
        {
            _onboarding = new OnboardingWindow(services, () => ShowMainWindow(services));
            _onboarding.Closed += async (_, _) =>
            {
                _onboarding = null;

                // Left Setup Without An Account: the app is exiting, so the services go with it
                if (_window is null)
                {
                    _services = null;
                    await DisposeServicesAsync(services);
                }
            };
            _onboarding.Activate();
        }
        else
        {
            ShowMainWindow(services);
        }

        // Another Launch Of This Profile Was Redirected Here (see Program), so come to the front
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        Program.HandleActivations(() => dispatcher.TryEnqueue(BringToFront));
    }

    // Opens the main window (at launch, or when onboarding finishes). The services live as long as it does, unless the
    // tray probe keeps them.
    void ShowMainWindow(LeafServices services)
    {
        _window = new MainWindow(services);
        _window.Activate();

        if (services.Options.TrayProbe)
        {
            StartTrayProbe(services);
            return;
        }

        _window.Closed += async (_, _) =>
        {
            // Nothing to bring back: the services are going away with the window
            _window   = null;
            _services = null;
            await DisposeServicesAsync(services);
        };
    }

    // Brings onboarding forward while it's open; otherwise shows the main window again when the tray probe closed it,
    // then restores and foregrounds it
    void BringToFront()
    {
        if (_onboarding is not null)
        {
            _onboarding.BringToFront();
            return;
        }

        if (_services is null)
        {
            return;
        }

        if (_window is null)
        {
            _window         = new MainWindow(_services);
            _window.Closed += (_, _) => _window = null;
        }

        _window.BringToFront();
    }

    // Window close must not crash the process on a disposal failure, so log and carry on
    static async Task DisposeServicesAsync(LeafServices services)
    {
        try
        {
            await services.DisposeAsync();
        }
        catch (Exception ex)
        {
            services.Log.Error("app.dispose.failed", ex);
        }
    }

    // Tray Probe: closes the window once it has rendered, switches sync to tray mode, and trims
    // memory, so the memory budget test can measure tray-only mode before the real tray arrives
    // in Milestone 4.
    void StartTrayProbe(LeafServices services)
    {
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        _probeTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _probeTimer.Interval    = TimeSpan.FromSeconds(3);
        _probeTimer.IsRepeating = false;
        _probeTimer.Tick += (_, _) =>
        {
            _window?.Close();
            _window = null;

            if (services.Google is { } google)
            {
                google.Loop.Mode = SyncMode.Tray;
            }

            MemoryTrimmer.Trim();
        };
        _probeTimer.Start();
    }
}
