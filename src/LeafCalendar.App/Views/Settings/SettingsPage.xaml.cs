using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Settings;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace LeafCalendar.App.Views.Settings;

/// <summary>
/// What every settings page gets as its navigation parameter: the app's services, the main window's calendar view
/// model (shared, so a change shows in the calendar right away), and the Settings view hosting the page, which forwards
/// the view model's change events while it's shown.
/// </summary>
public sealed record SettingsContext(LeafServices Services, CalendarViewModel Calendar, SettingsPage Host)
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

/// <summary>The Settings view's navigation parameter: the app's services, the calendar view model, and the page to show first.</summary>
public sealed record SettingsPageArgs(LeafServices Services, CalendarViewModel Calendar, SettingsSection Section);

/// <summary>
/// The Settings view, modeled on the Windows 11 Settings app and shown in the main window in place of the calendar
/// (<see cref="MainWindow.ShowSettings"/>; the title bar's back button returns to the calendar): a stock left
/// <see cref="NavigationView"/> (240 wide, collapsing when the window is narrow; the main window's title bar hamburger
/// opens and closes it) over a frame of setting pages (each fills the view, its column capped and centered): General,
/// Calendars, Time zones, Notifications, Tray, Shortcuts, Accounts, and About at the bottom of the pane. Every change
/// saves immediately through the shared view model. <see cref="Close"/> ends it: the pages' events are let go, and a
/// sign-in waiting on the browser is cancelled.
/// </summary>
public sealed partial class SettingsPage : Page
{
    readonly List<(SettingsSection Section, NavigationViewItem Item, Type Page)> _pages;
    SettingsContext _context = null!;
    CalendarViewModel _calendar = null!;
    AccountsViewModel? _accounts;
    SettingsSection? _shown;
    bool _inClientForm;

    /// <summary>Creates the view (the main window's Settings frame navigates to it with <see cref="SettingsPageArgs"/>).</summary>
    public SettingsPage()
    {
        InitializeComponent();

        // Pages (items from concrete lists: CsWinRT's AOT mode can't cast the native MenuItems vector)
        _pages =
        [
            (SettingsSection.General, NavItem("General", 0xE713, "SettingsNav_General"), typeof(GeneralPage)),
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
    }

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var args  = (SettingsPageArgs)e.Parameter;
        _calendar = args.Calendar;
        _context  = new SettingsContext(args.Services, args.Calendar, this);

        // Breadcrumb: the pane's layout changes as the window is resized
        Navigation.DisplayModeChanged += (_, a) => args.Services.Log.Trace("settings.pane", a.DisplayMode.ToString());

        // Follow The Calendar's Settings (pages show them)
        _calendar.LayoutChanged    += OnLayoutChanged;
        _calendar.CalendarsChanged += OnCalendarsChanged;
        Show(args.Section);
    }

    /// <summary>A setting changed (from a page, a menu, or a shortcut).</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>The calendar list, a color, or a calendar's visibility changed.</summary>
    public event EventHandler? CalendarsChanged;

    /// <summary>The view is closing (back to the calendar, or the window closed): a page lets go of what it listens to.</summary>
    public event EventHandler? Closed;

    /// <summary>True while the OAuth client form (a sub-page of Accounts) shows, so Back returns to Accounts first.</summary>
    public bool InClientForm => _inClientForm;

    /// <summary>Opens or closes the navigation pane (the title bar's hamburger).</summary>
    public void TogglePane() => Navigation.IsPaneOpen = !Navigation.IsPaneOpen;

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
        _context.Services.Log.Trace("settings.page", section.ToString());
        _shown        = section;
        _inClientForm = false;
        ContentFrame.Navigate(page.Page, _context, transition);
        Navigation.SelectedItem = page.Item;
    }

    /// <summary>
    /// The Accounts page's view model, one for the view's lifetime, so a sign-in or sync that's running keeps its busy
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

    /// <summary>Ends the view: the pages stop listening, and a sign-in waiting on the browser is cancelled (nothing is saved).</summary>
    public void Close()
    {
        _calendar.LayoutChanged    -= OnLayoutChanged;
        _calendar.CalendarsChanged -= OnCalendarsChanged;
        Closed?.Invoke(this, EventArgs.Empty);
        _accounts?.CancelSignIn();
    }

    static NavigationViewItem NavItem(string label, int glyph, string automationId)
    {
        var item = new NavigationViewItem { Content = label, Icon = new FontIcon { Glyph = char.ConvertFromUtf32(glyph), FontSize = 16 } };
        AutomationProperties.SetName(item, label);
        AutomationProperties.SetAutomationId(item, automationId);
        return item;
    }

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

    void OnLayoutChanged(object? sender, EventArgs e) => SettingsChanged?.Invoke(this, EventArgs.Empty);

    void OnCalendarsChanged(object? sender, EventArgs e) => CalendarsChanged?.Invoke(this, EventArgs.Empty);
}
