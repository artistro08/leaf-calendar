using System.Globalization;
using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel;

namespace LeafCalendar.App.Views.Settings;

/// <summary>
/// Settings › General: theme and interface scale, the calendar view options, date and time, working hours, the map
/// site for locations, and starting with Windows. Every change saves
/// right away through the shared calendar view model, so the main window follows it; changes made elsewhere (the view
/// menu, shortcuts) show here too. Starting with Windows is the package's startup task, whose state Windows owns.
/// </summary>
public sealed partial class GeneralPage : Page
{
    // Combo Box Order (matches the items in the XAML)
    static readonly AppTheme[] Themes        = [AppTheme.System, AppTheme.Light, AppTheme.Dark];
    static readonly DayOfWeek[] WeekStarts   = [DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Saturday];
    static readonly MapProvider[] MapSources = [MapProvider.Google, MapProvider.Bing];

    static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    // Work Day Toggles, Each With Its Own Day (never read back from the button)
    readonly List<(ToggleButton Button, DayOfWeek Day)> _workDays = [];

    // The startup task declared in Package.appxmanifest
    const string StartupTaskId = "LeafCalendarStartup";

    // The startup row's description when Windows leaves the switch to Leaf
    const string StartupDescription = "Leaf starts in the tray when you sign in to Windows, so reminders arrive on time.";

    SettingsContext _context = null!;

    // True until the saved values are shown (setting the slider's range in the constructor already raises ValueChanged)
    bool _loading = true;

    // True while the startup task's state is being shown
    bool _loadingStartup;

    // Counts startup loads so a slow older one can't overwrite a newer one's result or clear its flag
    int _startupLoads;

    /// <summary>Creates the page.</summary>
    public GeneralPage()
    {
        InitializeComponent();
        ScrollIndicator.ShowOnHover(PageScroll);
        HourHeightSlider.Minimum = LeafSettings.MinHourHeight;
        HourHeightSlider.Maximum = LeafSettings.MaxHourHeight;

        // Choices Built From The Settings' Own Lists ("80%", "Next 2 hours")
        InterfaceScaleBox.ItemsSource = LeafSettings.ScaleChoices.Select(s => s.ToString("0%", English)).ToList();
        UpcomingHoursBox.ItemsSource  = LeafSettings.UpcomingChoices.Select(h => string.Create(English, $"Next {h} hours")).ToList();
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

        // Milestone 5 Settings
        InterfaceScaleBox.SelectedIndex = LeafSettings.ScaleChoices.ToList().IndexOf(s.InterfaceScale);
        AllDayExpandedSwitch.IsOn       = s.AllDayExpanded;
        UpcomingHoursBox.SelectedIndex  = LeafSettings.UpcomingChoices.ToList().IndexOf(s.UpcomingHours);
        MapProviderBox.SelectedIndex    = Array.IndexOf(MapSources, s.MapProvider);
        LoadWorkingHours(s.WorkingHours, s.WeekStart, s.Use24HourTime);

        _loading = false;
    }

    // =========================================================================
    // APPEARANCE, UPCOMING, AND LOCATIONS
    // =========================================================================

