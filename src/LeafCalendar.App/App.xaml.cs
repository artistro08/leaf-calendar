using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using LeafCalendar.App.Interop;
using LeafCalendar.App.ViewModels;
using LeafCalendar.App.Views.Onboarding;
using LeafCalendar.App.Views.Settings;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Hosting;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Sync;
using LeafCalendar.Core.Tray;
using LeafCalendar.Core.Views;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.Storage;

namespace LeafCalendar.App;

/// <summary>
/// Leaf Calendar application entry. Leaf lives in the tray (spec 8.1): closing the main window keeps it running for
/// the tray icon, sync, and reminders, and only Quit ends it. Started by Windows at sign-in, it stays in the tray.
/// </summary>
/// <remarks>
/// The main window and Settings share one calendar view model, which the App creates when either opens and releases
/// once neither is open. Tray-only Leaf then polls every 60 seconds, runs in Windows efficiency mode, and trims its
/// memory (spec 3.4, 5.3).
/// </remarks>
[SuppressMessage("Design", "CA1001", Justification = "The App lives as long as the process; Quit disposes the tray icon, notifier, alerts, and view model.")]
public partial class App : Application
{
    MainWindow? _window;
    OnboardingWindow? _onboarding;
    LeafServices? _services;
    CalendarViewModel? _calendar;
    SettingsWindow? _hookedSettings;
    TrayIcon? _tray;
    SyncEngine? _attachedSync;
    DispatcherQueue _dispatcher = null!;
    DispatcherQueueTimer? _minuteTimer;
    DispatcherQueueTimer? _probeTimer;
    readonly LocalZoneWatcher _zone = new();
    AppLog? _log;
    bool _trayStarted;

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
        var options     = Program.Options;
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

        _log        = services.Log;
        _services   = services;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        // First Run: onboarding shows instead of the main window until there's an OAuth client and an account; the tray starts when it's done
        if (OnboardingFlow.IsNeeded(services.Tokens.GetClientCredentials() is not null, services.HasAccount()))
        {
            _onboarding = new OnboardingWindow(services, () =>
            {
                StartTray(services);
                ShowMainWindow();
            });
            _onboarding.Closed += async (_, _) =>
            {
                _onboarding = null;

                // Left Setup Without An Account: the app is exiting, so the services go with it
                if (!_trayStarted)
                {
                    _services = null;
                    await DisposeServicesAsync(services);
                }
            };
            _onboarding.Activate();
        }
        else
        {
            StartTray(services);

            // Started By Windows At Sign-In: stay in the tray
            if (Program.StartKind == ExtendedActivationKind.StartupTask)
            {
                GoToTray();
            }
            else
            {
                ShowMainWindow();
            }
        }

