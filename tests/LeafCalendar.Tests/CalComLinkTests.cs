using LeafCalendar.Core.Http;
using LeafCalendar.Core.Scheduling;

namespace LeafCalendar.Tests;

public class CalComLinkTests
{
    private static readonly CalComProvider Provider = new(new HttpClient(), "cal_live_x", CalComProvider.DefaultApiBase);
    private static readonly SchedulingLink WithGuests = new("calcom", "101", "30 min", 30, new Uri("https://cal.com/leaf-tester/30min"), true);
    private static readonly SchedulingLink NoGuests = WithGuests with { AllowsGuests = false };
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    [Fact]
    public void TimeLink_DateInShareZone_SlotInUtc()
    {
        // 03:30 UTC on the 14th is 10:30 PM on the 13th in Chicago
        var uri = Provider.TimeLink(WithGuests, new DateTimeOffset(2026, 10, 14, 3, 30, 0, TimeSpan.Zero), Chicago, []);
        var query = QueryString.Parse(uri.Query);

        Assert.Equal("https://cal.com/leaf-tester/30min", uri.GetLeftPart(UriPartial.Path));
        Assert.Equal("2026-10-13", query["date"]);
        Assert.Equal("2026-10-14T03:30:00.000Z", query["slot"]);
        Assert.False(query.ContainsKey("email"));
    }

    [Fact]
    public void TimeLink_SeveralGuests_FirstIsBookerRestAreGuests()
    {
        var uri = Provider.TimeLink(WithGuests, DateTimeOffset.UnixEpoch, Chicago, [new("Jane Doe", "jane@example.com"), new("", "sam@example.com"), new("", "lee@example.com")]);

        Assert.Contains("name=Jane%20Doe", uri.Query, StringComparison.Ordinal);
        Assert.Contains("email=jane%40example.com", uri.Query, StringComparison.Ordinal);
        Assert.Contains("guests=sam%40example.com&guests=lee%40example.com", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void TimeLink_GuestsNotAllowed_OnlyBooker()
    {
        var uri = Provider.TimeLink(NoGuests, DateTimeOffset.UnixEpoch, Chicago, [new("", "jane@example.com"), new("", "sam@example.com")]);

        Assert.Contains("email=jane%40example.com", uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("guests=", uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("name=", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void TimeLink_EscapesGuestValues()
    {
        var uri = Provider.TimeLink(WithGuests, DateTimeOffset.UnixEpoch, Chicago, [new("A&B=C <x>", "a+b@example.com")]);

        Assert.Contains("name=A%26B%3DC%20%3Cx%3E", uri.Query, StringComparison.Ordinal);
        Assert.Contains("email=a%2Bb%40example.com", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void TimeLink_BookingUrlWithQuery_AppendsWithAmpersand()
    {
        var link = WithGuests with { BookingUrl = new Uri("https://cal.com/team/x?layout=week") };

        var uri = Provider.TimeLink(link, DateTimeOffset.UnixEpoch, Chicago, []);

        Assert.StartsWith("?layout=week&date=", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void BlockLink_DateOnly()
    {
        var uri = Provider.BlockLink(WithGuests, new DateOnly(2026, 10, 13), [new("", "jane@example.com")]);

        Assert.Equal("?date=2026-10-13&email=jane%40example.com", uri.Query);
    }
}
