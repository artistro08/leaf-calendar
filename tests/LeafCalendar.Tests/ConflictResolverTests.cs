using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Editing;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class ConflictResolverTests : IDisposable
{
    const string Calendar = "leaf.tester@gmail.com";
    const string Mine     = """{"id":"evt-single","etag":"\"1\"","status":"confirmed","summary":"Mine","start":{"dateTime":"2026-10-01T13:00:00Z"},"end":{"dateTime":"2026-10-01T14:00:00Z"}}""";
    const string Googles  = """{"id":"evt-single","etag":"\"G9\"","status":"confirmed","summary":"Google's","location":"Room 9","start":{"dateTime":"2026-10-01T15:00:00Z"},"end":{"dateTime":"2026-10-01T16:00:00Z"}}""";

    static readonly string Account = TestDatabase.SampleAccount.Id;
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    readonly TestDatabase _db = new();
    readonly ConflictResolver _resolver;

    public ConflictResolverTests()
    {
        _resolver = new ConflictResolver(_db.Database);

        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        CalendarStore.ReplaceForAccount(conn, Account, JsonSerializer.Deserialize(Fixture.Read("calendar-list.json"), GoogleJsonContext.Default.CalendarListPage)!.Items);
        EventStore.ApplyJson(conn, null, Account, Calendar, Mine);
    }

    public void Dispose() => _db.Dispose();

    ConflictInfo Conflict(OutboxOperation operation, string? local, string? google)
    {
        using var conn = _db.Database.Open();
        var seq = OutboxStore.Add(conn, null, new OutboxEntry(0, Account, Calendar, "evt-single", operation, operation == OutboxOperation.Patch ? """{"summary":"Mine"}""" : null, "\"1\"", false, EventStore.Snapshot(conn, null, Account, Calendar, "evt-single"), null));
        if (operation == OutboxOperation.Delete)
        {
            EventStore.Remove(conn, null, Account, Calendar, "evt-single");
        }

        ConflictStore.Add(conn, null, seq, local, google, DateTimeOffset.UnixEpoch);
        return ConflictStore.GetAll(conn).Single();
    }

    StoredEvent? Get(string id)
    {
        using var conn = _db.Database.Open();
        return EventStore.Get(conn, Account, Calendar, id);
    }

    IReadOnlyList<OutboxEntry> Pending()
    {
        using var conn = _db.Database.Open();
        return OutboxStore.Pending(conn, Account);
    }

    [Fact]
    public void Compare_ChangedTitleWhenAndLocation_FlagsOnlyThose()
    {
        var fields = ConflictDiff.Compare(Mine, Googles, NewYork, use24h: false);

        Assert.Equal(["Title", "When", "Location", "Description", "Guests", "Repeats", "Color"], fields.Select(f => f.Field));
        Assert.Equal(["Title", "When", "Location"], fields.Where(f => f.Differs).Select(f => f.Field));
        Assert.Equal("Mine", fields[0].Mine);
        Assert.Equal("Google's", fields[0].Google);
        Assert.Equal("Thursday, October 1 · 11 AM – 12 PM", fields[1].Google);
    }

    [Fact]
    public void Compare_GoogleDeleted_SaysSo()
    {
        var fields = ConflictDiff.Compare(Mine, null, NewYork, use24h: false);

        Assert.Equal("Google's copy isn't available", fields[0].Google);
        Assert.True(fields[0].Differs);
    }

    [Fact]
    public void KeepMine_RequeuesOnGooglesEtag()
    {
        _resolver.KeepMine(Conflict(OutboxOperation.Patch, Mine, Googles));

        Assert.Equal("\"G9\"", Assert.Single(Pending()).BaseEtag);
        Assert.Equal((0, 1), _resolver.Counts());
    }

    [Fact]
    public void KeepMine_GoogleDeleted_ComesBackAsNewEvent()
    {
        _resolver.KeepMine(Conflict(OutboxOperation.Patch, Mine, null));

        var entry = Assert.Single(Pending());
        Assert.Equal(OutboxOperation.Create, entry.Operation);
        Assert.NotEqual("evt-single", entry.EventId);
        Assert.DoesNotContain("etag", entry.Payload!, StringComparison.Ordinal);
        Assert.Null(Get("evt-single"));
        Assert.Contains("\"Mine\"", Get(entry.EventId)!.RawJson, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepGoogles_AppliesGoogleVersionAndDropsLaterEdits()
    {
        var conflict = Conflict(OutboxOperation.Patch, Mine, Googles);
        using (var conn = _db.Database.Open())
        {
            OutboxStore.Add(conn, null, new OutboxEntry(0, Account, Calendar, "evt-single", OutboxOperation.Patch, """{"location":"Mine too"}""", "\"1\"", false, "[]", null));
        }

        _resolver.KeepGoogles(conflict);

        Assert.Empty(Pending());
        Assert.Equal((0, 0), _resolver.Counts());
        Assert.Contains("Google's", Get("evt-single")!.RawJson, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepGoogles_GoogleDeleted_RemovesLocalCopy()
    {
        _resolver.KeepGoogles(Conflict(OutboxOperation.Patch, Mine, null));

        Assert.Null(Get("evt-single"));
    }

    [Fact]
    public void KeepGoogles_LocalDelete_BringsGooglesVersionBack()
    {
        _resolver.KeepGoogles(Conflict(OutboxOperation.Delete, null, Googles));

        Assert.Contains("Google's", Get("evt-single")!.RawJson, StringComparison.Ordinal);
        Assert.Empty(Pending());
    }

    [Fact]
    public void UnsentFor_CountsPendingAndConflicted()
    {
        Conflict(OutboxOperation.Patch, Mine, Googles);
        using (var conn = _db.Database.Open())
        {
            OutboxStore.Add(conn, null, new OutboxEntry(0, Account, Calendar, "evt-other", OutboxOperation.Delete, null, null, false, "[]", null));
        }

        Assert.Equal(2, _resolver.UnsentFor(Account));
        Assert.Equal((1, 1), _resolver.Counts());
    }

    [Fact]
    public void LostResponse_GoogleAlreadyHasTheChange_ResolvesAsSent()
    {
        var google = """{"id":"evt-single","etag":"\"G9\"","status":"confirmed","summary":"Mine","start":{"dateTime":"2026-10-01T13:00:00Z"},"end":{"dateTime":"2026-10-01T14:00:00Z"}}""";
        Conflict(OutboxOperation.Patch, Mine, google);

        Assert.Empty(_resolver.GetAll());
        Assert.Equal((0, 0), _resolver.Counts());
        Assert.Equal("\"G9\"", Get("evt-single")!.Etag);
    }
}
