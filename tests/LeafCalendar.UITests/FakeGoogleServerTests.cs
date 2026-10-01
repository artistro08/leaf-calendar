using System.Text;
using System.Text.Json.Nodes;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

/// <summary>Checks the fake Google's own routes directly (no app window needed): video calls, free/busy, the calendar list, and the directory.</summary>
public sealed class FakeGoogleServerTests : IDisposable
{
    const string Calendar = "leaf.tester@gmail.com";
    const string Events   = "calendar/v3/calendars/leaf.tester%40gmail.com/events";

    readonly FakeGoogleServer _google = new();
    readonly HttpClient _http = new();

    public void Dispose()
    {
        _http.Dispose();
        _google.Dispose();
    }

    async Task<JsonObject> Send(HttpMethod method, string path, JsonObject body)
    {
        using var request = new HttpRequestMessage(method, new Uri(_google.BaseUri, path)) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        using var response = await _http.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsObject();
    }

    static JsonObject Timed(string id) => new()
    {
        ["id"]    = id,
        ["start"] = new JsonObject { ["dateTime"] = "2026-10-02T14:00:00Z" },
        ["end"]   = new JsonObject { ["dateTime"] = "2026-10-02T15:00:00Z" },
    };

    static JsonObject MeetRequest(string requestId) => new()
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
        body["hangoutLink"]    = "https://meet.google.com/seeded";
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
        using var set   = await _http.PatchAsync(new Uri(_google.BaseUri, entry), new StringContent("""{"summaryOverride":"Kids"}"""), TestContext.Current.CancellationToken);
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
}
