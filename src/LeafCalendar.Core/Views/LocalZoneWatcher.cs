namespace LeafCalendar.Core.Views;

/// <summary>
/// Notices when the PC's time zone changes while Leaf is running.
/// </summary>
/// <remarks>
/// <see cref="TimeZoneInfo.Local"/> is read once and cached for the life of the process, so a change in
/// Windows Settings never reaches it on its own. <see cref="Check"/> clears that cache and reads the zone
/// again; it's cheap enough to run every minute and whenever the window comes back to the front. A change
/// of zone, or of the same zone's rules (turning daylight saving off), counts.
/// </remarks>
/// <param name="read">Reads the current zone (the PC's by default).</param>
/// <param name="clearCache">Forgets the cached zone before each read (<see cref="TimeZoneInfo.ClearCachedData"/> by default).</param>
public sealed class LocalZoneWatcher(Func<TimeZoneInfo> read, Action clearCache)
{
    /// <summary>Watches the PC's time zone.</summary>
    public LocalZoneWatcher()
        : this(() => TimeZoneInfo.Local, TimeZoneInfo.ClearCachedData)
    {
    }

    /// <summary>The zone as of the last check.</summary>
    public TimeZoneInfo Zone { get; private set; } = read();

    /// <summary>Reads the zone again. Returns true, with <see cref="Zone"/> updated, when it changed.</summary>
    public bool Check()
    {
        clearCache();
        var now = read();
        if (now.Id == Zone.Id && now.HasSameRules(Zone))
        {
            return false;
        }

        Zone = now;
        return true;
    }
}
