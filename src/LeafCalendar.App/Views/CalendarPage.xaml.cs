using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Views;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using Windows.UI.Core;

namespace LeafCalendar.App.Views;

/// <summary>Navigation parameter for <see cref="CalendarPage"/>.</summary>
public sealed record CalendarPageArgs(CalendarViewModel ViewModel, Action OpenAccounts, Action ToggleTheme);

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

    /// <summary>
    /// Runs the calendar shortcut for a key press, called by the window so shortcuts work wherever focus is.
    /// Ignored while typing, or while a flyout, menu, dialog, or the go-to-date picker is open.
    /// </summary>
    /// <returns>True when the key was a shortcut and has been handled.</returns>
    public bool HandleShortcut(KeyRoutedEventArgs e)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or PasswordBox or AutoSuggestBox or NumberBox or RichEditBox or CalendarView
            || Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Count > 0)
        {
            return false;
        }

        var result = ShortcutMap.Resolve(e.Key.ToString(), IsDown(VirtualKey.Control), IsDown(VirtualKey.Shift), IsDown(VirtualKey.Menu));
        if (result.Command == CalendarCommand.None)
        {
            return false;
        }

        Execute(result);
        return true;
    }

    void Execute(ShortcutResult result)
    {
        var vm = ViewModel;
        switch (result.Command)
        {
            case CalendarCommand.Today:          vm.GoToToday(); break;
            case CalendarCommand.Previous:       vm.Previous(); break;
            case CalendarCommand.Next:           vm.Next(); break;
            case CalendarCommand.DayView:        vm.SetMode(Core.Settings.CalendarViewMode.Day); break;
            case CalendarCommand.WeekView:       vm.SetMode(Core.Settings.CalendarViewMode.Week); break;
            case CalendarCommand.MonthView:      vm.SetMode(Core.Settings.CalendarViewMode.Month); break;
            case CalendarCommand.Days:           vm.SetMode(Core.Settings.CalendarViewMode.Days, result.Days); break;
            case CalendarCommand.GoToDate:       ShowGoToDate(); break;
            case CalendarCommand.ToggleWeekends: vm.ToggleWeekends(); break;
            case CalendarCommand.ToggleDeclined: vm.ToggleDeclined(); break;
            case CalendarCommand.ZoomIn:         vm.ZoomBy(8); break;
            case CalendarCommand.ZoomOut:        vm.ZoomBy(-8); break;
            case CalendarCommand.ZoomReset:      vm.ZoomReset(); break;
            case CalendarCommand.ToggleTheme:    _args.ToggleTheme(); break;
            case CalendarCommand.NextEvent:      vm.SelectAdjacent(1); break;
            case CalendarCommand.PreviousEvent:  vm.SelectAdjacent(-1); break;
        }
    }

    void ShowGoToDate()
    {
        var picker = new CalendarView { SelectionMode = CalendarViewSelectionMode.Single, IsTodayHighlighted = true };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(picker, "GoToDateCalendar");
        picker.SetDisplayDate(new DateTimeOffset(ViewModel.PeriodStart.ToDateTime(TimeOnly.MinValue)));

        var flyout = new Flyout { Content = picker, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
        picker.SelectedDatesChanged += (s, a) =>
        {
            if (a.AddedDates.Count > 0)
            {
                flyout.Hide();
                ViewModel.NavigateTo(DateOnly.FromDateTime(a.AddedDates[0].Date));
            }
        };
        flyout.ShowAt(ViewHost);
    }

    static bool IsDown(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    void OnLayoutChanged(object? sender, EventArgs e) => ApplyView();

    void OnCalendarsChanged(object? sender, EventArgs e) => UpdateEmptyState();

    void UpdateEmptyState() =>
        EmptyState.Visibility = ViewModel.Calendars.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    void OnAddAccountClick(object sender, RoutedEventArgs e) => _args.OpenAccounts();
}
