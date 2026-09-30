using LeafCalendar.Core.Events;
using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Controls;

/// <summary>
/// Hour labels on the left: one column per extra zone (oldest on the left), then the PC's own zone
/// next to the days. An extra zone shows the time there at each local hour of the first visible day, so
/// half-hour zones read "5:30 PM".
/// </summary>
public sealed partial class TimeZoneGutter : Canvas
{
    readonly TimeGridView _owner;

    /// <summary>Creates the gutter owned by <paramref name="owner"/>.</summary>
    public TimeZoneGutter(TimeGridView owner) => _owner = owner;

    /// <summary>Redraws for <paramref name="day"/>.</summary>
    public void Render(DateOnly day)
    {
        var vm    = _owner.ViewModel;
        var dark  = _owner.IsDark;
        var hour  = _owner.HourHeight;
        var zones = vm.Settings.TimeZones.Select(z => TimeZoneInfo.FindSystemTimeZoneById(z.Id)).ToList();
        zones.Add(vm.Zone);
        var local = zones.Count - 1;

        Children.Clear();
        Width  = zones.Count * TimeGridView.ZoneColumnWidth;
        Height = _owner.BodyHeight;

        var midnight = OccurrenceQuery.LocalMidnight(day, vm.Zone);
        for (var z = 0; z < zones.Count; z++)
        {
            for (var h = 1; h < 24; h++)
            {
                var label = z == local
                    ? TimeLabels.HourLabel(h, vm.Settings.Use24HourTime)
                    : TimeLabels.TimeOfDay(midnight.AddHours(h), zones[z], vm.Settings.Use24HourTime);

                var text = new TextBlock
                {
                    Text          = label,
                    FontSize      = 11,
                    Width         = TimeGridView.ZoneColumnWidth - 8,
                    TextAlignment = TextAlignment.Right,
                    Foreground    = z == local ? LeafBrushes.SecondaryText(dark) : LeafBrushes.DimText(dark),
                };
                SetLeft(text, z * TimeGridView.ZoneColumnWidth);
                SetTop(text, h * hour - 8);
                Children.Add(text);
            }
        }
    }
}
