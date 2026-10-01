using LeafCalendar.App.Controls;
using LeafCalendar.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LeafCalendar.App.Views.Settings;

/// <summary>
/// Settings › Tray (spec 9): how many days the flyout lists, whether all-day events are in it, and how far ahead the
/// flyout header and the tooltip look. Changes save right away; the flyout and tooltip use them from their next refresh.
/// The next event is always looked for in today and tomorrow, whatever the agenda's days, so a lookahead that crosses
/// midnight still finds it. Which calendars appear follows the calendars shown in Leaf until the full settings page
/// (Milestone 5).
/// </summary>
public sealed partial class TrayPage : Page
{
    SettingsContext _context = null!;

    // True while the saved values are being shown (the controls' change events are ignored meanwhile)
    bool _loading = true;

    /// <summary>Creates the page.</summary>
    public TrayPage()
    {
        InitializeComponent();
        ScrollIndicator.ShowOnHover(PageScroll);
    }

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _context = (SettingsContext)e.Parameter;
        _context.Window.SettingsChanged += OnSettingsChanged;
        Load();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e) => _context.Window.SettingsChanged -= OnSettingsChanged;

    void OnSettingsChanged(object? sender, EventArgs e) => Load();

    // Show The Saved Values (the lookahead items are in LookaheadChoices order)
    void Load()
    {
        var s = _context.Calendar.Settings;
        _loading = true;

        DaysBox.Value              = s.FlyoutDays;
        AllDaySwitch.IsOn          = s.FlyoutAllDay;
        LookaheadBox.SelectedIndex = LeafSettings.LookaheadChoices.ToList().IndexOf(s.TrayLookaheadMinutes);

        _loading = false;
    }

    void OnDaysChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue))
        {
            return;
        }

        var days = (int)Math.Clamp(args.NewValue, 1, LeafSettings.MaxFlyoutDays);
        _context.Save(s => s with { FlyoutDays = days });
    }

    void OnAllDayToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            var on = AllDaySwitch.IsOn;
            _context.Save(s => s with { FlyoutAllDay = on });
        }
    }

    void OnLookaheadChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && LookaheadBox.SelectedIndex >= 0)
        {
            var minutes = LeafSettings.LookaheadChoices[LookaheadBox.SelectedIndex];
            _context.Save(s => s with { TrayLookaheadMinutes = minutes });
        }
    }
}
