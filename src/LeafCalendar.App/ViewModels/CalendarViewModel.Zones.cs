// Track A owns this file (Milestone 5 Task 5): the zone on screen (the PC's, Leaf's primary zone, time travel).
using CommunityToolkit.Mvvm.ComponentModel;
using LeafCalendar.Core.Views;

namespace LeafCalendar.App.ViewModels;

public sealed partial class CalendarViewModel
{
    private readonly LocalZoneWatcher _zones = new();

    // The zone the views were last sorted and drawn in (set by the constructor)
    private TimeZoneInfo? _applied;

    // Time travel's zone, for this session only (never saved)
    private string? _travelZoneId;

    /// <summary>
    /// The zone the grid is drawn in: time travel's, else Leaf's primary time zone, else the PC's (followed while Leaf
    /// runs, see <see cref="CheckTimeZone"/>).
    /// </summary>
    public TimeZoneInfo Zone => DisplayZone.Resolve(_travelZoneId, Settings?.PrimaryTimeZone, _zones.Zone);

    /// <summary>Your own zone (primary, else the PC's): where new events and zoneless drags belong, even while time travel is on.</summary>
    public TimeZoneInfo UserZone => DisplayZone.Resolve(null, Settings?.PrimaryTimeZone, _zones.Zone);

    /// <summary>The zone time travel shows (Z), or null when not traveling.</summary>
    public string? TravelZoneId => _travelZoneId;

    /// <summary>Windows' new zone, offered while a pinned primary zone stays on screen (null when there's no offer).</summary>
    [ObservableProperty]
    public partial TimeZoneInfo? ZoneSwitchOffer { get; set; }

    /// <summary>Shows the calendar in <paramref name="zoneId"/> for this session (null returns). Never logs the zone, which can say where you are.</summary>
    public void TravelTo(string? zoneId)
    {
        _travelZoneId = zoneId;
        OnPropertyChanged(nameof(TravelZoneId));
        _services.Log.Info("calendar.timetravel", zoneId is null ? "off" : "on");
        SyncZone();
    }

    /// <summary>Takes Windows' new zone as Leaf's primary time zone.</summary>
    public void AcceptZoneSwitch()
    {
        if (ZoneSwitchOffer is not { } offer)
        {
            return;
        }

        var id = TimeZoneCatalog.IanaId(offer);
        ZoneSwitchOffer = null;
        Update(s => s with { PrimaryTimeZone = id });
    }

    /// <summary>Keeps the pinned primary zone.</summary>
    public void DeclineZoneSwitch() => ZoneSwitchOffer = null;

    /// <summary>
    /// Follows the PC's time zone. When it changed since the last check and it's the zone on screen, the events are
    /// sorted into the new local days and the views redraw (hour labels, now line, today, the selected event's time,
    /// and the upcoming list). With a pinned primary zone the calendar stays put and Leaf offers to switch (when the
    /// prompt setting is on). Runs every minute and whenever the window is activated.
    /// </summary>
    public void CheckTimeZone()
    {
        var before = Zone;
        if (!_zones.Check())
        {
            return;
        }

        // A Pinned Primary Zone Stays; Offer To Switch To Windows' New One (or drop the offer once Windows is back)
        ZoneSwitchOffer = DisplayZone.ShouldOfferSwitch(Settings.PrimaryTimeZone, Settings.PromptOnZoneChange, _zones.Zone) ? _zones.Zone : null;

        if (Zone.Id != before.Id)
        {
            ApplyZoneChange();
        }
    }

    /// <summary>Re-sorts and redraws when the zone on screen changed since the views were last drawn (time travel, or the primary zone setting).</summary>
    public void SyncZone()
    {
        // An Offer Left Over From Before A Settings Change (following Windows again, or the prompt turned off) Goes
        if (ZoneSwitchOffer is not null && !DisplayZone.ShouldOfferSwitch(Settings.PrimaryTimeZone, Settings.PromptOnZoneChange, _zones.Zone))
        {
            ZoneSwitchOffer = null;
        }

        var zone = Zone;
        if (_applied?.Id == zone.Id)
        {
            return;
        }

        ApplyZoneChange();
    }

    // Sorts the events into the new zone's days and redraws everything that shows a time; never logs the zones (they say where you are)
    private void ApplyZoneChange()
    {
        _services.Log.Info("calendar.timezone.changed");
        Cache.Zone = Zone;
        _services.Editor.LocalZoneId = TimeZoneCatalog.IanaId(UserZone);
        Today = _services.Options.StartDate ?? LocalDate(Now);
        if (SelectedInfo is { } selected)
        {
            SelectedInfo = selected with { When = WhenText(selected.Occurrence) };
        }

        _applied = Zone;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        Run(RefreshAsync);
    }
}
