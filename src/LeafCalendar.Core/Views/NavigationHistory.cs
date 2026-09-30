using LeafCalendar.Core.Settings;

namespace LeafCalendar.Core.Views;

/// <summary>A place the calendar has shown: the view, its day count, and the first day of the period.</summary>
public sealed record ViewPlace(CalendarViewMode Mode, int CustomDayCount, DateOnly PeriodStart);

/// <summary>Browser-like back and forward history of the places the calendar has shown.</summary>
public sealed class NavigationHistory
{
    /// <summary>The most places remembered; older ones are forgotten.</summary>
    public const int Capacity = 50;

    readonly List<ViewPlace> _places = [];
    int _index = -1;

    /// <summary>True when there is an earlier place.</summary>
    public bool CanGoBack => _index > 0;

    /// <summary>True when there is a later place.</summary>
    public bool CanGoForward => _index < _places.Count - 1;

    /// <summary>Records a place. The same place as the current one is ignored, anything forward of the current place is dropped, and the oldest place is forgotten past <see cref="Capacity"/>.</summary>
    public void Visit(ViewPlace place)
    {
        if (_index >= 0 && _places[_index] == place)
        {
            return;
        }

        _places.RemoveRange(_index + 1, _places.Count - _index - 1);
        _places.Add(place);
        if (_places.Count > Capacity)
        {
            _places.RemoveAt(0);
        }

        _index = _places.Count - 1;
    }

    /// <summary>Steps back and returns that place, or null at the oldest place.</summary>
    public ViewPlace? Back() => CanGoBack ? _places[--_index] : null;

    /// <summary>Steps forward and returns that place, or null at the newest place.</summary>
    public ViewPlace? Forward() => CanGoForward ? _places[++_index] : null;
}
