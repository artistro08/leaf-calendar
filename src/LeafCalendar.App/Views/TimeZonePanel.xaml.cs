using System.Collections.ObjectModel;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Views;

/// <summary>Adds, renames, reorders, and removes extra time-zone columns (up to four). Changes save immediately.</summary>
public sealed partial class TimeZonePanel : UserControl
{
    readonly ObservableCollection<ZoneRow> _rows = [];
    IReadOnlyList<TimeZoneChoice> _suggestions = [];
    CalendarViewModel? _vm;

    /// <summary>Creates the panel.</summary>
    public TimeZonePanel()
    {
        InitializeComponent();
        ZoneList.ItemsSource = _rows;
    }

    /// <summary>Loads the zones from <paramref name="vm"/>.</summary>
    public void Attach(CalendarViewModel vm)
    {
        _vm = vm;
        _rows.Clear();

        var now = vm.Now;
        foreach (var zone in vm.Settings.TimeZones)
        {
            var match = TimeZoneCatalog.Search(TimeZoneCatalog.CityFor(zone.Id), now).FirstOrDefault(c => c.Id == zone.Id);
            _rows.Add(new ZoneRow(zone.Id, TimeZoneCatalog.CityFor(zone.Id), match?.Detail ?? zone.Id) { Label = zone.Label ?? "" });
        }

        UpdateLimit();
    }

    // Suggestions go to the box as plain strings: a list of Core records can't be marshaled to WinRT under Native AOT
    void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_vm is not null && args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
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
        if (_vm is null)
        {
            return;
        }

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
        UpdateLimit();
        if (_vm is null)
        {
            return;
        }

        // Skip When Nothing Changed (avoids a relayout per blur)
        List<ExtraTimeZone> zones = [.. _rows.Select(r => new ExtraTimeZone(r.Id, string.IsNullOrWhiteSpace(r.Label) ? null : r.Label.Trim()))];
        if (!zones.SequenceEqual(_vm.Settings.TimeZones))
        {
            _vm.Update(s => s with { TimeZones = zones });
        }
    }

    void UpdateLimit()
    {
        var full = _rows.Count >= LeafSettings.MaxTimeZones;
        Search.IsEnabled     = !full;
        LimitText.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
    }
}
