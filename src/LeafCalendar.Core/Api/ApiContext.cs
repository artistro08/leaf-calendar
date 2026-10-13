using LeafCalendar.Core.Data;
using LeafCalendar.Core.Editing;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Scheduling;
using LeafCalendar.Core.Settings;

namespace LeafCalendar.Core.Api;

/// <summary>
/// What the local API needs from the running app (local API spec 3.1). The App implements it; Core stays free of
/// windows. Called on pipe threads, so implementations marshal to the UI thread themselves.
/// </summary>
public interface IApiHost
{
    /// <summary>The zone Leaf shows times in (your primary zone, else Windows').</summary>
    TimeZoneInfo Zone { get; }

    /// <summary>The current settings.</summary>
    LeafSettings Settings { get; }

    /// <summary>Opens a link through Leaf's usual launch check; false when it didn't open.</summary>
    Task<bool> OpenLinkAsync(Uri link);

    /// <summary>Brings Leaf forward on a day.</summary>
    void ShowDay(DateOnly day);

    /// <summary>Brings Leaf forward with an event open.</summary>
    void ShowEvent(CalendarOccurrence occurrence);
}

/// <summary>Everything the API's methods use: the database, the editor (every write), the scheduling providers, the app, the clock, and who Leaf is.</summary>
public sealed record ApiContext(LeafDatabase Database, EventEditor Editor, SchedulingHub Scheduling, IApiHost Host, TimeProvider Time, string LeafVersion, string Profile);
