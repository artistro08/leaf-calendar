using System.Collections.ObjectModel;
using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LeafCalendar.App.Views.Settings;

/// <summary>
/// Settings › Time zones: Leaf's primary time zone (Windows' by default, or a zone Leaf keeps, with an offer to switch
/// when Windows' zone changes), then the extra time-zone columns (up to four), added, renamed, reordered (drag), and
/// removed. Changes save immediately and the grid follows. The "+" in the grid's corner opens this page.
/// </summary>
public sealed partial class TimeZonesPage : Page
{
    readonly ObservableCollection<ZoneRow> _rows = [];
    IReadOnlyList<TimeZoneChoice> _suggestions = [];
    IReadOnlyList<TimeZoneChoice> _primarySuggestions = [];

    // True while the saved values are being shown (the switches' Toggled events are ignored meanwhile)
    bool _loading;
    SettingsContext _context = null!;
    CalendarViewModel _vm = null!;

    /// <summary>Creates the page.</summary>
    public TimeZonesPage()
    {
        InitializeComponent();
        ScrollIndicator.ShowOnHover(PageScroll);
        ZoneList.ItemsSource = _rows;
    }

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _context = (SettingsContext)e.Parameter;
        _vm      = _context.Calendar;
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

    // The saved choice: following Windows (the box and prompt are off), or the pinned zone's city in the box
    void ShowPrimary()
    {
        var s    = _vm.Settings;
        _loading = true;

        FollowWindowsZoneSwitch.IsOn = s.PrimaryTimeZone is null;
        PrimaryZoneBox.Text          = s.PrimaryTimeZone is { } id ? TimeZoneCatalog.CityFor(id) : "";
        ZonePromptSwitch.IsOn        = s.PromptOnZoneChange;
        UpdatePrimaryState();

        _loading = false;
    }

    void UpdatePrimaryState()
    {
        PrimaryZoneBox.IsEnabled   = !FollowWindowsZoneSwitch.IsOn;
        ZonePromptSwitch.IsEnabled = !FollowWindowsZoneSwitch.IsOn;
    }

    // On: follow Windows again. Off: nothing changes until a zone is picked
    void OnFollowWindowsToggled(object sender, RoutedEventArgs e)
    {
        UpdatePrimaryState();
        if (!_loading && FollowWindowsZoneSwitch.IsOn && _vm.Settings.PrimaryTimeZone is not null)
        {
            PrimaryZoneBox.Text = "";
            _context.Save(s => s with { PrimaryTimeZone = null });
        }
    }

    void OnPrimaryTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            _primarySuggestions = TimeZoneCatalog.Search(sender.Text, _vm.Now);
            sender.ItemsSource  = _primarySuggestions.Select(c => c.ToString()).ToList();
        }
    }

    // A pick pins the zone (matched against our own list, never read back as a Core record)
    void OnPrimaryChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string text && _primarySuggestions.FirstOrDefault(c => c.ToString() == text) is { } choice)
        {
            _context.Save(s => s with { PrimaryTimeZone = choice.Id });
            sender.Text = choice.City;
        }
    }

    void OnZonePromptToggled(object sender, RoutedEventArgs e)
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

    // Suggestions go to the box as plain strings: a list of Core records can't be marshaled to WinRT under Native AOT
    void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            _suggestions       = TimeZoneCatalog.Search(sender.Text, _vm.Now);
            sender.ItemsSource = _suggestions.Select(c => c.ToString()).ToList();
        }
    }

    void OnSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (ChoiceFor(args.SelectedItem) is { } choice)
        {
            Add(choice);
        }
    }

    TimeZoneChoice? ChoiceFor(object? item) =>
        item is string text ? _suggestions.FirstOrDefault(c => c.ToString() == text) : null;

    void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var found  = TimeZoneCatalog.Search(args.QueryText, _vm.Now);
        var choice = ChoiceFor(args.ChosenSuggestion) ?? (found.Count > 0 ? found[0] : null);
        if (choice is not null)
        {
            Add(choice);
        }
    }

    void Add(TimeZoneChoice choice)
    {
        if (_rows.Count >= LeafSettings.MaxTimeZones || _rows.Any(r => r.Id == choice.Id))
        {
            return;
        }

        _rows.Add(new ZoneRow(choice.Id, choice.City, choice.Detail));
        Search.Text = "";
        Save();
    }

    void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ZoneRow row })
        {
            _rows.Remove(row);
            Save();
        }
    }

    void OnLabelLostFocus(object sender, RoutedEventArgs e)
    {
        // Read The Box Directly (the two-way binding may not have committed yet)
        if (sender is TextBox { DataContext: ZoneRow row } box)
        {
            row.Label = box.Text;
        }

        Save();
    }

    void OnReordered(ListViewBase sender, DragItemsCompletedEventArgs args) => Save();

    void Save()
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
    void UpdateState()
    {
        var full = _rows.Count >= LeafSettings.MaxTimeZones;
        Search.IsEnabled     = !full;
        LimitText.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
