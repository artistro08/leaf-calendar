using System.Text;
using System.Text.Json.Nodes;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

/// <summary>Checks the fake Google's own routes directly (no app window needed): video calls, free/busy, the calendar list, and the directory.</summary>
public sealed class FakeGoogleServerTests : IDisposable
{
    private const string Calendar = "leaf.tester@gmail.com";
    private const string Events = "calendar/v3/calendars/leaf.tester%40gmail.com/events";

    private readonly FakeGoogleServer _google = new();
    private readonly HttpClient _http = new();

    public void Dispose()
    {
        _http.Dispose();
        _google.Dispose();
    }

    private async Task<JsonObject> Send(HttpMethod method, string path, JsonObject body)
    {
        using var request = new HttpRequestMessage(method, new Uri(_google.BaseUri, path)) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        using var response = await _http.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsObject();
    }

    private static JsonObject Timed(string id) => new()
    {
        ["id"] = id,
        ["start"] = new JsonObject { ["dateTime"] = "2026-10-02T14:00:00Z" },
        ["end"] = new JsonObject { ["dateTime"] = "2026-10-02T15:00:00Z" },
    };

    private static JsonObject MeetRequest(string requestId) => new()
    {
        ["createRequest"] = new JsonObject { ["requestId"] = requestId, ["conferenceSolutionKey"] = new JsonObject { ["type"] = "hangoutsMeet" } },
    };

    [Fact]
    public async Task Insert_WithCreateRequestAndVersion1_StoresAMeetLink()
    {
        var body = Timed("meetnew001");
        body["conferenceData"] = MeetRequest("abcd1234");

        var created = await Send(HttpMethod.Post, Events + "?conferenceDataVersion=1", body);

        Assert.Equal("https://meet.google.com/fake-abcd", (string?)created["hangoutLink"]);
        Assert.NotNull(created["conferenceData"]!["conferenceId"]);
    }

    [Fact]
    public async Task Insert_WithoutVersion1_IgnoresConferenceData()
    {
        var body = Timed("meetnew002");
        body["conferenceData"] = MeetRequest("abcd1234");

        var created = await Send(HttpMethod.Post, Events, body);

        Assert.Null(created["hangoutLink"]);
        Assert.Null(created["conferenceData"]);
    }

    [Fact]
    public async Task Patch_TitleOnly_KeepsAStoredMeet()
    {
        var body = Timed("meetkeep01");
        body["conferenceData"] = MeetRequest("keep1234");
        await Send(HttpMethod.Post, Events + "?conferenceDataVersion=1", body);

        var patched = await Send(HttpMethod.Patch, Events + "/meetkeep01", new JsonObject { ["summary"] = "Renamed" });

        Assert.Equal("https://meet.google.com/fake-keep", (string?)patched["hangoutLink"]);
        Assert.NotNull(patched["conferenceData"]);
    }

    [Fact]
    public async Task Patch_NullConferenceData_RemovesBoth()
    {
        var body = Timed("meetgone01");
        body["conferenceData"] = MeetRequest("gone1234");
        await Send(HttpMethod.Post, Events + "?conferenceDataVersion=1", body);

        var patched = await Send(HttpMethod.Patch, Events + "/meetgone01?conferenceDataVersion=1", new JsonObject { ["conferenceData"] = null });

        Assert.Null(patched["hangoutLink"]);
        Assert.Null(patched["conferenceData"]);
    }

    [Fact]
    public void AddEvent_WithExistingMeet_KeepsItAsGiven()
    {
        var body = Timed("meetseed01");
        body["hangoutLink"] = "https://meet.google.com/seeded";
        body["conferenceData"] = new JsonObject { ["conferenceId"] = "seeded" };

        _google.AddEvent(Calendar, body);

        var stored = _google.EventOnGoogle(Calendar, "meetseed01")!;
        Assert.Equal("seeded", (string?)stored["conferenceData"]!["conferenceId"]);
        Assert.Equal("https://meet.google.com/seeded", (string?)stored["hangoutLink"]);
    }

    [Fact]
    public void AddEvent_DuplicateId_Throws()
    {
        _google.AddEvent(Calendar, Timed("dupseed001"));

        Assert.Throws<InvalidOperationException>(() => _google.AddEvent(Calendar, Timed("dupseed001")));
    }

    // =========================================================================
    // MILESTONE 5 ROUTES
    // =========================================================================

    [Fact]
    public async Task FreeBusy_KnownBusyUnknownAndSeeded()
    {
        _google.Busy["dana@example.com"] = [(new(2026, 10, 1, 15, 0, 0, TimeSpan.Zero), new(2026, 10, 1, 16, 0, 0, TimeSpan.Zero))];

        var response = await _http.PostAsync(new Uri(_google.BaseUri, "calendar/v3/freeBusy"), new StringContent("""
            {"timeMin":"2026-10-01T00:00:00Z","timeMax":"2026-10-02T00:00:00Z",
             "items":[{"id":"dana@example.com"},{"id":"nobody@example.org"},{"id":"leaf.tester@gmail.com"}]}
            """), TestContext.Current.CancellationToken);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["calendars"]!;

        Assert.Equal("2026-10-01T15:00:00Z", (string?)json["dana@example.com"]!["busy"]![0]!["start"]);
        Assert.Equal("notFound", (string?)json["nobody@example.org"]!["errors"]![0]!["reason"]);
        Assert.Equal(2, json["leaf.tester@gmail.com"]!["busy"]!.AsArray().Count); // dentist 9-10, design review 14-15 ET
        Assert.Single(_google.FreeBusyQueries);
    }

