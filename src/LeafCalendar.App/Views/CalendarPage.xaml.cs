using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Editing;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace LeafCalendar.App.Views;

/// <summary>Navigation parameter for <see cref="CalendarPage"/>.</summary>
public sealed record CalendarPageArgs(CalendarViewModel ViewModel, Action ToggleTheme);

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

    // "E then ..." sequences; a lone E edits when the 1.5 s timer runs out
    readonly KeySequence _keys = new(TimeProvider.System);
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _sequenceTimer;
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

        // A Lone E Edits Once The Sequence Times Out (not while E is still held, and not if an editor opened or
        // focus moved into a text box or popup meanwhile)
        _sequenceTimer = DispatcherQueue.CreateTimer();
        _sequenceTimer.Interval    = KeySequence.Timeout;
        _sequenceTimer.IsRepeating = false;
        _sequenceTimer.Tick       += (_, _) =>
        {
            var expired = _keys.Expire();
            if (ViewModel.Editing is null && !Controls.KeyState.IsDown(VirtualKey.E) && !ShortcutsBlocked())
            {
                Execute(expired);
            }
        };
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

        Sidebar.Attach(ViewModel);
        Details.Attach(ViewModel);
        ViewModel.PropertyChanged  += OnViewModelPropertyChanged;
        ViewModel.LayoutChanged    += OnLayoutChanged;
        ViewModel.CalendarsChanged += OnCalendarsChanged;

        // Repeating Events Ask Which Events A Change Applies To
        ViewModel.AskScope = (includeFollowing, includeThis) => ScopeDialog.AskAsync(XamlRoot, includeFollowing, includeThis);

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
        _sequenceTimer.Stop();
        _keys.Expire();
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
        // Closing the panel closes an open editor too (nothing to see, and shortcuts would stay off)
        if (!open)
        {
            ViewModel.CancelEdit();
        }

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

    // Nothing in the island (the grid keeps neighbor days realized and slides its header by composition) may draw
    // under the see-through panes
    void OnIslandSizeChanged(object sender, SizeChangedEventArgs e) =>
        Island.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };

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

    // Selecting an event (or several) opens the panel so the details are visible; the title follows the period
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

        if (e.PropertyName == nameof(CalendarViewModel.Editing) && ViewModel.Editing is not null && !ViewModel.Settings.DetailsPanelOpen)
        {
            SetDetailsOpen(true, animate: true);
            return;
        }

        var picked = (e.PropertyName == nameof(CalendarViewModel.SelectedInfo) && ViewModel.SelectedInfo is not null)
            || (e.PropertyName == nameof(CalendarViewModel.Selection) && ViewModel.Selection.Count > 1);
        if (picked && !ViewModel.IsRefreshingSelection && !ViewModel.Settings.DetailsPanelOpen)
        {
            SetDetailsOpen(true, animate: true);
        }
    }

    // A tap on empty calendar space clears the selection (events and chips mark their own taps handled)
    void OnViewHostTapped(object sender, TappedRoutedEventArgs e) => ViewModel.ClearSelection();

    void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Esc During A Drag Only Cancels The Drag
        if ((_view is Controls.TimeGridView grid && grid.CancelDrag()) || (_view is Controls.MonthGridView month && month.CancelDrag()))
        {
            args.Handled = true;
            return;
        }

        ViewModel.ClearSelection();
        args.Handled = true;
    }

    /// <summary>
    /// Runs the calendar shortcut for a key press, called by the window so shortcuts work wherever focus is.
    /// Ignored while typing, or while focus is in a flyout, menu, dialog, or the go-to-date picker (a hover tooltip
    /// never takes focus, so it doesn't block shortcuts). Event shortcuts act on the selection; E starts a 1.5 s
    /// sequence (spec 8.7). With several events selected, Delete and Ctrl+Shift+Delete act on all of them, while the
    /// one-event shortcuts (E, E then Y / N / M / E / U, V) show "Select one event" and Ctrl+J joins the next meeting.
    /// </summary>
    /// <returns>True when the key was a shortcut and has been handled.</returns>
    public bool HandleShortcut(KeyRoutedEventArgs e)
    {
        // The Editor Handles Its Own Keys
        if (ViewModel.Editing is not null)
        {
            return false;
        }

        if (ShortcutsBlocked())
        {
            return false;
        }

        // Modifiers Alone Never End A Sequence
        if (IsModifier(e.Key))
        {
            return false;
        }

        // A Held E Doesn't Repeat Into "E Then E"
        // Nor Does A Held Alt+Arrow Walk The Whole History
        if (e.KeyStatus.WasKeyDown && (_keys.IsPending || e.Key == VirtualKey.E
            || (e.Key is VirtualKey.Left or VirtualKey.Right && Controls.KeyState.IsDown(VirtualKey.Menu))))
        {
            return true;
        }

        var result = _keys.Resolve(e.Key.ToString(), Controls.KeyState.IsDown(VirtualKey.Control), Controls.KeyState.IsDown(VirtualKey.Shift), Controls.KeyState.IsDown(VirtualKey.Menu));
        _sequenceTimer.Stop();
        if (result.Command == CalendarCommand.SequenceStarted)
        {
            _sequenceTimer.Start();
            return true;
        }

        if (result.Command == CalendarCommand.None)
        {
            return false;
        }

        Execute(result);
        return true;
    }

    /// <summary>
    /// Goes back or forward through the visited places, under the same rules as the keyboard shortcuts: nothing
    /// happens while an event is being edited or while focus is in a text box, flyout, menu, or dialog.
    /// </summary>
    /// <returns>True when the calendar moved.</returns>
    public bool TryNavigateHistory(bool back)
    {
        if (ViewModel.Editing is not null || ShortcutsBlocked())
        {
            return false;
        }

        return back ? ViewModel.GoBack() : ViewModel.GoForward();
    }

    // Typing, or focus in a flyout, menu, dialog, or the go-to-date picker
    bool ShortcutsBlocked()
    {
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        return focused is TextBox or PasswordBox or AutoSuggestBox or NumberBox or RichEditBox or CalendarView || IsInOpenPopup(focused);
    }

    static bool IsModifier(VirtualKey key) => key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
        or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
        or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
        or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    void Execute(ShortcutResult result)
    {
        var vm = ViewModel;

        // One-Event Shortcuts With Several Selected: say why nothing happens
        if (vm.Selection.Count > 1 && result.Command is CalendarCommand.EditEvent or CalendarCommand.EditDuration
            or CalendarCommand.RsvpYes or CalendarCommand.RsvpNo or CalendarCommand.RsvpMaybe
            or CalendarCommand.EmailGuests or CalendarCommand.OpenMeetingLink)
        {
            vm.ShowMessage("Select one event");
            return;
        }

        switch (result.Command)
        {
            case CalendarCommand.Today:              vm.GoToToday(); break;
            case CalendarCommand.Previous:           vm.Previous(); break;
            case CalendarCommand.Next:               vm.Next(); break;
            case CalendarCommand.NavigateBack:       TryNavigateHistory(back: true); break;
            case CalendarCommand.NavigateForward:    TryNavigateHistory(back: false); break;
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
            case CalendarCommand.CreateEvent:        vm.BeginCreateNow(); break;
            case CalendarCommand.CancelEventQuietly: vm.Fire(() => vm.DeleteAsync([.. vm.Selection], sendUpdates: false), "event.delete.failed"); break;
            case CalendarCommand.SelectAll:          vm.SelectAllVisible(); break;
            case CalendarCommand.ToggleSelect:       vm.ToggleFocused(); break;
            case CalendarCommand.Copy:               vm.CopySelection(); break;
            case CalendarCommand.Cut:                vm.Fire(vm.CutSelectionAsync, "calendar.cut.failed"); break;
            case CalendarCommand.Paste:              vm.Paste(); break;
            case CalendarCommand.Undo:               vm.Undo(); break;
            case CalendarCommand.EditEvent:          vm.BeginEdit(); break;
            case CalendarCommand.EditDuration:       vm.BeginEdit(focusEnd: true); break;
            case CalendarCommand.RsvpYes:            vm.Fire(() => vm.RespondAsync(ResponseStatus.Accepted, null, emailOrganizer: true)); break;
            case CalendarCommand.RsvpNo:             vm.Fire(() => vm.RespondAsync(ResponseStatus.Declined, null, emailOrganizer: true)); break;
            case CalendarCommand.RsvpMaybe:          vm.Fire(() => vm.RespondAsync(ResponseStatus.Tentative, null, emailOrganizer: true)); break;
            case CalendarCommand.EmailGuests:        vm.Fire(vm.EmailGuestsAsync); break;
            case CalendarCommand.JoinMeeting:        vm.Fire(() => vm.JoinAsync()); break;
            case CalendarCommand.OpenMeetingLink:    vm.Fire(vm.OpenMeetingLinkAsync); break;
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

    // True when focus sits inside an open popup (flyouts, menus, and dialogs take focus; tooltips never do). Compared by
    // reference up the visual tree: type tests on popup content fail under Native AOT
    bool IsInOpenPopup(object? focused)
    {
        var roots = VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Select(p => p.Child).OfType<object>().ToList();
        if (roots.Count == 0)
        {
            return false;
        }

        try
        {
            // A focused link (a text element, not in the visual tree) starts from the text block that holds it
            var start = focused is TextElement text ? text.ContentStart.VisualParent : focused as DependencyObject;
            for (var current = start; current is not null; current = VisualTreeHelper.GetParent(current))
            {
                if (roots.Exists(root => ReferenceEquals(root, current)))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
        {
            // Something the visual tree can't walk (a text element Native AOT didn't recognize) isn't in a popup
        }

        return false;
    }

    void OnLayoutChanged(object? sender, EventArgs e) => ApplyView();

    void OnCalendarsChanged(object? sender, EventArgs e) => UpdateEmptyState();

    void UpdateEmptyState() =>
        EmptyState.Visibility = ViewModel.Calendars.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    void OnAddAccountClick(object sender, RoutedEventArgs e) => ViewModel.OpenSettings?.Invoke(SettingsSection.Accounts);
}
