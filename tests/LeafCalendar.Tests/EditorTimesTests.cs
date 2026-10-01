using LeafCalendar.Core.Editing;

namespace LeafCalendar.Tests;

public sealed class EditorTimesTests
{
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    static readonly TimeZoneInfo Tokyo   = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    [Fact]
    public void ToInstant_Ordinary() =>
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(9)), EditorTimes.ToInstant(new(2026, 10, 1), TimeSpan.FromHours(9), Tokyo));

    [Fact]
    public void ToInstant_SpringForwardGap_MovesForward() =>
        // 2:30 AM doesn't exist on Mar 8, 2026 in New York; it becomes 3:30 AM EDT
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 30, 0, TimeSpan.Zero), EditorTimes.ToInstant(new(2026, 3, 8), new TimeSpan(2, 30, 0), NewYork).ToUniversalTime());

    [Fact]
    public void ToInstant_FallBackRepeat_TakesTheFirst() =>
        // 1:30 AM happens twice on Nov 1, 2026; the first (EDT, 05:30Z) wins
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), EditorTimes.ToInstant(new(2026, 11, 1), new TimeSpan(1, 30, 0), NewYork).ToUniversalTime());

    [Fact]
    public void FromInstant_RoundTrips()
    {
        var at = new DateTimeOffset(2026, 10, 1, 13, 0, 0, TimeSpan.Zero);

        Assert.Equal((new DateOnly(2026, 10, 1), TimeSpan.FromHours(22)), EditorTimes.FromInstant(at, Tokyo));
        Assert.Equal(at, EditorTimes.ToInstant(new(2026, 10, 1), TimeSpan.FromHours(22), Tokyo));
    }
}
