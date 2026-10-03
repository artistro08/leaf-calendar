using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class TimeLabelsTests
{
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    static DateTimeOffset At(int hour, int minute) => new(2026, 10, 1, hour, minute, 0, TimeSpan.FromHours(-4));

    [Theory]
    [InlineData(0, false, "12 AM")]
    [InlineData(9, false, "9 AM")]
    [InlineData(12, false, "12 PM")]
    [InlineData(13, false, "1 PM")]
    [InlineData(9, true, "09:00")]
    [InlineData(21, true, "21:00")]
    public void HourLabel_Formats(int hour, bool use24h, string expected) => Assert.Equal(expected, TimeLabels.HourLabel(hour, use24h));

    [Fact]
    public void TimeOfDay_OnTheHourAndNot_Formats()
    {
        Assert.Equal("9 AM", TimeLabels.TimeOfDay(At(9, 0), NewYork, false));
        Assert.Equal("9:30 AM", TimeLabels.TimeOfDay(At(9, 30), NewYork, false));
        Assert.Equal("13:05", TimeLabels.TimeOfDay(At(13, 5), NewYork, true));
    }

    [Fact]
    public void Range_Formats() => Assert.Equal("9 AM – 10:30 AM", TimeLabels.Range(At(9, 0), At(10, 30), NewYork, false));

    [Theory]
    [InlineData(9, 0, 10, 30, false, "9 – 10:30")]
    [InlineData(13, 15, 14, 0, false, "1:15 – 2")]
    [InlineData(0, 0, 0, 30, false, "12 – 12:30")]
    [InlineData(9, 0, 10, 30, true, "09:00 – 10:30")]
    public void GridRange_HasNoAmPm(int fromHour, int fromMinute, int toHour, int toMinute, bool use24h, string expected) =>
        Assert.Equal(expected, TimeLabels.GridRange(At(fromHour, fromMinute), At(toHour, toMinute), NewYork, use24h));

    [Theory]
    [InlineData(9, 0, false, "9a")]
    [InlineData(13, 30, false, "1:30p")]
    [InlineData(0, 15, false, "12:15a")]
    [InlineData(13, 30, true, "13:30")]
    public void Compact_Formats(int hour, int minute, bool use24h, string expected) =>
        Assert.Equal(expected, TimeLabels.Compact(At(hour, minute), NewYork, use24h));

    [Fact]
    public void Relative_BeforeDuringAfter()
    {
        Assert.Equal("in 12 min", TimeLabels.Relative(At(9, 0), At(10, 0), At(8, 48)));
        Assert.Equal("in 2 h", TimeLabels.Relative(At(11, 0), At(12, 0), At(8, 50)));
        Assert.Equal("Now", TimeLabels.Relative(At(9, 0), At(10, 0), At(9, 30)));
        Assert.Equal("Ended", TimeLabels.Relative(At(9, 0), At(10, 0), At(10, 30)));
    }
}
