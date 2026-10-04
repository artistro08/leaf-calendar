using System.Text.Json;
using LeafCalendar.Core.Alerts;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class AlertLedgerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 17, 0, 0, TimeSpan.Zero);

    private readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void TryAdd_SameKeyTwice_OnlyFirstCounts()
    {
        using var conn = _db.Database.Open();

        Assert.True(AlertLedger.TryAdd(conn, "Reminder|a|10", AlertKind.Reminder, "T1", Now.AddHours(1), Now));
        Assert.False(AlertLedger.TryAdd(conn, "Reminder|a|10", AlertKind.Reminder, "T1", Now.AddHours(1), Now));
        Assert.True(AlertLedger.Contains(conn, "Reminder|a|10"));
        Assert.False(AlertLedger.Contains(conn, "Reminder|a|5"));
    }

    [Fact]
    public void TryAdd_SurvivesReopeningTheDatabase()
    {
        using (var conn = _db.Database.Open())
        {
            AlertLedger.TryAdd(conn, "JoinNow|a|0", AlertKind.JoinNow, "T2", Now.AddHours(1), Now);
        }

        using var again = _db.Database.Open();
        Assert.False(AlertLedger.TryAdd(again, "JoinNow|a|0", AlertKind.JoinNow, "T2", Now.AddHours(1), Now));
    }

    [Fact]
    public void OpenJoinNow_ListsOnlyJoinNowNotYetRetracted()
    {
        using var conn = _db.Database.Open();
        AlertLedger.TryAdd(conn, "JoinNow|a|0", AlertKind.JoinNow, "TA", Now.AddHours(1), Now);
        AlertLedger.TryAdd(conn, "JoinNow|b|0", AlertKind.JoinNow, "TB", Now.AddHours(2), Now);
        AlertLedger.TryAdd(conn, "Reminder|a|10", AlertKind.Reminder, "TC", Now.AddHours(1), Now);

        AlertLedger.MarkRetracted(conn, "JoinNow|a|0");

        var open = Assert.Single(AlertLedger.OpenJoinNow(conn));
        Assert.Equal(new LedgerEntry("JoinNow|b|0", AlertKind.JoinNow, "TB", Now.AddHours(2)), open);
    }

    [Fact]
    public void Prune_RemovesEventsThatEndedBeforeTheCutoff()
    {
        using var conn = _db.Database.Open();
        AlertLedger.TryAdd(conn, "old", AlertKind.Reminder, "T", Now.AddDays(-3), Now);
        AlertLedger.TryAdd(conn, "new", AlertKind.Invite, "T", Now.AddDays(30), Now);

        AlertLedger.Prune(conn, Now.AddDays(-2));

        Assert.False(AlertLedger.Contains(conn, "old"));
        Assert.True(AlertLedger.Contains(conn, "new"));
    }

    [Fact]
    public void HasPrefix_MatchesTheStartOnly()
    {
        using var conn = _db.Database.Open();
        AlertLedger.TryAdd(conn, "Invite|acct|cal|evt_1|0", AlertKind.Invite, "T", Now.AddDays(1), Now);

        Assert.True(AlertLedger.HasPrefix(conn, "Invite|acct|cal|evt_1|"));
        Assert.False(AlertLedger.HasPrefix(conn, "Invite|acct|cal|evt%|"));
        Assert.False(AlertLedger.HasPrefix(conn, "Invite|acct|cal|evt_10|"));
    }

    [Fact]
    public void Marks_RoundTripAndStartEmpty()
    {
        using var conn = _db.Database.Open();

        Assert.Null(AlertLedger.GetMark(conn, "invites-seeded:1"));
        AlertLedger.SetMark(conn, "invites-seeded:1", 1);
        AlertLedger.SetMark(conn, "invites-seeded:1", 2);

        Assert.Equal(2, AlertLedger.GetMark(conn, "invites-seeded:1"));
    }

    [Fact]
    public void PopupDefaults_ReadsPopupMinutesPerCalendar()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        var entries = JsonSerializer.Deserialize(Fixture.Read("calendar-list.json"), GoogleJsonContext.Default.CalendarListPage)!.Items;
        entries[1].DefaultReminders = [new ReminderOverride { Method = "email", Minutes = 30 }, new ReminderOverride { Method = "popup", Minutes = 50000 }, new ReminderOverride { Method = "popup", Minutes = 5 }];
        CalendarStore.ReplaceForAccount(conn, TestDatabase.SampleAccount.Id, entries);

        var defaults = CalendarStore.PopupDefaults(conn);

        Assert.Equal([10], defaults[(TestDatabase.SampleAccount.Id, "leaf.tester@gmail.com")]);
        Assert.Equal([5], defaults[(TestDatabase.SampleAccount.Id, "family123@group.calendar.google.com")]);
    }

    [Fact]
    public void RemoveForAccount_ForgetsOnlyThatAccountsAlerts()
    {
        using var conn = _db.Database.Open();
        AlertLedger.TryAdd(conn, "Reminder|acct|cal|evt|1|10", AlertKind.Reminder, "T1", Now.AddDays(1), Now);
        AlertLedger.TryAdd(conn, "Invite|acct|cal|evt|0", AlertKind.Invite, "T2", Now.AddDays(1), Now);
        AlertLedger.TryAdd(conn, "JoinNow|acct2|cal|evt|1|0", AlertKind.JoinNow, "T3", Now.AddDays(1), Now);
        AlertLedger.TryAdd(conn, "Invite|acc|cal|evt|0", AlertKind.Invite, "T4", Now.AddDays(1), Now);

        AlertLedger.RemoveForAccount(conn, "acct");

        Assert.False(AlertLedger.Contains(conn, "Reminder|acct|cal|evt|1|10"));
        Assert.False(AlertLedger.Contains(conn, "Invite|acct|cal|evt|0"));
        Assert.True(AlertLedger.Contains(conn, "JoinNow|acct2|cal|evt|1|0"));
        Assert.True(AlertLedger.Contains(conn, "Invite|acc|cal|evt|0"));
    }

    [Fact]
    public void DeleteMarksStartingWith_ClearsOnlyMatchingMarks()
    {
        using var conn = _db.Database.Open();
        AlertLedger.SetMark(conn, "invites-seeded:1|cal-a", 1);
        AlertLedger.SetMark(conn, "invites-seeded:1|cal_b", 1);
        AlertLedger.SetMark(conn, "invites-seeded:10|cal-a", 1);

        AlertLedger.DeleteMarksStartingWith(conn, "invites-seeded:1|");

        Assert.Null(AlertLedger.GetMark(conn, "invites-seeded:1|cal-a"));
        Assert.Null(AlertLedger.GetMark(conn, "invites-seeded:1|cal_b"));
        Assert.Equal(1, AlertLedger.GetMark(conn, "invites-seeded:10|cal-a"));
    }
}
