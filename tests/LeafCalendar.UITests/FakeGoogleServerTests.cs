using System.Text;
using System.Text.Json.Nodes;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

/// <summary>Checks the fake Google's video call handling directly (no app window needed).</summary>
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
}
