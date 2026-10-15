using System.Globalization;
using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Places;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel;
using Windows.System;

namespace LeafCalendar.App.Views.Settings;

/// <summary>
/// Settings › General: theme, the calendar view options, date and time, working hours, the map
/// site for locations, location suggestions (with the Maps API key), and starting with Windows. Every change saves
/// right away through the shared calendar view model, so the main window follows it; changes made elsewhere (the view
/// menu, shortcuts) show here too. Starting with Windows is the package's startup task, whose state Windows owns.
/// </summary>
public sealed partial class GeneralPage : Page
{
    // Combo Box Order (matches the items in the XAML)
    private static readonly AppTheme[] Themes = [AppTheme.System, AppTheme.Light, AppTheme.Dark];
    private static readonly DayOfWeek[] WeekStarts = [DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Saturday];
    private static readonly MapProvider[] MapSources = [MapProvider.Google, MapProvider.Bing];

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    // Work Day Checkboxes In The Dropdown, Each With Its Own Day (never read back from the box)
    private readonly List<(CheckBox Box, DayOfWeek Day)> _workDays = [];

    // The startup task declared in Package.appxmanifest
    private const string StartupTaskId = "LeafCalendarStartup";

    // The startup row's description when Windows leaves the switch to Leaf Calendar
    private const string StartupDescription = "Leaf Calendar starts when you sign in to Windows, so reminders arrive on time.";

    private SettingsContext _context = null!;

    // True until the saved values are shown (setting the slider's range in the constructor already raises ValueChanged)
    private bool _loading = true;

    // True while the startup task's state is being shown
    private bool _loadingStartup;

    // Counts startup loads so a slow older one can't overwrite a newer one's result or clear its flag
    private int _startupLoads;

    // Counts Maps key checks so a slow older answer can't replace a newer one's line
    private int _keyChecks;

    // The key the box was last filled with or saved, so only a real change saves (a key that couldn't be read when the
    // page opened shows as empty, and Save mustn't delete it unless the box changed)
    private string _keyShown = "";

    /// <summary>Creates the page.</summary>
    public GeneralPage()
    {
        InitializeComponent();
        ScrollIndicator.ShowOnHover(PageScroll);
        HourHeightSlider.Minimum = LeafSettings.MinHourHeight;
        HourHeightSlider.Maximum = LeafSettings.MaxHourHeight;
        PasswordClearButton.Attach(MapsKeyBox);

        // Choices Built From The Settings' Own List ("Next 2 hours")
        UpcomingHoursBox.ItemsSource = LeafSettings.UpcomingChoices.Select(h => string.Create(English, $"Next {h} hours")).ToList();
    }

    private CalendarViewModel Calendar => _context.Calendar;

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _context = (SettingsContext)e.Parameter;
        _context.Host.SettingsChanged += OnSettingsChanged;
        Load();
        _ = LoadStartupAsync();

