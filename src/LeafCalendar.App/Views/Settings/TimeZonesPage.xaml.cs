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
/// Settings › Time zones: adds, renames, reorders (drag), and removes the extra time-zone columns (up to four). Changes
/// save immediately and the grid follows. The "+" in the grid's corner opens this page.
/// </summary>
public sealed partial class TimeZonesPage : Page
{
    readonly ObservableCollection<ZoneRow> _rows = [];
    IReadOnlyList<TimeZoneChoice> _suggestions = [];
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
    }

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
