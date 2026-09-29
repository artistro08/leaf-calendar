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
    IDisposable? _view;
    bool _viewIsMonth;

    /// <summary>Creates the page.</summary>
    public CalendarPage() => InitializeComponent();

    /// <summary>The page's view model.</summary>
    public CalendarViewModel ViewModel => _args.ViewModel;

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _args = (CalendarPageArgs)e.Parameter;

        Sidebar.Attach(ViewModel, _args.OpenAccounts);
        Details.Attach(ViewModel);
        ViewModel.PropertyChanged  += OnViewModelPropertyChanged;
        ViewModel.LayoutChanged    += OnLayoutChanged;
        ViewModel.CalendarsChanged += OnCalendarsChanged;

        SetSidebarOpen(ViewModel.Settings.SidebarOpen);
        SetDetailsOpen(ViewModel.Settings.DetailsPanelOpen);
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
        Details.Detach();
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _view?.Dispose();
        _view = null;
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

    /// <summary>Shows or hides the details panel and remembers the choice.</summary>
    public void SetDetailsOpen(bool open)
    {
        DetailsColumn.Width    = new GridLength(open ? 320 : 0);
        DetailsHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        if (ViewModel.Settings.DetailsPanelOpen != open)
        {
            ViewModel.Update(s => s with { DetailsPanelOpen = open });
        }
    }

    /// <summary>Puts the view for the current mode into <see cref="ViewHost"/>, keeping one view per mode family.</summary>
    public void ApplyView()
    {
        var wantMonth = ViewModel.Mode == Core.Settings.CalendarViewMode.Month;
        if (_view is not null && wantMonth == _viewIsMonth)
        {
            return;
        }

        _view?.Dispose();
        ViewHost.Children.Clear();

        if (wantMonth)
        {
            var month = new Controls.MonthGridView(ViewModel);
            _view = month;
            ViewHost.Children.Add(month);
        }
        else
        {
            var grid = new Controls.TimeGridView(ViewModel);
            _view = grid;
            ViewHost.Children.Add(grid);
        }

        _viewIsMonth = wantMonth;
    }

    // Selecting an event opens the panel so the details are visible
    void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CalendarViewModel.SelectedInfo) && ViewModel.SelectedInfo is not null && !ViewModel.Settings.DetailsPanelOpen)
        {
            SetDetailsOpen(true);
        }
    }

    void OnEscapeInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.ClearSelection();
        args.Handled = true;
    }

    void OnLayoutChanged(object? sender, EventArgs e) => ApplyView();

    void OnCalendarsChanged(object? sender, EventArgs e) => UpdateEmptyState();

    void UpdateEmptyState() =>
        EmptyState.Visibility = ViewModel.Calendars.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    void OnAddAccountClick(object sender, RoutedEventArgs e) => _args.OpenAccounts();
}
