namespace LeafCalendar.Core.Hosting;

/// <summary>
/// A clock that starts at a chosen instant and then runs at real speed (UI tests' <c>--now</c>), so reminders, the
/// flyout, and the join shortcut can be tested against the fixtures' meetings. Timers are the real clock's.
/// </summary>
public sealed class ShiftedTimeProvider : TimeProvider
{
    private readonly TimeProvider _inner;
    private readonly DateTimeOffset _start;
    private readonly long _origin;

    /// <summary>Starts at <paramref name="start"/> now.</summary>
    public ShiftedTimeProvider(TimeProvider inner, DateTimeOffset start)
    {
        _inner = inner;
        _start = start;
        _origin = inner.GetTimestamp();
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => (_start + _inner.GetElapsedTime(_origin)).ToUniversalTime();

    /// <inheritdoc />
    public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;

    /// <inheritdoc />
    public override long TimestampFrequency => _inner.TimestampFrequency;

    /// <inheritdoc />
    public override long GetTimestamp() => _inner.GetTimestamp();

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        _inner.CreateTimer(callback, state, dueTime, period);
}
