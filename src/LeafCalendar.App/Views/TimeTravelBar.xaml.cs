using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Views;

/// <summary>
/// The bars above the calendar about its zone: "Viewing your calendar in Tokyo time" while time traveling (Z), and the
/// offer to follow Windows to a new zone while Leaf's primary zone is pinned.
/// </summary>
public sealed partial class TimeTravelBar : UserControl
{
    readonly CalendarViewModel _vm;

    /// <summary>Creates the bars on the page's view model.</summary>
    public TimeTravelBar(CalendarViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        Update(vm);
    }

    /// <summary>Opens, closes, and words the bars from the view model's travel zone, zone on screen, and switch offer.</summary>
    public void Update(CalendarViewModel vm)
    {
        var now = vm.Now;

        // Time Travel
        TravelBar.Message = vm.TravelZoneId is null ? "" : $"Viewing your calendar in {DisplayZone.Describe(vm.Zone, now)}.";
        TravelBar.IsOpen  = vm.TravelZoneId is not null;

        // Zone Switch Offer ("Keep London" names the pinned zone)
        if (vm.ZoneSwitchOffer is { } offer)
        {
            ZoneSwitchBar.Message = $"Your PC is now on {DisplayZone.Describe(offer, now)}. Show Leaf in that time zone?";
            KeepButton.Content    = "Keep " + TimeZoneCatalog.CityFor(TimeZoneCatalog.IanaId(DisplayZone.Resolve(null, vm.Settings.PrimaryTimeZone, vm.Zone)));
        }

        ZoneSwitchBar.IsOpen = vm.ZoneSwitchOffer is not null;
    }

    void OnReturnClick(object sender, RoutedEventArgs e) => _vm.TravelTo(null);

    void OnSwitchClick(object sender, RoutedEventArgs e) => _vm.AcceptZoneSwitch();

    void OnKeepClick(object sender, RoutedEventArgs e) => _vm.DeclineZoneSwitch();
}
