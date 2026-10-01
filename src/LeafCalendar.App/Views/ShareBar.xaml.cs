using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Tray;
using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Views;

/// <summary>
/// The card in the calendar view's bottom-right corner while you share availability (S): the zone the text is written in,
/// which calendars' busy times count, Cancel, and Copy (which copies, stops sharing, and says so in the notice). The picked
/// times are listed in the right panel (<see cref="ShareSlotsPanel"/>). Hidden when not sharing.
/// </summary>
public sealed partial class ShareBar : UserControl
{
    // The zone choices' IANA IDs, in the box's order (the pick is read by index, never back from the box's items)
    readonly List<string> _zoneIds = [];

    // One menu item per shareable calendar, kept so IsChecked is read from our own references
    readonly Dictionary<CalendarRef, ToggleMenuFlyoutItem> _calendarItems = [];

    CalendarViewModel? _vm;
    bool _shown;
    bool _filling;

    /// <summary>Creates the bar (hidden until sharing starts).</summary>
    public ShareBar() => InitializeComponent();

    /// <summary>Shows the bar while the view model is sharing (filling the choices when sharing starts) and enables Copy once a time is picked.</summary>
    public void Update(CalendarViewModel vm)
    {
        _vm = vm;
        if (!vm.IsSharing)
        {
            _shown     = false;
            Visibility = Visibility.Collapsed;
            return;
        }

        if (!_shown)
        {
            _shown = true;
            FillZones(vm);
            FillCalendars(vm);
        }

        CopyButton.IsEnabled = vm.ShareSlots.Count > 0;
        Visibility           = Visibility.Visible;
    }

    // The zone on screen, Windows' zone, and your extra zone columns, once each: "Tokyo (UTC+9)"
    void FillZones(CalendarViewModel vm)
    {
        var zones = new[] { vm.Zone, TimeZoneInfo.Local }
            .Concat(vm.Settings.TimeZones.Where(z => TimeZoneCatalog.IsKnown(z.Id)).Select(z => TimeZoneInfo.FindSystemTimeZoneById(z.Id)))
            .DistinctBy(TimeZoneCatalog.IanaId)
            .ToList();

        _filling = true;
        _zoneIds.Clear();
        _zoneIds.AddRange(zones.Select(TimeZoneCatalog.IanaId));
        ShareZoneBox.ItemsSource   = zones.Select(z => $"{TimeZoneCatalog.CityFor(TimeZoneCatalog.IanaId(z))} ({TimeZoneCatalog.OffsetLabel(z.GetUtcOffset(vm.Now))})").ToList();
        ShareZoneBox.SelectedIndex = Math.Max(0, _zoneIds.IndexOf(vm.ShareZoneId));
        _filling = false;
    }

    // A checked item per shareable calendar (its name cleaned: names are set by people)
    void FillCalendars(CalendarViewModel vm)
    {
        CalendarsMenu.Items.Clear();
        _calendarItems.Clear();
        foreach (var calendar in vm.ShareableCalendars())
        {
            var key  = new CalendarRef(calendar.AccountId, calendar.Id);
            var item = new ToggleMenuFlyoutItem { Text = DisplayText.Clean(calendar.Summary, 100), IsChecked = vm.ShareCalendars.Contains(key) };
            AutomationProperties.SetAutomationId(item, $"ShareCalendar_{calendar.Id}");
            item.Click += (_, _) => vm.SetShareCalendar(key, _calendarItems[key].IsChecked);
            _calendarItems[key] = item;
            CalendarsMenu.Items.Add(item);
        }
    }

    void OnZoneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || _vm is null || ShareZoneBox.SelectedIndex is var index && (index < 0 || index >= _zoneIds.Count))
        {
            return;
        }

        _vm.ShareZoneId = _zoneIds[index];
    }

    /// <summary>While a time is being dragged out, the card fades and lets the pointer through, so it never hides the slot under it.</summary>
    public void SetDragging(bool dragging)
    {
        Opacity          = dragging ? 0.2 : 1;
        IsHitTestVisible = !dragging;
    }

    void OnCancelClick(object sender, RoutedEventArgs e) => _vm?.StopSharing();

    void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_vm is { } vm)
        {
            vm.Fire(vm.CopyAvailabilityAsync, "share.copy.failed");
        }
    }
}
