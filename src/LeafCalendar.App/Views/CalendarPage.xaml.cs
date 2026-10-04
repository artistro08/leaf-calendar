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
/// under the title bar. The side panes lie over the page and slide by composition while the island stays put
/// (see CalendarPage.Panes.cs).
/// </summary>
public sealed partial class CalendarPage : Page
{
    /// <summary>Width of the open sidebar.</summary>
    public const double SidebarWidth = 264;

    /// <summary>Width of the open details panel at the minimum window and on normal ones (it grows on wide windows).</summary>
    public const double MinDetailsWidth = 320;

    /// <summary>Widest the details panel grows.</summary>
    public const double MaxDetailsWidth = 480;

    /// <summary>Width of the open details panel now (<see cref="DetailsWidthFor"/> the window's width).</summary>
    public double DetailsWidth { get; private set; } = MinDetailsWidth;

    /// <summary>
    /// The details panel's width in a window <paramref name="windowWidth"/> wide: a quarter of it on the 8 DIP grid,
    /// from <see cref="MinDetailsWidth"/> (every window up to 1280) to <see cref="MaxDetailsWidth"/> (1920 and wider),
    /// so the editor's fields get room on a wide screen.
    /// </summary>
    public static double DetailsWidthFor(double windowWidth) => Math.Clamp(Math.Round(windowWidth / 4 / 8) * 8, MinDetailsWidth, MaxDetailsWidth);

    /// <summary>The period title's inset from the island's left edge.</summary>
    public const double TitleInset = 17;

    /// <summary>
    /// The title bar toolbar ends this far in from the island's right edge, or from the caption buttons when
    /// the details panel is closed. Its last icon's glyph sits 8 further in, so the icons' ink ends about as
    /// far from the edge as the title's starts from the other one.
    /// </summary>
    public const double ToolbarInset = 6;

    // Room for the title bar's pane toggle, which sits over the island's corner while the sidebar is closed
    private const double PaneToggleClearance = 44;

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _noticeTimer;

    // "E then ..." sequences; E opens the editor at once on an event you can change, while the second key may still
    // come for 1.5 s (on an invite a lone E waits for the timer)
    private readonly KeySequence _keys = new(TimeProvider.System);
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _sequenceTimer;
    private bool _editorFromE;
    private CalendarPageArgs _args = null!;
    private IDisposable? _view;
    private bool _viewIsMonth;

