using System.Net;
using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.Sync;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class OutboxSenderTests : IDisposable
{
    const string Primary   = "leaf.tester@gmail.com";
    const string Family    = "family123@group.calendar.google.com";
    const string Account   = SyncHarness.AccountId;
    const string SingleUrl = SyncHarness.PrimaryEventsUrl + "/evt-single";
    const string BaseEtag  = "\"3181161784712000\"";

    readonly SyncHarness _h = new();
    readonly OutboxSender _sender;

    public OutboxSenderTests()
    {
        _h.Google.On(HttpMethod.Post, SyncHarness.TokenUrl, HttpStatusCode.OK, Fixture.Read("token-refresh.json"));
        _sender = new OutboxSender(_h.Client, _h.Db.Database, _h.Log, _h.Time);

        using var conn = _h.Db.Database.Open();
        CalendarStore.ReplaceForAccount(conn, Account, JsonSerializer.Deserialize(Fixture.Read("calendar-list.json"), GoogleJsonContext.Default.CalendarListPage)!.Items);
        foreach (var item in JsonSerializer.Deserialize(Fixture.Read("events-page1.json"), GoogleJsonContext.Default.EventsPage)!.Items)
        {
            EventStore.Apply(conn, null, Account, Primary, item);
        }
    }

    public void Dispose() => _h.Dispose();

    long Queue(string eventId, OutboxOperation operation, string? payload, string? etag = BaseEtag, DateTimeOffset? notBefore = null, string calendarId = Primary)
    {
        using var conn = _h.Db.Database.Open();
        return OutboxStore.Add(conn, null, new OutboxEntry(0, Account, calendarId, eventId, operation, payload, etag, false, EventStore.Snapshot(conn, null, Account, calendarId, eventId), notBefore));
    }

    StoredEvent? Get(string id, string calendarId = Primary)
    {
        using var conn = _h.Db.Database.Open();
        return EventStore.Get(conn, Account, calendarId, id);
    }

    IReadOnlyList<OutboxEntry> Pending()
    {
        using var conn = _h.Db.Database.Open();
        return OutboxStore.Pending(conn, Account);
    }

    Task<SendReport> Send() => _sender.SendAsync(Account, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Send_Patch_SendsIfMatchAndStoresGoogleVersion()
    {
        Queue("evt-single", OutboxOperation.Patch, """{"summary":"New"}""");
        _h.Google.On(HttpMethod.Patch, SingleUrl, HttpStatusCode.OK, """{"id":"evt-single","etag":"\"E1\"","status":"confirmed","summary":"New","start":{"dateTime":"2026-10-01T13:00:00Z"},"end":{"dateTime":"2026-10-01T14:00:00Z"}}""");

        var report = await Send();

        Assert.True(report.Changed);
        Assert.Equal(BaseEtag, _h.Google.Requests.Single(r => r.Method == HttpMethod.Patch).IfMatch);
        Assert.Empty(Pending());
        Assert.Equal("\"E1\"", Get("evt-single")!.Etag);
    }

    [Fact]
    public async Task Send_TwoEditsSameEvent_SecondUsesNewEtag()
    {
        Queue("evt-single", OutboxOperation.Patch, """{"start":{"dateTime":"2026-10-01T14:00:00Z"}}""");
        Queue("evt-single", OutboxOperation.Patch, """{"summary":"Renamed"}""");
        _h.Google.On(HttpMethod.Patch, SingleUrl, HttpStatusCode.OK, """{"id":"evt-single","etag":"\"E1\"","status":"confirmed"}""", once: true);
        _h.Google.On(HttpMethod.Patch, SingleUrl, HttpStatusCode.OK, """{"id":"evt-single","etag":"\"E2\"","status":"confirmed","summary":"Renamed","start":{"dateTime":"2026-10-01T14:00:00Z"},"end":{"dateTime":"2026-10-01T15:00:00Z"}}""");

        await Send();

        var patches = _h.Google.Requests.Where(r => r.Method == HttpMethod.Patch).ToList();
        Assert.Equal([BaseEtag, "\"E1\""], patches.Select(p => p.IfMatch));
        Assert.Equal("\"E2\"", Get("evt-single")!.Etag);
    }

    [Fact]
    public async Task Send_CreateRetriedAfterDrop_409TreatedAsSent()
    {
        Queue("leafnew0001", OutboxOperation.Create, """{"id":"leafnew0001","summary":"Offline","start":{"dateTime":"2026-10-02T13:00:00Z"},"end":{"dateTime":"2026-10-02T14:00:00Z"}}""", etag: null);
        _h.Google.On(r => r.Method == HttpMethod.Post && r.Uri.AbsoluteUri == SyncHarness.PrimaryEventsUrl + "?sendUpdates=none", _ => throw new HttpRequestException("Connection dropped after Google saved it."), once: true);
        _h.Google.On(r => r.Method == HttpMethod.Post && r.Uri.AbsoluteUri.StartsWith(SyncHarness.PrimaryEventsUrl + "?", StringComparison.Ordinal), _ => FakeHttpHandler.Json(HttpStatusCode.Conflict, """{"error":{"code":409,"errors":[{"reason":"duplicate"}]}}"""));
        _h.Google.On(HttpMethod.Get, SyncHarness.PrimaryEventsUrl + "/leafnew0001", HttpStatusCode.OK, """{"id":"leafnew0001","etag":"\"G1\"","status":"confirmed","summary":"Offline","start":{"dateTime":"2026-10-02T13:00:00Z"},"end":{"dateTime":"2026-10-02T14:00:00Z"}}""");

        await Send();
        Assert.Equal(1, Assert.Single(Pending()).Attempts);

        await Send();

        Assert.Equal(2, _h.Google.Requests.Count(r => r.Method == HttpMethod.Post && r.Uri.AbsoluteUri.StartsWith(SyncHarness.PrimaryEventsUrl + "?", StringComparison.Ordinal)));
        Assert.Empty(Pending());
        Assert.Equal("\"G1\"", Get("leafnew0001")!.Etag);
    }

    [Fact]
    public async Task Send_412_RecordsConflictAndOtherEventsKeepSending()
    {
        Queue("evt-single", OutboxOperation.Patch, """{"summary":"Mine"}""");
        Queue("evt-allday", OutboxOperation.Patch, """{"summary":"Holiday"}""", "\"3181161784712001\"");
        _h.Google.On(HttpMethod.Patch, SingleUrl, HttpStatusCode.PreconditionFailed, "{}");
        _h.Google.On(HttpMethod.Get, SingleUrl, HttpStatusCode.OK, """{"id":"evt-single","etag":"\"G9\"","summary":"Google's"}""");
        _h.Google.On(HttpMethod.Patch, SyncHarness.PrimaryEventsUrl + "/evt-allday", HttpStatusCode.OK, """{"id":"evt-allday","etag":"\"E5\"","status":"confirmed","start":{"date":"2026-10-12"},"end":{"date":"2026-10-13"}}""");

        var report = await Send();

        Assert.Equal(1, report.Conflicts);
        using var conn = _h.Db.Database.Open();
        var conflict = Assert.Single(ConflictStore.GetAll(conn));
        Assert.Equal("evt-single", conflict.Entry.EventId);
        Assert.Contains("Google's", conflict.GoogleJson!, StringComparison.Ordinal);
        Assert.Empty(OutboxStore.Pending(conn, Account));
        Assert.Equal("\"E5\"", EventStore.Get(conn, Account, Primary, "evt-allday")!.Etag);
    }

    [Fact]
    public async Task Send_PatchOnEventGoogleDeleted_ConflictWithoutGoogleVersion()
    {
        Queue("evt-single", OutboxOperation.Patch, """{"summary":"Mine"}""");
        _h.Google.On(HttpMethod.Patch, SingleUrl, HttpStatusCode.NotFound, "{}");

        var report = await Send();

        Assert.Equal(1, report.Conflicts);
        using var conn = _h.Db.Database.Open();
        Assert.Null(Assert.Single(ConflictStore.GetAll(conn)).GoogleJson);
    }

    [Fact]
    public async Task Send_DeleteAlreadyGone_IsDone()
    {
        Queue("evt-single", OutboxOperation.Delete, null);
        _h.Google.On(HttpMethod.Delete, SingleUrl, HttpStatusCode.Gone, "{}");

        await Send();

        Assert.Empty(Pending());
    }

    [Fact]
    public async Task Send_Forbidden_RestoresGoogleVersionAndReportsRejected()
    {
        using (var conn = _h.Db.Database.Open())
        {
            var snapshot = EventStore.Snapshot(conn, null, Account, Primary, "evt-single");
            OutboxStore.Add(conn, null, new OutboxEntry(0, Account, Primary, "evt-single", OutboxOperation.Patch, """{"summary":"Mine"}""", BaseEtag, false, snapshot, null));
            EventStore.ApplyJson(conn, null, Account, Primary, """{"id":"evt-single","status":"confirmed","summary":"Mine","start":{"dateTime":"2026-10-01T13:00:00Z"},"end":{"dateTime":"2026-10-01T14:00:00Z"}}""");
        }

        _h.Google.On(HttpMethod.Patch, SingleUrl, HttpStatusCode.Forbidden, Fixture.Read("error-forbidden.json"));
        _h.Google.On(HttpMethod.Get, SingleUrl, HttpStatusCode.OK, """{"id":"evt-single","etag":"\"G2\"","status":"confirmed","summary":"Dentist appointment","start":{"dateTime":"2026-10-01T13:00:00Z"},"end":{"dateTime":"2026-10-01T14:00:00Z"}}""");

        var report = await Send();

        Assert.Equal(1, report.Rejected);
        Assert.Empty(Pending());
        Assert.Contains("Dentist appointment", Get("evt-single")!.RawJson, StringComparison.Ordinal);
        Assert.Contains("outbox.rejected", File.ReadAllText(_h.LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_HeldDelete_WaitsForUndoWindow()
    {
        Queue("evt-single", OutboxOperation.Delete, null, notBefore: _h.Time.GetUtcNow().AddSeconds(6));
        _h.Google.On(HttpMethod.Delete, SingleUrl, HttpStatusCode.NoContent, "");

        await Send();
        Assert.DoesNotContain(_h.Google.Requests, r => r.Method == HttpMethod.Delete);

        _h.Time.Advance(TimeSpan.FromSeconds(7));
        await Send();

        Assert.Single(_h.Google.Requests, r => r.Method == HttpMethod.Delete);
        Assert.Empty(Pending());
    }

    [Fact]
    public async Task Send_Offline_StopsAndKeepsOrder()
    {
        Queue("evt-single", OutboxOperation.Patch, """{"summary":"A"}""");
        Queue("evt-allday", OutboxOperation.Patch, """{"summary":"B"}""");
        _h.Google.On(r => r.Method == HttpMethod.Patch, _ => throw new HttpRequestException("No network."));

        var report = await Send();

        Assert.False(report.Changed);
        Assert.Single(_h.Google.Requests, r => r.Method == HttpMethod.Patch);
        var pending = Pending();
        Assert.Equal(2, pending.Count);
        Assert.Equal("network", pending[0].LastError);
    }

    [Fact]
    public async Task Send_CalendarGone_DropsEntryWithoutCallingGoogle()
    {
        var seq = Queue("evt-single", OutboxOperation.Patch, """{"summary":"A"}""", calendarId: "removed@group.calendar.google.com");

        var report = await Send();

        Assert.True(report.Changed);
        Assert.Empty(Pending());
        Assert.DoesNotContain(_h.Google.Requests, r => r.Method == HttpMethod.Patch);
        Assert.Contains($"seq={seq}", File.ReadAllText(_h.LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_ConflictReadFailsAfterRejection_KeepsReportAndDefers()
    {
        Queue("evt-allday", OutboxOperation.Patch, """{"summary":"A"}""", "\"3181161784712001\"");
        Queue("evt-single", OutboxOperation.Patch, """{"summary":"B"}""");
        _h.Google.On(HttpMethod.Patch, SyncHarness.PrimaryEventsUrl + "/evt-allday", HttpStatusCode.Forbidden, Fixture.Read("error-forbidden.json"));
        _h.Google.On(HttpMethod.Patch, SingleUrl, HttpStatusCode.PreconditionFailed, "{}");
        _h.Google.On(r => r.Method == HttpMethod.Get && r.Uri.AbsoluteUri == SingleUrl, _ => throw new HttpRequestException("No network."));

        var report = await Send();

        Assert.Equal(new SendReport(true, 0, 1), report);
        var entry = Assert.Single(Pending());
        Assert.Equal("evt-single", entry.EventId);
        Assert.Equal("network", entry.LastError);
        using var conn = _h.Db.Database.Open();
        Assert.Empty(ConflictStore.GetAll(conn));
    }

    [Fact]
    public async Task Send_RejectedAndGoogleCopyUnreadable_RestoresSnapshot()
    {
        Queue("evt-single", OutboxOperation.Patch, """{"summary":"Mine"}""");
        var before = Get("evt-single")!.RawJson;
        using (var conn = _h.Db.Database.Open())
        {
            EventStore.ApplyJson(conn, null, Account, Primary, """{"id":"evt-single","status":"confirmed","summary":"Mine","start":{"dateTime":"2026-10-01T13:00:00Z"},"end":{"dateTime":"2026-10-01T14:00:00Z"}}""");
        }

        _h.Google.On(r => r.Uri.AbsoluteUri.StartsWith(SingleUrl, StringComparison.Ordinal), _ => FakeHttpHandler.Json(HttpStatusCode.Forbidden, Fixture.Read("error-forbidden.json")));

        var report = await Send();

        Assert.Equal(1, report.Rejected);
        Assert.Empty(Pending());
        Assert.Equal(before, Get("evt-single")!.RawJson);
        Assert.Single(_h.Google.Requests, r => r.Method == HttpMethod.Get);
    }

    [Theory]
    [InlineData("quotaExceeded")]
    [InlineData("dailyLimitExceeded")]
    public async Task Send_UsageLimit403_StaysPending(string reason)
    {
        Queue("evt-single", OutboxOperation.Patch, """{"summary":"A"}""");
        _h.Google.On(HttpMethod.Patch, SingleUrl, HttpStatusCode.Forbidden, $$$"""{"error":{"code":403,"errors":[{"reason":"{{{reason}}}"}]}}""");

        var report = await Send();

        Assert.Equal(0, report.Rejected);
        Assert.Equal("status 403", Assert.Single(Pending()).LastError);
    }

    [Fact]
    public async Task Send_CorruptRsvpPayload_RejectedWithoutPatch()
    {
        Queue("evt-single", OutboxOperation.Rsvp, "{not json");

        var report = await Send();

        Assert.Equal(1, report.Rejected);
        Assert.Empty(Pending());
        Assert.DoesNotContain(_h.Google.Requests, r => r.Method == HttpMethod.Patch);
        Assert.NotNull(Get("evt-single"));
    }

    [Fact]
    public async Task Send_AnswerWithoutEtag_ReadsItBackForTheNextEdit()
    {
        Queue("evt-single", OutboxOperation.Patch, """{"summary":"A"}""");
        Queue("evt-single", OutboxOperation.Patch, """{"summary":"B"}""");
        _h.Google.On(HttpMethod.Patch, SingleUrl, HttpStatusCode.OK, """{"id":"evt-single","status":"confirmed"}""", once: true);
        _h.Google.On(HttpMethod.Get, SingleUrl, HttpStatusCode.OK, """{"id":"evt-single","etag":"\"R1\"","status":"confirmed"}""");
        _h.Google.On(HttpMethod.Patch, SingleUrl, HttpStatusCode.OK, """{"id":"evt-single","etag":"\"E2\"","status":"confirmed","summary":"B","start":{"dateTime":"2026-10-01T13:00:00Z"},"end":{"dateTime":"2026-10-01T14:00:00Z"}}""");

        await Send();

        Assert.Equal([BaseEtag, "\"R1\""], _h.Google.Requests.Where(r => r.Method == HttpMethod.Patch).Select(p => p.IfMatch));
    }

    [Fact]
    public async Task Send_AnswerWithoutEtagAndReadFails_NextEditKeepsOldEtag()
    {
        Queue("evt-single", OutboxOperation.Patch, """{"summary":"A"}""");
        Queue("evt-single", OutboxOperation.Patch, """{"summary":"B"}""");
        _h.Google.On(HttpMethod.Patch, SingleUrl, HttpStatusCode.OK, """{"id":"evt-single","status":"confirmed"}""", once: true);
        _h.Google.On(r => r.Method == HttpMethod.Get && r.Uri.AbsoluteUri == SingleUrl, _ => throw new HttpRequestException("No network."));
        _h.Google.On(HttpMethod.Patch, SingleUrl, HttpStatusCode.PreconditionFailed, "{}");

        await Send();

        var patches = _h.Google.Requests.Where(r => r.Method == HttpMethod.Patch).ToList();
        Assert.Equal(2, patches.Count);
        Assert.All(patches, p => Assert.Equal(BaseEtag, p.IfMatch));
    }

    [Fact]
    public async Task Send_Rsvp_PatchesAttendeesOnGooglesLatestCopy()
    {
        Queue("evt-single", OutboxOperation.Rsvp, """{"responseStatus":"declined","comment":null}""");
        _h.Google.On(HttpMethod.Get, SingleUrl, HttpStatusCode.OK, """{"id":"evt-single","etag":"\"G7\"","status":"confirmed","attendees":[{"email":"boss@example.com","organizer":true,"responseStatus":"accepted"},{"email":"leaf.tester@gmail.com","self":true,"responseStatus":"needsAction"}]}""");
        _h.Google.On(HttpMethod.Patch, SingleUrl, HttpStatusCode.OK, """{"id":"evt-single","etag":"\"G8\"","status":"confirmed"}""");

        await Send();

        var patch = _h.Google.Requests.Single(r => r.Method == HttpMethod.Patch);
        Assert.Equal("\"G7\"", patch.IfMatch);
        using var body = JsonDocument.Parse(patch.Body!);
        Assert.Equal(["attendees"], body.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("declined", body.RootElement.GetProperty("attendees")[1].GetProperty("responseStatus").GetString());
        Assert.Equal("accepted", body.RootElement.GetProperty("attendees")[0].GetProperty("responseStatus").GetString());
    }

    [Fact]
    public async Task Send_Move_AppliesGoogleVersionInDestination()
    {
        using (var conn = _h.Db.Database.Open())
        {
            OutboxStore.Add(conn, null, new OutboxEntry(0, Account, Primary, "evt-single", OutboxOperation.Move, Family, BaseEtag, false, "[]", null));
            EventStore.MoveCalendar(conn, null, Account, Primary, Family, "evt-single");
        }

        _h.Google.On(HttpMethod.Post, SingleUrl + "/move", HttpStatusCode.OK, """{"id":"evt-single","etag":"\"M1\"","status":"confirmed","start":{"dateTime":"2026-10-01T13:00:00Z"},"end":{"dateTime":"2026-10-01T14:00:00Z"}}""");

        await Send();

        Assert.Equal("\"M1\"", Get("evt-single", Family)!.Etag);
        Assert.Null(Get("evt-single"));
    }
}
