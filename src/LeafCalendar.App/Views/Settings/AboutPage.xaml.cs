using System.Globalization;
using LeafCalendar.App.Controls;
using LeafCalendar.App.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel;

namespace LeafCalendar.App.Views.Settings;

/// <summary>Settings › About: the app's name and version (from the package), the GitHub link, the log folder, and Detailed logging.</summary>
public sealed partial class AboutPage : Page
{
    // Fixed Address (opened through LeafServices.LaunchAsync, which checks it and never throws)
    static readonly Uri GitHub = new("https://github.com/artistro08/leaf-calendar");

    LeafServices _services = null!;
    SettingsContext _context = null!;

    // True while the saved value is being shown (the switch's Toggled event is ignored meanwhile)
    bool _loading;

    /// <summary>Creates the page.</summary>
    public AboutPage()
    {
        InitializeComponent();
        ScrollIndicator.ShowOnHover(PageScroll);
    }

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _context  = (SettingsContext)e.Parameter;
        _services = _context.Services;

        // Show The Saved Choice
        _loading                   = true;
        DetailedLoggingSwitch.IsOn = _context.Calendar.Settings.DetailedLogging;
        _loading                   = false;
    }

    /// <summary>"Version 1.0.0.0", from the installed package.</summary>
    public static string VersionText
    {
        get
        {
            var v = Package.Current.Id.Version;
            return string.Create(CultureInfo.InvariantCulture, $"Version {v.Major}.{v.Minor}.{v.Build}.{v.Revision}");
        }
    }

    void OnGitHubClick(object sender, RoutedEventArgs e) => _ = _services.LaunchAsync(GitHub);

    // Leaf's own log folder (OpenFolderAsync never throws)
    void OnOpenLogsClick(object sender, RoutedEventArgs e) => _ = _services.OpenFolderAsync(Path.GetDirectoryName(_services.Log.FilePath)!);

    void OnDetailedLoggingToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var on = DetailedLoggingSwitch.IsOn;
        _context.Save(s => s with { DetailedLogging = on });
        CrashDump.Apply(_context.Calendar.Settings.DetailedLogging);
        _services.Log.Info("settings.logging.detailed", on ? "on" : "off");
    }
}
