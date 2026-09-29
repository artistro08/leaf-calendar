using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class CalendarPreferencesTests : IDisposable
{
    const string Primary = "leaf.tester@gmail.com";
    const string Family  = "family123@group.calendar.google.com";
    static readonly string Account = TestDatabase.SampleAccount.Id;

    readonly TestDatabase _db = new();

    public CalendarPreferencesTests()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        CalendarStore.ReplaceForAccount(conn, Account, Entries("calendar-list.json"));
    }

    public void Dispose() => _db.Dispose();

    static List<CalendarListEntry> Entries(string fixture) =>
        JsonSerializer.Deserialize(Fixture.Read(fixture), GoogleJsonContext.Default.CalendarListPage)!.Items;

    CalendarInfo Get(string id)
    {
        using var conn = _db.Database.Open();
        return CalendarStore.GetAll(conn).Single(c => c.Id == id);
    }

    [Fact]
    public void ReplaceForAccount_UnselectedInGoogle_StartsHidden()
    {
        using var conn = _db.Database.Open();
        var entries = Entries("calendar-list.json");
        entries.Add(new CalendarListEntry { Id = "holidays@group.v.calendar.google.com", Summary = "Holidays", AccessRole = "reader", Selected = false });

        CalendarStore.ReplaceForAccount(conn, Account, entries);

        Assert.False(CalendarStore.GetAll(conn).Single(c => c.Id.StartsWith("holidays", StringComparison.Ordinal)).IsVisible);
        Assert.True(Get(Primary).IsVisible);
    }

    [Fact]
    public void SetHidden_ThenGoogleListRefresh_KeepsLeafChoice()
    {
        using (var conn = _db.Database.Open())
        {
            CalendarStore.SetHidden(conn, Account, Family, hidden: true);
            CalendarStore.ReplaceForAccount(conn, Account, Entries("calendar-list.json"));
        }

        Assert.False(Get(Family).IsVisible);
    }

    [Fact]
    public void SetColor_OverridesGoogleColor_AndNullResets()
    {
        using var conn = _db.Database.Open();

        CalendarStore.SetColor(conn, Account, Family, "#16A765");
        Assert.Equal("#16A765", CalendarStore.GetAll(conn).Single(c => c.Id == Family).DisplayColor);

        CalendarStore.SetColor(conn, Account, Family, null);
        Assert.Equal("#f83a22", CalendarStore.GetAll(conn).Single(c => c.Id == Family).DisplayColor);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("#1234567")]
    public void SetColor_NotHex_Throws(string color)
    {
        using var conn = _db.Database.Open();

        Assert.Throws<ArgumentException>(() => CalendarStore.SetColor(conn, Account, Family, color));
    }

    [Fact]
    public void Reorder_ThenGoogleListRefresh_KeepsOrder()
    {
        using (var conn = _db.Database.Open())
        {
            CalendarStore.Reorder(conn, Account, [Family, Primary]);
            CalendarStore.ReplaceForAccount(conn, Account, Entries("calendar-list.json"));
        }

        using var check = _db.Database.Open();
        Assert.Equal([Family, Primary], CalendarStore.GetAll(check).Select(c => c.Id));
    }
}
