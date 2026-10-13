using LeafCalendar.Core.Api;

namespace LeafCalendar.Tests;

public sealed class ApiIdsTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Event_RoundTrips()
    {
        var key = ApiIds.ReadEvent(ApiIds.Event("109876", "a;b=c%@group.calendar.google.com", "evt_20261014T130000Z", Start));

        Assert.Equal(new EventKey("109876", "a;b=c%@group.calendar.google.com", "evt_20261014T130000Z", Start), key);
    }

    [Fact]
    public void Calendar_RoundTrips_AndIsNotAnEvent()
    {
        var id = ApiIds.Calendar("109876", "leaf.tester@gmail.com");

        Assert.Equal(("109876", "leaf.tester@gmail.com"), ApiIds.ReadCalendar(id));
        Assert.Null(ApiIds.ReadEvent(id));
        Assert.Null(ApiIds.ReadLink(id));
    }

    [Fact]
    public void Link_RoundTrips()
    {
        Assert.Equal(("calcom", "101"), ApiIds.ReadLink(ApiIds.Link("calcom", "101")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 !")]
    [InlineData("ZQ")]
    public void ReadEvent_Malformed_IsNull(string? id)
    {
        Assert.Null(ApiIds.ReadEvent(id));
    }

    [Fact]
    public void ReadEvent_TooLong_IsNull()
    {
        Assert.Null(ApiIds.ReadEvent(new string('A', 5000)));
    }

    [Fact]
    public void Ids_AreUrlSafe()
    {
        Assert.Matches("^[A-Za-z0-9_-]+$", ApiIds.Event("a", "b/c+d", "e", Start));
    }
}
