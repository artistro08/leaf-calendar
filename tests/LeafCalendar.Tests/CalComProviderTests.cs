using System.Net;
using LeafCalendar.Core.Scheduling;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class CalComProviderTests : IDisposable
{
    private const string Base = "https://api.cal.com/";
    private const string Key = "cal_live_test123";
    private readonly FakeHttpHandler _cal = new();

    private CalComProvider Create() => new(new HttpClient(_cal), Key, new Uri(Base));

    /// <inheritdoc />
    public void Dispose() => _cal.Dispose();

    [Fact]
    public async Task GetLinksAsync_SendsKeyAndVersion_DropsHiddenUnsafeAndNull()
    {
        _cal.On(HttpMethod.Get, Base + "v2/event-types", HttpStatusCode.OK, Fixture.Read("calcom-event-types.json"));

        var links = await Create().GetLinksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["101", "102"], links.Select(l => l.Id));
        Assert.Equal("30 min meeting", links[0].Name);
        Assert.Equal(30, links[0].Minutes);
        Assert.True(links[0].AllowsGuests);
        Assert.False(links[1].AllowsGuests);
        Assert.Equal(Key, _cal.Last.BearerToken);
        Assert.Equal("2024-06-14", _cal.Last.Header("cal-api-version"));
        Assert.DoesNotContain(Key, _cal.Last.Uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetLinksAsync_NullAndUnsafeFields_DropsThoseLinks()
    {
        _cal.On(HttpMethod.Get, Base + "v2/event-types", HttpStatusCode.OK, """{"status":"success","data":[{"id":7,"title":"x","lengthInMinutes":30,"bookingUrl":"https://user@cal.com/x","hidden":false},{"id":8}]}""");

        Assert.Empty(await Create().GetLinksAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task GetLinksAsync_KeyRefused_ThrowsAuth(HttpStatusCode status)
    {
        _cal.On(HttpMethod.Get, Base + "v2/event-types", status, """{"status":"error"}""");

        await Assert.ThrowsAsync<SchedulingAuthException>(() => Create().GetLinksAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetLinksAsync_Offline_ThrowsUnavailable()
    {
        _cal.Throw(new HttpRequestException("offline"));

        await Assert.ThrowsAsync<SchedulingUnavailableException>(() => Create().GetLinksAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetLinksAsync_RateLimited_ThrowsUnavailableWithRetryAfter()
    {
        _cal.On(_ => true, _ =>
        {
            var response = FakeHttpHandler.Json((HttpStatusCode)429, """{"status":"error"}""");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
            return response;
        });

        var error = await Assert.ThrowsAsync<SchedulingUnavailableException>(() => Create().GetLinksAsync(TestContext.Current.CancellationToken));
        Assert.Equal(TimeSpan.FromSeconds(90), error.RetryAfter);
    }

    [Fact]
    public async Task GetOpenTimesAsync_SendsRangeQuery_ParsesOffsets()
    {
        _cal.On(HttpMethod.Get, Base + "v2/slots", HttpStatusCode.OK, Fixture.Read("calcom-slots.json"));
        var start = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

        var open = await Create().GetOpenTimesAsync("101", start, start.AddDays(2), TestContext.Current.CancellationToken);

        Assert.Equal(3, open.Count);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 14, 0, 0, TimeSpan.Zero), open[0].Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 14, 30, 0, TimeSpan.Zero), open[0].End);
        Assert.Equal("101", _cal.Last.Query("eventTypeId"));
        Assert.Equal("2026-10-13T00:00:00.000Z", _cal.Last.Query("start"));
        Assert.Equal("2026-10-15T00:00:00.000Z", _cal.Last.Query("end"));
        Assert.Equal("range", _cal.Last.Query("format"));
        Assert.Equal("2024-09-04", _cal.Last.Header("cal-api-version"));
    }

    [Fact]
    public async Task GetOpenTimesAsync_EmptyData_ReturnsNone()
    {
        _cal.On(HttpMethod.Get, Base + "v2/slots", HttpStatusCode.OK, """{"status":"success","data":{}}""");
        var start = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

        Assert.Empty(await Create().GetOpenTimesAsync("101", start, start.AddDays(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetBookingsAsync_SendsFilters_ParsesAttendee()
    {
        _cal.On(HttpMethod.Get, Base + "v2/bookings", HttpStatusCode.OK, Fixture.Read("calcom-bookings.json"));
        var after = new DateTimeOffset(2026, 10, 12, 17, 0, 0, TimeSpan.Zero);

        var bookings = await Create().GetBookingsAsync("101", after, TestContext.Current.CancellationToken);

        Assert.Equal(["bk1", "bk2"], bookings.Select(b => b.Id));
        Assert.Equal("Jane Doe", bookings[0].AttendeeName);
        Assert.Equal("", bookings[1].AttendeeName);
        Assert.Equal("101", _cal.Last.Query("eventTypeId"));
        Assert.Equal("2026-10-12T17:00:00.000Z", _cal.Last.Query("afterCreatedAt"));
        Assert.Equal("upcoming", _cal.Last.Query("status"));
        Assert.Equal("2024-08-13", _cal.Last.Header("cal-api-version"));
    }

    [Fact]
    public async Task BookAsync_SendsBookerAndGuests_WhenAllowed()
    {
        _cal.On(HttpMethod.Post, Base + "v2/bookings", HttpStatusCode.Created, Fixture.Read("calcom-booking-created.json"));
        var link = new SchedulingLink("calcom", "101", "30 min meeting", 30, new Uri("https://cal.com/leaf-tester/30min"), AllowsGuests: true);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

        var booking = await Create().BookAsync(link, new DateTimeOffset(2026, 10, 13, 14, 0, 0, TimeSpan.Zero), [new("Jane Doe", "jane@example.com"), new("", "sam@example.com")], zone, TestContext.Current.CancellationToken);

        Assert.Equal("bk9", booking.Id);
        var body = System.Text.Json.Nodes.JsonNode.Parse(_cal.Last.Body!)!;
        Assert.Equal("2026-10-13T14:00:00.000Z", (string?)body["start"]);
        Assert.Equal(101, (int?)body["eventTypeId"]);
        Assert.Equal("Jane Doe", (string?)body["attendee"]!["name"]);
        Assert.Equal("jane@example.com", (string?)body["attendee"]!["email"]);
        Assert.Equal("America/Chicago", (string?)body["attendee"]!["timeZone"]);
        Assert.Equal(["sam@example.com"], body["guests"]!.AsArray().Select(g => (string?)g));
    }

    [Fact]
    public async Task BookAsync_NoNameNoGuestsAllowed_UsesEmailAsNameAndSendsNoGuests()
    {
        _cal.On(HttpMethod.Post, Base + "v2/bookings", HttpStatusCode.Created, Fixture.Read("calcom-booking-created.json"));
        var link = new SchedulingLink("calcom", "102", "Quick chat", 15, new Uri("https://cal.com/leaf-tester/15min"), AllowsGuests: false);

        await Create().BookAsync(link, DateTimeOffset.UtcNow, [new("", "jane@example.com"), new("", "sam@example.com")], TimeZoneInfo.Utc, TestContext.Current.CancellationToken);

        var body = System.Text.Json.Nodes.JsonNode.Parse(_cal.Last.Body!)!;
        Assert.Equal("jane@example.com", (string?)body["attendee"]!["name"]);
        Assert.Null(body["guests"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, """{"status":"error"}""")]
    [InlineData(HttpStatusCode.BadRequest, """{"status":"error","error":{"message":"User either already has booking at this time or is not available"}}""")]
    public async Task BookAsync_Taken_ThrowsSlotTaken(HttpStatusCode status, string body)
    {
        _cal.On(HttpMethod.Post, Base + "v2/bookings", status, body);
        var link = new SchedulingLink("calcom", "101", "x", 30, new Uri("https://cal.com/x"), true);

        await Assert.ThrowsAsync<SchedulingSlotTakenException>(() => Create().BookAsync(link, DateTimeOffset.UtcNow, [new("", "a@example.com")], TimeZoneInfo.Utc, TestContext.Current.CancellationToken));
    }
}
