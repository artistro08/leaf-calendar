using LeafCalendar.Core.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public class ShiftedTimeProviderTests
{
    [Fact]
    public void GetUtcNow_StartsAtTheGivenInstantAndRunsOn()
    {
        var real = new FakeTimeProvider(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
        var start = new DateTimeOffset(2026, 10, 1, 13, 55, 0, TimeSpan.FromHours(-4));
        var shifted = new ShiftedTimeProvider(real, start);

        Assert.Equal(start, shifted.GetUtcNow());

        real.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(start.AddMinutes(5), shifted.GetUtcNow());
    }

    [Fact]
    public void CreateTimer_UsesTheRealClocksTimers()
    {
        var real = new FakeTimeProvider();
        var shifted = new ShiftedTimeProvider(real, DateTimeOffset.UnixEpoch);
        var ticks = 0;
        using var timer = shifted.CreateTimer(_ => ticks++, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);

        real.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(1, ticks);
    }
}