    [Fact]
    public async Task CalendarListPatch_ShowsOnTheNextList()
    {
        using var patch = await _http.PatchAsync(new Uri(_google.BaseUri, "calendar/v3/users/me/calendarList/family123%40group.calendar.google.com"),
            new StringContent("""{"summaryOverride":"Kids"}"""), TestContext.Current.CancellationToken);
        var list = JsonNode.Parse(await _http.GetStringAsync(new Uri(_google.BaseUri, "calendar/v3/users/me/calendarList"), TestContext.Current.CancellationToken))!;

        Assert.True(patch.IsSuccessStatusCode);
        Assert.Equal("Kids", (string?)list["items"]!.AsArray().First(i => (string?)i!["id"] == "family123@group.calendar.google.com")!["summaryOverride"]);
        Assert.Contains(_google.Writes, w => w.Method == "PATCH" && w.Body.Contains("Kids", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CalendarListPatch_Null_RemovesTheOverride()
    {
        var entry = "calendar/v3/users/me/calendarList/family123%40group.calendar.google.com";
        using var set = await _http.PatchAsync(new Uri(_google.BaseUri, entry), new StringContent("""{"summaryOverride":"Kids"}"""), TestContext.Current.CancellationToken);
        using var reset = await _http.PatchAsync(new Uri(_google.BaseUri, entry), new StringContent("""{"summaryOverride":null}"""), TestContext.Current.CancellationToken);
        var list = JsonNode.Parse(await _http.GetStringAsync(new Uri(_google.BaseUri, "calendar/v3/users/me/calendarList"), TestContext.Current.CancellationToken))!;

        Assert.Null(list["items"]!.AsArray().First(i => (string?)i!["id"] == "family123@group.calendar.google.com")!["summaryOverride"]);
    }

    [Fact]
    public async Task DirectorySearch_Workspace_PrefixMatches()
    {
        _google.HostedDomain = "example.com";

        var json = JsonNode.Parse(await _http.GetStringAsync(new Uri(_google.BaseUri, "people/v1/people:searchDirectoryPeople?query=dan&readMask=names,emailAddresses"), TestContext.Current.CancellationToken))!;

        Assert.Equal("dana@example.com", (string?)Assert.Single(json["people"]!.AsArray())!["emailAddresses"]![0]!["value"]);
    }

    [Fact]
    public async Task DirectorySearch_PersonalAccount_IsRefused()
    {
        using var response = await _http.GetAsync(new Uri(_google.BaseUri, "people/v1/people:searchDirectoryPeople?query=dan&readMask=names,emailAddresses"), TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UserInfo_Workspace_HasTheDomain()
    {
        _google.HostedDomain = "example.com";

        var user = JsonNode.Parse(await _http.GetStringAsync(new Uri(_google.BaseUri, "userinfo"), TestContext.Current.CancellationToken))!;

        Assert.Equal("example.com", (string?)user["hd"]);
    }

    [Fact]
    public async Task RangeList_UnknownCalendar_Is404()
    {
        using var response = await _http.GetAsync(new Uri(_google.BaseUri, "calendar/v3/calendars/dana%40example.com/events?timeMin=2026-10-01T00:00:00Z&timeMax=2026-10-02T00:00:00Z"), TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RangeList_Teammate_ServesTheirEvents()
    {
        _google.TeammateEvents["dana@example.com"] = [new JsonObject { ["id"] = "dana1", ["summary"] = "Standup" }];

        var json = JsonNode.Parse(await _http.GetStringAsync(new Uri(_google.BaseUri, "calendar/v3/calendars/dana%40example.com/events?timeMin=2026-10-01T00:00:00Z&timeMax=2026-10-02T00:00:00Z"), TestContext.Current.CancellationToken))!;

        Assert.Equal("Standup", (string?)Assert.Single(json["items"]!.AsArray())!["summary"]);
    }

    // =========================================================================
    // CAL.COM ROUTES
    // =========================================================================

    private async Task<HttpResponseMessage> CalCom(HttpMethod method, string path, string? body = null, string token = FakeGoogleServer.CalComAccessToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(_google.BaseUri, "calcom/" + path));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await _http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;

    [Fact]
    public async Task CalComAuthorize_RedirectsBackWithACodeAndTheState()
    {
        using var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var response = await noRedirect.GetAsync(new Uri(_google.BaseUri, "calcom/auth/oauth2/authorize?client_id=c&redirect_uri=http%3A%2F%2F127.0.0.1%3A53817%2Fcalcom%2Fcallback&state=s%201"), TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("http://127.0.0.1:53817/calcom/callback?code=calcom-code&state=s%201", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task CalComToken_IssuesTokens_OrRefuses()
    {
        using var form = new FormUrlEncodedContent([new("grant_type", "authorization_code"), new("code", "calcom-code")]);
        using var issued = await _http.PostAsync(new Uri(_google.BaseUri, "calcom/v2/auth/oauth2/token"), form, TestContext.Current.CancellationToken);
        _google.CalComRefuseToken = true;
        using var form2 = new FormUrlEncodedContent([new("grant_type", "refresh_token"), new("refresh_token", "x")]);
        using var refused = await _http.PostAsync(new Uri(_google.BaseUri, "calcom/v2/auth/oauth2/token"), form2, TestContext.Current.CancellationToken);

        var token = await Json(issued);
        Assert.Equal(FakeGoogleServer.CalComAccessToken, (string?)token["access_token"]);
        Assert.Equal(FakeGoogleServer.CalComRefreshToken, (string?)token["refresh_token"]);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task CalComEventTypes_ListsTwoLinks_401WithAnotherToken_500WhenDown()
    {
        using var ok = await CalCom(HttpMethod.Get, "v2/event-types");
        using var wrong = await CalCom(HttpMethod.Get, "v2/event-types", token: "cal_live_wrong");
        _google.CalComLinksDown = true;
        using var down = await CalCom(HttpMethod.Get, "v2/event-types");

        var links = (await Json(ok))["data"]!.AsArray();
        Assert.Equal(["30 min meeting", "Quick chat"], links.Select(l => (string)l!["title"]!));
        Assert.False((bool)links[0]!["disableGuests"]!);
        Assert.True((bool)links[1]!["disableGuests"]!);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, down.StatusCode);
    }

    [Fact]
    public async Task CalComSlots_OnlyInsideTheRange_GroupedByDay()
    {
        var nine = new DateTimeOffset(2026, 10, 1, 13, 0, 0, TimeSpan.Zero);
        _google.CalComOpen = [(nine, nine.AddMinutes(30)), (nine.AddMinutes(30), nine.AddHours(1)), (nine.AddDays(1), nine.AddDays(1).AddMinutes(30))];

        using var response = await CalCom(HttpMethod.Get, "v2/slots?eventTypeId=101&start=2026-10-01T00:00:00.000Z&end=2026-10-02T00:00:00.000Z&format=range");

        var day = (await Json(response))["data"]!["2026-10-01"]!.AsArray();
        Assert.Equal(["2026-10-01T13:00:00Z", "2026-10-01T13:30:00Z"], day.Select(s => (string)s!["start"]!));
        Assert.Equal("2026-10-01T14:00:00Z", (string?)day[1]!["end"]);
    }

    [Fact]
    public async Task CalComBookings_AddedOnesAfterTheTime_ForTheirLinkOnly()
    {
        _google.AddCalComBooking(new DateTimeOffset(2026, 10, 1, 13, 0, 0, TimeSpan.Zero), "Jane Doe");
        _google.AddCalComBooking(new DateTimeOffset(2026, 10, 1, 14, 0, 0, TimeSpan.Zero), "Other Link", FakeGoogleServer.CalComQuickChat);

        using var after = await CalCom(HttpMethod.Get, "v2/bookings?eventTypeId=101&afterCreatedAt=2026-10-01T00:00:00.000Z&status=upcoming");
        using var later = await CalCom(HttpMethod.Get, $"v2/bookings?eventTypeId=101&afterCreatedAt={DateTimeOffset.UtcNow.AddHours(1):yyyy-MM-dd'T'HH:mm:ss'Z'}&status=upcoming");

        var booking = Assert.Single((await Json(after))["data"]!.AsArray())!;
        Assert.Equal("Jane Doe", (string?)booking["attendees"]![0]!["name"]);
        Assert.Equal("2026-10-01T13:00:00Z", (string?)booking["start"]);
        Assert.Empty((await Json(later))["data"]!.AsArray());
    }

    [Fact]
    public async Task CalComBook_RecordsTheBody_409WhenTaken_ThenListsTheBooking()
    {
        const string Body = """{"start":"2026-10-01T13:00:00.000Z","eventTypeId":101,"attendee":{"name":"Jane","email":"jane@example.com","timeZone":"America/New_York"}}""";
        _google.CalComTaken = true;

        using var taken = await CalCom(HttpMethod.Post, "v2/bookings", Body);
        using var booked = await CalCom(HttpMethod.Post, "v2/bookings", Body);
        using var list = await CalCom(HttpMethod.Get, "v2/bookings?eventTypeId=101&afterCreatedAt=2026-10-01T00:00:00.000Z&status=upcoming");

        Assert.Equal(System.Net.HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Created, booked.StatusCode);
        Assert.Equal("2026-10-01T13:00:00Z", (string?)(await Json(booked))["data"]!["start"]);
        Assert.Equal([Body, Body], _google.CalComBookings);
        Assert.Equal("Jane", (string?)Assert.Single((await Json(list))["data"]!.AsArray())!["attendees"]![0]!["name"]);
    }
}
