using System.Collections.ObjectModel;
using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LeafCalendar.App.Views.Settings;

/// <summary>
/// Settings › Time zones: Leaf's primary time zone (Windows' by default, or a zone Leaf keeps, with an offer to switch
/// when Windows' zone changes), then the extra time-zone columns (up to four), added, renamed, reordered (drag, or Move up and Move down in a row's "More options"), and
/// removed. Changes save immediately and the grid follows. The "+" in the grid's corner opens this page.
/// </summary>
public sealed partial class TimeZonesPage : Page
{
    private readonly ObservableCollection<ZoneRow> _rows = [];
    private IReadOnlyList<TimeZoneChoice> _suggestions = [];
    private List<ListViewItem> _suggestionRows = [];

    // True while the saved values are being shown (the switches' Toggled events are ignored meanwhile)
    private bool _loading;
    private SettingsContext _context = null!;
    private CalendarViewModel _vm = null!;

    /// <summary>Creates the page.</summary>
    public TimeZonesPage()
    {
        InitializeComponent();
        ScrollIndicator.ShowOnHover(PageScroll);
        FirstSuggestion.Highlight(Search);
        ZoneList.ItemsSource = _rows;
    }

    /// <summary>x:Bind helper: a zone's label box accessible name.</summary>
    public static string LabelName(string city) => $"Column label for {city}";

    /// <summary>x:Bind helper: a zone's remove button accessible name.</summary>
    public static string RemoveName(string city) => $"Remove {city}";

    /// <summary>x:Bind helper: a zone's "More options" button accessible name.</summary>
    public static string MoreName(string city) => $"More options for {city}";

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _context = (SettingsContext)e.Parameter;
        _vm = _context.Calendar;
        _rows.Clear();

        var now = _vm.Now;
        foreach (var zone in _vm.Settings.TimeZones)
        {
            var match = TimeZoneCatalog.Search(TimeZoneCatalog.CityFor(zone.Id), now).FirstOrDefault(c => c.Id == zone.Id);
            _rows.Add(new ZoneRow(zone.Id, TimeZoneCatalog.CityFor(zone.Id), match?.Detail ?? zone.Id) { Label = zone.Label ?? "" });
        }

