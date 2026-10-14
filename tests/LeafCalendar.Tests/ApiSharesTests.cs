using System.Net;
using System.Text.Json;
using LeafCalendar.Core.Api;
using LeafCalendar.Core.Data;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class ApiSharesTests : IDisposable
{
    private const string Cal = "https://api.cal.com/";

    // Thursday Oct 1 2026; the harness's clock is 8 AM New York time, working hours 9 to 5 on weekdays
    private const string Day = "\"start\":\"2026-10-01T00:00:00-04:00\",\"end\":\"2026-10-02T00:00:00-04:00\"";

    private readonly ApiHarness _api = new();

    public void Dispose() => _api.Dispose();

    // A signed-in Cal.com with the links fixture and open times on Oct 13 (10 to 11 AM New York) and Oct 14 (10 AM)
    private void SignIn()
    {
        _api.Tokens.SetProviderSecret("calcom", "client_id", "client-123");
        _api.Tokens.SetProviderSecret("calcom", "client_secret", "secret-1");
        _api.Tokens.SetProviderSecret("calcom", "refresh_token", "rt-1");
        _api.Http.On(r => r.Method == HttpMethod.Post && r.Uri.AbsoluteUri == Cal + "v2/auth/oauth2/token", _ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"access_token":"at-1","expires_in":1800}"""));
        _api.Http.On(HttpMethod.Get, Cal + "v2/event-types", HttpStatusCode.OK, Fixture.Read("calcom-event-types.json"));
        _api.Http.On(HttpMethod.Get, Cal + "v2/slots", HttpStatusCode.OK, Fixture.Read("calcom-slots.json"));
        _api.Scheduling.Load();
    }

    private async Task<string> LinkId()
    {
        var links = await _api.CallAsync("scheduling.links");
        return links.EnumerateArray().First(l => l.GetProperty("name").GetString() == "30 min meeting").GetProperty("id").GetString()!;
    }

    private static List<(string Start, string End)> Times(JsonElement times) =>
        [.. times.EnumerateArray().Select(t => (t.GetProperty("start").GetString()!, t.GetProperty("end").GetString()!))];

    private async Task<string> CreateAsync(string paramsJson) => (await _api.CallAsync("shares.create", paramsJson)).GetProperty("id").GetString()!;

    // =========================================================================
    // FREE TIMES
    // =========================================================================

    [Fact]
    public async Task Free_WorkingHoursMinusBusyEvents_FreeAndDeclinedDontCount()
    {
        _api.Store(ApiHarness.Primary, """{"id":"busy","status":"confirmed","summary":"Busy","start":{"dateTime":"2026-10-01T10:00:00-04:00"},"end":{"dateTime":"2026-10-01T11:00:00-04:00"}}""");
        _api.Store(ApiHarness.Primary, """{"id":"free","status":"confirmed","summary":"Free","transparency":"transparent","start":{"dateTime":"2026-10-01T13:00:00-04:00"},"end":{"dateTime":"2026-10-01T14:00:00-04:00"}}""");
        _api.Store(ApiHarness.Primary, """{"id":"no","status":"confirmed","summary":"No","attendees":[{"email":"leaf.tester@gmail.com","self":true,"responseStatus":"declined"}],"start":{"dateTime":"2026-10-01T15:00:00-04:00"},"end":{"dateTime":"2026-10-01T16:00:00-04:00"}}""");

        var free = Times(await _api.CallAsync("availability.free", "{" + Day + "}"));

        Assert.Equal([("2026-10-01T09:00:00-04:00", "2026-10-01T10:00:00-04:00"), ("2026-10-01T11:00:00-04:00", "2026-10-01T17:00:00-04:00")], free);
    }

    [Fact]
    public async Task Free_StartsNow()
    {
        _api.Time.SetUtcNow(new DateTimeOffset(2026, 10, 1, 13, 15, 0, TimeSpan.Zero));

        var free = Times(await _api.CallAsync("availability.free", "{" + Day + "}"));

        Assert.Equal([("2026-10-01T09:15:00-04:00", "2026-10-01T17:00:00-04:00")], free);
    }

    [Fact]
    public async Task Free_ShorterThanMinutes_IsLeftOut()
    {
        _api.Store(ApiHarness.Primary, """{"id":"busy","status":"confirmed","summary":"Busy","start":{"dateTime":"2026-10-01T10:00:00-04:00"},"end":{"dateTime":"2026-10-01T16:00:00-04:00"}}""");

        _api.Store(ApiHarness.Primary, """{"id":"busy2","status":"confirmed","summary":"Busy","start":{"dateTime":"2026-10-01T16:30:00-04:00"},"end":{"dateTime":"2026-10-01T17:00:00-04:00"}}""");

        var free = Times(await _api.CallAsync("availability.free", "{" + Day + ",\"minutes\":60}"));

        Assert.Equal([("2026-10-01T09:00:00-04:00", "2026-10-01T10:00:00-04:00")], free);
    }

    [Fact]
    public async Task Free_OnlyWorkDays()
    {
        var free = Times(await _api.CallAsync("availability.free", """{"start":"2026-10-03T00:00:00-04:00","end":"2026-10-06T00:00:00-04:00"}"""));

        Assert.Equal([("2026-10-05T09:00:00-04:00", "2026-10-05T17:00:00-04:00")], free);
    }

    [Theory]
    [InlineData("""{"start":"2026-10-01T00:00:00Z","end":"2026-11-05T00:00:00Z"}""")]
    [InlineData("""{"start":"2026-10-01T00:00:00Z","end":"2026-10-02T00:00:00Z","minutes":2}""")]
    [InlineData("""{"start":"2026-10-01","end":"2026-10-02"}""")]
    public async Task Free_BadRange_IsInvalid(string paramsJson)
    {
        Assert.Equal(ApiErrors.InvalidParams, (await _api.FailAsync("availability.free", paramsJson)).Code);
    }

    [Fact]
    public async Task Free_WithLink_KeepsOnlyItsOpenTimes()
    {
        SignIn();

        var free = Times(await _api.CallAsync("availability.free", $$"""{"start":"2026-10-13T00:00:00-04:00","end":"2026-10-14T00:00:00-04:00","linkId":"{{await LinkId()}}"}"""));

        Assert.Equal([("2026-10-13T10:00:00-04:00", "2026-10-13T11:00:00-04:00")], free);
    }

    // =========================================================================
    // SAVED TIMES
    // =========================================================================

    [Fact]
    public async Task Create_PlainTimes_SavesThemAndReturnsTheShareText()
    {
        var result = await _api.CallAsync("shares.create", """{"title":"Coffee","times":[{"start":"2026-10-02T10:00:00-04:00","end":"2026-10-02T11:00:00-04:00"},{"start":"2026-10-02T10:30:00-04:00","end":"2026-10-02T12:00:00-04:00"}],"guests":[{"name":"Sam","email":"sam@example.com"},{"email":"kim@example.com"}]}""");

        Assert.Equal("Here are some times that work for me:\r\nFri Oct 2: 10 AM–12 PM ET", result.GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("html").ValueKind);
        Assert.Equal(1, _api.Host.SharesChangedCount);

        var share = Assert.Single((await _api.CallAsync("shares.list")).EnumerateArray());
        Assert.Equal(result.GetProperty("id").GetString(), share.GetProperty("id").GetString());
        Assert.Equal("Coffee", share.GetProperty("title").GetString());
        Assert.Equal([("2026-10-02T10:00:00-04:00", "2026-10-02T12:00:00-04:00")], Times(share.GetProperty("times")));
        Assert.Equal(["sam@example.com", "kim@example.com"], share.GetProperty("guests").EnumerateArray().Select(g => g.GetProperty("email").GetString()));
        Assert.Equal("Sam", share.GetProperty("guests")[0].GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, share.GetProperty("link").ValueKind);
    }

    [Fact]
    public async Task Create_Message_PutsTheTimesAtThePlaceholder()
    {
        var result = await _api.CallAsync("shares.create", """{"message":"Any of these?\n{times}\nThanks!","times":[{"start":"2026-10-02T14:00:00-04:00","end":"2026-10-02T15:30:00-04:00"}]}""");

        Assert.Equal("Any of these?\r\nFri Oct 2: 2–3:30 PM ET\r\nThanks!", result.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Create_WithLink_EachTimeCarriesItsBookingLink()
    {
        SignIn();

        var result = await _api.CallAsync("shares.create", $$"""{"times":[{"start":"2026-10-13T10:00:00-04:00","end":"2026-10-13T11:00:00-04:00"}],"linkId":"{{await LinkId()}}"}""");

        var text = result.GetProperty("text").GetString()!;
        Assert.Contains("Tue Oct 13, 10–10:30 AM ET: https://cal.com/leaf-tester/30min", text, StringComparison.Ordinal);
        Assert.Contains("Tue Oct 13, 10:30–11 AM ET: https://cal.com/leaf-tester/30min", text, StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://cal.com/leaf-tester/30min", result.GetProperty("html").GetString(), StringComparison.Ordinal);
        Assert.Equal(30, Assert.Single((await _api.CallAsync("shares.list")).EnumerateArray()).GetProperty("link").GetProperty("minutes").GetInt32());
    }

    [Theory]
    [InlineData("""{"times":[]}""")]
    [InlineData("""{"times":[{"start":"2026-10-02T11:00:00-04:00","end":"2026-10-02T10:00:00-04:00"}]}""")]
    [InlineData("""{"times":[{"start":"2026-10-02","end":"2026-10-03"}]}""")]
    [InlineData("""{"times":[{"start":"2026-10-02T10:00:00-04:00","end":"2026-10-02T11:00:00-04:00"}],"guests":[{"email":"not an address"}]}""")]
    public async Task Create_BadParams_IsInvalid(string paramsJson)
    {
        Assert.Equal(ApiErrors.InvalidParams, (await _api.FailAsync("shares.create", paramsJson)).Code);
    }

    [Fact]
    public async Task Create_PassedTime_IsInvalid()
    {
        Assert.Equal(ApiErrors.InvalidParams, (await _api.FailAsync("shares.create", """{"times":[{"start":"2026-10-01T06:00:00-04:00","end":"2026-10-01T07:00:00-04:00"}]}""")).Code);
        Assert.Empty((await _api.CallAsync("shares.list")).EnumerateArray());
    }

    [Fact]
    public async Task Create_WithLink_InABackOff_SplitsByTheLinksLengthWithoutAsking()
    {
        SignIn();
        var link = await LinkId();
        _api.Scheduling.WaitFor("calcom", TimeSpan.FromMinutes(5));
        var asked = _api.Http.Requests.Count;

        var result = await _api.CallAsync("shares.create", $$"""{"times":[{"start":"2026-10-13T10:00:00-04:00","end":"2026-10-13T11:00:00-04:00"}],"linkId":"{{link}}"}""");

        Assert.Contains("Tue Oct 13, 10:30–11 AM ET: https://cal.com/leaf-tester/30min", result.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal(asked, _api.Http.Requests.Count);
    }

    [Fact]
    public async Task Free_WithLink_InABackOff_IsUnavailableWithoutAsking()
    {
        SignIn();
        var link = await LinkId();
        _api.Scheduling.WaitFor("calcom", TimeSpan.FromMinutes(5));
        var asked = _api.Http.Requests.Count;

        Assert.Equal(ApiErrors.Unavailable, (await _api.FailAsync("availability.free", $$"""{"start":"2026-10-13T00:00:00-04:00","end":"2026-10-14T00:00:00-04:00","linkId":"{{link}}"}""")).DataCode);
        Assert.Equal(asked, _api.Http.Requests.Count);
    }

    [Fact]
    public async Task List_LeavesOutPassedTimes()
    {
        await CreateAsync("""{"times":[{"start":"2026-10-01T10:00:00-04:00","end":"2026-10-01T11:00:00-04:00"},{"start":"2026-10-02T10:00:00-04:00","end":"2026-10-02T11:00:00-04:00"}]}""");
        _api.Time.SetUtcNow(new DateTimeOffset(2026, 10, 1, 16, 0, 0, TimeSpan.Zero));

        var share = Assert.Single((await _api.CallAsync("shares.list")).EnumerateArray());

        Assert.Equal([("2026-10-02T10:00:00-04:00", "2026-10-02T11:00:00-04:00")], Times(share.GetProperty("times")));
    }

    [Fact]
    public async Task Book_PlainTimes_CreatesTheEventWithTheGuestsAndDeletesTheTimes()
    {
        var id = await CreateAsync("""{"title":"Coffee","times":[{"start":"2026-10-02T10:00:00-04:00","end":"2026-10-02T12:00:00-04:00"}],"guests":[{"name":"Sam","email":"sam@example.com"}]}""");

        var booked = await _api.CallAsync("shares.book", $$"""{"id":"{{id}}","start":"2026-10-02T10:30:00-04:00","end":"2026-10-02T11:00:00-04:00"}""");

        var entry = Assert.Single(_api.Outbox());
        Assert.Equal(OutboxOperation.Create, entry.Operation);
        Assert.True(entry.SendUpdates);
        Assert.Contains("sam@example.com", entry.Payload, StringComparison.Ordinal);
        var got = await _api.CallAsync("events.get", $$"""{"id":"{{booked.GetProperty("id").GetString()}}"}""");
        Assert.Equal("Coffee", got.GetProperty("title").GetString());
        Assert.Equal("2026-10-02T10:30:00-04:00", got.GetProperty("start").GetString());
        Assert.Empty((await _api.CallAsync("shares.list")).EnumerateArray());
        Assert.Equal(2, _api.Host.SharesChangedCount);
    }

    [Fact]
    public async Task Book_NoTitle_IsHeldTimes()
    {
        var id = await CreateAsync("""{"times":[{"start":"2026-10-02T10:00:00-04:00","end":"2026-10-02T11:00:00-04:00"}]}""");

        var booked = await _api.CallAsync("shares.book", $$"""{"id":"{{id}}","start":"2026-10-02T10:00:00-04:00","end":"2026-10-02T11:00:00-04:00"}""");

        var got = await _api.CallAsync("events.get", $$"""{"id":"{{booked.GetProperty("id").GetString()}}"}""");
        Assert.Equal("Held times", got.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("\"start\":\"2026-10-02T09:30:00-04:00\",\"end\":\"2026-10-02T10:30:00-04:00\"")]
    [InlineData("\"start\":\"2026-10-02T10:30:00-04:00\",\"end\":\"2026-10-02T11:30:00-04:00\"")]
    [InlineData("\"start\":\"2026-10-02T10:30:00-04:00\"")]
    public async Task Book_PlainTimes_OutsideTheTimesOrNoEnd_IsInvalid(string times)
    {
        var id = await CreateAsync("""{"times":[{"start":"2026-10-02T10:00:00-04:00","end":"2026-10-02T11:00:00-04:00"}]}""");

        Assert.Equal(ApiErrors.InvalidParams, (await _api.FailAsync("shares.book", $$"""{"id":"{{id}}",{{times}}}""")).Code);
        Assert.Empty(_api.Outbox());
    }

    [Fact]
    public async Task Book_WithLink_BooksOnTheProviderAndDeletesTheTimes()
    {
        SignIn();
        _api.Http.On(HttpMethod.Post, Cal + "v2/bookings", HttpStatusCode.Created, Fixture.Read("calcom-booking-created.json"));
        var id = await CreateAsync($$"""{"times":[{"start":"2026-10-13T10:00:00-04:00","end":"2026-10-13T11:00:00-04:00"}],"guests":[{"name":"Sam","email":"sam@example.com"}],"linkId":"{{await LinkId()}}"}""");

        var booked = await _api.CallAsync("shares.book", $$"""{"id":"{{id}}","start":"2026-10-13T10:30:00-04:00"}""");

        Assert.Equal(JsonValueKind.Null, booked.GetProperty("id").ValueKind);
        Assert.Empty(_api.Outbox());
        Assert.Empty((await _api.CallAsync("shares.list")).EnumerateArray());
    }

    [Fact]
    public async Task Book_WithLink_Taken_KeepsTheTimes()
    {
        SignIn();
        _api.Http.On(HttpMethod.Post, Cal + "v2/bookings", HttpStatusCode.Conflict, """{"status":"error"}""");
        var id = await CreateAsync($$"""{"times":[{"start":"2026-10-13T10:00:00-04:00","end":"2026-10-13T11:00:00-04:00"}],"guests":[{"email":"sam@example.com"}],"linkId":"{{await LinkId()}}"}""");

        Assert.Equal(ApiErrors.Taken, (await _api.FailAsync("shares.book", $$"""{"id":"{{id}}","start":"2026-10-13T10:00:00-04:00"}""")).DataCode);
        Assert.Single((await _api.CallAsync("shares.list")).EnumerateArray());
    }

    [Fact]
    public async Task Book_WithLink_RunningPastTheSavedTimes_IsInvalid()
    {
        SignIn();
        var id = await CreateAsync($$"""{"times":[{"start":"2026-10-13T10:00:00-04:00","end":"2026-10-13T11:00:00-04:00"}],"guests":[{"email":"sam@example.com"}],"linkId":"{{await LinkId()}}"}""");

        Assert.Equal(ApiErrors.InvalidParams, (await _api.FailAsync("shares.book", $$"""{"id":"{{id}}","start":"2026-10-13T10:45:00-04:00"}""")).Code);
        Assert.DoesNotContain(_api.Http.Requests, r => r.Uri.AbsoluteUri.StartsWith(Cal + "v2/bookings", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Book_WithLink_RateLimited_WaitsBeforeAskingAgain()
    {
        SignIn();
        _api.Http.On(r => r.Method == HttpMethod.Post && r.Uri.AbsoluteUri == Cal + "v2/bookings", _ =>
        {
            var response = FakeHttpHandler.Json(HttpStatusCode.TooManyRequests, """{"status":"error"}""");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return response;
        });
        var id = await CreateAsync($$"""{"times":[{"start":"2026-10-13T10:00:00-04:00","end":"2026-10-13T11:00:00-04:00"}],"guests":[{"email":"sam@example.com"}],"linkId":"{{await LinkId()}}"}""");
        var book = $$"""{"id":"{{id}}","start":"2026-10-13T10:00:00-04:00"}""";

        Assert.Equal(ApiErrors.Unavailable, (await _api.FailAsync("shares.book", book)).DataCode);
        Assert.True(_api.Scheduling.IsWaiting("calcom"));
        var asked = _api.Http.Requests.Count;
        Assert.Equal(ApiErrors.Unavailable, (await _api.FailAsync("shares.book", book)).DataCode);
        Assert.Equal(asked, _api.Http.Requests.Count);
        Assert.Single((await _api.CallAsync("shares.list")).EnumerateArray());
    }

    [Fact]
    public async Task Book_WithLink_NoGuest_IsInvalid()
    {
        SignIn();
        var id = await CreateAsync($$"""{"times":[{"start":"2026-10-13T10:00:00-04:00","end":"2026-10-13T11:00:00-04:00"}],"linkId":"{{await LinkId()}}"}""");

        Assert.Equal(ApiErrors.InvalidParams, (await _api.FailAsync("shares.book", $$"""{"id":"{{id}}","start":"2026-10-13T10:00:00-04:00"}""")).Code);
    }

    [Fact]
    public async Task Delete_RemovesTheTimes()
    {
        var id = await CreateAsync("""{"times":[{"start":"2026-10-02T10:00:00-04:00","end":"2026-10-02T11:00:00-04:00"}]}""");

        Assert.Equal("{}", (await _api.CallAsync("shares.delete", $$"""{"id":"{{id}}"}""")).GetRawText());

        Assert.Empty((await _api.CallAsync("shares.list")).EnumerateArray());
        Assert.Equal(2, _api.Host.SharesChangedCount);
    }

    [Theory]
    [InlineData("shares.delete", "")]
    [InlineData("shares.book", ",\"start\":\"2026-10-02T10:00:00-04:00\",\"end\":\"2026-10-02T11:00:00-04:00\"")]
    public async Task UnknownId_IsNotFound(string method, string more)
    {
        Assert.Equal(ApiErrors.NotFound, (await _api.FailAsync(method, $$"""{"id":"{{ApiIds.Share(999)}}"{{more}}}""")).DataCode);
    }

    [Fact]
    public async Task NotAShareId_IsInvalid()
    {
        Assert.Equal(ApiErrors.InvalidParams, (await _api.FailAsync("shares.delete", """{"id":"12"}""")).Code);
    }
}
