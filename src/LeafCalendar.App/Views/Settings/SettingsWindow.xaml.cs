using System.Diagnostics.CodeAnalysis;
using LeafCalendar.App.Interop;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Settings;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace LeafCalendar.App.Views.Settings;

/// <summary>
/// What every settings page gets as its navigation parameter: the app's services, the main window's calendar view
/// model (shared, so a change shows in the main window right away), and the window, which forwards the view model's
/// change events while it's open.
/// </summary>
public sealed record SettingsContext(LeafServices Services, CalendarViewModel Calendar, SettingsWindow Window)
{
    /// <summary>
    /// Saves a change from a page's control. It runs from control events, so nothing may escape: a save that fails
    /// (the database can't be written) is logged by its type only and the setting stays as it was.
    /// </summary>
    public void Save(Func<LeafSettings, LeafSettings> change)
    {
        try
        {
            Calendar.Update(change);
        }
        catch (Exception ex)
        {
            Services.Log.Info("settings.save.failed", $"error={ex.GetType().Name}");
        }
    }
}

/// <summary>
/// The Settings window, modeled on the Windows 11 Settings app: Mica, a stock title bar with the app icon and a pane
/// toggle, and a stock left <see cref="NavigationView"/> (240 wide, collapsing when the window is narrow) over a frame
/// of setting pages: General, Calendars, Time zones, Notifications, Tray, Shortcuts, Accounts, and About at the bottom
/// of the pane. There's one at a time: <see cref="Open"/> brings the open one forward. It opens at 1000 × 720 DIPs the first time (then at the size it last closed at),
/// centered on the monitor under the cursor, and stays open when the main window closes (Leaf lives in the tray). Every
/// change saves immediately through the shared view model.
/// </summary>
[SuppressMessage("Design", "CA1001", Justification = "Windows aren't disposable.")]
public sealed partial class SettingsWindow : Window
{
    // Sizes In DIPs (Sony Control's opening size; the minimum still fits every page, with the pane collapsed)
    const double OpenWidth     = 1000;
    const double OpenHeight    = 720;
    const double MinimumWidth  = 640;
    const double MinimumHeight = 500;

    readonly OverlappedPresenter _presenter = OverlappedPresenter.Create();
    readonly List<(SettingsSection Section, NavigationViewItem Item, Type Page)> _pages;
    readonly SettingsContext _context;
    AccountsViewModel? _accounts;
    SettingsSection? _shown;
    bool _inClientForm;

    // The window's size while restored, saved on close
    Core.Views.WindowSize? _restoredSize;

    SettingsWindow(LeafServices services, CalendarViewModel calendar)
    {
        InitializeComponent();
        _context = new SettingsContext(services, calendar, this);

        // Pages (items from concrete lists: CsWinRT's AOT mode can't cast the native MenuItems vector)
        _pages =
        [
            (SettingsSection.General, NavItem("General", 0xE771, "SettingsNav_General"), typeof(GeneralPage)),
            (SettingsSection.Calendars, NavItem("Calendars", 0xE787, "SettingsNav_Calendars"), typeof(CalendarsPage)),
            (SettingsSection.TimeZones, NavItem("Time zones", 0xE774, "SettingsNav_TimeZones"), typeof(TimeZonesPage)),
            (SettingsSection.Notifications, NavItem("Notifications", 0xEA8F, "SettingsNav_Notifications"), typeof(NotificationsPage)),
            (SettingsSection.Tray, NavItem("Tray", 0xE7C4, "SettingsNav_Tray"), typeof(TrayPage)),
            (SettingsSection.Shortcuts, NavItem("Shortcuts", 0xE765, "SettingsNav_Shortcuts"), typeof(ShortcutsPage)),
            (SettingsSection.Accounts, NavItem("Accounts", 0xE77B, "SettingsNav_Accounts"), typeof(AccountsPage)),
            (SettingsSection.About, NavItem("About", 0xE946, "SettingsNav_About"), typeof(AboutPage)),
        ];
        Navigation.MenuItemsSource       = _pages.Where(p => p.Section != SettingsSection.About).Select(p => (object)p.Item).ToList();
        Navigation.FooterMenuItemsSource = _pages.Where(p => p.Section == SettingsSection.About).Select(p => (object)p.Item).ToList();

        // Window Presenter (ours, kept, so its minimum size can be set without casting AppWindow.Presenter)
        AppWindow.SetPresenter(_presenter);

        // Title Bar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        RootGrid.Loaded += (_, _) =>
        {
            ApplyMinimumSize();
            RootGrid.XamlRoot.Changed += (_, _) => ApplyMinimumSize();
        };

        // Follow The Main Window's Settings (theme, and pages that show them)
        ApplyTheme(calendar.Settings.Theme);
        calendar.LayoutChanged    += OnLayoutChanged;
        calendar.CalendarsChanged += OnCalendarsChanged;
        Closed += (_, _) =>
        {
            calendar.LayoutChanged    -= OnLayoutChanged;
            calendar.CalendarsChanged -= OnCalendarsChanged;
            Current = null;

            // Remember The Size For Next Time (the restored size, and whether it was maximized)
            if (_restoredSize is { } size)
            {
                calendar.Remember(s => s with { SettingsWindowSize = size with { Maximized = _presenter.State == OverlappedPresenterState.Maximized } }, inBackground: false);
            }
        };
    }

    /// <summary>The open Settings window, or null.</summary>
    public static new SettingsWindow? Current { get; private set; }

