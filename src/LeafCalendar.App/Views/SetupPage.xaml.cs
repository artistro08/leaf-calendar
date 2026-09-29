using LeafCalendar.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LeafCalendar.App.Views;

/// <summary>OAuth client setup page.</summary>
public sealed partial class SetupPage : Page
{
    /// <summary>Creates the page.</summary>
    public SetupPage() => InitializeComponent();

    /// <summary>Page view model (passed as the navigation parameter).</summary>
    public SetupViewModel ViewModel { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = (SetupViewModel)e.Parameter;
        Bindings.Update();
    }
}
