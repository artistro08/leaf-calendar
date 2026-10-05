using System.Diagnostics.CodeAnalysis;
using LeafCalendar.App.Interop;
using LeafCalendar.App.Notifications;
using LeafCalendar.App.Tray;
using LeafCalendar.App.ViewModels;
using LeafCalendar.App.Views.Onboarding;
using LeafCalendar.Core.Alerts;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Events;
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
    /// <summary>Leaf's icon file, for each window's title bar and taskbar button.</summary>
    internal static readonly string IconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "LeafCalendar.ico");

    private MainWindow? _window;
    private OnboardingWindow? _onboarding;
    private readonly RepeatFilter _toastRepeats = new(TimeProvider.System);
    private LeafServices? _services;
    private CalendarViewModel? _calendar;
    private TrayIcon? _tray;
    private TrayHost? _host;
    private Notifier? _notifier;
    private AlertCenter? _alerts;
    private SyncEngine? _attachedSync;
    private DispatcherQueue _dispatcher = null!;
    private DispatcherQueueTimer? _minuteTimer;
    private DispatcherQueueTimer? _probeTimer;
    private DispatcherQueueTimer? _gcStressTimer;
    private readonly LocalZoneWatcher _zone = new();

    // The tray settings (and the day) the tooltip and agenda were last refreshed for
    private (int, bool, int, bool, string? Zone, DateTime Today) _trayKey;

    // Each tray read off the UI thread takes the next number; only the newest one's result is shown
    private int _tooltipGeneration;
    private int _agendaGeneration;
    private AppLog? _log;
    private bool _trayStarted;
    private bool _quitting;

    // Whether the internet connection is metered, read off the UI thread on each network change
    private volatile bool _metered;

    /// <summary>Loads XAML resources and hooks crash logging.</summary>
    public App()
    {
        InitializeComponent();

        // Crash Logging: type and stack always, a dump with Detailed logging on. A XAML error's own message is kept (it's
        // the framework's); a managed exception's message can carry event content, so it isn't
        UnhandledException += (_, e) => OnCrash("app.unhandled", e.Exception, e.Exception is System.Runtime.InteropServices.COMException ? e.Message : null);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => OnCrash("app.unhandled.domain", e.ExceptionObject as Exception, null);
        TaskScheduler.UnobservedTaskException += (_, e) => _log?.Crash("app.task.unobserved", e.Exception);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RemoveTrayIcon();
    }

    // A crash is about to end Leaf: what happened, then the dump
    private void OnCrash(string eventName, Exception? exception, string? message)
    {
        if (_log is not { } log || exception is null)
        {
            return;
        }

        log.Crash(eventName, exception, message);
        CrashDump.Write(log);
        RemoveTrayIcon();
    }

    // Leaf is ending without Quit (a crash, or the process exiting): the tray icon mustn't be left behind
    private void RemoveTrayIcon()
    {
        try
        {
            _tray?.Dispose();
        }
#pragma warning disable CA1031 // Runs as the process ends; nothing here may throw
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.Info("tray.remove.failed", $"error={ex.GetType().Name}");
        }
    }

    /// <inheritdoc />
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var options = Program.Options;
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
            _log.Info("app.start.failed", $"error={ex.GetType().Name}");
            throw;
        }

        _log = services.Log;
        _services = services;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        // Back From Sign-In In The Browser: the window that asked comes to the front
        services.SignInReturned += (_, _) => _dispatcher.TryEnqueue(BringSignInWindowToFront);

        // Detailed Logging (Settings › About) Starts With The Saved Choice
        _log.Detailed = CurrentSettings().DetailedLogging;
        CrashDump.Install(_log, Path.GetRelativePath(localFolder, _log.Directory));
        _log.Info("app.start", $"kind={Program.StartKind}");

        // Crash Tests: collect constantly, so an object Windows still uses after .NET let go of it fails right away
        if (options.GcStress)
        {
            _gcStressTimer = _dispatcher.CreateTimer();
            _gcStressTimer.Interval = TimeSpan.FromMilliseconds(20);
            _gcStressTimer.Tick += (_, _) =>
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            };
            _gcStressTimer.Start();
        }

        // How This Launch Started Couldn't Be Read (the type only)
        if (Program.StartReadError is { } readError)
        {
            _log.Info("app.activation.unreadable", $"error={readError}");
        }

        // First Run: onboarding shows instead of the main window until there's an OAuth client and an account; the tray starts when it's done
        if (OnboardingFlow.IsNeeded(services.HasOAuthClient(), services.HasAccount()))
        {
            // Started By Windows At Sign-In Before Setup Was Finished: no setup window at sign-in, just exit
            if (Program.StartKind == ExtendedActivationKind.StartupTask)
            {
                _services = null;
                _ = ExitQuietlyAsync(services);
                return;
            }

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
                    QuitStep("instance", () => AppInstance.GetCurrent().UnregisterKey());
                    _services = null;
                    await DisposeServicesAsync(services);
                }
            };
            _onboarding.Activate();
        }
        else
        {
            StartTray(services);

            // Started By Windows At Sign-In (minimized unless Settings says to open the window), Restarted By Windows After
            // An Update Or A Crash, Or By A Notification Click: the tray, plus what the click asked for
            var signInMinimized = Program.StartKind == ExtendedActivationKind.StartupTask && !CurrentSettings().OpenWindowAtSignIn;
            if (signInMinimized || options.Restarted || Program.StartKind == ExtendedActivationKind.AppNotification)
            {
                GoToTray();
                if (Program.StartKind == ExtendedActivationKind.AppNotification)
                {
                    HandleToast(Program.StartArgument);
                }
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
    private void StartTray(LeafServices services)
    {
        if (_trayStarted)
        {
            return;
        }

        _trayStarted = true;
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        // Tray Icon (without one Leaf still runs, and launching it again brings the window back)
        try
        {
            _tray = new TrayIcon(services.Log, TrayDay(), services.Options.Profile, visible: !CurrentSettings().HideTrayIcon);
            _tray.Invoked += (_, byKeyboard) => ToggleAgenda(byKeyboard);
        }
        catch (Exception ex)
        {
            // Any failure here would end the launch, and Leaf is still useful without its icon
            services.Log.Info("tray.create.failed", $"error={ex.GetType().Name}");
        }

        // Tray Menu And Flyout (the host is created once and kept hidden; without it Leaf runs on without them)
        try
        {
            var host = new TrayHost(services.Log);
            host.OpenRequested += (_, _) => ShowMainWindow();
            host.NewEventRequested += (_, _) => NewEvent();
            host.JoinNextRequested += (_, _) => JoinNext();
            host.SyncRequested += (_, _) => SyncNow();
            host.SettingsRequested += (_, _) => OpenSettings(SettingsSection.General);
            host.QuitRequested += (_, _) => Quit();
            host.AgendaOpened += (_, _) => UpdateSyncMode(flyoutOpened: true);
            host.AgendaClosed += (_, _) => UpdateSyncMode();
            host.OpenEventRequested += (_, occurrence) => RevealEvent(occurrence);
            host.JoinRequested += (_, occurrence) => JoinEvent(occurrence);
            _host = host;
            if (_tray is not null)
            {
                _tray.ContextMenuRequested += (_, point) => ShowTrayMenu(point.X, point.Y);
            }
        }
        catch (Exception ex)
        {
            services.Log.Info("tray.menu.create.failed", $"error={ex.GetType().Name}");
        }

        // Global Shortcuts (spec 8.6), on the tray icon's window; JoinNext and ToggleAgenda never throw, since they run
        // from the window procedure
        services.Shortcuts.Pressed += (_, action) =>
        {
            if (action == ShortcutAction.Join)
            {
                // Like the window's Ctrl+J: the next meeting in the upcoming list's lookahead, not only within 10 minutes
                JoinNext(TimeSpan.FromHours(CurrentSettings().UpcomingHours));
            }
            else
            {
                ToggleAgenda(byKeyboard: true);
            }
        };
        if (_tray is not null)
        {
            _tray.HotkeyPressed += (_, id) => services.Shortcuts.OnHotkey(id);
            services.Shortcuts.Attach(_tray.Handle, CurrentSettings());
        }

        // Notifications (registered before any click is handled)
        _notifier = new Notifier(services);
        _notifier.Invoked += (_, argument) => _dispatcher.TryEnqueue(() => HandleToast(argument));
        _notifier.Register();
        // ponytail: all-day reminders count from the PC zone's midnight; pass the primary zone into AlertCenter if a pinned-zone user notices
        _alerts = new AlertCenter(services, _notifier, () => _zone.Zone);
        _alerts.Start();

        // Minute Clock (tooltip countdown, time zone)
        _minuteTimer = _dispatcher!.CreateTimer();
        _minuteTimer.Interval = TimeSpan.FromMinutes(1);
        _minuteTimer.Tick += (_, _) => OnMinute();
        _minuteTimer.Start();

        // Sync Changes Refresh The Tooltip (the Google services are rebuilt when the OAuth client changes)
        services.GoogleChanged += (_, _) => _dispatcher.TryEnqueue(() =>
        {
            AttachSync();

            // The new loop starts at the tray's pace; it takes the window's and flyout's pace now
            UpdateSyncMode();
        });
        AttachSync();
        RefreshTooltip();

        // Energy Saver Or A Metered Connection Changes The Tray's Sync Pace (both events arrive off the UI thread). The
        // connection's cost is read there and on the pool now, never on the UI thread: it can stall on some VPN setups.
        Windows.System.Power.PowerManager.EnergySaverStatusChanged += (_, _) => _dispatcher?.TryEnqueue(() => UpdateSyncMode());
        Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged += _ => OnNetworkChanged();
        _ = Task.Run(OnNetworkChanged);

        // Windows Restarts Leaf Into The Tray After An Update Or A Crash, So Reminders Go On (a sign-in restart is the
        // startup task's; UI tests' fake profiles don't register)
        if (services.Options.FakeGoogle is null)
        {
            _ = Windows.Win32.PInvoke.RegisterApplicationRestart($"--profile {services.Options.Profile} --restarted", Windows.Win32.System.Recovery.REGISTER_APPLICATION_RESTART_FLAGS.RESTART_NO_REBOOT);
        }
    }

    private void AttachSync()
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
    private void OnSyncDataChanged(object? sender, EventArgs e) => _dispatcher?.TryEnqueue(() =>
    {
        RefreshTooltip();
        RefreshAgenda();
    });

    // The minute clock's tick: nothing may escape it (an unhandled exception here ends Leaf; the next minute tries again)
    private void OnMinute()
    {
        try
        {
            // A New PC Time Zone Re-Plans The Alerts (all-day reminders count from local midnight)
            if (_zone.Check())
            {
                _alerts?.Invalidate();
            }

            _tray?.SetDay(TrayDay());
        }
#pragma warning disable CA1031 // A timer tick that throws ends the process
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.Info("app.minute.failed", $"error={ex.GetType().Name}");
        }

        RefreshTooltip();
        RefreshAgenda();
    }

    // Today's day of the month on the PC clock (the taskbar's date), for the tray icon
    private int TrayDay() => TimeZoneInfo.ConvertTime(_services?.Time.GetUtcNow() ?? DateTimeOffset.UtcNow, _zone.Zone).Day;

    // "Standup in 12 min" (spec 8.1), within the tray lookahead setting. The read runs on the thread pool: the flyout's
    // mouse hook runs on this thread, and a synthetic 2,000-event profile measured medians of 31.5 ms (tooltip) and
    // 54.8 ms (agenda) per tray read here.
    private async void RefreshTooltip()
    {
        // async void: anything that escapes here would end the process (runs from the minute clock and syncs)
        try
        {
            if (_services is not { } services || _tray is null)
            {
                return;
            }

            var generation = ++_tooltipGeneration;
            var zone = _zone.Zone;
            var tooltip = await Task.Run(() => LoadTooltip(services, zone));

            // An Older Refresh Finishing Late Never Replaces A Newer One (and Quit may have taken the icon meanwhile)
            if (generation == _tooltipGeneration && tooltip is not null && _tray is { } tray)
            {
                tray.SetTooltip(tooltip);
            }
        }
        catch (Exception ex)
        {
            _log?.Info("tray.tooltip.failed", $"error={ex.GetType().Name}");
        }
    }

    // The tooltip's text, or null when the database can't be read (runs on the thread pool; the zone comes from the UI thread)
    private static string? LoadTooltip(LeafServices services, TimeZoneInfo localZone)
    {
        try
        {
            using var conn = services.Database.Open();
            return TrayAgenda.Tooltip(LoadNext(conn, SettingsStore.Load(conn), services.Time.GetUtcNow(), localZone));
        }
        catch (Exception ex)
        {
            // The type only, never content
            services.Log.Info("tray.tooltip.failed", $"error={ex.GetType().Name}");
            return null;
        }
    }

    // The flyout header's and tooltip's next event: timed events within the lookahead (its own two days, so a one-day agenda still sees past midnight)
    private static NextUp? LoadNext(SqliteConnection conn, LeafSettings settings, DateTimeOffset now, TimeZoneInfo localZone) =>
        TrayAgenda.Next(TrayAgenda.Load(conn, now, DisplayZone.Resolve(null, settings.PrimaryTimeZone, localZone), TrayAgenda.NextDays, includeAllDay: false, settings.Use24HourTime), now, TimeSpan.FromMinutes(settings.TrayLookaheadMinutes));

    // The saved settings (the view model may not exist while Leaf is only in the tray)
    private LeafSettings CurrentSettings()
    {
        if (_calendar is { } vm)
        {
            return vm.Settings;
        }

        try
        {
            using var conn = _services!.Database.Open();
            return SettingsStore.Load(conn);
        }
        catch (Exception ex)
        {
            // Runs from timers and syncs, so nothing may escape: the defaults stand in until the database reads again
            _log?.Info("settings.load.failed", $"error={ex.GetType().Name}");
            return new LeafSettings().Normalize();
        }
    }

    // =========================================================================
    // WINDOWS
    // =========================================================================

    // Opens the main window, or brings the open one to the front
    private void ShowMainWindow()
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

        services.Log.Trace("window.main", "open");
        var window = new MainWindow(services, AcquireCalendar());
        window.Closed += (_, _) =>
        {
            services.Log.Trace("window.main", "closed");
            _window = null;
            if (!_quitting)
            {
                _dispatcher?.TryEnqueue(ReleaseIfHidden);
            }
        };
        window.MinimizedChanged += (_, _) => UpdateSyncMode();
        _window = window;
        EfficiencyMode.Set(false);
        window.Activate();
        window.BringToFront();

        // Tray Probe: the memory budget test measures tray-only Leaf a few seconds after the window rendered
        if (services.Options.TrayProbe && _probeTimer is null)
        {
            _probeTimer = _dispatcher!.CreateTimer();
            _probeTimer.Interval = TimeSpan.FromSeconds(3);
            _probeTimer.IsRepeating = false;
            _probeTimer.Tick += (_, _) => _window?.Close();
            _probeTimer.Start();
        }
    }

    // A sign-in starts in onboarding or the main window (Settings › Accounts, or allowing contact suggestions): whichever is
    // showing comes back when the browser is done
    private void BringSignInWindowToFront()
    {
        if (_onboarding is { } onboarding)
        {
            onboarding.BringToFront();
        }
        else
        {
            _window?.BringToFront();
        }
    }

    // Brings onboarding forward while it's open; otherwise the main window
    private void BringToFront()
    {
        if (_onboarding is { } onboarding)
        {
            onboarding.BringToFront();
            return;
        }

        ShowMainWindow();
    }

    private void OnActivated(Activation activation)
    {
        // An Activation Windows Handed Over That Couldn't Be Read (the type only)
        if (activation.ReadError is { } readError)
        {
            _log?.Info("app.activation.unreadable", $"error={readError}");
        }

        switch (activation.Kind)
        {
            // Windows' Sign-In Start While Leaf Already Runs Changes Nothing
            case ExtendedActivationKind.StartupTask:
                return;

            // A Notification Click That Windows Handed To A New Process, Which Passed It On
            case ExtendedActivationKind.AppNotification:
                HandleToast(activation.Arguments);
                return;

            // UI Tests Click Notifications With "--toast-action" On A Second Launch (fake-Google profiles only)
            case ExtendedActivationKind.Launch when _services?.Options.FakeGoogle is not null && TestToastAction(activation.Arguments) is { } toast:
                HandleToast(toast);
                return;

            // The leaf-calendar: Link (the sign-in pages' "Open Leaf Calendar"), A Second Launch, Or Anything Else Just
            // Brings Leaf Forward (a link's address is never read, so a page can't make Leaf do anything else)
            default:
                BringToFront();
                return;
        }
    }

    private static string? TestToastAction(string? commandLine) =>
        string.IsNullOrWhiteSpace(commandLine) ? null : LaunchOptions.Parse(LaunchOptions.SplitCommandLine(commandLine)).ToastAction;

    // The main window and Settings share one view model, so a Settings change shows in the calendar at once
    private CalendarViewModel AcquireCalendar()
    {
        if (_calendar is null)
        {
            _calendar = new CalendarViewModel(_services!, _dispatcher!);
            _calendar.OpenSettings = OpenSettings;
            _calendar.QuitApp = Quit;

            // A Settings Change (the Tray page's days, all-day, lookahead; the primary zone) Shows In The Tray Right Away
            // Only when a tray setting changed or the day rolled over (other layout changes don't touch the tray)
            _calendar.LayoutChanged += (_, _) =>
            {
                var s = CurrentSettings();
                _tray?.SetVisible(!s.HideTrayIcon);
                var zone = DisplayZone.Resolve(null, s.PrimaryTimeZone, _zone.Zone);
                var key = (s.FlyoutDays, s.FlyoutAllDay, s.TrayLookaheadMinutes, s.Use24HourTime, Zone: s.PrimaryTimeZone, Today: TimeZoneInfo.ConvertTime(_services!.Time.GetUtcNow(), zone).Date);
                if (key == _trayKey)
                {
                    return;
                }

                _trayKey = key;
                RefreshTooltip();
                RefreshAgenda();
            };

            // A Calendar Hidden Or Shown In Leaf Leaves Or Joins The Tray Right Away (the tray shows what Leaf shows)
            // Its Alerts Re-Plan Too (hidden calendars and default reminders feed the plan)
            _calendar.CalendarsChanged += (_, _) =>
            {
                _alerts?.Invalidate();
                RefreshTooltip();
                RefreshAgenda();
            };
        }

        return _calendar;
    }

    // Settings shows in the main window, in place of the calendar
    private void OpenSettings(SettingsSection section)
    {
        ShowMainWindow();
        _window?.ShowSettings(section);
    }

    // Tray only: no window uses the view model any more, so its caches and timers go
    private void ReleaseIfHidden()
    {
        if (_window is not null)
        {
            return;
        }

        _calendar?.Dispose();
        _calendar = null;
        GoToTray();
    }

    // Tray-pace polling (60 s, or 5 minutes on Energy Saver or a metered connection), efficiency mode, and a trimmed
    // working set
    private void GoToTray()
    {
        UpdateSyncMode();
        MemoryTrimmer.Trim();
    }

    // =========================================================================
    // FLYOUT
    // =========================================================================

    // Left-click, Enter on the icon, or the flyout shortcut (raised from the tray window's procedure, so nothing may
    // escape); a click opens it without a focus ring
    private void ToggleAgenda(bool byKeyboard)
    {
        if (_host is not { } host)
        {
            return;
        }

        try
        {
            if (host.IsAgendaOpen)
            {
                host.HideAgenda();
                return;
            }

            // The First Build Stays On This Thread, So The Flyout Opens With Its Content (a refresh still running is now stale)
            ++_agendaGeneration;
            if (_services is { } services && BuildAgenda(services, _zone.Zone) is { } model)
            {
                host.ShowAgenda(model, _tray?.IconRect(), CurrentSettings().Theme, byKeyboard);
            }
        }
        catch (Exception ex)
        {
            _log?.Info("tray.flyout.failed", $"error={ex.GetType().Name}");
        }
    }

    // A sync or the minute clock while the flyout is open (async void, so nothing may escape); the read runs on the
    // thread pool, like the tooltip's
    private async void RefreshAgenda()
    {
        try
        {
            if (_host is not { IsAgendaOpen: true } host || _services is not { } services)
            {
                return;
            }

            var generation = ++_agendaGeneration;
            var zone = _zone.Zone;
            var model = await Task.Run(() => BuildAgenda(services, zone));

            // An Older Refresh Finishing Late Never Replaces A Newer One, And A Closed Flyout Stays Closed
            if (generation == _agendaGeneration && model is not null && !_quitting && host.IsAgendaOpen)
            {
                host.UpdateAgenda(model);
            }
        }
        catch (Exception ex)
        {
            _log?.Info("tray.flyout.refresh.failed", $"error={ex.GetType().Name}");
        }
    }

    // The agenda (days and all-day per the Tray settings) and its header; any thread (the zone comes from the UI thread)
    private static AgendaModel? BuildAgenda(LeafServices services, TimeZoneInfo localZone)
    {
        try
        {
            using var conn = services.Database.Open();
            var settings = SettingsStore.Load(conn);
            var now = services.Time.GetUtcNow();
            var days = TrayAgenda.Load(conn, now, DisplayZone.Resolve(null, settings.PrimaryTimeZone, localZone), settings.FlyoutDays, settings.FlyoutAllDay, settings.Use24HourTime);
            return new AgendaModel(days, LoadNext(conn, settings, now, localZone), TrayAgenda.NothingNext(settings.TrayLookaheadMinutes));
        }
        catch (Exception ex)
        {
            // The type only, never content
            services.Log.Info("tray.agenda.failed", $"error={ex.GetType().Name}");
            return null;
        }
    }

    // A flyout row or a notification: the main window on that event
    private void RevealEvent(CalendarOccurrence occurrence)
    {
        try
        {
            ShowMainWindow();
            _dispatcher?.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                try
                {
                    _calendar?.Reveal(occurrence);
                }
                catch (Exception ex)
                {
                    _log?.Info("tray.reveal.failed", $"error={ex.GetType().Name}");
                }
            });
        }
        catch (Exception ex)
        {
            // A flyout click handler, so nothing may escape
            _log?.Info("tray.reveal.failed", $"error={ex.GetType().Name}");
        }
    }

    // A Join button: the event's own link, Meet with its account
    private void JoinEvent(CalendarOccurrence occurrence)
    {
        if (_services is not { } services)
        {
            return;
        }

        try
        {
            Uri? link;
            using (var conn = services.Database.Open())
            {
                link = JoinPicker.MeetingLink(conn, occurrence) is { } meeting ? JoinPicker.JoinLink(conn, new JoinTarget(occurrence, meeting)) : null;
            }

            // The Meeting Lost Its Link Since The Flyout Drew It: say so in the log and show the agenda as it is now
            if (link is null)
            {
                services.Log.Info("tray.join.no-link");
                RefreshAgenda();
                return;
            }

            // LaunchAsync re-checks the link and never throws
            _ = services.LaunchAsync(link);
        }
        catch (Exception ex)
        {
            // A flyout click handler, so nothing may escape; the type only, never content
            services.Log.Info("tray.join.failed", $"error={ex.GetType().Name}");
        }
    }

    // 15 s while a window or the flyout is on screen, 60 s in the tray or minimized, 5 minutes there on Energy Saver or a
    // metered connection (spec 5.3); opening the flyout syncs at once
    private void UpdateSyncMode(bool flyoutOpened = false)
    {
        // Runs from the flyout's open and close events, so nothing may escape
        try
        {
            var visible = _window is { IsMinimized: false } || _host?.IsAgendaOpen == true;
            if (_services?.Google is { } google)
            {
                google.Loop.Mode = SyncLoop.ModeFor(visible, IsEnergySaverOn(), _metered);
                if (flyoutOpened)
                {
                    google.Loop.TriggerNow();
                }
            }

            EfficiencyMode.Set(!visible);
        }
        catch (Exception ex)
        {
            _log?.Info("tray.syncmode.failed", $"error={ex.GetType().Name}");
        }
    }

    // Windows' Energy Saver (Windows 11's battery saver); a failed read counts as off
    private static bool IsEnergySaverOn()
    {
        try
        {
            return Windows.System.Power.PowerManager.EnergySaverStatus == Windows.System.Power.EnergySaverStatus.On;
        }
#pragma warning disable CA1031 // Any WinRT failure means "not on", so the pace still updates
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    // The Connection Changed (off the UI thread): its cost is read here, then the pace updates on the UI thread
    private void OnNetworkChanged()
    {
        _metered = IsMetered();
        _dispatcher?.TryEnqueue(() => UpdateSyncMode());
    }

    // A metered internet connection (fixed or variable cost); no connection, or a failed read, counts as unmetered
    private static bool IsMetered()
    {
        try
        {
            return Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile()?.GetConnectionCost().NetworkCostType
                is Windows.Networking.Connectivity.NetworkCostType.Fixed or Windows.Networking.Connectivity.NetworkCostType.Variable;
        }
#pragma warning disable CA1031 // Any WinRT failure means "not metered", so the pace still updates
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    // =========================================================================
    // NOTIFICATION CLICKS
    // =========================================================================

    // A notification or one of its buttons was clicked (spec 8.4); on the UI thread, and nothing may escape
    private void HandleToast(string? argument)
    {
        // Setup Isn't Finished: nothing to act on yet, so setup comes forward
        if (_onboarding is not null)
        {
            BringToFront();
            return;
        }

        if (_services is not { } services || ToastArgs.Parse(argument) is not { } toast)
        {
            return;
        }

        // The Same Click Twice (a click that starts Leaf also arrives as NotificationInvoked), so one reply is sent
        if (_toastRepeats.IsRepeat(argument!))
        {
            services.Log.Info("notification.repeat");
            return;
        }

        // Another Profile's Notification (every profile shares Leaf's notification identity; names ignore case, like
        // Program's single-instance key)
        if (!string.Equals(toast.Profile, services.Options.Profile, StringComparison.OrdinalIgnoreCase))
        {
            services.Log.Info("notification.other-profile");
            return;
        }

        try
        {
            switch (toast.Action)
            {
                case ToastAction.ReviewConflicts:
                    ShowMainWindow();
                    _dispatcher?.TryEnqueue(DispatcherQueuePriority.Low, () => _ = _window?.ReviewConflictsAsync());
                    return;

                case ToastAction.SignIn:
                    OpenSettings(SettingsSection.Accounts);
                    return;
            }

            // About An Event: find the instance again by its IDs (it may have moved or gone since)
            CalendarOccurrence? occurrence;
            using (var conn = services.Database.Open())
            {
                occurrence = OccurrenceLookup.Find(conn, toast.AccountId!, toast.CalendarId!, toast.EventId!, toast.Start!.Value, _zone.Zone);
            }

            if (occurrence is null)
            {
                services.Log.Info("notification.event-gone");

                // An Open Click Still Brings The Calendar Forward, Since The Click Should Show Something
                if (toast.Action == ToastAction.Open)
                {
                    ShowMainWindow();
                }

                return;
            }

            switch (toast.Action)
            {
                case ToastAction.Open:
                    RevealEvent(occurrence);
                    break;

                // The link comes from the stored event and goes through LinkSafety at launch, never from the arguments
                case ToastAction.Join:
                    JoinEvent(occurrence);
                    break;

                default:
                    Respond(occurrence, toast.Action);
                    break;
            }
        }
        catch (Exception ex)
        {
            // The type only, never content or the arguments
            services.Log.Info("notification.action.failed", $"error={ex.GetType().Name}");
        }
    }

    // Yes / No / Maybe on an invitation: Google emails the organizer, like its own buttons; a repeating invitation is answered for the series, a changed instance of one for itself
    private void Respond(CalendarOccurrence occurrence, ToastAction action)
    {
        var response = action switch
        {
            ToastAction.Accept => ResponseStatus.Accepted,
            ToastAction.Decline => ResponseStatus.Declined,
            _ => ResponseStatus.Tentative,
        };
        var scope = InviteWatcher.ReplyScope(occurrence);
        _services!.Editor.Respond(occurrence, response, note: null, sendUpdates: true, scope);
    }

    // =========================================================================
    // TRAY ACTIONS
    // =========================================================================

    // Right-click on the icon (raised from the tray window's procedure, so nothing may escape); the join item names the
    // meeting Join next meeting would open now, and is off when there's none
    private void ShowTrayMenu(int x, int y)
    {
        try
        {
            _host?.ShowMenu(x, y, CurrentSettings().Theme, NextJoin());
        }
        catch (Exception ex)
        {
            _log?.Info("tray.menu.failed", $"error={ex.GetType().Name}");
        }
    }

    // New event: the main window's editor, at the next free slot
    private void NewEvent()
    {
        ShowMainWindow();
        _dispatcher?.TryEnqueue(DispatcherQueuePriority.Low, () => _calendar?.BeginCreateNow());
    }

    // The meeting the menu's join item opens (the join rule, spec 8.5), or null; a failed read leaves the item off
    private JoinTarget? NextJoin()
    {
        if (_services is not { } services)
        {
            return null;
        }

        try
        {
            using var conn = services.Database.Open();
            var now = services.Time.GetUtcNow();
            return JoinPicker.Pick(JoinPicker.Candidates(conn, now, _zone.Zone), now);
        }
        catch (Exception ex)
        {
            // The type only, never content
            services.Log.Info("tray.menu.join.failed", $"error={ex.GetType().Name}");
            return null;
        }
    }

    // Join next meeting: the join rule (spec 8.5) for the tray menu; the join shortcut passes a lookahead, so with nothing
    // in the next 10 minutes it opens the next meeting within it. Else "No meeting to join"
    private void JoinNext(TimeSpan? lookahead = null)
    {
        if (_services is not { } services)
        {
            return;
        }

        try
        {
            Uri? link;
            using (var conn = services.Database.Open())
            {
                var now = services.Time.GetUtcNow();
                link = lookahead is { } ahead ? JoinPicker.FindNext(conn, now, _zone.Zone, ahead) : JoinPicker.Find(conn, now, _zone.Zone);
            }

            if (link is null)
            {
                _notifier?.Show(ToastContent.NoMeeting(CurrentSettings().NotificationSound));
                return;
            }

            // LaunchAsync re-checks the link and never throws
            _ = services.LaunchAsync(link);
        }
        catch (Exception ex)
        {
            // A menu click handler, so nothing may escape; the type only, never content
            services.Log.Info("tray.join.failed", $"error={ex.GetType().Name}");
        }
    }

    // Sync now: calendars and events, the calendar list included
    private async void SyncNow()
    {
        // async void: anything that escapes here would end the process
        try
        {
            // Through The Calendar When It's Open, So Its Title Bar Shows The Sync Running
            if (_calendar is { } calendar)
            {
                await calendar.SyncNowAsync();
            }
            else if (_services?.Google is { } google)
            {
                await Task.Run(() => google.Sync.SyncAllAsync(refreshCalendarLists: true, CancellationToken.None));
            }
        }
        catch (Exception ex)
        {
            _log?.Info("tray.sync.failed", $"error={ex.GetType().Name}");
        }
    }

    // Quit: the only way Leaf ends once it's in the tray
    private async void Quit()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        try
        {
            // Each Step On Its Own, So One Failure Can't Skip The Database And Sync Teardown
            QuitStep("restart", () => _ = Windows.Win32.PInvoke.UnregisterApplicationRestart());
            QuitStep("timer", () => _minuteTimer?.Stop());
            QuitStep("alerts", () => _alerts?.Dispose());
            QuitStep("notifier", () => _notifier?.Dispose());
            QuitStep("shortcuts", () => _services?.Shortcuts.Suspend());
            QuitStep("tray", () =>
            {
                _tray?.Dispose();
                _tray = null;
            });
            QuitStep("window", () => _window?.Close());
            QuitStep("host", () => _host?.Shutdown());
            QuitStep("calendar", () =>
            {
                _calendar?.Dispose();
                _calendar = null;
            });

            // Let Go Of The Single-Instance Key Before The Slow Teardown, So A Launch Now Starts Fresh Instead Of Redirecting Here
            QuitStep("instance", () => AppInstance.GetCurrent().UnregisterKey());
            if (_services is { } services)
            {
                _services = null;
                await DisposeServicesAsync(services);
            }
        }
        catch (Exception ex)
        {
            _log?.Info("app.quit.failed", $"error={ex.GetType().Name}");
        }
        finally
        {
            Exit();
        }
    }

    // One Quit step: logged by its name and the error type, then Quit carries on
    private void QuitStep(string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _log?.Info("app.quit.step.failed", $"step={step} error={ex.GetType().Name}");
        }
    }

    // Nothing to show: the services go, then the app ends
    private async Task ExitQuietlyAsync(LeafServices services)
    {
        QuitStep("instance", () => AppInstance.GetCurrent().UnregisterKey());
        await DisposeServicesAsync(services);
        Exit();
    }

    // Window close must not crash the process on a disposal failure, so log and carry on
    private static async Task DisposeServicesAsync(LeafServices services)
    {
        try
        {
            await services.DisposeAsync();
        }
        catch (Exception ex)
        {
            services.Log.Info("app.dispose.failed", $"error={ex.GetType().Name}");
        }
    }
}
