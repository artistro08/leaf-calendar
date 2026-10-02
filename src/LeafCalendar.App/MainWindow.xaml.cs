using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using LeafCalendar.App.ViewModels;
using LeafCalendar.App.Views;
using LeafCalendar.App.Views.Settings;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Sync;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace LeafCalendar.App;

/// <summary>
/// Main window: a tall XAML title bar (caption buttons match its 48 px height) holding the calendar
/// toolbar, a Mica backdrop, and a page frame holding the calendar (first-run setup is its own window,
/// <see cref="Views.Onboarding.OnboardingWindow"/>, shown before this one). On the calendar page the
/// frame runs under the title bar, so the sidebars and the calendar island reach the top edge; the
/// title bar stays transparent and only its buttons take clicks. The pane toggle shows only on the
/// calendar. Settings and accounts live in their own window (<see cref="SettingsWindow"/>), which
/// shares the App's calendar view model, so its changes show here right away; it stays open when this window closes.
/// </summary>
[SuppressMessage("Design", "CA1001", Justification = "Windows aren't disposable; the App owns the view model.")]
public sealed partial class MainWindow : Window
{
    // Title Bar Toolbar Slide (the island's right edge moves with the details pane, so the toolbar follows it
    // on the SplitView's own timing and curve; the transform is built here and kept, since reading
    // RenderTransform back fails its cast under Native AOT)
    static readonly TimeSpan PaneOpenDuration  = TimeSpan.FromMilliseconds(200);
    static readonly TimeSpan PaneCloseDuration = TimeSpan.FromMilliseconds(100);

    // Smallest window, in DIPs: both panes open (264 + 320) around an island that still fits the
    // widest title ("September 2026": 17 in, 170 wide), a 16 gap, and the widest toolbar (about 257,
    // with "31 days" on the view button, plus 36 for the sync status slot and its gap) 6 in from the island's right edge, which also leaves the
    // week grid its 56 gutter and seven 48-wide days. The height keeps the sidebar's mini month, an account with three calendars, and
    // its footer, and shows about eight hours of the grid at the default hour height.
    const double MinimumWidth  = CalendarPage.SidebarWidth + CalendarPage.TitleInset + 170 + 16 + 257 + 36 + CalendarPage.ToolbarInset + CalendarPage.DetailsWidth;
    const double MinimumHeight = 540;

    // The event actions' right end, in from the details panel's left edge: the edit glyph (8 in on its 32-wide
    // button) starts at the panel's 16 px content inset, and the delete button touches it
    const double EventActionsSpan = 16 - 8 + 32 + 32;

    readonly LeafServices _services;

    // Lifts the whole title bar 2 physical pixels (set per display scale in LiftTitleBar)
    readonly TranslateTransform _titleBarLift = new();
    readonly TranslateTransform _toolbarShift = new();
    readonly OverlappedPresenter _presenter = OverlappedPresenter.Create();
    readonly CalendarViewModel _calendar;

    // Changes waiting (online) show only once they've waited this long
    static readonly TimeSpan WaitingDelay = TimeSpan.FromSeconds(2);
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _waitingTimer;
    bool _waitingDue;

    // The window's size while restored (not maximized or minimized), saved on close
    Core.Views.WindowSize _restoredSize;
    Storyboard? _toolbarSlide;
    (double Right, double Toggle, bool Sidebar, bool Calendar)? _titleBarLayout;

