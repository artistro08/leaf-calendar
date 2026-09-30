using System.Diagnostics.CodeAnalysis;
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
/// toolbar, a Mica backdrop, and a page frame (setup, then the calendar). On the calendar page the
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
    // with "31 days" on the view button) 6 in from the island's right edge, which also leaves the
    // week grid its 56 gutter and seven 48-wide days. The height keeps the sidebar's mini month, an account with three calendars, and
    // its footer, and shows about eight hours of the grid at the default hour height.
    const double MinimumWidth  = CalendarPage.SidebarWidth + CalendarPage.TitleInset + 170 + 16 + 257 + CalendarPage.ToolbarInset + CalendarPage.DetailsWidth;
    const double MinimumHeight = 540;

    // The event actions' right end, in from the details panel's left edge: the edit glyph (8 in on its 32-wide
    // button) starts at the panel's 16 px content inset, and the delete button touches it
    const double EventActionsSpan = 16 - 8 + 32 + 32;

    readonly LeafServices _services;
    readonly IconSource? _appIcon;
    readonly TranslateTransform _toolbarShift = new();
    readonly OverlappedPresenter _presenter = OverlappedPresenter.Create();
    CalendarViewModel? _calendar;
    Storyboard? _toolbarSlide;
    (double Right, bool Sidebar, bool Calendar)? _titleBarLayout;

    /// <summary>Creates the window. <see cref="App"/> owns the services.</summary>
    public MainWindow(LeafServices services)
    {
        _services = services;
        InitializeComponent();
        _appIcon = AppTitleBar.IconSource;
        ToolbarSlide.RenderTransform = _toolbarShift;

        // Shortcuts are handled at the root so they work wherever focus is
        RootGrid.PreviewKeyDown += (_, e) =>
        {
            if (ContentFrame.Content is CalendarPage page && page.HandleShortcut(e))
            {
                e.Handled = true;
            }
        };

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

        if (_services.Google is null)
        {
            ShowSetup();
        }
        else
        {
            ShowCalendar();
        }
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

    void ShowSetup() =>
        ContentFrame.Navigate(typeof(SetupPage), new SetupViewModel(_services.Tokens, OnCredentialsSavedAsync, _services.Log));

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
            _calendar.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(CalendarViewModel.SelectedInfo) or nameof(CalendarViewModel.Editing) or nameof(CalendarViewModel.Selection))
                {
                    UpdateEventActions();
                }
            };
            ApplyTheme(_calendar.Settings.Theme);
        }

        ContentFrame.Navigate(typeof(CalendarPage), new CalendarPageArgs(_calendar, ToggleTheme));
        ContentFrame.BackStack.Clear();
    }

    async Task OnCredentialsSavedAsync()
    {
        await _services.ReloadGoogleAsync();
        ShowCalendar();
    }

    void OnNavigated(object sender, NavigationEventArgs e)
    {
        var page       = e.Content as CalendarPage;
        var onCalendar = page is not null;

        // The calendar runs under the title bar; setup sits below it
        Grid.SetRow(ContentFrame, onCalendar ? 0 : 1);
        Grid.SetRowSpan(ContentFrame, onCalendar ? 2 : 1);

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
    // (not while editing). Edit shows only for one event you can change. Delete stays put for an event you can't
    // delete, just disabled (the Delete key does nothing for it either); several selected events delete the ones you
    // can change. The title bar only lets clicks through where its buttons are when it computes its regions, so
    // they're recomputed once the buttons have their new layout.
    void UpdateEventActions()
    {
        var several    = _calendar is { Selection.Count: > 1 };
        var canEdit    = _calendar?.SelectedInfo is { CanEdit: true };
        var show       = ContentFrame.Content is CalendarPage { IsDetailsOpen: true } && _calendar is { Editing: null } && (several || _calendar.SelectedInfo is not null);
        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        var edit       = !several && canEdit ? Visibility.Visible : Visibility.Collapsed;
        var delete     = several || canEdit;

        ToolTipService.SetToolTip(DeleteEventButton, delete ? "Delete event (Delete)" : "You can't delete this event");
        DeleteEventButton.IsEnabled = delete;
        if (EventActions.Visibility == visibility && EditEventButton.Visibility == edit)
        {
            return;
        }

        EventActions.Visibility    = visibility;
        EditEventButton.Visibility = edit;
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
            page.SetDetailsOpen(DetailsToggle.IsChecked == true, animate: true);
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
}
