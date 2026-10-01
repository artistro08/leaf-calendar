using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class CalendarStoreTests : IDisposable
{
    readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    static List<CalendarListEntry> Entries(string fixture) =>
        JsonSerializer.Deserialize(Fixture.Read(fixture), GoogleJsonContext.Default.CalendarListPage)!.Items;

    [Fact]
    public void ReplaceForAccount_NewList_StoresInOrder()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);

        CalendarStore.ReplaceForAccount(conn, TestDatabase.SampleAccount.Id, Entries("calendar-list.json"));

        var calendars = CalendarStore.GetForAccount(conn, TestDatabase.SampleAccount.Id);
        Assert.Equal(["leaf.tester@gmail.com", "family123@group.calendar.google.com"], calendars.Select(c => c.Id));
        Assert.True(calendars[0].IsPrimary);
        Assert.Equal("Family", calendars[1].Summary);
        Assert.Equal("writer", calendars[1].AccessRole);
    }

    [Fact]
    public void ReplaceForAccount_Again_KeepsSyncTokenAndDropsMissingCalendars()
    {
        using var conn = _db.Database.Open();
        var accountId = TestDatabase.SampleAccount.Id;
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        CalendarStore.ReplaceForAccount(conn, accountId, Entries("calendar-list.json"));
        CalendarStore.SetSyncToken(conn, null, accountId, "leaf.tester@gmail.com", "sync-token-1");

        CalendarStore.ReplaceForAccount(conn, accountId, Entries("calendar-list-primary-only.json"));

        var calendar = Assert.Single(CalendarStore.GetForAccount(conn, accountId));
        Assert.Equal("sync-token-1", calendar.SyncToken);
    }

    [Fact]
    public void GetAll_HostileName_IsCleanedAndCapped()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        var hostile = Entries("calendar-list-primary-only.json");
        hostile[0].Summary = "Te‮am​\nPlans" + new string('x', 300);

        CalendarStore.ReplaceForAccount(conn, TestDatabase.SampleAccount.Id, hostile);

        var name = Assert.Single(CalendarStore.GetAll(conn)).Summary;
        Assert.StartsWith("Team Plans", name, StringComparison.Ordinal);
        Assert.Equal(100, name.Length);
    }

    [Fact]
    public void SetSummaryOverride_ShowsTheName_GoogleNameKept()
    {
        using var conn = _db.Database.Open();
        var accountId = TestDatabase.SampleAccount.Id;
        const string family = "family123@group.calendar.google.com";
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        CalendarStore.ReplaceForAccount(conn, accountId, Entries("calendar-list.json"));

        CalendarStore.SetSummaryOverride(conn, accountId, family, "Kids");
        var renamed = CalendarStore.GetAll(conn).Single(c => c.Id == family);
        Assert.Equal(("Kids", "Family"), (renamed.Summary, renamed.GoogleName));

        CalendarStore.SetSummaryOverride(conn, accountId, family, null);
        Assert.Equal("Family", CalendarStore.GetAll(conn).Single(c => c.Id == family).Summary);
    }

    [Fact]
    public void GetAll_HostileGoogleName_IsCleanedToo()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        var hostile = Entries("calendar-list-primary-only.json");
        hostile[0].Summary         = "Te‮am";
        hostile[0].SummaryOverride = "Mine";

        CalendarStore.ReplaceForAccount(conn, TestDatabase.SampleAccount.Id, hostile);

        var calendar = Assert.Single(CalendarStore.GetAll(conn));
        Assert.Equal(("Mine", "Team"), (calendar.Summary, calendar.GoogleName));
    }

    [Fact]
    public void SetDefaultReminders_FeedsThePopupDefaults()
    {
        using var conn = _db.Database.Open();
        var accountId = TestDatabase.SampleAccount.Id;
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        CalendarStore.ReplaceForAccount(conn, accountId, Entries("calendar-list.json"));

        CalendarStore.SetDefaultReminders(conn, accountId, "family123@group.calendar.google.com", """[{"method":"popup","minutes":30}]""");

        Assert.Equal([30], CalendarStore.PopupDefaults(conn)[(accountId, "family123@group.calendar.google.com")]);
        Assert.Equal("""[{"method":"popup","minutes":30}]""", CalendarStore.DefaultRemindersJson(conn, accountId, "family123@group.calendar.google.com"));
        Assert.Null(CalendarStore.DefaultRemindersJson(conn, accountId, "missing"));
    }

    [Fact]
    public void DeleteAccount_WithCalendars_CascadesToCalendars()
    {
        using var conn = _db.Database.Open();
        var accountId = TestDatabase.SampleAccount.Id;
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        CalendarStore.ReplaceForAccount(conn, accountId, Entries("calendar-list.json"));

        AccountStore.Delete(conn, accountId);

        Assert.Empty(CalendarStore.GetForAccount(conn, accountId));
    }
}
