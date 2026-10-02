using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
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

    static CalendarListEntry Todoist(bool selected, bool hidden = false) =>
        new() { Id = "todoist@group.calendar.google.com", Summary = "Todoist", AccessRole = "reader", Selected = selected, Hidden = hidden };

    void Refresh(params CalendarListEntry[] extra)
    {
        using var conn = _db.Database.Open();
        CalendarStore.ReplaceForAccount(conn, Account, [.. Entries("calendar-list.json"), .. extra]);
    }

    bool Listed(string id)
    {
        using var conn = _db.Database.Open();
        return CalendarStore.GetAll(conn).Any(c => c.Id == id);
    }

    [Fact]
    public void ReplaceForAccount_HiddenFromGooglesList_IsNotListed()
    {
        Refresh(Todoist(selected: true, hidden: true));

        Assert.False(Listed(Todoist(true).Id));
        Assert.True(Listed(Primary));
    }

    [Fact]
    public void ReplaceForAccount_HiddenLater_IsKeptOutOfSight_AndComesBackAsItWas()
    {
        Refresh(Todoist(selected: true));
        using (var conn = _db.Database.Open())
        {
            CalendarStore.SetColor(conn, Account, Todoist(true).Id, "#16A765");
            EventStore.ApplyJson(conn, null, Account, Todoist(true).Id, """{"id":"task","status":"confirmed","summary":"Task","start":{"date":"2026-10-01"},"end":{"date":"2026-10-02"}}""");
        }

        // Hidden: not listed, its events not shown or searched, but kept (an edit waiting for Google isn't dropped)
        Refresh(Todoist(selected: true, hidden: true));
        Assert.False(Listed(Todoist(true).Id));
        using (var conn = _db.Database.Open())
        {
            Assert.Equal(1, EventStore.Count(conn, Account, Todoist(true).Id));
            Assert.Contains(CalendarStore.GetForAccount(conn, Account), c => c.Id == Todoist(true).Id && c.Hidden && !c.IsVisible);
            Assert.DoesNotContain(OccurrenceQuery.Load(conn, new DateOnly(2026, 9, 30), new DateOnly(2026, 10, 3), TimeZoneInfo.Utc, includeDeclined: true), o => o.CalendarId == Todoist(true).Id);
        }

        // Shown Again: back with its color
        Refresh(Todoist(selected: true));
        var back = Get(Todoist(true).Id);
        Assert.True(back.IsVisible);
        Assert.Equal("#16A765", back.DisplayColor);
    }

    [Fact]
    public void LegacyRow_HiddenInLeaf_StaysHidden_WhileGoogleShowsIt()
    {
        // A row from before Leaf recorded Google's choice (google_shown null), hidden in Leaf: Google ticking it doesn't
        // bring it back
        Refresh(Todoist(selected: true));
        using (var conn = _db.Database.Open())
        {
            CalendarStore.SetHidden(conn, Account, Todoist(true).Id, hidden: true);
            conn.Execute(null, "UPDATE calendars SET google_shown = NULL;");
        }

        Refresh(Todoist(selected: true));

        Assert.False(Get(Todoist(true).Id).IsVisible);
    }

    [Fact]
    public void LegacyRow_ShownInLeaf_HidesWhenGoogleHasItOff()
    {
        Refresh(Todoist(selected: true));
        using (var conn = _db.Database.Open())
        {
            conn.Execute(null, "UPDATE calendars SET google_shown = NULL;");
        }

        Refresh(Todoist(selected: false));

        Assert.False(Get(Todoist(true).Id).IsVisible);
    }

    [Fact]
    public void ReplaceForAccount_TurnedOffInGoogle_HidesInLeaf()
    {
        Refresh(Todoist(selected: true));
        Assert.True(Get(Todoist(true).Id).IsVisible);

        Refresh(Todoist(selected: false));

        Assert.False(Get(Todoist(true).Id).IsVisible);
    }

    [Fact]
    public void ReplaceForAccount_TurnedOnInGoogle_ShowsInLeaf()
    {
        Refresh(Todoist(selected: false));
        Assert.False(Get(Todoist(true).Id).IsVisible);

        Refresh(Todoist(selected: true));

        Assert.True(Get(Todoist(true).Id).IsVisible);
    }

    [Fact]
    public void ReplaceForAccount_GoogleUnchanged_KeepsLeafsChoiceToShowIt()
    {
        // Off in Google, but shown in Leaf on purpose: a refresh that doesn't change Google's choice leaves it shown
        Refresh(Todoist(selected: false));
        using (var conn = _db.Database.Open())
        {
            CalendarStore.SetHidden(conn, Account, Todoist(true).Id, hidden: false);
        }

        Refresh(Todoist(selected: false));

        Assert.True(Get(Todoist(true).Id).IsVisible);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    public void IsEnabledInGoogle_TickedAndNotHidden(bool selected, bool hidden, bool expected) =>
        Assert.Equal(expected, CalendarStore.IsEnabledInGoogle(Todoist(selected, hidden)));
}
