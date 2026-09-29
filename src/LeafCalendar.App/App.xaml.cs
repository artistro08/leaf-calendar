using LeafCalendar.App.Interop;
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
    DispatcherQueueTimer? _probeTimer;

    /// <summary>Loads XAML resources.</summary>
    public App() => InitializeComponent();

    /// <inheritdoc />
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var options  = LaunchOptions.Parse([.. Environment.GetCommandLineArgs().Skip(1)]);
        var services = new LeafServices(options, ApplicationData.Current.LocalFolder.Path);

        _window = new MainWindow(services);
        _window.Activate();

        // Services Lifetime: the tray probe keeps them alive after the window closes
        if (options.TrayProbe)
        {
            StartTrayProbe(services);
        }
        else
        {
            _window.Closed += async (_, _) => await DisposeServicesAsync(services);
        }
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
