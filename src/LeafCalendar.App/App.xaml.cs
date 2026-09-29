using LeafCalendar.App.Interop;
using LeafCalendar.Core.Hosting;
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
        var options = LaunchOptions.Parse([.. Environment.GetCommandLineArgs().Skip(1)]);

        // The window owns the services and disposes them when it closes
        _window = new MainWindow(new LeafServices(options, ApplicationData.Current.LocalFolder.Path));
        _window.Activate();

        if (options.TrayProbe)
        {
            StartTrayProbe();
        }
    }

    // Tray Probe: closes the window once it has rendered and trims memory, so the memory budget
    // test can measure tray-only mode before the real tray arrives in Milestone 4.
    void StartTrayProbe()
    {
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        _probeTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _probeTimer.Interval    = TimeSpan.FromSeconds(3);
        _probeTimer.IsRepeating = false;
        _probeTimer.Tick += (_, _) =>
        {
            _window?.Close();
            _window = null;
            MemoryTrimmer.Trim();
        };
        _probeTimer.Start();
    }
}
