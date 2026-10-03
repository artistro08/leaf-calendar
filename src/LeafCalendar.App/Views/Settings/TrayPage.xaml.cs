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
/// midnight still finds it. The tray shows the calendars shown in Leaf, so there's no calendar choice here.
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
        DaysBox.Maximum     = LeafSettings.MaxFlyoutDays;
        DaysRow.Description = $"How many days the tray flyout lists, starting today. From 1 to {LeafSettings.MaxFlyoutDays}. It shows the calendars you show in Leaf.";
        ScrollIndicator.ShowOnHover(PageScroll);
    }

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _context = (SettingsContext)e.Parameter;
        _context.Host.SettingsChanged += OnSettingsChanged;
        Load();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e) => _context.Host.SettingsChanged -= OnSettingsChanged;

    void OnSettingsChanged(object? sender, EventArgs e) => Load();

    // Show The Saved Values (the lookahead items are in LookaheadChoices order)
    void Load()
    {
        var s = _context.Calendar.Settings;
        _loading = true;

        IconSwitch.IsOn            = !s.HideTrayIcon;
        DaysBox.Value              = s.FlyoutDays;
        AllDaySwitch.IsOn          = s.FlyoutAllDay;
        LookaheadBox.SelectedIndex = LeafSettings.LookaheadChoices.ToList().IndexOf(s.TrayLookaheadMinutes);

        _loading = false;
    }

    void OnDaysChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading)
        {
            return;
        }

        // A cleared box goes back to the saved value
        if (double.IsNaN(args.NewValue))
        {
            Load();
            return;
        }

        var days = (int)Math.Clamp(args.NewValue, 1, LeafSettings.MaxFlyoutDays);
        _context.Save(s => s with { FlyoutDays = days });
    }

    void OnIconToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            var hidden = !IconSwitch.IsOn;
            _context.Save(s => s with { HideTrayIcon = hidden });
        }
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