    /// <summary>Creates the window on the App's calendar view model (Settings shares it). <see cref="App"/> owns both.</summary>
    public MainWindow(LeafServices services, CalendarViewModel calendar)
    {
        _services = services;
        _calendar = calendar;
        InitializeComponent();

        // Sync Status Waiting Delay
        _waitingTimer             = DispatcherQueue.CreateTimer();
        _waitingTimer.Interval    = WaitingDelay;
        _waitingTimer.IsRepeating = false;
        _waitingTimer.Tick       += (_, _) =>
        {
            _waitingDue = true;
            ShowSyncState();
        };
        ToolbarSlide.RenderTransform = _toolbarShift;

        // Shortcuts are handled at the root so they work wherever focus is
        RootGrid.PreviewKeyDown += (_, e) =>
        {
            if (ContentFrame.Content is CalendarPage page && page.HandleShortcut(e))
            {
                e.Handled = true;
            }
        };

        // Mouse Back And Forward Buttons (even over controls that handle the press)
        RootGrid.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) =>
        {
            if (ContentFrame.Content is not CalendarPage page)
            {
                return;
            }

            // Only The Press That Just Happened Counts, Not A Button Still Held
            var kind = e.GetCurrentPoint(RootGrid).Properties.PointerUpdateKind;
            if (kind == Microsoft.UI.Input.PointerUpdateKind.XButton1Pressed && page.TryNavigateHistory(back: true))
            {
                e.Handled = true;
            }
            else if (kind == Microsoft.UI.Input.PointerUpdateKind.XButton2Pressed && page.TryNavigateHistory(back: false))
            {
                e.Handled = true;
            }
        }), handledEventsToo: true);

        // Window Presenter (ours, kept, so its minimum size can be set without casting AppWindow.Presenter)
        AppWindow.SetPresenter(_presenter);

        // Window Size (as it last closed, else the first-run default; a restored window's size is kept as it changes)
        // (the minimum applies first, and a size saved before the minimum grew is grown to it)
        var opening = (_calendar.Settings.MainWindowSize ?? Core.Views.WindowSize.MainDefault).AtLeast(MinimumWidth, MinimumHeight);
        ApplyMinimumSize();
        _restoredSize = opening with { Maximized = false };
        Interop.WindowPlacement.Restore(AppWindow, _presenter, opening);
        AppWindow.Changed += (_, e) =>
        {
            if (e.DidSizeChange && _presenter.State == OverlappedPresenterState.Restored)
            {
                _restoredSize = Interop.WindowPlacement.SizeOf(AppWindow);
            }
        };

        // Title Bar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppTitleBar.RenderTransform = _titleBarLift;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        ToolbarHost.SizeChanged += (_, _) => UpdateTitleBarLayout(animate: false);
        SearchButton.LayoutUpdated += (_, _) => FollowSearchAnchor();
        RootGrid.Loaded += (_, _) =>
        {
            ApplyMinimumSize();
            LiftTitleBar();
            RootGrid.XamlRoot.Changed += (_, _) =>
            {
                ApplyMinimumSize();
                LiftTitleBar();
                UpdateTitleBarLayout(animate: false);
            };
        };

        Activated += OnActivated;
        Closed    += (_, _) =>
        {
            // Closing doesn't navigate, so release the page's views here; the view model is the App's (Settings may
            // still be open on it, and Leaf stays in the tray)
            (ContentFrame.Content as CalendarPage)?.Detach();
            _waitingTimer.Stop();
            _calendar.LayoutChanged   -= OnCalendarLayoutChanged;
            _calendar.PropertyChanged -= OnCalendarPropertyChanged;

            // Remember The Size For Next Time (the restored size, and whether it was maximized)
            var size = _restoredSize with { Maximized = _presenter.State == OverlappedPresenterState.Maximized };
            _calendar.Remember(s => s with { MainWindowSize = size }, inBackground: false);
        };

        ShowCalendar();
    }

    /// <summary>Restores the window if it's minimized and brings it to the front (another launch was redirected here).</summary>
    public void BringToFront()
    {
        if (_presenter.State == OverlappedPresenterState.Minimized)
        {
            _presenter.Restore();
        }

        Activate();
        Interop.Foreground.Take(new HWND(Win32Interop.GetWindowFromWindowId(AppWindow.Id)));
    }

    /// <summary>Applies the app theme to the content and caption buttons.</summary>
    public void ApplyTheme(AppTheme theme) => ApplyTheme(AppWindow, RootGrid, theme);

    /// <summary>Applies the app theme to a window's content (<paramref name="root"/>) and caption buttons.</summary>
    internal static void ApplyTheme(AppWindow window, FrameworkElement root, AppTheme theme)
    {
        root.RequestedTheme = ElementThemeOf(theme);

        window.TitleBar.PreferredTheme = theme switch
        {
            AppTheme.Light => TitleBarTheme.Light,
            AppTheme.Dark  => TitleBarTheme.Dark,
            _              => TitleBarTheme.UseDefaultAppMode,
        };
    }

    /// <summary>The XAML theme for an app theme (shared by the windows and the tray host).</summary>
    internal static ElementTheme ElementThemeOf(AppTheme theme) => theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark  => ElementTheme.Dark,
        _              => ElementTheme.Default,
    };

    // =========================================================================
    // NAVIGATION
    // =========================================================================

    void ShowCalendar()
    {
        // Listen While Open (the App set the view model's OpenSettings)
        _calendar.LayoutChanged   += OnCalendarLayoutChanged;
        _calendar.PropertyChanged += OnCalendarPropertyChanged;
        ApplyTheme(_calendar.Settings.Theme);

        ShowSyncState();

        ContentFrame.Navigate(typeof(CalendarPage), new CalendarPageArgs(_calendar, ToggleTheme));
        ContentFrame.BackStack.Clear();
    }

    void OnCalendarLayoutChanged(object? sender, EventArgs e)
    {
        SyncMenu();
        ApplyTheme(_calendar.Settings.Theme);
    }

    void OnNavigated(object sender, NavigationEventArgs e)
    {
        var page       = e.Content as CalendarPage;
        var onCalendar = page is not null;
        _searchRide    = null;

        CalendarToolbar.Visibility            = onCalendar ? Visibility.Visible : Visibility.Collapsed;
        SearchButton.Visibility               = CalendarToolbar.Visibility;
        DetailsToggle.Visibility              = CalendarToolbar.Visibility;
        AppTitleBar.IsPaneToggleButtonVisible = onCalendar;

        if (page is not null)
        {
            page.CommandMenuShown += (_, open) => DimForCommandMenu(open);
            page.PanesChanged += (_, e) =>
            {
                UpdateTitleBarLayout(e.Animate);
                UpdateEventActions();
                DetailsToggle.IsChecked = page.IsDetailsOpen;
                ShowPaneGlyphs(page);
            };
            ShowPaneGlyphs(page);
            SyncMenu();
        }

        UpdateTitleBarLayout(animate: false);
        UpdateEventActions();
    }

    // The pane toggles' glyphs: a pane's panel is filled while it's open
    static void ShowPaneGlyphs(CalendarPage page)
    {
        Controls.PaneGlyph.SetOpen("Sidebar", page.IsSidebarOpen);
        Controls.PaneGlyph.SetOpen("Details", page.IsDetailsOpen);
    }

    // Pin the details toggle beside the caption buttons, and line the toolbar up with the calendar island's right edge
    // (ending at the toggle when the details panel is closed). Then re-punch the title bar's click-through holes for the buttons.
    // Where the title bar's content area ends is measured, not assumed: TitleBar reserves the caption
    // buttons' width in screen pixels as if they were DIPs, so above 100% its content area stops short
    // of the buttons (about 34 DIPs at 125%). Resizing calls this for every step of the drag, so
    // nothing happens unless the layout really changed.
    void UpdateTitleBarLayout(bool animate)
    {
        var page = ContentFrame.Content as CalendarPage;
        if (ToolbarHost.ActualWidth <= 0 || RootGrid.XamlRoot is null)
        {
            return;
        }

        // The Search Icon Moves With The Toolbar Host's Left Edge (Back appearing), Even When Nothing Else Changed
        PlaceSearchButton();

        // Target: The Toolbar Inset In From The Island's Right Edge, Or Up To The Toggle (the inset in from the caption buttons)
        var scale   = RootGrid.XamlRoot.RasterizationScale;
        var width   = RootGrid.ActualWidth;
        var caption = AppWindow.TitleBar.RightInset / scale;
        var hostEnd = ToolbarHost.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point(ToolbarHost.ActualWidth, 0)).X;
        var toggle  = Math.Round((hostEnd - (width - caption - CalendarPage.ToolbarInset)) * scale) / scale;
        var target  = width - CalendarPage.DetailsWidth - CalendarPage.ToolbarInset;
        var right   = page is null ? 0
            : page.IsDetailsOpen ? Math.Round((hostEnd - target) * scale) / scale
            : toggle + DetailsToggle.Width;
        var layout  = (Right: right, Toggle: toggle, Sidebar: page?.IsSidebarOpen ?? true, Calendar: page is not null);
        if (layout == _titleBarLayout)
        {
            return;
        }

        var previous = _titleBarLayout;
        _titleBarLayout = layout;

        DetailsToggle.Margin   = new Thickness(0, 0, layout.Toggle, 0);
        CalendarToolbar.Margin = new Thickness(0, 0, layout.Right, 0);
        EventActions.Margin    = new Thickness(0, 0, layout.Right - CalendarPage.ToolbarInset - EventActionsSpan, 0);

        // Slide From The Old Spot (so the toolbar tracks the island's edge instead of jumping ahead of it)
        _toolbarSlide?.Stop();
        _toolbarShift.X = 0;
        var distance = layout.Right - (previous?.Right ?? layout.Right);
        if (animate && distance != 0 && new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            var slide = new DoubleAnimationUsingKeyFrames();
            slide.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = distance });
            slide.KeyFrames.Add(new SplineDoubleKeyFrame
            {
                KeyTime   = distance > 0 ? PaneOpenDuration : PaneCloseDuration,
                KeySpline = new KeySpline { ControlPoint1 = new Windows.Foundation.Point(0, 0.35), ControlPoint2 = new Windows.Foundation.Point(0.15, 1) },
                Value     = 0,
            });
            Storyboard.SetTarget(slide, _toolbarShift);
            Storyboard.SetTargetProperty(slide, "X");

            _toolbarSlide = new Storyboard { Children = { slide } };
            _toolbarSlide.Completed += (_, _) => AppTitleBar.RecomputeDragRegions();
            _toolbarSlide.Begin();
        }

        AppTitleBar.RecomputeDragRegions();
    }

    // The search icon: centered over the mini month's Next month button while the sidebar is open, else right after the
    // title bar's pane toggle (and the period title moves clear of it). Its margin is its resting spot (where it takes
    // clicks); while the sidebar slides it rides the sidebar's edge on the compositor, so it never jumps or lags.
    void PlaceSearchButton()
    {
        if (ContentFrame.Content is not CalendarPage page || RootGrid.XamlRoot is null)
        {
            return;
        }

        var scale  = RootGrid.XamlRoot.RasterizationScale;
        var hostX  = ToolbarHost.TransformToVisual(RootGrid).TransformPoint(default).X;
        var closed = 0.0;
        var open   = page.MiniMonthNextCenterX is { } center ? Math.Max(closed, Math.Round((center - hostX - SearchButton.Width / 2) * scale) / scale) : closed;
        var left   = page.IsSidebarOpen ? open : closed;

        page.KeepTitleClearOf(hostX + closed + SearchButton.Width);
        if (_searchRide == (closed, open, left))
        {
            return;
        }

        // No Forced Layout Here (it ran the whole window's layout, the calendar's included, inside a pane toggle and held up the slide):
        // FollowSearchAnchor re-punches the icon's hole once the next layout pass has moved it
        _searchRide         = (closed, open, left);
        SearchButton.Margin = new Thickness(left, 0, 0, 0);
        page.RideSidebarEdge(SearchButton, closed, open, left);
    }

    // The search icon's closed, open, and resting spots last handed to the compositor (toolbar host DIPs)
    (double Closed, double Open, double Resting)? _searchRide;

    // The dim's fade (the design standard's 167 ms), whether the menu is open now, and the timer that collapses it
    static readonly TimeSpan DimFadeDuration = TimeSpan.FromMilliseconds(167);
    bool _dimOpen;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _dimTimer;

    // The command menu's dim: shown, then faded in on the next tick (so the opacity transition runs); faded out, then
    // collapsed once the fade is over, unless the menu opened again meanwhile
    void DimForCommandMenu(bool open)
    {
        _dimOpen = open;
        if (open)
        {
            CommandMenuDim.Visibility = Visibility.Visible;
            DispatcherQueue.TryEnqueue(() => CommandMenuDim.Opacity = _dimOpen ? 1 : 0);
            return;
        }

        CommandMenuDim.Opacity = 0;
        if (_dimTimer is null)
        {
            _dimTimer             = DispatcherQueue.CreateTimer();
            _dimTimer.Interval    = DimFadeDuration;
            _dimTimer.IsRepeating = false;
            _dimTimer.Tick       += (_, _) =>
            {
                if (!_dimOpen)
                {
                    CommandMenuDim.Visibility = Visibility.Collapsed;
                }
            };
        }

        _dimTimer.Stop();
        _dimTimer.Start();
    }

    // Where the search icon and the toolbar host were when the icon's click-through hole was last punched, and where
    // its anchor was (window DIPs)
    (double Button, double Host, double Anchor, bool Open)? _searchSpot;

    // After any layout pass: the Next month button, the toolbar host, or the icon itself can move without a size change
    // (the sidebar settling, Back appearing). Re-place the icon and re-punch its
    // click-through hole. The title bar also re-punches its holes on its own when its content moves, from where the icon
    // was before the new margin landed, so the hole is punched again once that layout pass is over; a stale hole
    // leaves the icon in the drag region, where a click does nothing. Nothing happens unless something actually moved.
    void FollowSearchAnchor()
    {
        if (ContentFrame.Content is not CalendarPage page || RootGrid.XamlRoot is null || SearchButton.ActualWidth <= 0)
        {
            return;
        }

        var spot = (
            SearchButton.TransformToVisual(RootGrid).TransformPoint(default).X,
            ToolbarHost.TransformToVisual(RootGrid).TransformPoint(default).X,
            page.MiniMonthNextCenterX ?? -1,
            page.IsSidebarOpen);
        if (spot == _searchSpot)
        {
            return;
        }

        _searchSpot = spot;
        PlaceSearchButton();
        AppTitleBar.RecomputeDragRegions();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (AppTitleBar.IsLoaded)
            {
                AppTitleBar.RecomputeDragRegions();
            }
        });
    }

    // Edit And Delete: shown in the details panel's title bar row while the open panel shows an event or a selection
    // (not while editing). Both stay put, disabled when they can't act: Edit needs one event you can change; Delete
    // is disabled when none of the selected events can be deleted (the Delete key does nothing then either); several
    // selected events delete the ones you can change. A disabled button shows no tooltip, so each button sits in a
    // wrapper that carries it (the action while enabled, the reason while disabled); the reason is also the help text.
    // The title bar only lets clicks through where its buttons are when it computes its regions, so they're
    // recomputed once the buttons have their new layout.
    void UpdateEventActions()
    {
        var several    = _calendar is { Selection.Count: > 1 };
        var canEdit    = _calendar?.SelectedInfo is { CanEdit: true };
        var show       = ContentFrame.Content is CalendarPage { IsDetailsOpen: true } && _calendar is { Editing: null } && (several || _calendar.SelectedInfo is not null);
        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        var edit       = !several && canEdit;
        var delete     = _calendar is { CanDeleteSelection: true };

        var editReason   = several ? "Select one event to edit it" : "You can't edit this event";
        var deleteReason = several ? "You can't delete these events" : "You can't delete this event";

        EditEventButton.IsEnabled = edit;
        ToolTipService.SetToolTip(EditEventTip, edit ? "Edit event (E)" : editReason);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(EditEventButton, edit ? "" : editReason);
        DeleteEventButton.IsEnabled = delete;
        ToolTipService.SetToolTip(DeleteEventTip, delete ? "Delete event (Delete)" : deleteReason);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(DeleteEventButton, delete ? "" : deleteReason);
        if (EventActions.Visibility == visibility)
        {
            return;
        }

        EventActions.Visibility = visibility;
        EventActions.UpdateLayout();
        AppTitleBar.RecomputeDragRegions();
    }

    // The minimum size is the content's, in DIPs; the presenter takes the whole window in screen pixels, so it
    // follows the monitor's scale and adds the window frame (the invisible resize borders, about 14 DIPs across)
    void ApplyMinimumSize()
    {
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? Interop.WindowPlacement.ScaleOf(AppWindow);
        var frame = AppWindow.Size;
        var inner = AppWindow.ClientSize;
        _presenter.PreferredMinimumWidth  = (int)Math.Ceiling(MinimumWidth * scale) + Math.Max(0, frame.Width - inner.Width);
        _presenter.PreferredMinimumHeight = (int)Math.Ceiling(MinimumHeight * scale) + Math.Max(0, frame.Height - inner.Height);
    }

    void OnPaneToggleRequested(TitleBar sender, object args)
    {
        if (ContentFrame.Content is CalendarPage page && _calendar is not null)
        {
            page.SetSidebarOpen(!_calendar.Settings.SidebarOpen, animate: true);
        }
    }

    void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        // The Calendar's Chrome Dims With The Title Bar While Another Window Is Active
        var active = args.WindowActivationState != WindowActivationState.Deactivated;
        if (ContentFrame.Content is CalendarPage page)
        {
            page.SetWindowActive(active);
        }

        if (!active)
        {
            return;
        }

        // Back From Settings: The PC's Time Zone May Have Changed
        _calendar?.CheckTimeZone();
        if (_services.Google is not { } google)
        {
            return;
        }

        google.Loop.Mode = SyncMode.Visible;
        google.Loop.TriggerNow();
    }

    // =========================================================================
    // TOOLBAR
    // =========================================================================

    void OnTodayClick(object sender, RoutedEventArgs e) => _calendar?.GoToToday();

    void OnSearchClick(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.Content is CalendarPage page)
        {
            page.RunCommand(Core.Views.CalendarCommand.CommandMenu);
        }
    }

    // The title bar's Back shows only after a command-menu jump
    void OnBackRequested(TitleBar sender, object args) => _calendar.BackFromJump();

    void OnPreviousClick(object sender, RoutedEventArgs e) => _calendar?.Previous();

    void OnNextClick(object sender, RoutedEventArgs e) => _calendar?.Next();

    void OnViewModeClick(object sender, RoutedEventArgs e)
    {
        if (_calendar is null || sender is not MenuFlyoutItem { Tag: string tag })
        {
            return;
        }

        if (tag.StartsWith("Days:", StringComparison.Ordinal))
        {
            _calendar.SetMode(CalendarViewMode.Days, int.Parse(tag[5..], System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            _calendar.SetMode(Enum.Parse<CalendarViewMode>(tag));
        }

        SyncMenu();
    }

    async void OnCustomDaysClick(object sender, RoutedEventArgs e)
    {
        if (_calendar is null)
        {
            return;
        }

        try
        {
            var box = new NumberBox { Minimum = 1, Maximum = 31, Value = _calendar.Settings.CustomDayCount, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(box, "CustomDaysBox");

            var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme, Title = "Number of days", Content = box, PrimaryButtonText = "Show", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !double.IsNaN(box.Value))
            {
                _calendar.SetMode(CalendarViewMode.Days, (int)box.Value);
                SyncMenu();
            }
        }
        catch (Exception ex)
        {
            _services.Log.Error("calendar.custom-days.failed", ex);
        }
    }

    void OnEditEventClick(object sender, RoutedEventArgs e) => _calendar?.BeginEdit();

    void OnDeleteEventClick(object sender, RoutedEventArgs e)
    {
        if (_calendar is { } vm)
        {
            vm.Fire(() => vm.DeleteAsync([.. vm.Selection], sendUpdates: true), "details.delete.failed");
        }
    }

    void OnDetailsToggleClick(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.Content is CalendarPage page)
        {
            var open = DetailsToggle.IsChecked == true;
            page.SetDetailsOpen(open, animate: true);

            // Focus Leaves The Closed Panel (a hidden editor's title box would take C and E as typing)
            if (!open)
            {
                DetailsToggle.Focus(FocusState.Programmatic);
            }
        }
    }

    // Centered in the 48 DIP row, the title bar's glyphs sat about 2 physical pixels below the caption buttons' glyphs
    // at 125% and 150% alike (Windows draws those in whole pixels, a little above center), so the lift is in pixels, not DIPs
    void LiftTitleBar() => _titleBarLift.Y = -2 / (RootGrid.XamlRoot?.RasterizationScale ?? 1);

    // Ctrl+Shift+L: flip between light and dark based on what's showing now
    void ToggleTheme()
    {
        if (_calendar is null)
        {
            return;
        }

        var next = RootGrid.ActualTheme == ElementTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        _calendar.Update(s => s with { Theme = next });
    }

    // Keep the view button's label and the details toggle in step with the settings (Settings, shortcuts, and the menu all change them)
    void SyncMenu()
    {
        if (_calendar is null)
        {
            return;
        }

        var s = _calendar.Settings;
        var view = s.ViewMode switch
        {
            CalendarViewMode.Day   => "Day",
            CalendarViewMode.Month => "Month",
            CalendarViewMode.Days  => $"{s.CustomDayCount} days",
            _                      => "Week",
        };
        ViewModeLabel.Text      = view;
        DetailsToggle.IsChecked = s.DetailsPanelOpen;
        AutomationProperties.SetName(ViewModeButton, view);

        // Pager Arrows Point The Way The View Moves (month scrolls up and down; ← and → still page it): they turn a
        // quarter to point up and down in Month view, and back when it's left
        RotatePagers(s.ViewMode == CalendarViewMode.Month);
    }

    // The pager arrows' turn last shown: null until the first layout, which sets it without animating
    bool? _pagersVertical;

    // The arrows' rotations, made here and held, never read back from the glyphs (a typed read-back of a WinRT
    // property fails under Native AOT, and the arrows never turned there)
    RotateTransform? _previousTurn;
    RotateTransform? _nextTurn;

    // Turns the left and right chevrons a quarter clockwise (up and down) for Month view, animated once shown
    void RotatePagers(bool vertical)
    {
        if (_pagersVertical == vertical)
        {
            return;
        }

        var animate     = _pagersVertical is not null;
        _pagersVertical = vertical;
        var angle       = vertical ? 90 : 0;
        if (_previousTurn is null || _nextTurn is null)
        {
            PreviousGlyph.RenderTransform = _previousTurn = new RotateTransform();
            NextGlyph.RenderTransform     = _nextTurn     = new RotateTransform();
        }

        foreach (var turn in new[] { _previousTurn, _nextTurn })
        {
            if (!animate)
            {
                turn.Angle = angle;
                continue;
            }

            var spin = new DoubleAnimation
            {
                To             = angle,
                Duration       = TimeSpan.FromMilliseconds(167),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(spin, turn);
            Storyboard.SetTargetProperty(spin, nameof(RotateTransform.Angle));
            new Storyboard { Children = { spin } }.Begin();
        }
    }

    // =========================================================================
    // SYNC STATE
    // =========================================================================

    void OnCalendarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CalendarViewModel.SelectedInfo) or nameof(CalendarViewModel.Editing) or nameof(CalendarViewModel.Selection))
        {
            UpdateEventActions();
        }
        else if (e.PropertyName is nameof(CalendarViewModel.ConflictCount) or nameof(CalendarViewModel.PendingCount) or nameof(CalendarViewModel.IsOffline) or nameof(CalendarViewModel.IsSyncing))
        {
            ShowSyncState();
        }
        else if (e.PropertyName == nameof(CalendarViewModel.ShowBack))
        {
            AppTitleBar.IsBackButtonVisible = _calendar.ShowBack;
            AppTitleBar.RecomputeDragRegions();
        }
    }

    // One icon slot left of the view button, by priority: conflicts, offline, changes waiting. Online, waiting shows only
    // once changes have waited a moment (an edit normally goes out within a second, so the icon doesn't flash).
    // Words live in the tooltips and accessible names.
    void ShowSyncState()
    {
        if (_calendar is not { } vm)
        {
            return;
        }

        // Waiting (Online): only after it has lasted WaitingDelay
        if (vm.PendingCount == 0)
        {
            _waitingTimer.Stop();
            _waitingDue = false;
        }
        else if (!vm.IsOffline && !_waitingDue && !_waitingTimer.IsRunning)
        {
            _waitingTimer.Start();
        }

        // A Sync You Asked For Shows Its Progress Ring In The Slot Until It Ends; then the slot shows what it found
        var syncing   = vm.IsSyncing;
        var conflicts = !syncing && vm.ConflictCount > 0;
        var offline   = !syncing && !conflicts && vm.IsOffline;
        var waiting   = !syncing && !conflicts && vm.PendingCount > 0 && (vm.IsOffline || _waitingDue);
        var count     = vm.PendingCount == 1 ? "1 change waiting to sync" : string.Create(CultureInfo.InvariantCulture, $"{vm.PendingCount} changes waiting to sync");
        var review    = vm.ConflictCount == 1 ? "1 change needs your review" : string.Create(CultureInfo.InvariantCulture, $"{vm.ConflictCount} changes need your review");
        var away      = vm.PendingCount == 0
            ? "Can't reach Google. Changes you make are sent when you're back online."
            : $"Can't reach Google. {count}. They're sent when you're back online.";

        // Conflicts
        ConflictsBadge.Value       = vm.ConflictCount;
        ConflictsButton.Visibility = conflicts ? Visibility.Visible : Visibility.Collapsed;
        SetWords(ConflictsButton, review, review);

        // Offline (under the waiting button when both show; the waiting one then takes the offline glyph and is the tab stop)
        OfflineButton.Visibility = offline ? Visibility.Visible : Visibility.Collapsed;
        OfflineButton.IsTabStop  = !waiting;
        SetWords(OfflineButton, away, away);

        // Waiting
        WaitingBadge.Value       = vm.PendingCount;
        WaitingGlyph.Glyph       = vm.IsOffline ? "" : "";
        WaitingButton.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        SetWords(WaitingButton, count, vm.IsOffline ? away : $"{count}. Select to try now.");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(WaitingButton, vm.IsOffline ? away : "");

        // Syncing
        SyncingRing.IsActive   = syncing;
        SyncingRing.Visibility = syncing ? Visibility.Visible : Visibility.Collapsed;

        // Show The Slot And Re-Punch The Title Bar's Click-Through Holes
        SyncStatus.Visibility = syncing || conflicts || offline || waiting ? Visibility.Visible : Visibility.Collapsed;
        CalendarToolbar.UpdateLayout();
        AppTitleBar.RecomputeDragRegions();
    }

    static void SetWords(Button button, string name, string tooltip)
    {
        var changed = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button) != name;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, tooltip);

        // Narrator Reads A New Status Once It's Done Talking (the buttons are polite live regions)
        if (changed && button.Visibility == Visibility.Visible)
        {
            Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(button)?.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
        }
    }

    // Offline or waiting: try sending now
    // Try now: a sync you asked for, so its ring shows while it runs
    void OnSyncStatusClick(object sender, RoutedEventArgs e) => _calendar?.Fire(_calendar.SyncNowAsync, "sync.now.failed");

    /// <summary>Opens the conflict dialog (the toolbar's conflicts button, or the "needs your review" notification). Never throws.</summary>
    public async Task ReviewConflictsAsync()
    {
        // A Window Just Opened For A Notification May Not Have Laid Out Yet
        if (!RootGrid.IsLoaded)
        {
            void Later(object sender, RoutedEventArgs e)
            {
                RootGrid.Loaded -= Later;
                _ = ReviewConflictsAsync();
            }

            RootGrid.Loaded += Later;
            return;
        }

        try
        {
            await ConflictDialog.ReviewAsync(RootGrid.XamlRoot, _calendar, RootGrid.ActualTheme == ElementTheme.Dark);
        }
        catch (Exception ex)
        {
            // The type only: an exception's message can carry event content
            _services.Log.Info("conflict.review.failed", $"error={ex.GetType().Name}");
        }
    }

    // ReviewConflictsAsync never throws
    async void OnConflictsClick(object sender, RoutedEventArgs e) => await ReviewConflictsAsync();
}