        UpdateState();
        ShowPrimary();
    }

    // =========================================================================
    // PRIMARY TIME ZONE
    // =========================================================================

    // The saved choice: following Windows (the box, showing Windows' zone, and the prompt are off), or the pinned zone
    private void ShowPrimary()
    {
        var s = _vm.Settings;
        _loading = true;

        FollowWindowsZoneSwitch.IsOn = s.PrimaryTimeZone is null;
        PrimaryZoneBox.Show(s.PrimaryTimeZone ?? TimeZoneCatalog.IanaId(_vm.UserZone), _vm.Now);
        ZonePromptSwitch.IsOn = s.PromptOnZoneChange;
        UpdatePrimaryState();
        UpdatePrimarySummary();

        _loading = false;
    }

    // The expander's summary: the saved zone's city, or Windows' with a note that Leaf follows it
    private void UpdatePrimarySummary() => PrimaryZoneSummary.Text = _vm.Settings.PrimaryTimeZone is { } id
        ? TimeZoneCatalog.CityFor(id)
        : $"Same as Windows ({TimeZoneCatalog.CityFor(_vm.UserZone.Id)})";

    // Following Windows: the box shows Windows' zone, off, and the prompt row is hidden (its saved choice is kept)
    private void UpdatePrimaryState()
    {
        PrimaryZoneBox.IsEnabled = !FollowWindowsZoneSwitch.IsOn;
        ZonePromptRow.Visibility = FollowWindowsZoneSwitch.IsOn ? Visibility.Collapsed : Visibility.Visible;
    }

    // On: follow Windows again. Off: the zone the box shows is kept (picking the same zone again raises no change)
    private void OnFollowWindowsToggled(object sender, RoutedEventArgs e)
    {
        UpdatePrimaryState();
        if (!_loading && FollowWindowsZoneSwitch.IsOn && _vm.Settings.PrimaryTimeZone is not null)
        {
            PrimaryZoneBox.Show(TimeZoneCatalog.IanaId(_vm.UserZone), _vm.Now);
            _context.Save(s => s with { PrimaryTimeZone = null });
            UpdatePrimarySummary();
        }
        else if (!_loading && !FollowWindowsZoneSwitch.IsOn && PrimaryZoneBox.ZoneId is { } id)
        {
            _context.Save(s => s with { PrimaryTimeZone = id });
            UpdatePrimarySummary();
        }
    }

    // A pick pins the zone
    private void OnPrimaryZoneChanged(object? sender, string id)
    {
        _context.Save(s => s with { PrimaryTimeZone = id });
        UpdatePrimarySummary();
    }

    private void OnZonePromptToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            var on = ZonePromptSwitch.IsOn;
            _context.Save(s => s with { PromptOnZoneChange = on });
        }
    }

    // =========================================================================
    // EXTRA TIME ZONES
    // =========================================================================

    // Suggestions go to the box as rows of plain strings (ZoneSuggestions), the zone you're in shown disabled
    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            _suggestions = TimeZoneCatalog.Search(sender.Text, _vm.Now);
            _suggestionRows = ZoneSuggestions.Rows(_suggestions, _vm.Zone);
            sender.ItemsSource = _suggestionRows;
        }
    }

    // Enter or a click adds the highlighted (or clicked) zone, else the best match; highlighting alone adds nothing
    private void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var found = TimeZoneCatalog.Search(args.QueryText, _vm.Now);
        var choice = ZoneSuggestions.Chosen(_suggestionRows, _suggestions, args.ChosenSuggestion) ?? ZoneSuggestions.FirstPickable(found, _vm.Zone);
        if (choice is not null)
        {
            Add(choice);
        }
    }

    private void Add(TimeZoneChoice choice)
    {
        if (_rows.Count >= LeafSettings.MaxTimeZones || _rows.Any(r => r.Id == choice.Id))
        {
            return;
        }

        _rows.Add(new ZoneRow(choice.Id, choice.City, choice.Detail));
        Search.Text = "";
        Save();
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ZoneRow row })
        {
            _rows.Remove(row);
            Save();
        }
    }

    // Move Up And Move Down (the keyboard's way to reorder), off at the ends of the list; saved like a drag
    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ZoneRow row } button)
        {
            return;
        }

        var index = _rows.IndexOf(row);
        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("Move up", "ZoneMenu_MoveUp", index > 0, () => Move(row, -1)));
        menu.Items.Add(MenuItem("Move down", "ZoneMenu_MoveDown", index >= 0 && index < _rows.Count - 1, () => Move(row, 1)));
        menu.ShowAt(button);
    }

    private static MenuFlyoutItem MenuItem(string text, string automationId, bool enabled, Action click)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
        AutomationProperties.SetAutomationId(item, automationId);
        item.Click += (_, _) => click();
        return item;
    }

    private void Move(ZoneRow row, int by)
    {
        var index = _rows.IndexOf(row);
        if (index < 0 || index + by < 0 || index + by >= _rows.Count)
        {
            return;
        }

        _rows.Move(index, index + by);
        Save();
    }

    private void OnLabelLostFocus(object sender, RoutedEventArgs e)
    {
        // Read The Box Directly (the two-way binding may not have committed yet)
        if (sender is TextBox { DataContext: ZoneRow row } box)
        {
            row.Label = box.Text;
        }

        Save();
    }

    private void OnReordered(ListViewBase sender, DragItemsCompletedEventArgs args) => Save();

    private void Save()
    {
        UpdateState();

        // Skip When Nothing Changed (avoids a relayout per blur)
        List<ExtraTimeZone> zones = [.. _rows.Select(r => new ExtraTimeZone(r.Id, string.IsNullOrWhiteSpace(r.Label) ? null : r.Label.Trim()))];
        if (!zones.SequenceEqual(_vm.Settings.TimeZones))
        {
            _context.Save(s => s with { TimeZones = zones });
        }
    }

    // The search box is off at the limit; an empty list says so
    private void UpdateState()
    {
        var full = _rows.Count >= LeafSettings.MaxTimeZones;
        Search.IsEnabled = !full;
        LimitText.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
