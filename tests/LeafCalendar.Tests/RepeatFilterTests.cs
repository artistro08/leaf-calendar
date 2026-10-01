using LeafCalendar.Core.Alerts;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class RepeatFilterTests
{
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void IsRepeat_SameArgumentsWithinFiveSeconds_IsARepeat()
    {
        var filter = new RepeatFilter(_time);

        Assert.False(filter.IsRepeat("a=1"));
        _time.Advance(TimeSpan.FromSeconds(4));
        Assert.True(filter.IsRepeat("a=1"));
    }

    [Fact]
    public void IsRepeat_FiveSecondsLater_IsNew()
    {
        var filter = new RepeatFilter(_time);
        filter.IsRepeat("a=1");

        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.False(filter.IsRepeat("a=1"));
    }

    [Fact]
    public void IsRepeat_OtherArgumentsBetween_StillARepeat()
    {
        var filter = new RepeatFilter(_time);
        filter.IsRepeat("a=1");

        Assert.False(filter.IsRepeat("a=2"));
        Assert.True(filter.IsRepeat("a=1"));
    }
}