    /// <summary>Creates the page.</summary>
    public CalendarPage()
    {
        InitializeComponent();

        // Notices Hide Themselves (before the delete's 6 s undo window ends)
        _noticeTimer = DispatcherQueue.CreateTimer();
        _noticeTimer.Interval = TimeSpan.FromSeconds(5);
        _noticeTimer.IsRepeating = false;
        _noticeTimer.Tick += (_, _) => ViewModel.DismissNotice();

        // A Lone E Edits Once The Sequence Times Out (not while E is still held, and not if an editor opened or
        // focus moved into a text box or popup meanwhile; an instant E's editor is already open)
        _sequenceTimer = DispatcherQueue.CreateTimer();
        _sequenceTimer.Interval = KeySequence.Timeout;
        _sequenceTimer.IsRepeating = false;
        _sequenceTimer.Tick += (_, _) =>
        {
            var instant = _editorFromE;
            _editorFromE = false;
            var expired = _keys.Expire();
            if (!instant && ViewModel.Editing is null && !Controls.KeyState.IsDown(VirtualKey.E) && !ShortcutsBlocked())
            {
                Execute(expired);
            }
        };

        // A Click In The Details Panel Means The Instant E's Editor Is In Use (later keys are typing, not a second key)
        Details.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _editorFromE = false), handledEventsToo: true);

        // The Toasts Are Raised Over The Calendar Like Flyouts (the zone and overlay bars are raised as they're added)
        Float(SharingHint);
        Float(NoticeBar);

        // Focus Starts On The Calendar (after Windows' own first pick, which was the mini month's first chevron; not out of
        // a box or menu something already opened)
        Loaded += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!ShortcutsBlocked())
            {
                FocusCalendar();
            }
        });
    }

    /// <summary>
    /// Rests keyboard focus on the calendar itself, which draws no focus ring: shortcuts and the arrow keys drive the
    /// calendar from there, and Space or Enter press nothing. Tab moves on to the sidebar as usual.
    /// </summary>
    /// <returns>True when the calendar took focus (false while it's hidden, such as behind Settings).</returns>
    public bool FocusCalendar() => Focus(FocusState.Programmatic);

    /// <summary>The page's view model.</summary>
    public CalendarViewModel ViewModel => _args.ViewModel;

    /// <summary>The sidebar or details panel started to open or close (the island already has its new size).</summary>
    public event EventHandler<PanesChangedEventArgs>? PanesChanged;

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _args = (CalendarPageArgs)e.Parameter;

        Sidebar.Attach(ViewModel);
        Details.Attach(ViewModel);
        Sidebar.ShortcutsRequested += OnShortcutsRequested;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.LayoutChanged += OnLayoutChanged;
        ViewModel.CalendarsChanged += OnCalendarsChanged;
        ViewModel.DetailsOpenRequested += OnDetailsOpenRequested;

        // Repeating Events Ask Which Events A Change Applies To
        ViewModel.AskScope = (includeFollowing, includeThis) => ScopeDialog.AskAsync(this, includeFollowing, includeThis);

        PeriodTitle.Text = ViewModel.PeriodTitle;
        SetSidebarOpen(ViewModel.Settings.SidebarOpen, animate: false);
        SetDetailsOpen(ViewModel.Settings.DetailsPanelOpen, animate: false);
        ViewModel.ReloadCalendars();
        UpdateEmptyState();
        DrillIntoView();
        ApplyView();

        // Each Track's Own Wiring (Milestone 5)
        AttachNavigate();
        AttachPeople();
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
        // Each Track's Own Unwiring (the view model outlives the window in tray mode)
        DetachNavigate();
        DetachPeople();

        ViewModel.LayoutChanged -= OnLayoutChanged;
        ViewModel.CalendarsChanged -= OnCalendarsChanged;
        ViewModel.DetailsOpenRequested -= OnDetailsOpenRequested;
        ViewModel.AskScope = null;
        _noticeTimer.Stop();
        _sequenceTimer.Stop();
        if (_framePending)
        {
            // A slide's frame wait (a static event) would otherwise hold the detached page until some window renders
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendered -= OnSlideFrame;
            _framePending = false;
        }

        _keys.Expire();
        _editorFromE = false;
        Sidebar.Detach();
        Details.Detach();
        Sidebar.ShortcutsRequested -= OnShortcutsRequested;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _view?.Dispose();
        _view = null;
        ViewHost.Children.Clear();
    }

    /// <summary>
    /// Opacity of the window's chrome while another window is active: the stock title bar's own
    /// <c>TitleBarDeactivatedOpacity</c>, so the period title and the sidebar dim with the title bar's icons.
    /// </summary>
    public const double InactiveOpacity = 0.5;

    /// <summary>Dims the period title, the sidebar, and the details panel (or the share panel in its place) while the window isn't the active one, like the title bar does.</summary>
    public void SetWindowActive(bool active)
    {
        var opacity = active ? 1 : InactiveOpacity;
        PeriodTitle.Opacity = opacity;
        Details.Opacity = opacity;
        ShortcutsCornerButton.Opacity = opacity;
        _slotsPanel?.Opacity = opacity;
        Sidebar.SetWindowActive(active);
    }

    /// <summary>Shows or hides the sidebar (sliding when <paramref name="animate"/>) and remembers the choice.</summary>
    public void SetSidebarOpen(bool open, bool animate)
    {
        SetPaneOpen(sidebar: true, open, animate);
        if (ViewModel.Settings.SidebarOpen != open)
        {
            ViewModel.Remember(s => s with { SidebarOpen = open });
        }
    }

    /// <summary>Shows or hides the details panel (sliding when <paramref name="animate"/>) and remembers the choice.</summary>
    public void SetDetailsOpen(bool open, bool animate)
    {
        SetPaneOpen(sidebar: false, open, animate);

        // Closing Ends An E Sequence (the next key is a shortcut again, not typing into the hidden editor's title)
        if (!open)
        {
            _editorFromE = false;
            _sequenceTimer.Stop();
            _keys.Expire();
        }

        if (ViewModel.Settings.DetailsPanelOpen != open)
        {
            ViewModel.Remember(s => s with { DetailsPanelOpen = open });
        }
    }

    // Slides the pane (CalendarPage.Panes.cs) and reports the change so the title bar can follow
    private void SetPaneOpen(bool sidebar, bool open, bool animate)
    {
        // With the sidebar closed, its keyboard button stands in the window's bottom-left corner instead
        if (sidebar)
        {
            ShortcutsCornerButton.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        }

        // The title bar (toolbar, search icon) follows once the slide starts, so it moves with the island and not ahead of it
        SlidePane(sidebar, open, animate, () => PanesChanged?.Invoke(this, new PanesChangedEventArgs(animate, open)));

        // With the sidebar closed the title bar's pane toggle sits over the island's corner, so the title moves right
        PlaceTitle();
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

    // Selecting an event (or several) opens the panel so the details are visible; the title follows the period
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
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

        // An Edit Ended (Cancel, Esc, Another Event) Ends A Waiting "E Then ..." Too, So The Timer Can't Reopen It (also
        // after a click in the panel already marked the instant E's editor as in use)
        if (e.PropertyName == nameof(CalendarViewModel.Editing) && ViewModel.Editing is null && (_editorFromE || _keys.IsPending))
        {
            _editorFromE = false;
            _sequenceTimer.Stop();
            _keys.Expire();
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

    private void OnDetailsOpenRequested(object? sender, EventArgs e) => SetDetailsOpen(true, animate: true);

    // The keyboard buttons (the sidebar's, or the window's bottom-left one while it's closed): the same cheat sheet as ?,
    // even while the editor shows (the sheet doesn't touch the edit)
    private void OnShortcutsRequested(object? sender, EventArgs e) => ShowShortcutSheet();

    private void OnShortcutsClick(object sender, RoutedEventArgs e) => ShowShortcutSheet();

    // A tap on empty calendar space clears the selection and ends an edit (events and chips mark their own taps handled)
    private void OnViewHostTapped(object sender, TappedRoutedEventArgs e) => ViewModel.ClearSelection();

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Esc With The Cheat Sheet Open Only Closes It
        if (_sheet is not null)
        {
            CloseShortcutSheet();
            args.Handled = true;
            return;
        }

        // Esc During A Drag Only Cancels The Drag
        if ((_view is Controls.TimeGridView grid && grid.CancelDrag()) || (_view is Controls.MonthGridView month && month.CancelDrag()))
        {
            args.Handled = true;
            return;
        }

        // Esc While Scheduling Stops It
        if (ViewModel.IsSharing)
        {
            args.Handled = true;
            ViewModel.StopSharing();
            return;
        }

        ViewModel.ClearSelection();
        args.Handled = true;
    }

    /// <summary>
    /// Runs the calendar shortcut for a key press, called by the window so shortcuts work wherever focus is.
    /// Ignored while typing, or while focus is in a flyout, menu, dialog, or the go-to-date picker (a hover tooltip
    /// never takes focus, so it doesn't block shortcuts). Event shortcuts act on the selection; E opens the editor at once
    /// and starts a 1.5 s sequence (spec 8.7): while the editor is untouched, Y / N / M / E close it and reply or email,
    /// U moves to the end time, and any other key types into the title. With several events selected, Delete and
    /// Ctrl+Shift+Delete act on all of them, while the one-event shortcuts (E, E then Y / N / M / E / U, V) show "Select one event" and Ctrl+J joins the next meeting.
    /// </summary>
    /// <returns>True when the key was a shortcut and has been handled.</returns>
    public bool HandleShortcut(KeyRoutedEventArgs e)
    {
        // Second Key After An Instant E (the editor opened but nothing was typed yet)
        if (_editorFromE && _keys.IsPending && ViewModel.Editing is not null && !IsModifier(e.Key))
        {
            if (e.KeyStatus.WasKeyDown && e.Key == VirtualKey.E)
            {
                return true;
            }

            var second = ResolveShortcut(e.Key);
            _editorFromE = false;
            _sequenceTimer.Stop();
            switch (second.Command)
            {
                case CalendarCommand.EditDuration:
                    Details.EditorView?.FocusEndTime();
                    return true;
                case CalendarCommand.RsvpYes or CalendarCommand.RsvpNo or CalendarCommand.RsvpMaybe or CalendarCommand.EmailGuests:
                    ViewModel.CancelEdit();
                    Execute(second);
                    return true;
                case CalendarCommand.EditTimeZone:
                    EditTimeZone();
                    return true;
                case CalendarCommand.ParticipantOverlay:
                    ViewModel.CancelEdit();
                    Execute(second);
                    return true;
                default:
                    // Typing: the title box gets the key, even if its own focus hasn't landed yet
                    Details.EditorView?.FocusTitleNow();
                    return false;
            }
        }

        // S Over A New, Untouched Event Shares Availability Instead (the empty editor closes; typing in a box stays typing)
        if (e.Key == VirtualKey.S && ViewModel.Editing is { IsNew: true } fresh && IsUntouched(fresh) && !ShortcutsBlocked()
            && !Controls.KeyState.IsDown(VirtualKey.Control) && !Controls.KeyState.IsDown(VirtualKey.Shift) && !Controls.KeyState.IsDown(VirtualKey.Menu))
        {
            ViewModel.CancelEdit();
            StartShareAvailability();
            return true;
        }

        // A Hidden Editor Doesn't Block Shortcuts (the editor handles its own keys while it shows)
        if (EditorShowing)
        {
            return false;
        }

        if (ShortcutsBlocked())
        {
            return false;
        }

        // Plain Left And Right Move Between Days While Focus Is In The Mini Month (a grid that moves focus with the arrows)
        if (e.Key is VirtualKey.Left or VirtualKey.Right && !Controls.KeyState.IsDown(VirtualKey.Menu)
            && FocusWithin(element => element is UIElement { XYFocusKeyboardNavigation: XYFocusKeyboardNavigationMode.Enabled }))
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

        var result = ResolveShortcut(e.Key);
        _sequenceTimer.Stop();
        if (result.Command == CalendarCommand.SequenceStarted)
        {
            // E Brings Back A Hidden Editor Instead Of Starting Another
            if (ViewModel.Editing is not null)
            {
                _keys.Expire();
                SetDetailsOpen(true, animate: true);
                return true;
            }

            // E Edits At Once When The One Selected Event Can Be Changed (an invite waits for the second key; not while
            // sharing, where the share panel stands in the editor's place)
            _sequenceTimer.Start();
            if (ViewModel.SelectedInfo is { CanEdit: true } && !ViewModel.IsSharing)
            {
                ViewModel.BeginEdit();
                _editorFromE = ViewModel.Editing is not null;
            }

            return true;
        }

        if (result.Command == CalendarCommand.None)
        {
            return false;
        }

        // C Brings Back A Hidden Editor Too (a drag or double-click on empty time starts a new event instead)
        if (result.Command == CalendarCommand.CreateEvent && ViewModel.Editing is not null)
        {
            SetDetailsOpen(true, animate: true);
            return true;
        }

        Execute(result);
        return true;
    }

    /// <summary>
    /// Runs a calendar command as if its shortcut were pressed (the command menu's one path into every action). Like
    /// the C key, "create" brings back a hidden editor instead of replacing its unsaved text.
    /// </summary>
    public void RunCommand(CalendarCommand command, int days = 0)
    {
        if (command == CalendarCommand.CreateEvent && ViewModel.Editing is not null)
        {
            SetDetailsOpen(true, animate: true);
            return;
        }

        // The Editor Handles Its Own Keys While It Shows, So Menu Commands Wait Too (the title bar's search button still
        // opens the menu: a row picked there hides the editor first)
        if (EditorShowing && command is not (CalendarCommand.CommandMenu or CalendarCommand.Search))
        {
            return;
        }

        Execute(new ShortcutResult(command, days));
    }

    /// <summary>
    /// Goes back or forward through the visited places, under the same rules as the keyboard shortcuts: nothing
    /// happens while the editor shows or while focus is in a text box, flyout, menu, or dialog.
    /// </summary>
    /// <returns>True when the calendar moved.</returns>
    public bool TryNavigateHistory(bool back)
    {
        if (EditorShowing || ShortcutsBlocked())
        {
            return false;
        }

        return back ? ViewModel.GoBack() : ViewModel.GoForward();
    }

    // The editor is on screen: the details panel is open and showing it, not the share panel in its place
    private bool EditorShowing => ViewModel.Editing is not null && IsDetailsOpen && !ViewModel.IsSharing;

    // Typing, or focus in a flyout, menu, or dialog
    private bool ShortcutsBlocked()
    {
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        return focused is TextBox or PasswordBox or AutoSuggestBox or NumberBox or RichEditBox || IsInOpenPopup(focused);
    }

    // True when the focused element, or something it sits in, matches (a focused link, not in the visual tree, never does)
    private bool FocusWithin(Func<DependencyObject, bool> match)
    {
        for (DependencyObject? current = FocusManager.GetFocusedElement(XamlRoot) as UIElement; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (match(current))
            {
                return true;
            }
        }

        return false;
    }

    // Nothing changed since it opened: the same calendar, and no field Google would be sent (every field of the draft)
    private static bool IsUntouched(EventEditorViewModel editor)
    {
        var now = editor.ToDraft();
        return now.AccountId == editor.Before.AccountId && now.CalendarId == editor.Before.CalendarId && EventJson.BuildPatch(editor.Before, now).Count == 0;
    }

    private static bool IsModifier(VirtualKey key) => key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
        or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
        or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
        or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    // A key press as a shortcut; a punctuation key brings the character it types on this layout, so ? is whichever key types "?"
    private ShortcutResult ResolveShortcut(VirtualKey key)
    {
        var shift = Controls.KeyState.IsDown(VirtualKey.Shift);
        return _keys.Resolve(key.ToString(), Controls.KeyState.IsDown(VirtualKey.Control), shift, Controls.KeyState.IsDown(VirtualKey.Menu), Controls.KeyState.Typed(key, shift));
    }

    private void Execute(ShortcutResult result)
    {
        var vm = ViewModel;
        vm.Trace("command", result.Command.ToString());

        // One-Event Shortcuts With Several Selected: say why nothing happens
        if (vm.Selection.Count > 1 && result.Command is CalendarCommand.EditEvent or CalendarCommand.EditDuration
            or CalendarCommand.RsvpYes or CalendarCommand.RsvpNo or CalendarCommand.RsvpMaybe
            or CalendarCommand.EmailGuests or CalendarCommand.OpenMeetingLink
            or CalendarCommand.EditTimeZone or CalendarCommand.ParticipantOverlay)
        {
            vm.ShowMessage("Select one event");
            return;
        }

        // Sharing Marks Times, So Nothing Opens An Editor Behind The Share Panel (the grids refuse to create too)
        if (vm.IsSharing && result.Command is CalendarCommand.CreateEvent or CalendarCommand.EditEvent or CalendarCommand.EditDuration)
        {
            return;
        }

        switch (result.Command)
        {
            case CalendarCommand.Today: vm.GoToToday(); break;
            case CalendarCommand.Previous: vm.Previous(); break;
            case CalendarCommand.Next: vm.Next(); break;
            case CalendarCommand.NavigateBack: TryNavigateHistory(back: true); break;
            case CalendarCommand.NavigateForward: TryNavigateHistory(back: false); break;
            case CalendarCommand.DayView: vm.SetMode(Core.Settings.CalendarViewMode.Day); break;
            case CalendarCommand.WeekView: vm.SetMode(Core.Settings.CalendarViewMode.Week); break;
            case CalendarCommand.MonthView: vm.SetMode(Core.Settings.CalendarViewMode.Month); break;
            case CalendarCommand.Days: vm.SetMode(Core.Settings.CalendarViewMode.Days, result.Days); break;
            case CalendarCommand.GoToDate: ShowGoToDate(); break;
            case CalendarCommand.ToggleWeekends: vm.ToggleWeekends(); break;
            case CalendarCommand.ToggleDeclined: vm.ToggleDeclined(); break;
            case CalendarCommand.ZoomIn: vm.ZoomBy(8); break;
            case CalendarCommand.ZoomOut: vm.ZoomBy(-8); break;
            case CalendarCommand.ZoomReset: vm.ZoomReset(); break;
            case CalendarCommand.ToggleTheme: _args.ToggleTheme(); break;
            case CalendarCommand.NextEvent: vm.SelectAdjacent(1); break;
            case CalendarCommand.PreviousEvent: vm.SelectAdjacent(-1); break;
            case CalendarCommand.DeleteSelected: vm.Fire(() => vm.DeleteAsync([.. vm.Selection], sendUpdates: true), "event.delete.failed"); break;
            case CalendarCommand.CreateEvent: vm.BeginCreateNow(); break;
            case CalendarCommand.CancelEventQuietly: vm.Fire(() => vm.DeleteAsync([.. vm.Selection], sendUpdates: false), "event.delete.failed"); break;
            case CalendarCommand.SelectAll: vm.SelectAllVisible(); break;
            case CalendarCommand.ToggleSelect: vm.ToggleFocused(); break;
            case CalendarCommand.Copy: vm.CopySelection(); break;
            case CalendarCommand.Cut: vm.Fire(vm.CutSelectionAsync, "calendar.cut.failed"); break;
            case CalendarCommand.Paste: vm.Paste(); break;
            case CalendarCommand.Undo: vm.Undo(); break;
            case CalendarCommand.EditEvent: vm.BeginEdit(); break;
            case CalendarCommand.EditDuration: vm.BeginEdit(focusEnd: true); break;
            case CalendarCommand.RsvpYes: vm.Fire(() => vm.RespondAsync(ResponseStatus.Accepted, null, emailOrganizer: true)); break;
            case CalendarCommand.RsvpNo: vm.Fire(() => vm.RespondAsync(ResponseStatus.Declined, null, emailOrganizer: true)); break;
            case CalendarCommand.RsvpMaybe: vm.Fire(() => vm.RespondAsync(ResponseStatus.Tentative, null, emailOrganizer: true)); break;
            case CalendarCommand.EmailGuests: vm.Fire(vm.EmailGuestsAsync); break;
            case CalendarCommand.JoinMeeting: vm.Fire(() => vm.JoinAsync()); break;
            case CalendarCommand.OpenMeetingLink: vm.Fire(vm.OpenMeetingLinkAsync); break;
            case CalendarCommand.CommandMenu or CalendarCommand.Search: OpenCommandMenu(); break;
            case CalendarCommand.ShortcutSheet: ShowShortcutSheet(); break;
            case CalendarCommand.OpenSettings: vm.OpenSettings?.Invoke(SettingsSection.General); break;
            case CalendarCommand.TimeTravel: StartTimeTravel(); break;
            case CalendarCommand.ShareAvailability: StartShareAvailability(); break;
            case CalendarCommand.PeopleOverlay: ShowPeopleOverlay(); break;
            case CalendarCommand.MeetWith: ShowMeetWith(); break;
            case CalendarCommand.ParticipantOverlay: ShowParticipantOverlay(); break;
            case CalendarCommand.EditTimeZone: EditTimeZone(); break;
        }
    }

    // Jump to date: the command menu, asking for a date in words ("nov 5th", "10 weeks", "next fri"); its date row goes
    // there. (A CalendarView in a flyout anchored to the page had no room, was squashed against the top, and crashed
    // in a layout cycle.)
    private void ShowGoToDate()
    {
        OpenCommandMenu();
        _commandMenu?.AskForDate();
    }

    private void ShowNotice()
    {
        _noticeTimer.Stop();
        if (ViewModel.Notice is not { } notice)
        {
            NoticeBar.IsOpen = false;
            return;
        }

        NoticeBar.Message = notice.Text;
        UndoButton.Visibility = notice.CanUndo ? Visibility.Visible : Visibility.Collapsed;
        NoticeBar.IsOpen = true;
        ResumeNoticeTimer();
    }

    // The Notice Waits While The Pointer Or Focus Is On It (its Undo doesn't vanish from under you), then hides 5 s later
    private bool _noticePointerOver;

    private void ResumeNoticeTimer()
    {
        if (ViewModel.Notice is not null && !_noticePointerOver && !FocusWithin(element => ReferenceEquals(element, NoticeBar)))
        {
            _noticeTimer.Start();
        }
    }

    private void OnNoticePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _noticePointerOver = true;
        _noticeTimer.Stop();
    }

    private void OnNoticePointerExited(object sender, PointerRoutedEventArgs e)
    {
        _noticePointerOver = false;
        ResumeNoticeTimer();
    }

    private void OnNoticeGotFocus(object sender, RoutedEventArgs e) => _noticeTimer.Stop();

    private void OnNoticeLostFocus(object sender, RoutedEventArgs e) => ResumeNoticeTimer();

    private void OnUndoClick(object sender, RoutedEventArgs e) => ViewModel.Undo();

    // Only the user's close counts; the bar also closes in code when one notice replaces another
    private void OnNoticeClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (args.Reason == InfoBarCloseReason.CloseButton)
        {
            ViewModel.DismissNotice();
        }
    }

    // True when focus sits inside an open popup (flyouts, menus, and dialogs take focus; tooltips never do). Compared by
    // reference up the visual tree: type tests on popup content fail under Native AOT
    private bool IsInOpenPopup(object? focused)
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

            // Focus On Something The Tree Can't Place (a popup's own element, such as a date picker's day, that Native
            // AOT didn't recognize): with a popup open, that's where it is, so the shortcut stays out of it
            if (start is null && focused is not null)
            {
                return true;
            }

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

    private void OnLayoutChanged(object? sender, EventArgs e)
    {
        DrillIntoView();
        ApplyView();
    }

    // How many days the last view showed (a month counts as 35), to tell drilling in from zooming out
    private int _viewSpan;

    // The drill: fewer days than before drill in (the view grows from 95%), more zoom out (it settles from 105%), the way
    // Windows moves into and out of a level, with a quick fade up from half
    private const float DrillInFrom = 0.95f;
    private const float ZoomOutFrom = 1.05f;
    private const float DrillFadeFrom = 0.5f;
    private static readonly TimeSpan DrillDuration = TimeSpan.FromMilliseconds(167);
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(83);

    // Only a change of span animates (a layout change that keeps it, like the hour zoom, doesn't). It starts at once:
    // the view switch lays the new view out on its new days directly (CalendarViewModel.SwitchingTo), so there's no
    // scroll to wait out and nothing is hidden first; a brand-new time grid keeps itself hidden only until its first
    // layout lands (TimeGridView), which the fade covers
    private void DrillIntoView()
    {
        var span = ViewModel.Mode == Core.Settings.CalendarViewMode.Month ? 35 : ViewModel.VisibleColumns;
        var was = _viewSpan;
        _viewSpan = span;
        if (was == 0 || was == span || ViewHost.ActualWidth <= 0 || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            return;
        }

        var from = span < was ? DrillInFrom : ZoomOutFrom;
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(ViewHost);
        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.1f, 0.9f), new System.Numerics.Vector2(0.2f, 1));
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Scale");
        visual.CenterPoint = new System.Numerics.Vector3((float)ViewHost.ActualWidth / 2, (float)ViewHost.ActualHeight / 2, 0);

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, new System.Numerics.Vector3(from, from, 1));
        scale.InsertKeyFrame(1, System.Numerics.Vector3.One, easing);
        scale.Duration = DrillDuration;
        visual.StartAnimation("Scale", scale);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, DrillFadeFrom);
        fade.InsertKeyFrame(1, 1, easing);
        fade.Duration = FadeDuration;
        visual.StartAnimation("Opacity", fade);
    }

    private void OnCalendarsChanged(object? sender, EventArgs e) => UpdateEmptyState();

    private void UpdateEmptyState() =>
        EmptyState.Visibility = ViewModel.Calendars.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnAddAccountClick(object sender, RoutedEventArgs e) => ViewModel.OpenSettings?.Invoke(SettingsSection.Accounts);
}