        // Later Launches Of This Profile Were Redirected Here (see Program)
        var dispatcher = _dispatcher;
        Program.HandleActivations(activation => dispatcher.TryEnqueue(() => OnActivated(activation)));
    }

    // =========================================================================
    // TRAY
    // =========================================================================

    // The tray icon and the minute clock. From here on, closing the last window doesn't end Leaf; Quit does.
    void StartTray(LeafServices services)
    {
        if (_trayStarted)
        {
            return;
        }

        _trayStarted           = true;
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        // Tray Icon (without one Leaf still runs, and launching it again brings the window back)
        try
        {
            _tray          = new TrayIcon(services.Log);
            _tray.Invoked += (_, _) => ShowMainWindow();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            services.Log.Error("tray.create.failed", ex);
        }

        // Minute Clock (tooltip countdown, time zone)
        _minuteTimer          = _dispatcher!.CreateTimer();
        _minuteTimer.Interval = TimeSpan.FromMinutes(1);
        _minuteTimer.Tick    += (_, _) => OnMinute();
        _minuteTimer.Start();

        // Sync Changes Refresh The Tooltip (the Google services are rebuilt when the OAuth client changes)
        services.GoogleChanged += (_, _) => _dispatcher.TryEnqueue(AttachSync);
        AttachSync();
        RefreshTooltip();
    }

    void AttachSync()
    {
        if (_attachedSync is not null)
        {
            _attachedSync.DataChanged -= OnSyncDataChanged;
        }

        _attachedSync = _services?.Google?.Sync;
        if (_attachedSync is not null)
        {
            _attachedSync.DataChanged += OnSyncDataChanged;
        }
    }

    // Raised on the sync thread
    void OnSyncDataChanged(object? sender, EventArgs e) => _dispatcher?.TryEnqueue(RefreshTooltip);

    void OnMinute()
    {
        _zone.Check();
        RefreshTooltip();
    }

    // "Standup in 12 min" (spec 8.1), within the tray lookahead setting
    void RefreshTooltip()
    {
        if (_services is not { } services || _tray is null)
        {
            return;
        }

        try
        {
            using var conn = services.Database.Open();
            var settings   = SettingsStore.Load(conn);
            var now        = services.Time.GetUtcNow();
            var soon       = TrayAgenda.Load(conn, now, _zone.Zone, 2, includeAllDay: false, settings.Use24HourTime);
            _tray.SetTooltip(TrayAgenda.Tooltip(TrayAgenda.Next(soon, now, TimeSpan.FromMinutes(settings.TrayLookaheadMinutes))));
        }
        catch (Exception ex) when (ex is SqliteException or JsonException or InvalidOperationException)
        {
            services.Log.Error("tray.tooltip.failed", ex);
        }
    }

    // The saved settings (the view model may not exist while Leaf is only in the tray)
    LeafSettings CurrentSettings()
    {
        if (_calendar is { } vm)
        {
            return vm.Settings;
        }

        using var conn = _services!.Database.Open();
        return SettingsStore.Load(conn);
    }

    // =========================================================================
    // WINDOWS
    // =========================================================================

    // Opens the main window, or brings the open one to the front
    void ShowMainWindow()
    {
        if (_services is not { } services)
        {
            return;
        }

        if (_window is { } open)
        {
            open.BringToFront();
            return;
        }

        var window = new MainWindow(services, AcquireCalendar());
        window.Closed += (_, _) =>
        {
            _window = null;
            _dispatcher?.TryEnqueue(ReleaseIfHidden);
        };
        _window = window;
        EfficiencyMode.Set(false);
        window.Activate();
        window.BringToFront();

        // Tray Probe: the memory budget test measures tray-only Leaf a few seconds after the window rendered
        if (services.Options.TrayProbe && _probeTimer is null)
        {
            _probeTimer             = _dispatcher!.CreateTimer();
            _probeTimer.Interval    = TimeSpan.FromSeconds(3);
            _probeTimer.IsRepeating = false;
            _probeTimer.Tick       += (_, _) => _window?.Close();
            _probeTimer.Start();
        }
    }

    // Brings onboarding forward while it's open; otherwise the main window
    void BringToFront()
    {
        if (_onboarding is { } onboarding)
        {
            onboarding.BringToFront();
            return;
        }

        ShowMainWindow();
    }

    void OnActivated(Activation activation)
    {
        // Windows' sign-in start while Leaf already runs changes nothing
        if (activation.Kind == ExtendedActivationKind.StartupTask)
        {
            return;
        }

        BringToFront();
    }

    // The main window and Settings share one view model, so a Settings change shows in the calendar at once
    CalendarViewModel AcquireCalendar()
    {
        if (_calendar is null)
        {
            _calendar              = new CalendarViewModel(_services!, _dispatcher!);
            _calendar.OpenSettings = OpenSettings;
        }

        return _calendar;
    }

    void OpenSettings(SettingsSection section)
    {
        if (_services is not { } services)
        {
            return;
        }

        SettingsWindow.Open(services, AcquireCalendar(), section);
        EfficiencyMode.Set(false);

        // Watch Each Settings Window Once, To Release The View Model When It And The Main Window Are Both Closed
        if (SettingsWindow.Current is { } open && !ReferenceEquals(open, _hookedSettings))
        {
            _hookedSettings = open;
            open.Closed    += (_, _) =>
            {
                _hookedSettings = null;
                _dispatcher?.TryEnqueue(ReleaseIfHidden);
            };
        }
    }

    // Tray only: no window uses the view model any more, so its caches and timers go
    void ReleaseIfHidden()
    {
        if (_window is not null || SettingsWindow.Current is not null)
        {
            return;
        }

        _calendar?.Dispose();
        _calendar = null;
        GoToTray();
    }

    // 60-second polling, efficiency mode, and a trimmed working set
    void GoToTray()
    {
        if (_services?.Google is { } google)
        {
            google.Loop.Mode = SyncMode.Tray;
        }

        EfficiencyMode.Set(true);
        MemoryTrimmer.Trim();
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
}
