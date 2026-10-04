namespace LeafCalendar.Core.Alerts;

/// <summary>
/// Spots a notification click that arrives twice: the same arguments again within five seconds.
/// </summary>
/// <remarks>
/// A click that starts Leaf can reach it both as the launch and as <c>NotificationInvoked</c>, and acting twice would
/// send two replies to an invitation. The first sighting counts from when it arrived, so a real second click after five
/// seconds still works. Call from one thread (the UI thread).
/// </remarks>
/// <param name="time">The clock.</param>
public sealed class RepeatFilter(TimeProvider time)
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

    /// <summary>True when the same <paramref name="arguments"/> arrived less than five seconds ago.</summary>
    public bool IsRepeat(string arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        // Forget Old Sightings
        var now = time.GetUtcNow();
        foreach (var old in _seen.Where(s => now - s.Value >= Window).Select(s => s.Key).ToList())
        {
            _seen.Remove(old);
        }

        return !_seen.TryAdd(arguments, now);
    }
}
