using System.Diagnostics.CodeAnalysis;
using LeafCalendar.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LeafCalendar.App.Views;

/// <summary>Navigation parameter for <see cref="CalendarPage"/>.</summary>
public sealed record CalendarPageArgs(CalendarViewModel ViewModel, Action OpenAccounts);

/// <summary>The main calendar page: sidebar, the current view, and (from Task 16) the details panel.</summary>
public sealed partial class CalendarPage : Page
{
    CalendarPageArgs _args = null!;

    /// <summary>Creates the page.</summary>
    public CalendarPage() => InitializeComponent();

    /// <summary>The page's view model.</summary>
    public CalendarViewModel ViewModel => _args.ViewModel;

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _args = (CalendarPageArgs)e.Parameter;

        Sidebar.Attach(ViewModel, _args.OpenAccounts);
        ViewModel.LayoutChanged    += OnLayoutChanged;
        ViewModel.CalendarsChanged += OnCalendarsChanged;

        SetSidebarOpen(ViewModel.Settings.SidebarOpen);
        ViewModel.ReloadCalendars();
        UpdateEmptyState();
        ApplyView();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.LayoutChanged    -= OnLayoutChanged;
        ViewModel.CalendarsChanged -= OnCalendarsChanged;
        Sidebar.Detach();
        ViewHost.Children.Clear();
    }

    /// <summary>Shows or hides the sidebar and remembers the choice.</summary>
    public void SetSidebarOpen(bool open)
    {
        SidebarColumn.Width = new GridLength(open ? 264 : 0);
        Sidebar.Visibility  = open ? Visibility.Visible : Visibility.Collapsed;

        if (ViewModel.Settings.SidebarOpen != open)
        {
            ViewModel.Update(s => s with { SidebarOpen = open });
        }
    }

    /// <summary>Puts the view for the current mode into <see cref="ViewHost"/> (views arrive in Tasks 13 and 14).</summary>
    [SuppressMessage("Performance", "CA1822", Justification = "Instance API; Tasks 13 and 14 fill in the views.")]
    public void ApplyView()
    {
    }

    void OnLayoutChanged(object? sender, EventArgs e) => ApplyView();

    void OnCalendarsChanged(object? sender, EventArgs e) => UpdateEmptyState();

    void UpdateEmptyState() =>
        EmptyState.Visibility = ViewModel.Calendars.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    void OnAddAccountClick(object sender, RoutedEventArgs e) => _args.OpenAccounts();
}
