using LeafCalendar.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Views;

/// <summary>Sidebar: mini month and calendars (Task 12), booking pages link, and Accounts.</summary>
public sealed partial class SidebarView : UserControl
{
    CalendarViewModel? _viewModel;
    Action? _openAccounts;

    /// <summary>Creates the sidebar.</summary>
    public SidebarView() => InitializeComponent();

    /// <summary>Connects the sidebar to the page's view model.</summary>
    public void Attach(CalendarViewModel viewModel, Action openAccounts)
    {
        _viewModel    = viewModel;
        _openAccounts = openAccounts;
    }

    /// <summary>Disconnects from the view model.</summary>
    public void Detach()
    {
        _viewModel    = null;
        _openAccounts = null;
    }

    void OnAccountsClick(object sender, RoutedEventArgs e) => _openAccounts?.Invoke();
}
