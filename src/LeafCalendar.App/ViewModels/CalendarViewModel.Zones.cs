// Track A owns this file (Milestone 5 Task 5): the zone on screen (the PC's, Leaf's primary zone, time travel).
using LeafCalendar.Core.Views;

namespace LeafCalendar.App.ViewModels;

public sealed partial class CalendarViewModel
{
    readonly LocalZoneWatcher _zones = new();

    // The zone the views were last sorted and drawn in (set by the constructor)
    TimeZoneInfo? _applied;

    /// <summary>The zone the grid is drawn in: the PC's, followed while Leaf runs (see <see cref="CheckTimeZone"/>).</summary>
    public TimeZoneInfo Zone => _zones.Zone;

    /// <summary>
    /// Follows the PC's time zone. When it changed since the last check, the events are sorted into the new
    /// local days and the views redraw (hour labels, now line, today, the selected event's time, and the
    /// upcoming list). Runs every minute and whenever the window is activated.
    /// </summary>
    public void CheckTimeZone()
    {
        var before = Zone;
        if (!_zones.Check())
        {
            return;
        }

        ApplyZoneChange(before);
    }

    /// <summary>Re-sorts and redraws when the zone on screen changed since the views were last drawn (called after every settings change).</summary>
    public void SyncZone()
    {
        var zone = Zone;
        if (_applied?.Id == zone.Id)
        {
            return;
        }

        ApplyZoneChange(_applied ?? zone);
    }

    // Sorts the events into the new zone's days and redraws everything that shows a time; never logs the zones (they say where you are)
    void ApplyZoneChange(TimeZoneInfo before)
    {
        _services.Log.Info("calendar.timezone.changed");
        Cache.Zone                   = Zone;
        _services.Editor.LocalZoneId = TimeZoneCatalog.IanaId(Zone);
        Today                        = _services.Options.StartDate ?? LocalDate(Now);
        if (SelectedInfo is { } selected)
        {
            SelectedInfo = selected with { When = WhenText(selected.Occurrence) };
        }

        _applied = Zone;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        Run(RefreshAsync);
    }
}
