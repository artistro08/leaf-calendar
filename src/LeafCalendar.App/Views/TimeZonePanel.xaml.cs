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

    void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_vm is not null && args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            sender.ItemsSource = TimeZoneCatalog.Search(sender.Text, _vm.Now);
        }
    }

    void OnSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is TimeZoneChoice choice)
        {
            Add(choice);
        }
    }

    void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (_vm is null)
        {
            return;
        }

        var found  = TimeZoneCatalog.Search(args.QueryText, _vm.Now);
        var choice = args.ChosenSuggestion as TimeZoneChoice ?? (found.Count > 0 ? found[0] : null);
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

    void OnLabelLostFocus(object sender, RoutedEventArgs e) => Save();

    void OnReordered(ListViewBase sender, DragItemsCompletedEventArgs args) => Save();

    void Save()
    {
        _vm?.Update(s => s with { TimeZones = [.. _rows.Select(r => new ExtraTimeZone(r.Id, r.Label))] });
        UpdateLimit();
    }

    void UpdateLimit()
    {
        var full = _rows.Count >= LeafSettings.MaxTimeZones;
        Search.IsEnabled     = !full;
        LimitText.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
    }
}
