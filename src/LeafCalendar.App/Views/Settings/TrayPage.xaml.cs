using LeafCalendar.App.Controls;
using LeafCalendar.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LeafCalendar.App.Views.Settings;

/// <summary>
/// Settings › Tray (spec 9): how many days the flyout lists, whether all-day events are in it, and how far ahead the
/// flyout header and the tooltip look. Changes save right away; the flyout and tooltip use them from their next refresh.
/// The next event is always looked for in today and tomorrow, whatever the agenda's days, so a lookahead that crosses
/// midnight still finds it. Last, which of the calendars shown in Leaf the flyout and tooltip include.
/// </summary>
public sealed partial class TrayPage : Page
{
    SettingsContext _context = null!;

    // True while the saved values are being shown (the controls' change events are ignored meanwhile)
    bool _loading = true;

    // The calendar rows on screen
    List<TrayCalendarRow> _calendarRows = [];

    /// <summary>Creates the page.</summary>
    public TrayPage()
    {
        InitializeComponent();
        DaysBox.Maximum  = LeafSettings.MaxFlyoutDays;
        DaysRow.Description = $"How many days the tray flyout lists, starting today. From 1 to {LeafSettings.MaxFlyoutDays}. It shows the calendars you show in Leaf.";
        ScrollIndicator.ShowOnHover(PageScroll);
    }

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _context = (SettingsContext)e.Parameter;
        _context.Window.SettingsChanged  += OnSettingsChanged;
        _context.Window.CalendarsChanged += OnSettingsChanged;
        Load();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _context.Window.SettingsChanged  -= OnSettingsChanged;
        _context.Window.CalendarsChanged -= OnSettingsChanged;
    }

    /// <summary>x:Bind helper: the swatch brush for a hex color.</summary>
    public static Brush Brush(string hex) => LeafBrushes.FromHex(hex);

    void OnSettingsChanged(object? sender, EventArgs e) => Load();

    // Show The Saved Values (the lookahead items are in LookaheadChoices order)
    void Load()
    {
        var s = _context.Calendar.Settings;
        _loading = true;

        DaysBox.Value              = s.FlyoutDays;
        AllDaySwitch.IsOn          = s.FlyoutAllDay;
        LookaheadBox.SelectedIndex = LeafSettings.LookaheadChoices.ToList().IndexOf(s.TrayLookaheadMinutes);

        // Calendars Shown In Leaf, By Account (a plain list of App rows; kept when nothing changed, so a toggled checkbox keeps focus)
        var excluded = s.TrayExcludedCalendars.ToHashSet();
        var rows     = _context.Calendar.CalendarGroups()
            .SelectMany(g => g.Calendars.Where(c => c.Info.IsVisible).Select(c => new TrayCalendarRow(
                c.Info.AccountId, c.Info.Id, c.Name, g.Email, c.Info.DisplayColor, !excluded.Contains(new CalendarRef(c.Info.AccountId, c.Info.Id)), SetIncluded)))
            .ToList();
        if (!rows.Select(Shown).SequenceEqual(_calendarRows.Select(Shown)))
        {
            _calendarRows            = rows;
            CalendarList.ItemsSource = _calendarRows;
        }

        _loading = false;
    }

    // What a row shows (two rows that show the same need no rebuild)
    static (string, string, string, string, string, bool) Shown(TrayCalendarRow r) => (r.AccountId, r.CalendarId, r.Name, r.AccountEmail, r.Color, r.IsOn);

    // A checkbox changed: the calendar leaves or rejoins the exclusion list
    void SetIncluded(TrayCalendarRow row, bool included)
    {
        if (_loading)
        {
            return;
        }

        var calendar = new CalendarRef(row.AccountId, row.CalendarId);
        _context.Save(s => s with
        {
            TrayExcludedCalendars = included ? [.. s.TrayExcludedCalendars.Where(c => c != calendar)] : [.. s.TrayExcludedCalendars.Where(c => c != calendar), calendar],
        });
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

/// <summary>One calendar in Settings › Tray (an App class, so a WinRT list never holds Core types).</summary>
public sealed class TrayCalendarRow(string accountId, string calendarId, string name, string accountEmail, string color, bool isOn, Action<TrayCalendarRow, bool> changed)
{
    bool _isOn = isOn;

    /// <summary>The calendar's account.</summary>
    public string AccountId { get; } = accountId;

    /// <summary>The calendar's ID.</summary>
    public string CalendarId { get; } = calendarId;

    /// <summary>The calendar's name.</summary>
    public string Name { get; } = name;

    /// <summary>The account's address, under the name.</summary>
    public string AccountEmail { get; } = accountEmail;

    /// <summary>The calendar's color (hex).</summary>
    public string Color { get; } = color;

    /// <summary><c>TrayCalendar_&lt;calendarId&gt;</c>.</summary>
    public string AutomationId => $"TrayCalendar_{CalendarId}";

    /// <summary>True when the flyout and tooltip include the calendar; the checkbox sets it (two-way x:Bind).</summary>
    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (value == _isOn)
            {
                return;
            }

            _isOn = value;
            changed(this, value);
        }
    }
}
