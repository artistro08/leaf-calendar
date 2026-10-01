using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel;

namespace LeafCalendar.App.Views.Settings;

/// <summary>
/// Settings › General: theme, the calendar view options, date and time, and starting with Windows. Every change saves
/// right away through the shared calendar view model, so the main window follows it; changes made elsewhere (the view
/// menu, shortcuts) show here too. Starting with Windows is the package's startup task, whose state Windows owns.
/// </summary>
public sealed partial class GeneralPage : Page
{
    // Combo Box Order (matches the items in the XAML)
    static readonly AppTheme[] Themes      = [AppTheme.System, AppTheme.Light, AppTheme.Dark];
    static readonly DayOfWeek[] WeekStarts = [DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Saturday];

    // The startup task declared in Package.appxmanifest
    const string StartupTaskId = "LeafCalendarStartup";

    // The startup row's description when Windows leaves the switch to Leaf
    const string StartupDescription = "Leaf starts in the tray when you sign in to Windows, so reminders arrive on time.";

    SettingsContext _context = null!;

    // True until the saved values are shown (setting the slider's range in the constructor already raises ValueChanged)
    bool _loading = true;

    // True while the startup task's state is being shown
    bool _loadingStartup;

    /// <summary>Creates the page.</summary>
    public GeneralPage()
    {
        InitializeComponent();
        ScrollIndicator.ShowOnHover(PageScroll);
        HourHeightSlider.Minimum = LeafSettings.MinHourHeight;
        HourHeightSlider.Maximum = LeafSettings.MaxHourHeight;
    }

    CalendarViewModel Calendar => _context.Calendar;

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _context = (SettingsContext)e.Parameter;
        _context.Window.SettingsChanged += OnSettingsChanged;
        Load();
        _ = LoadStartupAsync();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e) => _context.Window.SettingsChanged -= OnSettingsChanged;

    void OnSettingsChanged(object? sender, EventArgs e) => Load();

    // Show The Saved Values (the controls' change events are ignored meanwhile)
    void Load()
    {
        var s = Calendar.Settings;
        _loading = true;

        ThemeBox.SelectedIndex     = Array.IndexOf(Themes, s.Theme);
        DaysBox.Value              = s.CustomDayCount;
        HourHeightSlider.Value     = s.HourHeight;
        WeekendsSwitch.IsOn        = s.ShowWeekends;
        DeclinedSwitch.IsOn        = s.ShowDeclined;
        WeekNumbersSwitch.IsOn     = s.ShowWeekNumbers;
        WeekStartBox.SelectedIndex = Array.IndexOf(WeekStarts, s.WeekStart);
        Clock24Switch.IsOn         = s.Use24HourTime;

        _loading = false;
    }

    void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && ThemeBox.SelectedIndex >= 0)
        {
            var theme = Themes[ThemeBox.SelectedIndex];
            Calendar.Update(s => s with { Theme = theme });
        }
    }

    // In the custom view the new count shows right away; otherwise it's kept for when that view is picked
    void OnDaysChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue))
        {
            return;
        }

        var days = (int)Math.Clamp(args.NewValue, 1, 31);
        if (Calendar.Mode == CalendarViewMode.Days)
        {
            Calendar.SetMode(CalendarViewMode.Days, days);
        }
        else
        {
            Calendar.Update(s => s with { CustomDayCount = days });
        }
    }

    void OnHourHeightChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_loading && e.NewValue != Calendar.Settings.HourHeight)
        {
            Calendar.Update(s => s with { HourHeight = e.NewValue });
        }
    }

    void OnWeekendsToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading && WeekendsSwitch.IsOn != Calendar.Settings.ShowWeekends)
        {
            Calendar.ToggleWeekends();
        }
    }

    void OnDeclinedToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading && DeclinedSwitch.IsOn != Calendar.Settings.ShowDeclined)
        {
            Calendar.ToggleDeclined();
        }
    }

    void OnWeekNumbersToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            var on = WeekNumbersSwitch.IsOn;
            Calendar.Update(s => s with { ShowWeekNumbers = on });
        }
    }

    void OnWeekStartChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && WeekStartBox.SelectedIndex >= 0)
        {
            var day = WeekStarts[WeekStartBox.SelectedIndex];
            Calendar.Update(s => s with { WeekStart = day });
            Calendar.NavigateTo(Calendar.PeriodStart);
        }
    }

    void On24HourToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            var on = Clock24Switch.IsOn;
            Calendar.Update(s => s with { Use24HourTime = on });
        }
    }

    // =========================================================================
    // START WITH WINDOWS
    // =========================================================================

    // Windows owns the startup state: turned off in Task Manager, only the user can turn it back on there; a policy
    // decides it for them. Runs unawaited from navigation, so nothing may escape
    async Task LoadStartupAsync()
    {
        _loadingStartup = true;
        try
        {
            var task  = await StartupTask.GetAsync(StartupTaskId);
            var state = task.State;
            StartupSwitch.IsOn      = state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            StartupSwitch.IsEnabled = state is StartupTaskState.Enabled or StartupTaskState.Disabled;
            StartupRow.Description  = state switch
            {
                StartupTaskState.DisabledByUser                                       => "Turned off in Task Manager › Startup apps. Turn it on there.",
                StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy => "Your organization manages this setting.",
                _                                                                     => StartupDescription,
            };
        }
        catch (Exception ex)
        {
            // Not packaged, or the task is missing: the switch stays off; the type only
            StartupSwitch.IsEnabled = false;
            _context.Services.Log.Info("settings.startup.failed", $"error={ex.GetType().Name}");
        }
        finally
        {
            _loadingStartup = false;
        }
    }

    async void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingStartup)
        {
            return;
        }

        // async void: anything that escapes here would end the process
        try
        {
            var on   = StartupSwitch.IsOn;
            var task = await StartupTask.GetAsync(StartupTaskId);
            if (on)
            {
                await task.RequestEnableAsync();
            }
            else
            {
                task.Disable();
            }

            await LoadStartupAsync();
        }
        catch (Exception ex)
        {
            _context.Services.Log.Info("settings.startup.failed", $"error={ex.GetType().Name}");
        }
    }
}
