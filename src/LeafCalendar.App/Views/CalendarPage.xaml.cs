using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Editing;
using LeafCalendar.Core.Views;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using Windows.UI.Core;

namespace LeafCalendar.App.Views;

/// <summary>Navigation parameter for <see cref="CalendarPage"/>.</summary>
public sealed record CalendarPageArgs(CalendarViewModel ViewModel, Action OpenAccounts, Action ToggleTheme);

/// <summary>A side pane started to open or close.</summary>
public sealed class PanesChangedEventArgs(bool animate, bool opening) : EventArgs
{
    /// <summary>True when the pane slides (a user toggle); false for the first layout.</summary>
    public bool Animate { get; } = animate;

    /// <summary>True when the pane is opening.</summary>
    public bool Opening { get; } = opening;
}

/// <summary>
/// The main calendar page in three parts: the sidebar and the details panel on the window's Mica,
/// and between them a flat "island" holding the period title and the current view. The page runs
/// under the title bar. The side panes are the panes of two nested inline <see cref="SplitView"/>s
/// (sidebar on the outer one, details on the inner one), so they open and close with WinUI's own slide.
/// </summary>
public sealed partial class CalendarPage : Page
{
    /// <summary>Width of the open sidebar.</summary>
    public const double SidebarWidth = 264;

    /// <summary>Width of the open details panel.</summary>
    public const double DetailsWidth = 320;

    /// <summary>The period title's inset from the island's left edge.</summary>
    public const double TitleInset = 17;

    /// <summary>
    /// The title bar toolbar ends this far in from the island's right edge, or from the caption buttons when
    /// the details panel is closed. Its last icon's glyph sits 8 further in, so the icons' ink ends about as
    /// far from the edge as the title's starts from the other one.
    /// </summary>
    public const double ToolbarInset = 6;

    // Room for the title bar's pane toggle, which sits over the island's corner while the sidebar is closed
    const double PaneToggleClearance = 44;

    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _noticeTimer;
    CalendarPageArgs _args = null!;
    IDisposable? _view;
    bool _viewIsMonth;

    /// <summary>Creates the page.</summary>
    public CalendarPage()
    {
        InitializeComponent();

        // Notices Hide Themselves (before the delete's 6 s undo window ends)
        _noticeTimer = DispatcherQueue.CreateTimer();
        _noticeTimer.Interval    = TimeSpan.FromSeconds(5);
        _noticeTimer.IsRepeating = false;
        _noticeTimer.Tick       += (_, _) => ViewModel.DismissNotice();
    }

    /// <summary>The page's view model.</summary>
    public CalendarViewModel ViewModel => _args.ViewModel;

    /// <summary>True when the sidebar takes up room. A SplitView resizes its content as soon as the pane starts to open or close, then slides it.</summary>
    public bool IsSidebarOpen => SidebarSplit.IsPaneOpen;

    /// <summary>True when the details panel takes up room.</summary>
    public bool IsDetailsOpen => DetailsSplit.IsPaneOpen;

    /// <summary>The sidebar or details panel started to open or close (the island has its new size and is sliding into place).</summary>
    public event EventHandler<PanesChangedEventArgs>? PanesChanged;

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _args = (CalendarPageArgs)e.Parameter;

        Sidebar.Attach(ViewModel, _args.OpenAccounts);
        Details.Attach(ViewModel);
        ViewModel.PropertyChanged  += OnViewModelPropertyChanged;
        ViewModel.LayoutChanged    += OnLayoutChanged;
        ViewModel.CalendarsChanged += OnCalendarsChanged;

        // Repeating Events Ask Which Events A Change Applies To
        ViewModel.AskScope = includeFollowing => ScopeDialog.AskAsync(XamlRoot, includeFollowing);

