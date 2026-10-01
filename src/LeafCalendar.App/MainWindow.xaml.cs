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
/// shares the calendar view model, so its changes show here right away.
/// </summary>
[SuppressMessage("Design", "CA1001", Justification = "Windows aren't disposable; the view model is disposed when the window closes.")]
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
    readonly IconSource? _appIcon;
    readonly TranslateTransform _toolbarShift = new();
    readonly OverlappedPresenter _presenter = OverlappedPresenter.Create();
    CalendarViewModel? _calendar;

    // Changes waiting (online) show only once they've waited this long
    static readonly TimeSpan WaitingDelay = TimeSpan.FromSeconds(2);
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _waitingTimer;
    bool _waitingDue;
    Storyboard? _toolbarSlide;
    (double Right, bool Sidebar, bool Calendar)? _titleBarLayout;

    /// <summary>Creates the window. <see cref="App"/> owns the services.</summary>
    public MainWindow(LeafServices services)
    {
        _services = services;
        InitializeComponent();
        _appIcon = AppTitleBar.IconSource;

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

        // Title Bar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        ToolbarHost.SizeChanged += (_, _) => UpdateTitleBarLayout(animate: false);
        RootGrid.Loaded += (_, _) =>
        {
            ApplyMinimumSize();
            RootGrid.XamlRoot.Changed += (_, _) =>
            {
                ApplyMinimumSize();
                UpdateTitleBarLayout(animate: false);
            };
        };

        Activated += OnActivated;
        Closed    += (_, _) =>
        {
            // Settings Closes With The Main Window (it runs on the same services)
            SettingsWindow.Current?.Close();

            // Closing doesn't navigate, so release the page's views here
            (ContentFrame.Content as CalendarPage)?.Detach();
            _calendar?.Dispose();
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
        PInvoke.SetForegroundWindow(new HWND(Win32Interop.GetWindowFromWindowId(AppWindow.Id)));
    }

    /// <summary>Applies the app theme to the content and caption buttons.</summary>
    public void ApplyTheme(AppTheme theme) => ApplyTheme(AppWindow, RootGrid, theme);

    /// <summary>Applies the app theme to a window's content (<paramref name="root"/>) and caption buttons.</summary>
    internal static void ApplyTheme(AppWindow window, FrameworkElement root, AppTheme theme)
    {
        root.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark  => ElementTheme.Dark,
            _              => ElementTheme.Default,
        };

        window.TitleBar.PreferredTheme = theme switch
        {
            AppTheme.Light => TitleBarTheme.Light,
            AppTheme.Dark  => TitleBarTheme.Dark,
            _              => TitleBarTheme.UseDefaultAppMode,
        };
    }

    // =========================================================================
    // NAVIGATION
    // =========================================================================

    void ShowCalendar()
    {
        if (_calendar is null)
        {
            _calendar = new CalendarViewModel(_services, DispatcherQueue);
            _calendar.OpenSettings     = section => SettingsWindow.Open(_services, _calendar, section);
            _calendar.LayoutChanged   += (_, _) =>
            {
                SyncMenu();
                ApplyTheme(_calendar.Settings.Theme);
            };
            _calendar.PropertyChanged += OnCalendarPropertyChanged;
            ApplyTheme(_calendar.Settings.Theme);
        }

        ShowSyncState();

        ContentFrame.Navigate(typeof(CalendarPage), new CalendarPageArgs(_calendar, ToggleTheme));
        ContentFrame.BackStack.Clear();
    }

    void OnNavigated(object sender, NavigationEventArgs e)
    {
        var page       = e.Content as CalendarPage;
        var onCalendar = page is not null;

        CalendarToolbar.Visibility            = onCalendar ? Visibility.Visible : Visibility.Collapsed;
        AppTitleBar.IsPaneToggleButtonVisible = onCalendar;

        if (page is not null)
        {
            page.PanesChanged += (_, e) =>
            {
                UpdateTitleBarLayout(e.Animate);
                UpdateEventActions();
                DetailsToggle.IsChecked = page.IsDetailsOpen;
            };
            SyncMenu();
        }

        UpdateTitleBarLayout(animate: false);
        UpdateEventActions();
    }

    // Line the toolbar up with the calendar island's right edge (next to the caption buttons when
    // the details panel is closed), and hide the app name when the sidebar is closed so the
    // island's title has room. Then re-punch the title bar's click-through holes for the buttons.
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

        // Target: The Toolbar Inset In From The Island's Right Edge, Or From The Caption Buttons
        var scale   = RootGrid.XamlRoot.RasterizationScale;
        var width   = RootGrid.ActualWidth;
        var caption = AppWindow.TitleBar.RightInset / scale;
        var hostEnd = ToolbarHost.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point(ToolbarHost.ActualWidth, 0)).X;
        var target  = page is { IsDetailsOpen: true } ? width - CalendarPage.DetailsWidth - CalendarPage.ToolbarInset : width - caption - CalendarPage.ToolbarInset;
        var right   = page is null ? 0 : Math.Round((hostEnd - target) * scale) / scale;
        var layout  = (Right: right, Sidebar: page?.IsSidebarOpen ?? true, Calendar: page is not null);
        if (layout == _titleBarLayout)
        {
            return;
        }

        var previous = _titleBarLayout;
        _titleBarLayout = layout;

        CalendarToolbar.Margin = new Thickness(0, 0, layout.Right, 0);
        EventActions.Margin    = new Thickness(0, 0, layout.Right - CalendarPage.ToolbarInset - EventActionsSpan, 0);
        AppTitleBar.Title      = layout.Sidebar ? "Leaf Calendar" : "";
        AppTitleBar.IconSource = layout.Sidebar ? _appIcon : null;

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
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1;
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
        if (args.WindowActivationState == WindowActivationState.Deactivated)
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

            var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = "Number of days", Content = box, PrimaryButtonText = "Show", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
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
        ViewModeButton.Content  = s.ViewMode switch
        {
            CalendarViewMode.Day   => "Day",
            CalendarViewMode.Month => "Month",
            CalendarViewMode.Days  => $"{s.CustomDayCount} days",
            _                      => "Week",
        };
        DetailsToggle.IsChecked = s.DetailsPanelOpen;
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
        else if (e.PropertyName is nameof(CalendarViewModel.ConflictCount) or nameof(CalendarViewModel.PendingCount) or nameof(CalendarViewModel.IsOffline))
        {
            ShowSyncState();
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

        var conflicts = vm.ConflictCount > 0;
        var offline   = !conflicts && vm.IsOffline;
        var waiting   = !conflicts && vm.PendingCount > 0 && (vm.IsOffline || _waitingDue);
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

        // Show The Slot And Re-Punch The Title Bar's Click-Through Holes
        SyncStatus.Visibility = conflicts || offline || waiting ? Visibility.Visible : Visibility.Collapsed;
        CalendarToolbar.UpdateLayout();
        AppTitleBar.RecomputeDragRegions();
    }

    static void SetWords(Button button, string name, string tooltip)
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, tooltip);
    }

    // Offline or waiting: try sending now
    void OnSyncStatusClick(object sender, RoutedEventArgs e) => _services.Google?.Loop.TriggerNow();

    async void OnConflictsClick(object sender, RoutedEventArgs e)
    {
        if (_calendar is not { } vm)
        {
            return;
        }

        // async void: anything that escapes here would end the process
        try
        {
            await ConflictDialog.ReviewAsync(RootGrid.XamlRoot, vm, RootGrid.ActualTheme == ElementTheme.Dark);
        }
        catch (Exception ex)
        {
            vm.LogError("conflict.review.failed", ex);
        }
    }
}