    /// <summary>A setting changed (from this window, a menu, or a shortcut).</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>The calendar list, a color, or a calendar's visibility changed.</summary>
    public event EventHandler? CalendarsChanged;

    /// <summary>Opens Settings on <paramref name="section"/>, or shows that page in the open window and brings it forward.</summary>
    public static void Open(LeafServices services, CalendarViewModel calendar, SettingsSection section)
    {
        if (Current is { } open)
        {
            open.Show(section);
            open.BringToFront();
            return;
        }

        var window = new SettingsWindow(services, calendar);
        window.Place();
        Current = window;
        window.Show(section);
        window.Activate();
    }

    /// <summary>Shows a page (without a transition the first time, then with the stock drill-in; back from the OAuth client form it slides back).</summary>
    public void Show(SettingsSection section)
    {
        if (_shown == section)
        {
            return;
        }

        // First Page: No Transition; Back From The OAuth Client Form: Slide Back; Otherwise: Drill In
        var page       = _pages.Find(p => p.Section == section);
        var transition = _inClientForm
            ? new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromLeft }
            : _shown is null ? (NavigationTransitionInfo)new SuppressNavigationTransitionInfo() : new DrillInNavigationTransitionInfo();
        _shown        = section;
        _inClientForm = false;
        ContentFrame.Navigate(page.Page, _context, transition);
        Navigation.SelectedItem = page.Item;
    }

    /// <summary>
    /// The Accounts page's view model, one for the window's lifetime, so a sign-in or sync that's running keeps its busy
    /// state (and its buttons stay off) when you leave the page and come back.
    /// </summary>
    public AccountsViewModel Accounts => _accounts ??= new AccountsViewModel(_context.Services, _context.Calendar.ReloadCalendars);

    /// <summary>Shows the OAuth client form (a sub-page of Accounts, which stays selected; it slides in, and Save and Cancel slide back).</summary>
    public void ShowClientSetup()
    {
        _shown        = null;
        _inClientForm = true;
        ContentFrame.Navigate(typeof(ClientPage), _context, new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });
    }

    /// <summary>Applies the app theme to the content and caption buttons.</summary>
    public void ApplyTheme(AppTheme theme) => MainWindow.ApplyTheme(AppWindow, RootGrid, theme);

    // Restores the window if it's minimized and brings it to the front
    void BringToFront()
    {
        if (_presenter.State == OverlappedPresenterState.Minimized)
        {
            _presenter.Restore();
        }

        Activate();
        PInvoke.SetForegroundWindow(new HWND(Win32Interop.GetWindowFromWindowId(AppWindow.Id)));
    }

    // Centered on the monitor under the cursor at the size it last closed at (else the opening size), with the minimum from
    // that monitor's scale; a restored window's size is kept as it changes, for the next time
    void Place()
    {
        if (_context.Calendar.Settings.SettingsWindowSize is { } saved)
        {
            WindowPlacement.Restore(AppWindow, _presenter, saved);
            ApplyMinimumSize();
        }
        else
        {
            SetMinimumSize(WindowPlacement.CenterOnCursorMonitor(AppWindow, OpenWidth, OpenHeight));
        }

        _restoredSize = (_context.Calendar.Settings.SettingsWindowSize ?? WindowPlacement.SizeOf(AppWindow)) with { Maximized = false };
        AppWindow.Changed += (_, e) =>
        {
            if (e.DidSizeChange && _presenter.State == OverlappedPresenterState.Restored)
            {
                _restoredSize = WindowPlacement.SizeOf(AppWindow);
            }
        };
    }

    void ApplyMinimumSize() => SetMinimumSize(RootGrid.XamlRoot?.RasterizationScale ?? 1);

    // The minimum is the content's, in DIPs; the presenter takes the whole window in screen pixels, frame included
    void SetMinimumSize(double scale)
    {
        _presenter.PreferredMinimumWidth  = (int)Math.Ceiling(MinimumWidth * scale) + Math.Max(0, AppWindow.Size.Width - AppWindow.ClientSize.Width);
        _presenter.PreferredMinimumHeight = (int)Math.Ceiling(MinimumHeight * scale) + Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height);
    }

    static NavigationViewItem NavItem(string label, int glyph, string automationId)
    {
        var item = new NavigationViewItem { Content = label, Icon = new FontIcon { Glyph = char.ConvertFromUtf32(glyph), FontSize = 16 } };
        AutomationProperties.SetName(item, label);
        AutomationProperties.SetAutomationId(item, automationId);
        return item;
    }

    void OnPaneToggleRequested(TitleBar sender, object args) => Navigation.IsPaneOpen = !Navigation.IsPaneOpen;

    // Compared by reference: type tests on items read back from WinRT fail under Native AOT
    void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var selected = args.SelectedItem;
        foreach (var page in _pages)
        {
            if (ReferenceEquals(page.Item, selected))
            {
                Show(page.Section);
                return;
            }
        }
    }

    // Also raised for the item that's already selected, so Accounts brings you back from the OAuth client form
    void OnItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        var invoked = args.InvokedItemContainer;
        foreach (var page in _pages)
        {
            if (ReferenceEquals(page.Item, invoked))
            {
                Show(page.Section);
                return;
            }
        }
    }

    void OnLayoutChanged(object? sender, EventArgs e)
    {
        ApplyTheme(_context.Calendar.Settings.Theme);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    void OnCalendarsChanged(object? sender, EventArgs e) => CalendarsChanged?.Invoke(this, EventArgs.Empty);
}
