using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Editing;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class ConflictResolverTests : IDisposable
{
    const string Calendar = "leaf.tester@gmail.com";
    const string Mine     = """{"id":"evt-single","etag":"\"1\"","status":"confirmed","summary":"Mine","start":{"dateTime":"2026-10-01T13:00:00Z"},"end":{"dateTime":"2026-10-01T14:00:00Z"}}""";
    const string Googles  = """{"id":"evt-single","etag":"\"G9\"","status":"confirmed","summary":"Google's","location":"Room 9","start":{"dateTime":"2026-10-01T15:00:00Z"},"end":{"dateTime":"2026-10-01T16:00:00Z"}}""";

    static readonly string Account = TestDatabase.SampleAccount.Id;
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    readonly TestDatabase _db = new();
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
    readonly ConflictResolver _resolver;

    public ConflictResolverTests()
    {
        _resolver = new ConflictResolver(_db.Database, _time);

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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_ClearsThatCalendarsSyncToken_SoSkippedChangesReload(bool keepMine)
    {
        const string Family = "family123@group.calendar.google.com";
        using (var conn = _db.Database.Open())
        {
            CalendarStore.SetSyncToken(conn, null, Account, Calendar, "token-primary");
            CalendarStore.SetSyncToken(conn, null, Account, Family, "token-family");
        }

        var conflict = Conflict(OutboxOperation.Patch, Mine, Googles);
        if (keepMine)
        {
            _resolver.KeepMine(conflict);
        }
        else
        {
            _resolver.KeepGoogles(conflict);
        }

        using var check = _db.Database.Open();
        var calendars = CalendarStore.GetForAccount(check, Account);
        Assert.Null(calendars.Single(c => c.Id == Calendar).SyncToken);
        Assert.Equal("token-family", calendars.Single(c => c.Id == Family).SyncToken);
    }

    const string NewSeries = """{"id":"leafsplit001","status":"confirmed","summary":"Standup v2","start":{"dateTime":"2026-10-09T13:30:00Z"},"end":{"dateTime":"2026-10-09T14:00:00Z"},"recurrence":["RRULE:FREQ=WEEKLY"]}""";

    // A split whose end (the conflicted patch) the new series waits behind
    ConflictInfo SplitConflict()
    {
        var conflict = Conflict(OutboxOperation.Patch, Mine, Googles);
        using var conn = _db.Database.Open();
        OutboxStore.Add(conn, null, new OutboxEntry(0, Account, Calendar, "leafsplit001", OutboxOperation.Create, NewSeries, null, false, "[]", null, DependsOn: conflict.Entry.Seq));
        EventStore.ApplyJson(conn, null, Account, Calendar, NewSeries);
        return conflict;
    }

    [Fact]
    public void KeepGoogles_OnASplitsEnd_DropsTheNewSeries()
    {
        var conflict = SplitConflict();
        Assert.Equal((1, 0), _resolver.Counts());

        _resolver.KeepGoogles(conflict);

        Assert.Empty(Pending());
        Assert.Null(Get("leafsplit001"));
        Assert.Contains("Google's", Get("evt-single")!.RawJson, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepMine_OnASplitsEnd_KeepsTheNewSeriesBehindIt()
    {
        var conflict = SplitConflict();

        _resolver.KeepMine(conflict);

        var entries = Pending();
        Assert.Equal(2, entries.Count);
        Assert.Equal(entries[0].Seq, entries[1].DependsOn);
        Assert.NotNull(Get("leafsplit001"));
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
    public void Counts_HeldDelete_WaitsOutTheUndoWindow()
    {
        using (var conn = _db.Database.Open())
        {
            OutboxStore.Add(conn, null, new OutboxEntry(0, Account, Calendar, "evt-other", OutboxOperation.Delete, null, null, false, "[]", _time.GetUtcNow().AddSeconds(6)));
        }

        Assert.Equal((0, 0), _resolver.Counts());
        Assert.Equal(1, _resolver.UnsentFor(Account));

        _time.Advance(TimeSpan.FromSeconds(6));

        Assert.Equal((0, 1), _resolver.Counts());
    }

    [Fact]
    public void Counts_EditBehindAConflict_IsNotWaiting()
    {
        Conflict(OutboxOperation.Patch, Mine, Googles);
        using (var conn = _db.Database.Open())
        {
            OutboxStore.Add(conn, null, new OutboxEntry(0, Account, Calendar, "evt-single", OutboxOperation.Patch, """{"location":"Mine too"}""", "\"1\"", false, "[]", null));
        }

        Assert.Equal((1, 0), _resolver.Counts());
        Assert.Equal(2, _resolver.UnsentFor(Account));
    }
}
