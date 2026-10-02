using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;

namespace LeafCalendar.Tests;

public sealed class AvailabilityTextTests
{
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    static readonly TimeZoneInfo Tokyo   = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    // Wall-clock times in New York on a given day
    static BusyRange Et(int month, int day, int h1, int m1, int h2, int m2)
    {
        var start = new DateTime(2026, month, day, h1, m1, 0);
        var end   = new DateTime(2026, month, day, h2, m2, 0);
        return new(new DateTimeOffset(start, NewYork.GetUtcOffset(start)), new DateTimeOffset(end, NewYork.GetUtcOffset(end)));
    }

    [Fact]
    public void Format_SpecExampleShape() =>
        Assert.Equal("Wed Sep 30: 10–11 AM, 2–4 PM ET", AvailabilityText.Format([Et(9, 30, 10, 0, 11, 0), Et(9, 30, 14, 0, 16, 0)], NewYork, use24Hour: false));

    [Theory]
    [InlineData(11, 0, 13, 0, "11 AM–1 PM")]
    [InlineData(10, 30, 11, 0, "10:30–11 AM")]
    [InlineData(11, 30, 12, 15, "11:30 AM–12:15 PM")]
    [InlineData(12, 0, 13, 0, "12–1 PM")]
    public void Format_Meridiem(int h1, int m1, int h2, int m2, string expected) =>
        Assert.Equal($"Thu Oct 1: {expected} ET", AvailabilityText.Format([Et(10, 1, h1, m1, h2, m2)], NewYork, use24Hour: false));

    [Fact]
    public void Format_24Hour() =>
        Assert.Equal("Thu Oct 1: 10:00–11:00, 14:00–16:00 ET", AvailabilityText.Format([Et(10, 1, 10, 0, 11, 0), Et(10, 1, 14, 0, 16, 0)], NewYork, use24Hour: true));

    [Fact]
    public void Format_SeveralDays_OneLineEach() =>
        Assert.Equal("Thu Oct 1: 9–10 AM ET\r\nFri Oct 2: 3–4 PM ET", AvailabilityText.Format([Et(10, 2, 15, 0, 16, 0), Et(10, 1, 9, 0, 10, 0)], NewYork, use24Hour: false));

    [Fact]
    public void Format_CrossesMidnightInZone_SplitsDays() =>
        // 10 AM–12 PM in New York is 11 PM–1 AM in Tokyo
        Assert.Equal("Thu Oct 1: 11 PM–12 AM Tokyo time\r\nFri Oct 2: 12–1 AM Tokyo time",
            AvailabilityText.Format([Et(10, 1, 10, 0, 12, 0)], Tokyo, use24Hour: false));

    [Fact]
    public void Format_DstFallBack_UsesTheWallClock()
    {
        // Nov 1, 2026: 12:00 AM EDT (04:00Z) to 3:00 AM EST (08:00Z) is four real hours, shown as the clock reads
        var range = new BusyRange(new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero));

        Assert.Equal("Sun Nov 1: 12–3 AM ET", AvailabilityText.Format([range], NewYork, use24Hour: false));
    }

    // A stretch ending at midnight says so plainly: "12 AM" never shares the start's AM, and 24-hour reads "24:00"
    [Fact]
    public void Format_EndsAtMidnight_CarriesBothHalves() =>
        Assert.Equal("Thu Oct 1: 9 AM–12 AM ET", AvailabilityText.Format([new(Et(10, 1, 9, 0, 9, 0).Start, Et(10, 2, 0, 0, 0, 0).Start)], NewYork, use24Hour: false));

    [Fact]
    public void Format_EndsAtMidnight_24Hour_Is2400() =>
        Assert.Equal("Thu Oct 1: 21:00–24:00 ET", AvailabilityText.Format([new(Et(10, 1, 21, 0, 21, 0).Start, Et(10, 2, 0, 0, 0, 0).Start)], NewYork, use24Hour: true));

    [Fact]
    public void Format_CrossesMidnight_24Hour_SplitsAt2400() =>
        Assert.Equal("Thu Oct 1: 23:00–24:00 Tokyo time\r\nFri Oct 2: 00:00–01:00 Tokyo time",
            AvailabilityText.Format([Et(10, 1, 10, 0, 12, 0)], Tokyo, use24Hour: true));

    [Fact]
    public void Format_Nothing_IsEmpty() => Assert.Equal("", AvailabilityText.Format([], NewYork, false));

    [Theory]
    [InlineData("America/New_York", "ET")]
    [InlineData("Eastern Standard Time", "ET")]
    [InlineData("America/Chicago", "CT")]
    [InlineData("America/Denver", "MT")]
    [InlineData("America/Phoenix", "MT")]
    [InlineData("America/Los_Angeles", "PT")]
    [InlineData("America/Anchorage", "AKT")]
    [InlineData("Pacific/Honolulu", "HT")]
    [InlineData("UTC", "UTC")]
    [InlineData("Asia/Tokyo", "Tokyo time")]
    public void ZoneLabel_Short(string id, string expected) =>
        Assert.Equal(expected, AvailabilityText.ZoneLabel(TimeZoneInfo.FindSystemTimeZoneById(id)));

    const string Times = "Thu Oct 1: 10–11 AM ET\r\nFri Oct 2: 2–4 PM ET";

    [Fact]
    public void Compose_Default_PutsTheTimesUnderTheGreeting() =>
        Assert.Equal("Here are some times that work for me:\r\n" + Times, AvailabilityText.Compose(AvailabilityText.DefaultMessage, Times));

    [Fact]
    public void Compose_PlaceholderInTheMiddle_IsReplacedThere() =>
        Assert.Equal("Hi!\r\n" + Times + "\r\nLet me know.", AvailabilityText.Compose("Hi!\n{times}\nLet me know.", Times));

    [Fact]
    public void Compose_PlaceholderTwice_IsReplacedBothTimes() =>
        Assert.Equal("A\r\nA", AvailabilityText.Compose("{times}\n{times}", "A"));

    [Fact]
    public void Compose_NoPlaceholder_AddsTheTimesAfter() =>
        Assert.Equal("Free:\r\n" + Times, AvailabilityText.Compose("Free:", Times));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \r\n ")]
    public void Compose_Blank_IsJustTheTimes(string? message) =>
        Assert.Equal(Times, AvailabilityText.Compose(message, Times));

    [Fact]
    public void Compose_LineBreaks_AreWindowsBreaks() =>
        Assert.Equal("a\r\nb\r\nc\r\nX", AvailabilityText.Compose("a\rb\r\nc\n{times}", "X"));

    [Fact]
    public void Compose_TrimsTheMessagesEnds() =>
        Assert.Equal("Hi\r\nX", AvailabilityText.Compose("  Hi\n{times}  \n", "X"));
}
