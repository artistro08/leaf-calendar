using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class OutboxStoreTests : IDisposable
{
    private const string Calendar = "leaf.tester@gmail.com";
    private static readonly string Account = TestDatabase.SampleAccount.Id;

    private readonly TestDatabase _db = new();

    public OutboxStoreTests()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
    }

    public void Dispose() => _db.Dispose();

    private static OutboxEntry Entry(string eventId, OutboxOperation operation = OutboxOperation.Patch, string? etag = "\"1\"") =>
        new(0, Account, Calendar, eventId, operation, """{"summary":"x"}""", etag, SendUpdates: false, BeforeJson: "[]", NotBefore: null);

    [Fact]
    public void Add_ThenPending_ReturnsEntriesInOrder()
    {
        using var conn = _db.Database.Open();
        var first = OutboxStore.Add(conn, null, Entry("a"));
        var second = OutboxStore.Add(conn, null, Entry("b", OutboxOperation.Delete) with { SendUpdates = true, NotBefore = DateTimeOffset.UnixEpoch.AddDays(1) });

        var pending = OutboxStore.Pending(conn, Account);

        Assert.Equal([first, second], pending.Select(e => e.Seq));
        Assert.Equal(OutboxOperation.Delete, pending[1].Operation);
        Assert.True(pending[1].SendUpdates);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddDays(1), pending[1].NotBefore);
        Assert.Equal("\"1\"", pending[0].BaseEtag);
        Assert.Equal("[]", pending[0].BeforeJson);
    }

    [Fact]
    public void Rebase_UpdatesOnlyLaterEntriesForThatEvent()
    {
        using var conn = _db.Database.Open();
        var first = OutboxStore.Add(conn, null, Entry("a"));
        var second = OutboxStore.Add(conn, null, Entry("a"));
        var other = OutboxStore.Add(conn, null, Entry("b"));

        OutboxStore.Rebase(conn, null, Account, Calendar, "a", first, "\"2\"");

        Assert.Equal("\"1\"", OutboxStore.Get(conn, null, first)!.BaseEtag);
        Assert.Equal("\"2\"", OutboxStore.Get(conn, null, second)!.BaseEtag);
        Assert.Equal("\"1\"", OutboxStore.Get(conn, null, other)!.BaseEtag);
    }

    [Fact]
    public void RecordAttempt_CountsAndKeepsError()
    {
        using var conn = _db.Database.Open();
        var seq = OutboxStore.Add(conn, null, Entry("a"));

        OutboxStore.RecordAttempt(conn, seq, "network");
        OutboxStore.RecordAttempt(conn, seq, "status 503");

        var entry = OutboxStore.Get(conn, null, seq)!;
        Assert.Equal(2, entry.Attempts);
        Assert.Equal("status 503", entry.LastError);
    }

    [Fact]
    public void ConflictAdd_MovesEntryOutOfPending()
    {
        using var conn = _db.Database.Open();
        var seq = OutboxStore.Add(conn, null, Entry("a"));

        ConflictStore.Add(conn, null, seq, """{"id":"a"}""", null, DateTimeOffset.UnixEpoch);

        Assert.Empty(OutboxStore.Pending(conn, Account));
        var conflict = Assert.Single(ConflictStore.GetAll(conn));
        Assert.Equal(seq, conflict.Entry.Seq);
        Assert.Equal(OutboxState.Conflict, conflict.Entry.State);
        Assert.Null(conflict.GoogleJson);
        Assert.Equal(1, ConflictStore.Count(conn));
        Assert.Equal(1, OutboxStore.CountForAccount(conn, Account));
    }

    [Fact]
    public void Requeue_ClearsConflictState()
    {
        using var conn = _db.Database.Open();
        var seq = OutboxStore.Add(conn, null, Entry("a"));
        ConflictStore.Add(conn, null, seq, null, """{"id":"a","etag":"\"9\""}""", DateTimeOffset.UnixEpoch);

        ConflictStore.Remove(conn, null, seq);
        OutboxStore.Requeue(conn, null, seq, "\"9\"");

        Assert.Equal("\"9\"", Assert.Single(OutboxStore.Pending(conn, Account)).BaseEtag);
        Assert.Equal(0, ConflictStore.Count(conn));
    }

    [Fact]
    public void EventIdsFor_ListsPendingAndConflictedEventsOfThatCalendar()
    {
        using var conn = _db.Database.Open();
        OutboxStore.Add(conn, null, Entry("a"));
        var conflicted = OutboxStore.Add(conn, null, Entry("b"));
        ConflictStore.Add(conn, null, conflicted, null, null, DateTimeOffset.UnixEpoch);
        OutboxStore.Add(conn, null, Entry("c") with { CalendarId = "other" });

        Assert.Equal(["a", "b"], OutboxStore.EventIdsFor(conn, null, Account, Calendar).Order());
    }

    [Fact]
    public void AccountDelete_RemovesItsOutbox()
    {
        using var conn = _db.Database.Open();
        OutboxStore.Add(conn, null, Entry("a"));

        AccountStore.Delete(conn, Account);

        Assert.Equal(0, OutboxStore.Count(conn));
    }

    [Fact]
    public void AccountDelete_WithACopyStillUnsent_PutsBackTheOriginalWaitingBehindIt()
    {
        const string Original = """{"id":"evt-a","status":"confirmed","summary":"Mine","start":{"dateTime":"2026-10-01T13:00:00Z"},"end":{"dateTime":"2026-10-01T14:00:00Z"}}""";
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount with { Id = "other-account" });
        CalendarStore.ReplaceForAccount(conn, Account, [new CalendarListEntry { Id = Calendar, Summary = "Mine", AccessRole = "owner" }]);
        var create = OutboxStore.Add(conn, null, Entry("copy", OutboxOperation.Create) with { AccountId = "other-account" });
        OutboxStore.Add(conn, null, Entry("evt-a", OutboxOperation.Delete) with { BeforeJson = $"[{Original}]", DependsOn = create });

        AccountStore.Delete(conn, "other-account");

        Assert.Equal(0, OutboxStore.Count(conn));
        Assert.NotNull(EventStore.Get(conn, Account, Calendar, "evt-a"));
    }
}