        // The Saved Key, Once (Load runs again on every settings change and would replace what's being typed)
        _keyShown = _context.Services.Places.SavedKey ?? "";
        MapsKeyBox.Password = _keyShown;
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e) => _context.Host.SettingsChanged -= OnSettingsChanged;

    private void OnSettingsChanged(object? sender, EventArgs e) => Load();

    // Show The Saved Values (the controls' change events are ignored meanwhile)
    private void Load()
    {
        var s = Calendar.Settings;
        _loading = true;

        ThemeBox.SelectedIndex = Array.IndexOf(Themes, s.Theme);
        DaysBox.Value = s.CustomDayCount;
        HourHeightSlider.Value = s.HourHeight;
        WeekendsSwitch.IsOn = s.ShowWeekends;
        DeclinedSwitch.IsOn = s.ShowDeclined;
        WeekNumbersSwitch.IsOn = s.ShowWeekNumbers;
        WeekStartBox.SelectedIndex = Array.IndexOf(WeekStarts, s.WeekStart);
        Clock24Switch.IsOn = s.Use24HourTime;

        // Milestone 5 Settings
        AllDayExpandedSwitch.IsOn = s.AllDayExpanded;
        UpcomingHoursBox.SelectedIndex = LeafSettings.UpcomingChoices.ToList().IndexOf(s.UpcomingHours);
        MapProviderBox.SelectedIndex = Array.IndexOf(MapSources, s.MapProvider);
        PlacesSwitch.IsOn = s.LocationSuggestions;
        MapsKeyBox.IsEnabled = s.LocationSuggestions;
        UpdateMapsKeySave();
        LoadWorkingHours(s.WorkingHours, s.WeekStart, s.Use24HourTime);
        StartMinimizedSwitch.IsOn = !s.OpenWindowAtSignIn;

        _loading = false;
    }

    // =========================================================================
    // APPEARANCE, UPCOMING, AND LOCATIONS
    // =========================================================================

    private void OnAllDayExpandedToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            var on = AllDayExpandedSwitch.IsOn;
            _context.Save(s => s with { AllDayExpanded = on });
        }
    }

    private void OnUpcomingHoursChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && UpcomingHoursBox.SelectedIndex >= 0)
        {
            var hours = LeafSettings.UpcomingChoices[UpcomingHoursBox.SelectedIndex];
            _context.Save(s => s with { UpcomingHours = hours });
        }
    }

    private void OnMapProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && MapProviderBox.SelectedIndex >= 0)
        {
            var provider = MapSources[MapProviderBox.SelectedIndex];
            _context.Save(s => s with { MapProvider = provider });
        }
    }

    private void OnPlacesToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        // Turning On Opens The Key Row
        var on = PlacesSwitch.IsOn;
        MapsKeyBox.IsEnabled = on;
        UpdateMapsKeySave();
        PlacesExpander.IsExpanded |= on;
        _context.Save(s => s with { LocationSuggestions = on });
    }

    // Save is on only while the box differs from the saved key
    private void OnMapsKeyChanged(object sender, RoutedEventArgs e) => UpdateMapsKeySave();

    private void UpdateMapsKeySave() => MapsKeySaveButton.IsEnabled = MapsKeyBox.IsEnabled && MapsKeyBox.Password.Trim() != _keyShown;

    // Enter saves too (not gated on Save, which follows PasswordChanged a moment after typing or pasting)
    private void OnMapsKeyKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            OnMapsKeySave(sender, e);
        }
    }

    // Save (or Enter) stores a changed key (an empty box removes it), then one lookup says whether Google accepts it.
    // Network trouble says nothing; only the newest check's answer shows.
    private async void OnMapsKeySave(object sender, RoutedEventArgs e)
    {
        try
        {
            var places = _context.Services.Places;
            var key = MapsKeyBox.Password.Trim();
            if (key == _keyShown)
            {
                return;
            }

            places.SaveKey(key);
            _keyShown = key;
            UpdateMapsKeySave();
            var check = ++_keyChecks;
            ShowKeyCheck("");
            if (key.Length == 0)
            {
                return;
            }

            var answer = await places.CheckKeyAsync(key, CancellationToken.None);
            if (check == _keyChecks)
            {
                ShowKeyCheck(answer switch
                {
                    KeyCheck.Works => "Key is setup and active.",
                    KeyCheck.Refused => "Google didn't accept this key. Check that Places API (New) and billing are turned on for it.",
                    _ => "",
                });
            }
        }
        catch (Exception ex)
        {
            _context.Services.Log.Info("places.key.save.failed", $"error={ex.GetType().Name}");
        }
    }

    private void ShowKeyCheck(string text)
    {
        MapsKeyCheckText.Text = text;
        MapsKeyCheckText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // =========================================================================
    // WORKING HOURS
    // =========================================================================

    // The switch, the two times, and a checkbox per weekday in the week's own order (the rows are off while the switch is)
    private void LoadWorkingHours(WorkingHours hours, DayOfWeek weekStart, bool use24Hour)
    {
        WorkingHoursSwitch.IsOn = hours.Enabled;
        WorkingHoursRows.IsEnabled = hours.Enabled;
        WorkingStartPicker.ClockIdentifier = use24Hour ? "24HourClock" : "12HourClock";
        WorkingEndPicker.ClockIdentifier = WorkingStartPicker.ClockIdentifier;
        WorkingStartPicker.Time = TimeSpan.FromMinutes(hours.StartMinute);
        WorkingEndPicker.Time = TimeSpan.FromMinutes(hours.EndMinute);
        WorkingHoursError.Visibility = Visibility.Collapsed;

        // Day Checkboxes (rebuilt only when the week's first day changed)
        if (_workDays.Count == 0 || _workDays[0].Day != weekStart)
        {
            WorkDaysPanel.Children.Clear();
            _workDays.Clear();
            for (var i = 0; i < 7; i++)
            {
                var day = (DayOfWeek)(((int)weekStart + i) % 7);
                var box = new CheckBox { Content = English.DateTimeFormat.GetDayName(day) };
                AutomationProperties.SetAutomationId(box, $"WorkingDay_{day}");
                box.Click += (_, _) => OnWorkDayClick();
                WorkDaysPanel.Children.Add(box);
                _workDays.Add((box, day));
            }
        }

        foreach (var (box, day) in _workDays)
        {
            box.IsChecked = hours.Days.Contains(day);
        }

        ShowWorkDays(WorkingHoursMath.DaysLabel(hours.Days, weekStart));
    }

    private void OnWorkingHoursToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            var on = WorkingHoursSwitch.IsOn;
            WorkingHoursRows.IsEnabled = on;
            _context.Save(s => s with { WorkingHours = s.WorkingHours with { Enabled = on } });
        }
    }

    private void OnWorkingStartChanged(object? sender, TimePickerValueChangedEventArgs e) => SaveHours(e.OldTime, isStart: true);

    private void OnWorkingEndChanged(object? sender, TimePickerValueChangedEventArgs e) => SaveHours(e.OldTime, isStart: false);

    // An end that isn't after the start is refused: the line says why, the picker goes back, and nothing is saved
    private void SaveHours(TimeSpan before, bool isStart)
    {
        if (_loading)
        {
            return;
        }

        var start = (int)WorkingStartPicker.Time.TotalMinutes;
        var end = (int)WorkingEndPicker.Time.TotalMinutes;
        if (end <= start)
        {
            // Put back after the picker finishes its own change: set inside TimeChanged, the picker overwrites it
            WorkingHoursError.Visibility = Visibility.Visible;
            var picker = isStart ? WorkingStartPicker : WorkingEndPicker;
            DispatcherQueue.TryEnqueue(() =>
            {
                _loading = true;
                picker.Time = before;
                _loading = false;
            });
            return;
        }

        WorkingHoursError.Visibility = Visibility.Collapsed;
        _context.Save(s => s with { WorkingHours = s.WorkingHours with { StartMinute = start, EndMinute = end } });
    }

    // The button says which days (screen readers hear it after the name, as help text)
    private void ShowWorkDays(string label)
    {
        WorkDaysButton.Content = label;
        AutomationProperties.SetHelpText(WorkDaysButton, label);
    }

    private void OnWorkDayClick()
    {
        if (_loading)
        {
            return;
        }

        List<DayOfWeek> days = [.. _workDays.Where(d => d.Box.IsChecked == true).Select(d => d.Day)];
        ShowWorkDays(WorkingHoursMath.DaysLabel(days, _workDays[0].Day));
        _context.Save(s => s with { WorkingHours = s.WorkingHours with { Days = days } });
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && ThemeBox.SelectedIndex >= 0)
        {
            var theme = Themes[ThemeBox.SelectedIndex];
            _context.Save(s => s with { Theme = theme });
        }
    }

    // In the custom view the new count shows right away; otherwise it's kept for when that view is picked
    private void OnDaysChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading)
        {
            return;
        }

        // A cleared box goes back to the saved value
        if (double.IsNaN(args.NewValue))
        {
            _loading = true;
            DaysBox.Value = Calendar.Settings.CustomDayCount;
            _loading = false;
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

    // The value beside the slider follows it, also while the saved value is shown ("48 px")
    private void OnHourHeightChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        HourHeightValue.Text = string.Create(English, $"{e.NewValue:0} px");
        if (!_loading && e.NewValue != Calendar.Settings.HourHeight)
        {
            _context.Save(s => s with { HourHeight = e.NewValue });
        }
    }

    private void OnWeekendsToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading && WeekendsSwitch.IsOn != Calendar.Settings.ShowWeekends)
        {
            Calendar.ToggleWeekends();
        }
    }

    private void OnDeclinedToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading && DeclinedSwitch.IsOn != Calendar.Settings.ShowDeclined)
        {
            Calendar.ToggleDeclined();
        }
    }

    private void OnWeekNumbersToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            var on = WeekNumbersSwitch.IsOn;
            _context.Save(s => s with { ShowWeekNumbers = on });
        }
    }

    private void OnWeekStartChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && WeekStartBox.SelectedIndex >= 0)
        {
            var day = WeekStarts[WeekStartBox.SelectedIndex];
            _context.Save(s => s with { WeekStart = day });
            Calendar.NavigateTo(Calendar.PeriodStart);
        }
    }

    private void On24HourToggled(object sender, RoutedEventArgs e)
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
    private async Task LoadStartupAsync()
    {
        var load = ++_startupLoads;
        _loadingStartup = true;
        try
        {
            var task = await StartupTask.GetAsync(StartupTaskId);
            if (load != _startupLoads)
            {
                return;
            }

            var state = task.State;
            StartupSwitch.IsOn = state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            StartMinimizedSwitch.IsEnabled = StartupSwitch.IsOn;
            StartupSwitch.IsEnabled = state is StartupTaskState.Enabled or StartupTaskState.Disabled;
            StartupRow.Description = state switch
            {
                StartupTaskState.DisabledByUser => "Turned off in Task Manager › Startup apps. Turn it on there.",
                StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy => "Your organization manages this setting.",
                _ => StartupDescription,
            };
        }
        catch (Exception ex)
        {
            // Not packaged, or the task is missing: the switch stays off; the type only
            StartupSwitch.IsEnabled = false;
            StartMinimizedSwitch.IsEnabled = false;
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

    // On (the default): Windows' sign-in start stays in the tray; off: it opens the main window too
    private void OnStartMinimizedToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            var open = !StartMinimizedSwitch.IsOn;
            _context.Save(s => s with { OpenWindowAtSignIn = open });
        }
    }

    private async void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingStartup)
        {
            return;
        }

        // async void: anything that escapes here would end the process
        try
        {
            var on = StartupSwitch.IsOn;
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
