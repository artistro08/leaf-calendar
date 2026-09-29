using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using LeafCalendar.App.ViewModels;
using LeafCalendar.App.Views;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Sync;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LeafCalendar.App;

/// <summary>
/// Main window: a tall XAML title bar (caption buttons match its 48 px height) holding the period
/// title and the calendar toolbar, a Mica backdrop, and a page frame (setup, calendar, accounts).
/// The back button shows only when the frame can go back; the pane toggle only on the calendar.
/// </summary>
[SuppressMessage("Design", "CA1001", Justification = "Windows aren't disposable; the view model is disposed when the window closes.")]
public sealed partial class MainWindow : Window
{
    readonly LeafServices _services;
    CalendarViewModel? _calendar;
    bool _syncingMenu;

    /// <summary>Creates the window. <see cref="App"/> owns the services.</summary>
    public MainWindow(LeafServices services)
    {
        _services = services;
        InitializeComponent();

        // Title Bar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        Activated += OnActivated;
        Closed    += (_, _) => _calendar?.Dispose();

        if (_services.Google is null)
        {
            ShowSetup();
        }
        else
        {
            ShowCalendar();
        }
    }

    /// <summary>Applies the app theme to the content and caption buttons.</summary>
    public void ApplyTheme(AppTheme theme)
    {
        RootGrid.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark  => ElementTheme.Dark,
            _              => ElementTheme.Default,
        };

        AppWindow.TitleBar.PreferredTheme = theme switch
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
            _calendar.PropertyChanged += OnCalendarPropertyChanged;
            ApplyTheme(_calendar.Settings.Theme);
        }

        ContentFrame.Navigate(typeof(CalendarPage), new CalendarPageArgs(_calendar, ShowAccounts));
        ContentFrame.BackStack.Clear();
    }

    void ShowAccounts() =>
        ContentFrame.Navigate(typeof(AccountsPage), new AccountsViewModel(_services, ShowSetup));

    async Task OnCredentialsSavedAsync()
    {
        await _services.ReloadGoogleAsync();
        ShowCalendar();
    }

    void OnNavigated(object sender, NavigationEventArgs e)
    {
        var onCalendar = e.Content is CalendarPage;

        CalendarToolbar.Visibility            = onCalendar ? Visibility.Visible : Visibility.Collapsed;
        PeriodTitle.Visibility                = onCalendar ? Visibility.Visible : Visibility.Collapsed;
        AppTitleBar.IsPaneToggleButtonVisible = onCalendar;

        if (onCalendar && _calendar is not null)
        {
            PeriodTitle.Text = _calendar.PeriodTitle;
            SyncMenu();
        }
    }

    void OnBackRequested(TitleBar sender, object args)
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
        }
    }

    void OnPaneToggleRequested(TitleBar sender, object args)
    {
        if (ContentFrame.Content is CalendarPage page && _calendar is not null)
        {
            page.SetSidebarOpen(!_calendar.Settings.SidebarOpen);
        }
    }

    void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated || _services.Google is not { } google)
        {
            return;
        }

        google.Loop.Mode = SyncMode.Visible;
        google.Loop.TriggerNow();
    }

    // =========================================================================
    // TOOLBAR
    // =========================================================================

    void OnCalendarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CalendarViewModel.PeriodTitle) && _calendar is not null)
        {
            PeriodTitle.Text = _calendar.PeriodTitle;
        }
    }

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

    void OnWeekendsClick(object sender, RoutedEventArgs e)
    {
        if (!_syncingMenu)
        {
            _calendar?.ToggleWeekends();
        }
    }

    void OnDeclinedClick(object sender, RoutedEventArgs e)
    {
        if (!_syncingMenu)
        {
            _calendar?.ToggleDeclined();
        }
    }

    void OnWeekNumbersClick(object sender, RoutedEventArgs e) =>
        _calendar?.Update(s => s with { ShowWeekNumbers = WeekNumbersItem.IsChecked });

    void On24HourClick(object sender, RoutedEventArgs e) =>
        _calendar?.Update(s => s with { Use24HourTime = Clock24Item.IsChecked });

    void OnWeekStartClick(object sender, RoutedEventArgs e)
    {
        if (_calendar is not null && sender is RadioMenuFlyoutItem { Tag: string tag })
        {
            _calendar.Update(s => s with { WeekStart = Enum.Parse<DayOfWeek>(tag) });
            _calendar.NavigateTo(_calendar.PeriodStart);
        }
    }

    void OnThemeClick(object sender, RoutedEventArgs e)
    {
        if (_calendar is not null && sender is RadioMenuFlyoutItem { Tag: string tag })
        {
            var theme = Enum.Parse<AppTheme>(tag);
            _calendar.Update(s => s with { Theme = theme });
            ApplyTheme(theme);
        }
    }

    // Keep the menu's check marks and the button label in step with the settings
    void SyncMenu()
    {
        if (_calendar is null)
        {
            return;
        }

        _syncingMenu = true;
        var s = _calendar.Settings;

        ViewModeButton.Content    = s.ViewMode switch
        {
            CalendarViewMode.Day   => "Day",
            CalendarViewMode.Month => "Month",
            CalendarViewMode.Days  => $"{s.CustomDayCount} days",
            _                      => "Week",
        };
        WeekendsItem.IsChecked      = s.ShowWeekends;
        DeclinedItem.IsChecked      = s.ShowDeclined;
        WeekNumbersItem.IsChecked   = s.ShowWeekNumbers;
        Clock24Item.IsChecked       = s.Use24HourTime;
        WeekStartSunday.IsChecked   = s.WeekStart == DayOfWeek.Sunday;
        WeekStartMonday.IsChecked   = s.WeekStart == DayOfWeek.Monday;
        WeekStartSaturday.IsChecked = s.WeekStart == DayOfWeek.Saturday;
        ThemeSystem.IsChecked       = s.Theme == AppTheme.System;
        ThemeLight.IsChecked        = s.Theme == AppTheme.Light;
        ThemeDark.IsChecked         = s.Theme == AppTheme.Dark;

        _syncingMenu = false;
    }
}
