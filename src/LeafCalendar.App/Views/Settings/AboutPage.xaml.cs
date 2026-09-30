using System.Globalization;
using LeafCalendar.App.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel;

namespace LeafCalendar.App.Views.Settings;

/// <summary>Settings › About: the app's name and version (from the package), and the GitHub link.</summary>
public sealed partial class AboutPage : Page
{
    // Fixed Address (opened through LeafServices.LaunchAsync, which checks it and never throws)
    static readonly Uri GitHub = new("https://github.com/artistro08/leaf-calendar");

    LeafServices _services = null!;

    /// <summary>Creates the page.</summary>
    public AboutPage()
    {
        InitializeComponent();
        ScrollIndicator.ShowOnHover(PageScroll);
    }

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e) => _services = ((SettingsContext)e.Parameter).Services;

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
}
