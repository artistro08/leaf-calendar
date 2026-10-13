using LeafCalendar.App.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LeafCalendar.App.Views.Settings;

/// <summary>
/// Settings › Connected apps (local API spec, section 6): the switch that lets other apps on this PC connect (off by
/// default), the apps you allowed with a Remove button each, a card when another program holds Leaf's pipe name, and a
/// link to the developer reference. Changes save right away; a removed app is refused at its next request.
/// </summary>
public sealed partial class ConnectedAppsPage : Page
{
    private static readonly Uri DocsUri = new("https://github.com/artistro08/leaf-calendar/blob/main/docs/api.md");

    private SettingsContext _context = null!;

    // True while the saved values are being shown (the switch's change event is ignored meanwhile)
    private bool _loading = true;

    /// <summary>Creates the page.</summary>
    public ConnectedAppsPage()
    {
        InitializeComponent();
        ScrollIndicator.ShowOnHover(PageScroll);
    }

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _context = (SettingsContext)e.Parameter;
        _context.Host.SettingsChanged += OnSettingsChanged;
        _context.Services.ApiStateChanged += OnApiStateChanged;
        Load();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _context.Host.SettingsChanged -= OnSettingsChanged;
        _context.Services.ApiStateChanged -= OnApiStateChanged;
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Load();

    // Raised on whatever thread started or stopped the pipe
    private void OnApiStateChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Load);

    // Show The Saved Values And The Approved Apps (in the order they were approved)
    private void Load()
    {
        var s = _context.Calendar.Settings;
        _loading = true;
        AllowSwitch.IsOn = s.AllowApiConnections;
        NameTakenRow.Visibility = s.AllowApiConnections && _context.Services.ApiNameTaken ? Visibility.Visible : Visibility.Collapsed;
        NoAppsText.Visibility = s.ApprovedApps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        AppsList.Children.Clear();
        for (var i = 0; i < s.ApprovedApps.Count; i++)
        {
            var app = s.ApprovedApps[i];
            var remove = new Button { Content = "Remove" };
            AutomationProperties.SetName(remove, $"Remove {app.Name}");
            AutomationProperties.SetAutomationId(remove, $"ApiRemoveButton_{i}");
            var path = app.Path;
            remove.Click += (_, _) => _context.Save(x => x.WithoutApprovedApp(path));
            AppsList.Children.Add(new SettingRow { Glyph = "", Header = app.Name, Description = app.Path, Content = remove });
        }

        _loading = false;
    }

    private void OnAllowToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            var on = AllowSwitch.IsOn;
            _context.Save(s => s with { AllowApiConnections = on });
        }
    }

    private void OnDocsClick(object sender, RoutedEventArgs e) => _ = _context.Services.LaunchAsync(DocsUri);
}
