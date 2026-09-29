using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Events;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LeafCalendar.App.Views;

/// <summary>
/// Right panel. With nothing selected it lists upcoming events (next 8 hours); with an event
/// selected it shows that event's details as plain text. Links aren't clickable until Milestone 3
/// adds the link allowlist and Join.
/// </summary>
public sealed partial class DetailsPanel : UserControl
{
    CalendarViewModel? _vm;

    /// <summary>Creates the panel.</summary>
    public DetailsPanel() => InitializeComponent();

    /// <summary>x:Bind helper: a brush for a hex color.</summary>
    public static SolidColorBrush Brush(string hex) => LeafBrushes.FromHex(hex);

    /// <summary>Connects to the view model.</summary>
    public void Attach(CalendarViewModel vm)
    {
        _vm = vm;
        UpcomingList.ItemsSource = vm.Upcoming;
        vm.Upcoming.CollectionChanged += OnUpcomingChanged;
        vm.PropertyChanged            += OnViewModelPropertyChanged;
        UpdateUpcomingEmpty();
        Show(vm.SelectedInfo);
    }

    /// <summary>Disconnects from the view model.</summary>
    public void Detach()
    {
        if (_vm is null)
        {
            return;
        }

        _vm.Upcoming.CollectionChanged -= OnUpcomingChanged;
        _vm.PropertyChanged            -= OnViewModelPropertyChanged;
        _vm = null;
    }

    void OnUpcomingChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateUpcomingEmpty();

    void UpdateUpcomingEmpty() =>
        UpcomingEmpty.Visibility = _vm?.Upcoming.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

    void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CalendarViewModel.SelectedInfo))
        {
            Show(_vm?.SelectedInfo);
        }
    }

    void Show(SelectedEventInfo? info)
    {
        UpcomingView.Visibility = info is null ? Visibility.Visible : Visibility.Collapsed;
        DetailsView.Visibility  = info is null ? Visibility.Collapsed : Visibility.Visible;
        if (info is null)
        {
            return;
        }

        var d = info.Details;
        TitleText.Text       = d.Title;
        WhenText.Text        = info.When;
        CalendarText.Text    = info.CalendarName;
        CalendarDot.Fill     = LeafBrushes.FromHex(info.CalendarColor);
        LocationText.Text    = d.Location is { Length: > 0 } location ? $"Location: {location}" : "";
        ConferenceText.Text  = d.ConferenceUri is { } uri ? $"Video call: {uri.AbsoluteUri}" : "";
        ResponseText.Text    = d.SelfResponse switch
        {
            ResponseStatus.Declined    => "Your response: Not going",
            ResponseStatus.Tentative   => "Your response: Maybe",
            ResponseStatus.NeedsAction => "Your response: Not answered yet",
            _                          => d.GuestCount > 0 ? "Your response: Going" : "",
        };
        GuestsText.Text      = d.GuestCount > 0 ? string.Create(CultureInfo.InvariantCulture, $"{d.GuestCount} guests") : "";
        DescriptionText.Text = d.Description;

        foreach (var block in new[] { LocationText, ConferenceText, ResponseText, GuestsText, DescriptionText })
        {
            block.Visibility = block.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    void OnUpcomingClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null && sender is Button { Tag: CalendarOccurrence occurrence })
        {
            _vm.Select(occurrence);
        }
    }

    void OnCloseClick(object sender, RoutedEventArgs e) => _vm?.ClearSelection();
}