        PeriodTitle.Text = ViewModel.PeriodTitle;
        SetSidebarOpen(ViewModel.Settings.SidebarOpen, animate: false);
        SetDetailsOpen(ViewModel.Settings.DetailsPanelOpen, animate: false);
        ViewModel.ReloadCalendars();
        UpdateEmptyState();
        ApplyView();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e) => Detach();

    /// <summary>
    /// Disconnects from the view model and disposes the current view. Called on navigation away
    /// and when the window closes (which doesn't navigate), so the time grid's minute clock stops
    /// instead of repainting a closed window in tray mode.
    /// </summary>
    public void Detach()
    {
        ViewModel.LayoutChanged    -= OnLayoutChanged;
        ViewModel.CalendarsChanged -= OnCalendarsChanged;
        ViewModel.AskScope = null;
        _noticeTimer.Stop();
        Sidebar.Detach();
        Details.Detach();
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _view?.Dispose();
        _view = null;
        ViewHost.Children.Clear();
    }

    /// <summary>Shows or hides the sidebar (sliding when <paramref name="animate"/>) and remembers the choice.</summary>
    public void SetSidebarOpen(bool open, bool animate)
    {
        SetPaneOpen(SidebarSplit, open, animate);

        if (ViewModel.Settings.SidebarOpen != open)
        {
            ViewModel.Update(s => s with { SidebarOpen = open });
        }
    }

    /// <summary>Shows or hides the details panel (sliding when <paramref name="animate"/>) and remembers the choice.</summary>
    public void SetDetailsOpen(bool open, bool animate)
    {
        SetPaneOpen(DetailsSplit, open, animate);

        if (ViewModel.Settings.DetailsPanelOpen != open)
        {
            ViewModel.Update(s => s with { DetailsPanelOpen = open });
        }
    }

    // The SplitView plays its own pane transition; this only reports the change so the title bar can follow
    void SetPaneOpen(SplitView split, bool open, bool animate)
    {
        split.IsPaneOpen = open;

        // With the sidebar closed the title bar's pane toggle sits over the island's corner, so the title moves right
        PeriodTitle.Margin = new Thickness(IsSidebarOpen ? TitleInset : PaneToggleClearance + TitleInset, 9, 0, 8);
        PanesChanged?.Invoke(this, new PanesChangedEventArgs(animate, open));
    }

    // Inline panes slide the content with them, and a closing right pane starts the content one pane width
    // to the left; clip it so the island never shows through the sidebar's see-through pane
    void OnDetailsSplitSizeChanged(object sender, SizeChangedEventArgs e) =>
        DetailsSplit.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };

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

    // Selecting an event opens the panel so the details are visible; the title follows the period
    void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CalendarViewModel.Notice))
        {
            ShowNotice();
            return;
        }

        if (e.PropertyName == nameof(CalendarViewModel.PeriodTitle))
        {
            PeriodTitle.Text = ViewModel.PeriodTitle;
            return;
        }

        if (e.PropertyName == nameof(CalendarViewModel.SelectedInfo) && ViewModel.SelectedInfo is not null && !ViewModel.IsRefreshingSelection && !ViewModel.Settings.DetailsPanelOpen)
        {
            SetDetailsOpen(true, animate: true);
        }
    }

    // A tap on empty calendar space clears the selection (events and chips mark their own taps handled)
    void OnViewHostTapped(object sender, TappedRoutedEventArgs e) => ViewModel.ClearSelection();

    void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
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
            || VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Count > 0)
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
            case CalendarCommand.Today:              vm.GoToToday(); break;
            case CalendarCommand.Previous:           vm.Previous(); break;
            case CalendarCommand.Next:               vm.Next(); break;
            case CalendarCommand.DayView:            vm.SetMode(Core.Settings.CalendarViewMode.Day); break;
            case CalendarCommand.WeekView:           vm.SetMode(Core.Settings.CalendarViewMode.Week); break;
            case CalendarCommand.MonthView:          vm.SetMode(Core.Settings.CalendarViewMode.Month); break;
            case CalendarCommand.Days:               vm.SetMode(Core.Settings.CalendarViewMode.Days, result.Days); break;
            case CalendarCommand.GoToDate:           ShowGoToDate(); break;
            case CalendarCommand.ToggleWeekends:     vm.ToggleWeekends(); break;
            case CalendarCommand.ToggleDeclined:     vm.ToggleDeclined(); break;
            case CalendarCommand.ZoomIn:             vm.ZoomBy(8); break;
            case CalendarCommand.ZoomOut:            vm.ZoomBy(-8); break;
            case CalendarCommand.ZoomReset:          vm.ZoomReset(); break;
            case CalendarCommand.ToggleTheme:        _args.ToggleTheme(); break;
            case CalendarCommand.NextEvent:          vm.SelectAdjacent(1); break;
            case CalendarCommand.PreviousEvent:      vm.SelectAdjacent(-1); break;
            case CalendarCommand.DeleteSelected:     vm.Fire(() => vm.DeleteAsync([.. vm.Selection], sendUpdates: true), "event.delete.failed"); break;
            case CalendarCommand.CancelEventQuietly: vm.Fire(() => vm.DeleteAsync([.. vm.Selection], sendUpdates: false), "event.delete.failed"); break;
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

    void ShowNotice()
    {
        _noticeTimer.Stop();
        if (ViewModel.Notice is not { } notice)
        {
            NoticeBar.IsOpen = false;
            return;
        }

        NoticeBar.Message     = notice.Text;
        UndoButton.Visibility = notice.CanUndo ? Visibility.Visible : Visibility.Collapsed;
        NoticeBar.IsOpen      = true;
        _noticeTimer.Start();
    }

    void OnUndoClick(object sender, RoutedEventArgs e) => ViewModel.Undo();

    // Only the user's close counts; the bar also closes in code when one notice replaces another
    void OnNoticeClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (args.Reason == InfoBarCloseReason.CloseButton)
        {
            ViewModel.DismissNotice();
        }
    }

    static bool IsDown(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    void OnLayoutChanged(object? sender, EventArgs e) => ApplyView();

    void OnCalendarsChanged(object? sender, EventArgs e) => UpdateEmptyState();

    void UpdateEmptyState() =>
        EmptyState.Visibility = ViewModel.Calendars.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    void OnAddAccountClick(object sender, RoutedEventArgs e) => _args.OpenAccounts();
}