    void OnInterfaceScaleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && InterfaceScaleBox.SelectedIndex >= 0)
        {
            var scale = LeafSettings.ScaleChoices[InterfaceScaleBox.SelectedIndex];
            _context.Save(s => s with { InterfaceScale = scale });
        }
    }

    void OnAllDayExpandedToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            var on = AllDayExpandedSwitch.IsOn;
            _context.Save(s => s with { AllDayExpanded = on });
        }
    }

    void OnUpcomingHoursChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && UpcomingHoursBox.SelectedIndex >= 0)
        {
            var hours = LeafSettings.UpcomingChoices[UpcomingHoursBox.SelectedIndex];
            _context.Save(s => s with { UpcomingHours = hours });
        }
    }

    void OnMapProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && MapProviderBox.SelectedIndex >= 0)
        {
            var provider = MapSources[MapProviderBox.SelectedIndex];
            _context.Save(s => s with { MapProvider = provider });
        }
    }

    // =========================================================================
    // WORKING HOURS
    // =========================================================================

    // The switch, the two times, and a toggle per weekday in the week's own order (the rows are off while the switch is)
    void LoadWorkingHours(WorkingHours hours, DayOfWeek weekStart, bool use24Hour)
    {
        WorkingHoursSwitch.IsOn            = hours.Enabled;
        WorkingHoursRows.IsEnabled         = hours.Enabled;
        WorkingStartPicker.ClockIdentifier = use24Hour ? "24HourClock" : "12HourClock";
        WorkingEndPicker.ClockIdentifier   = WorkingStartPicker.ClockIdentifier;
        WorkingStartPicker.Time            = TimeSpan.FromMinutes(hours.StartMinute);
        WorkingEndPicker.Time              = TimeSpan.FromMinutes(hours.EndMinute);
        WorkingHoursError.Visibility       = Visibility.Collapsed;

        // Day Toggles (rebuilt only when the week's first day changed)
        if (_workDays.Count == 0 || _workDays[0].Day != weekStart)
        {
            WorkDaysPanel.Children.Clear();
            _workDays.Clear();
            for (var i = 0; i < 7; i++)
            {
                var day    = (DayOfWeek)(((int)weekStart + i) % 7);
                var toggle = new ToggleButton { MinWidth = 40, Width = 40, Padding = new Thickness(0), Content = English.DateTimeFormat.GetAbbreviatedDayName(day) };
                AutomationProperties.SetName(toggle, English.DateTimeFormat.GetDayName(day));
                AutomationProperties.SetAutomationId(toggle, $"WorkingDay_{day}");
                toggle.Click += (_, _) => OnWorkDayClick();
                WorkDaysPanel.Children.Add(toggle);
                _workDays.Add((toggle, day));
            }
        }

        foreach (var (button, day) in _workDays)
        {
            button.IsChecked = hours.Days.Contains(day);
        }
    }

    void OnWorkingHoursToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            var on = WorkingHoursSwitch.IsOn;
            WorkingHoursRows.IsEnabled = on;
            _context.Save(s => s with { WorkingHours = s.WorkingHours with { Enabled = on } });
        }
    }

    void OnWorkingStartChanged(object? sender, TimePickerValueChangedEventArgs e) => SaveHours(e.OldTime, isStart: true);

    void OnWorkingEndChanged(object? sender, TimePickerValueChangedEventArgs e) => SaveHours(e.OldTime, isStart: false);

    // An end that isn't after the start is refused: the line says why, the picker goes back, and nothing is saved
    void SaveHours(TimeSpan before, bool isStart)
    {
        if (_loading)
        {
            return;
        }

        var start = (int)WorkingStartPicker.Time.TotalMinutes;
        var end   = (int)WorkingEndPicker.Time.TotalMinutes;
        if (end <= start)
        {
            // Put back after the picker finishes its own change: set inside TimeChanged, the picker overwrites it
            WorkingHoursError.Visibility = Visibility.Visible;
            var picker = isStart ? WorkingStartPicker : WorkingEndPicker;
            DispatcherQueue.TryEnqueue(() =>
            {
                _loading    = true;
                picker.Time = before;
                _loading    = false;
            });
            return;
        }

        WorkingHoursError.Visibility = Visibility.Collapsed;
        _context.Save(s => s with { WorkingHours = s.WorkingHours with { StartMinute = start, EndMinute = end } });
    }

    void OnWorkDayClick()
    {
        if (_loading)
        {
            return;
        }

        List<DayOfWeek> days = [.. _workDays.Where(d => d.Button.IsChecked == true).Select(d => d.Day)];
        _context.Save(s => s with { WorkingHours = s.WorkingHours with { Days = days } });
    }

    void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && ThemeBox.SelectedIndex >= 0)
        {
            var theme = Themes[ThemeBox.SelectedIndex];
            _context.Save(s => s with { Theme = theme });
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
            _context.Save(s => s with { CustomDayCount = days });
        }
    }

    void OnHourHeightChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_loading && e.NewValue != Calendar.Settings.HourHeight)
        {
            _context.Save(s => s with { HourHeight = e.NewValue });
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
            _context.Save(s => s with { ShowWeekNumbers = on });
        }
    }

    void OnWeekStartChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && WeekStartBox.SelectedIndex >= 0)
        {
            var day = WeekStarts[WeekStartBox.SelectedIndex];
            _context.Save(s => s with { WeekStart = day });
            Calendar.NavigateTo(Calendar.PeriodStart);
        }
    }

    void On24HourToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            var on = Clock24Switch.IsOn;
            _context.Save(s => s with { Use24HourTime = on });
        }
    }

    // =========================================================================
    // START WITH WINDOWS
    // =========================================================================

    // Windows owns the startup state: turned off in Task Manager, only the user can turn it back on there; a policy
    // decides it for them. Runs unawaited from navigation, so nothing may escape
    async Task LoadStartupAsync()
    {
        var load = ++_startupLoads;
        _loadingStartup = true;
        try
        {
            var task  = await StartupTask.GetAsync(StartupTaskId);
            if (load != _startupLoads)
            {
                return;
            }

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
            if (load == _startupLoads)
            {
                _loadingStartup = false;
            }
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

        }
        catch (Exception ex)
        {
            _context.Services.Log.Info("settings.startup.failed", $"error={ex.GetType().Name}");
        }
        finally
        {
            // Show what Windows really says, also when the change failed or was refused
            await LoadStartupAsync();
        }
    }
}
