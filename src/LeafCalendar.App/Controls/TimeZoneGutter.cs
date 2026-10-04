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

    // The labels the last render drew, zone by zone, hours 1 to 23
    string[] _drawn = [];

    /// <summary>Creates the gutter owned by <paramref name="owner"/>.</summary>
    public TimeZoneGutter(TimeGridView owner) => _owner = owner;

    /// <summary>
    /// Redraws for <paramref name="day"/>. With <paramref name="onlyIfChanged"/> (scrolling to a new first day), it
    /// keeps what's drawn when the labels come out the same, as they do on every day but a DST change's.
    /// </summary>
    public void Render(DateOnly day, bool onlyIfChanged = false)
    {
        var vm    = _owner.ViewModel;
        var dark  = _owner.IsDark;
        var hour  = _owner.HourHeight;
        var zones = vm.Settings.TimeZones.Select(z => TimeZoneInfo.FindSystemTimeZoneById(z.Id)).ToList();
        zones.Add(vm.Zone);
        var local = zones.Count - 1;

        // Each Row Is A Wall-Clock Hour Of The Day, As The Grid Draws It (so a DST change doesn't shift the labels)
        var labels = new string[zones.Count * 23];
        for (var z = 0; z < zones.Count; z++)
        {
            for (var h = 1; h < 24; h++)
            {
                labels[z * 23 + h - 1] = z == local
                    ? TimeLabels.HourLabel(h, vm.Settings.Use24HourTime)
                    : TimeLabels.TimeOfDay(DragMath.Instant(day, h * 60, vm.Zone), zones[z], vm.Settings.Use24HourTime);
            }
        }

        if (onlyIfChanged && labels.AsSpan().SequenceEqual(_drawn))
        {
            return;
        }

        _drawn = labels;
        Children.Clear();
        Width  = zones.Count * TimeGridView.ZoneColumnWidth;
        Height = _owner.BodyHeight;

        for (var z = 0; z < zones.Count; z++)
        {
            for (var h = 1; h < 24; h++)
            {
                var text = new TextBlock
                {
                    Text          = labels[z * 23 + h - 1],
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
