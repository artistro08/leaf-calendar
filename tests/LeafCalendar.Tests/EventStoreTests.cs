using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class EventStoreTests : IDisposable
{
    const string Calendar = "leaf.tester@gmail.com";
    static readonly string Account = TestDatabase.SampleAccount.Id;

    readonly TestDatabase _db = new();

    public EventStoreTests()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        CalendarStore.ReplaceForAccount(
            conn,
            Account,
            JsonSerializer.Deserialize(Fixture.Read("calendar-list.json"), GoogleJsonContext.Default.CalendarListPage)!.Items);
    }

    public void Dispose() => _db.Dispose();

    static List<JsonElement> Items(string fixture) =>
        JsonSerializer.Deserialize(Fixture.Read(fixture), GoogleJsonContext.Default.EventsPage)!.Items;

    void ApplyAll(string fixture)
    {
        using var conn = _db.Database.Open();
        foreach (var item in Items(fixture))
        {
            EventStore.Apply(conn, null, Account, Calendar, item);
        }
    }

    [Fact]
    public void Apply_TimedEvent_StoresUtcRangeAndRawJson()
    {
        ApplyAll("events-page1.json");

        using var conn = _db.Database.Open();
        var stored = EventStore.Get(conn, Account, Calendar, "evt-single")!;

        Assert.Equal(new DateTimeOffset(2026, 10, 1, 13, 0, 0, TimeSpan.Zero), stored.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 14, 0, 0, TimeSpan.Zero), stored.End);
        Assert.False(stored.IsAllDay);
        Assert.Equal("\"3181161784712000\"", stored.Etag);
        Assert.Contains("Dentist appointment", stored.RawJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_AllDayEvent_StoresMidnightUtcDates()
    {
        ApplyAll("events-page1.json");

        using var conn = _db.Database.Open();
        var stored = EventStore.Get(conn, Account, Calendar, "evt-allday")!;

        Assert.True(stored.IsAllDay);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero), stored.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero), stored.End);
    }

    [Fact]
    public void Apply_CanceledStandalone_IsNotStored()
    {
        ApplyAll("events-page2.json");

        using var conn = _db.Database.Open();

        Assert.Null(EventStore.Get(conn, Account, Calendar, "evt-deleted"));
    }

    [Fact]
    public void Apply_CanceledOccurrence_KeptWithOriginalStart()
    {
        ApplyAll("events-page2.json");

        using var conn = _db.Database.Open();
        var occurrence = EventStore.Get(conn, Account, Calendar, "evt-weekly_20261007T133000Z")!;

        Assert.Equal("cancelled", occurrence.Status);
        Assert.Equal("evt-weekly", occurrence.RecurringEventId);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 13, 30, 0, TimeSpan.Zero), occurrence.OriginalStart);
        Assert.Null(occurrence.Start);
    }

    [Fact]
    public void Apply_CanceledMaster_RemovesMasterAndItsOccurrences()
    {
        ApplyAll("events-page2.json");

        using (var conn = _db.Database.Open())
        {
            using var doc = JsonDocument.Parse("""{"id":"evt-weekly","status":"cancelled"}""");
            EventStore.Apply(conn, null, Account, Calendar, doc.RootElement);
        }

        using var check = _db.Database.Open();
        Assert.Null(EventStore.Get(check, Account, Calendar, "evt-weekly"));
        Assert.Null(EventStore.Get(check, Account, Calendar, "evt-weekly_20261007T133000Z"));
    }

    [Fact]
    public void Apply_SameIdTwice_Updates()
    {
        ApplyAll("events-page1.json");
        ApplyAll("events-incremental.json");

        using var conn = _db.Database.Open();

        Assert.Equal(new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.Zero), EventStore.Get(conn, Account, Calendar, "evt-single")!.Start);
        Assert.Null(EventStore.Get(conn, Account, Calendar, "evt-allday"));
        Assert.Equal(2, EventStore.Count(conn, Account, Calendar));
    }

    [Fact]
    public void DeleteAllForCalendar_WithEvents_EmptiesOnlyThatCalendar()
    {
        ApplyAll("events-page1.json");
        using var conn = _db.Database.Open();

        EventStore.DeleteAllForCalendar(conn, null, Account, Calendar);

        Assert.Equal(0, EventStore.Count(conn, Account, Calendar));
    }

    [Fact]
    public void SnapshotRestore_SeriesWithException_RoundTrips()
    {
        ApplyAll("events-page2.json");
        using var conn = _db.Database.Open();
        var snapshot = EventStore.Snapshot(conn, null, Account, Calendar, "evt-weekly");

        EventStore.Remove(conn, null, Account, Calendar, "evt-weekly");
        Assert.Null(EventStore.Get(conn, Account, Calendar, "evt-weekly_20261007T133000Z"));

        EventStore.Restore(conn, null, Account, Calendar, "evt-weekly", snapshot);

        Assert.NotNull(EventStore.Get(conn, Account, Calendar, "evt-weekly"));
        Assert.Equal("cancelled", EventStore.Get(conn, Account, Calendar, "evt-weekly_20261007T133000Z")!.Status);
    }

    [Fact]
    public void Snapshot_MissingEvent_IsEmptyArray()
    {
        using var conn = _db.Database.Open();

        Assert.Equal("[]", EventStore.Snapshot(conn, null, Account, Calendar, "nope"));
    }

    [Fact]
    public void MoveCalendar_MovesSeriesAndExceptions()
    {
        ApplyAll("events-page2.json");
        using var conn = _db.Database.Open();

        EventStore.MoveCalendar(conn, null, Account, Calendar, "family123@group.calendar.google.com", "evt-weekly");

        Assert.Null(EventStore.Get(conn, Account, Calendar, "evt-weekly"));
        Assert.NotNull(EventStore.Get(conn, Account, "family123@group.calendar.google.com", "evt-weekly"));
        Assert.NotNull(EventStore.Get(conn, Account, "family123@group.calendar.google.com", "evt-weekly_20261007T133000Z"));
    }

    [Fact]
    public void RemoveExceptionsFrom_KeepsEarlierExceptions()
    {
        ApplyAll("events-page2.json");
        using var conn = _db.Database.Open();

        EventStore.RemoveExceptionsFrom(conn, null, Account, Calendar, "evt-weekly", new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero));
        Assert.NotNull(EventStore.Get(conn, Account, Calendar, "evt-weekly_20261007T133000Z"));

        EventStore.RemoveExceptionsFrom(conn, null, Account, Calendar, "evt-weekly", new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero));
        Assert.Null(EventStore.Get(conn, Account, Calendar, "evt-weekly_20261007T133000Z"));
    }

    [Fact]
    public void Apply_CanceledSeriesWithPendingEdit_KeepsLocalRow()
    {
        ApplyAll("events-page1.json");
        using var conn = _db.Database.Open();
        OutboxStore.Add(conn, null, new OutboxEntry(0, Account, Calendar, "evt-single", OutboxOperation.Patch, "{}", null, false, "[]", null));

        EventStore.ApplyJson(conn, null, Account, Calendar, """{"id":"evt-single","status":"cancelled"}""");

        Assert.NotNull(EventStore.Get(conn, Account, Calendar, "evt-single"));
    }
}
