using System.Globalization;
using LeafCalendar.App.Controls;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel;

namespace LeafCalendar.App.Views.Settings;

/// <summary>Settings › About: the app's name and version (from the package), and the GitHub link.</summary>
public sealed partial class AboutPage : Page
{
    /// <summary>Creates the page.</summary>
    public AboutPage()
    {
        InitializeComponent();
        ScrollIndicator.ShowOnHover(PageScroll);
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
}
