using System.Text.Json;
using LeafCalendar.Core.Alerts;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class AlertSchedulerTests : IDisposable
{
    const string Primary = "leaf.tester@gmail.com";
    static readonly string Account = TestDatabase.SampleAccount.Id;

    // A Meet call 18:00-19:00Z on Oct 1; the primary calendar's default reminder is 10 minutes (17:50Z)
    const string Meeting = """
        {"id":"evt-meet","status":"confirmed","summary":"Design review","hangoutLink":"https://meet.google.com/abc-defg-hij",
         "start":{"dateTime":"2026-10-01T18:00:00Z"},"end":{"dateTime":"2026-10-01T19:00:00Z"}}
        """;

    readonly TestDatabase _db = new();
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 17, 0, 0, TimeSpan.Zero));
    readonly List<Alert> _due = [];
    readonly List<string> _retracted = [];
    readonly List<AlertScheduler> _schedulers = [];
    int _syncSoon;

    public AlertSchedulerTests()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        CalendarStore.ReplaceForAccount(conn, Account, JsonSerializer.Deserialize(Fixture.Read("calendar-list.json"), GoogleJsonContext.Default.CalendarListPage)!.Items);
        Store(Meeting);
    }

    public void Dispose()
    {
        foreach (var scheduler in _schedulers)
        {
            scheduler.Dispose();
        }

        _db.Dispose();
    }

    void Store(string json)
    {
        using var conn = _db.Database.Open();
        EventStore.ApplyJson(conn, null, Account, Primary, json);
    }

    AlertScheduler Scheduler()
    {
        var scheduler = new AlertScheduler(_db.Database, _time, () => TimeZoneInfo.Utc);
        scheduler.AlertDue       += (_, alert) => _due.Add(alert);
        scheduler.AlertRetracted += (_, tag) => _retracted.Add(tag);
        scheduler.SyncSoon       += (_, _) => _syncSoon++;
        scheduler.Failed         += (_, ex) => throw new InvalidOperationException("Scheduler failed", ex);
        _schedulers.Add(scheduler);
        return scheduler;
    }

    void At(int hour, int minute, int second = 0) => _time.SetUtcNow(new DateTimeOffset(2026, 10, 1, hour, minute, second, TimeSpan.Zero));

    [Fact]
    public void Check_AtReminderTime_RaisesTheReminderOnce()
    {
        var scheduler = Scheduler();
        At(17, 49, 50);
        scheduler.Check();
        Assert.Empty(_due);

        At(17, 50, 5);
        scheduler.Check();
        scheduler.Check();

        var alert = Assert.Single(_due);
        Assert.Equal(AlertKind.Reminder, alert.Kind);
        Assert.NotNull(alert.MeetingLink);
    }

    [Fact]
    public void Check_NewSchedulerAfterRestart_DoesNotRepeat()
    {
        var first = Scheduler();
        At(17, 45);
        first.Check();
        At(17, 50, 5);
        first.Check();
        Assert.Single(_due);
        first.Dispose();

        // Restart A Minute Later: the one-hour look-back sees the 17:50 reminder, and the ledger stops it
        At(17, 51);
        Scheduler().Check();

        Assert.Single(_due);
    }

    [Fact]
    public void Check_AtStart_RaisesJoinNowWithTheLink()
    {
        var scheduler = Scheduler();
        At(17, 55);
        scheduler.Check();

        At(18, 0, 5);
        scheduler.Check();

        var join = Assert.Single(_due, a => a.Kind == AlertKind.JoinNow);
        Assert.Equal("https://meet.google.com/abc-defg-hij", join.MeetingLink!.AbsoluteUri);
    }

    [Fact]
    public void Check_WokeAfterMeetingEnded_SkipsIt()
    {
        // A Half-Hour Meeting (18:00-18:30Z), so its reminder and join are still inside the look-back on wake
        Store(Meeting.Replace("T19:00", "T18:30", StringComparison.Ordinal));
        var scheduler = Scheduler();
        At(17, 40);
        scheduler.Check();

        // Asleep From 17:40 To 18:45: the reminder (17:50) and the join (18:00) both came due, but the meeting is over
        At(18, 45);
        scheduler.Check();

        Assert.Empty(_due);
    }

    [Fact]
    public void Check_WokeDuringMeeting_ShowsOnlyJoinNow()
    {
        var scheduler = Scheduler();
        At(17, 40);
        scheduler.Check();

        At(18, 20);
        scheduler.Check();

        var alert = Assert.Single(_due);
        Assert.Equal(AlertKind.JoinNow, alert.Kind);
    }

    [Fact]
    public void Check_StartedDuringMeeting_ShowsJoinNow()
    {
        At(18, 5);
        Scheduler().Check();

        Assert.Equal(AlertKind.JoinNow, Assert.Single(_due).Kind);
    }

    [Fact]
    public void Check_JoinNowTurnedOff_StillShowsTheReminderThatCameDue()
    {
        var scheduler = Scheduler();
        scheduler.IsEnabled = kind => kind != AlertKind.JoinNow;
        At(17, 40);
        scheduler.Check();

        At(18, 20);
        scheduler.Check();

        Assert.Equal(AlertKind.Reminder, Assert.Single(_due).Kind);
    }

    [Fact]
    public void Check_KindTurnedOff_NothingShows()
    {
        var scheduler = Scheduler();
        scheduler.IsEnabled = kind => kind == AlertKind.Invite;
        At(17, 45);
        scheduler.Check();

        At(18, 1);
        scheduler.Check();

        Assert.Empty(_due);
    }

    [Fact]
    public void Check_ReadsEachSettingOncePerPass()
    {
        var scheduler = Scheduler();
        var reads     = new List<AlertKind>();
        scheduler.IsEnabled = kind =>
        {
            reads.Add(kind);
            return true;
        };
        At(18, 20);

        scheduler.Check();

        Assert.Equal(Enum.GetValues<AlertKind>(), reads.Order());
    }

    [Fact]
    public void Check_OneMinuteBefore_AsksForOneSync()
    {
        var scheduler = Scheduler();
        At(17, 49, 5);
        scheduler.Check();
        Assert.Equal(1, _syncSoon);

        At(17, 49, 20);
        scheduler.Check();
        Assert.Equal(1, _syncSoon);
    }

    [Fact]
    public void Check_MeetingMovedAfterJoinNow_RetractsItsToast()
    {
        var scheduler = Scheduler();
        At(17, 59);
        scheduler.Check();
        At(18, 0, 5);
        scheduler.Check();
        var join = Assert.Single(_due, a => a.Kind == AlertKind.JoinNow);

        // Moved To 20:00 On Google
        Store(Meeting.Replace("T18:00", "T20:00", StringComparison.Ordinal).Replace("T19:00", "T21:00", StringComparison.Ordinal));
        scheduler.Invalidate();
        scheduler.Check();

        Assert.Equal([join.Tag], _retracted);
    }

    [Fact]
    public void Check_MeetingDeclinedAfterJoinNow_RetractsItsToast()
    {
        var scheduler = Scheduler();
        At(17, 59);
        scheduler.Check();
        At(18, 0, 5);
        scheduler.Check();

        Store(Meeting.Replace("\"hangoutLink\"", "\"attendees\":[{\"email\":\"leaf.tester@gmail.com\",\"self\":true,\"responseStatus\":\"declined\"}],\"hangoutLink\"", StringComparison.Ordinal));
        scheduler.Invalidate();
        scheduler.Check();

        Assert.Single(_retracted);
    }

    [Fact]
    public void Check_MeetingEnded_RetractsJoinNowOnce()
    {
        var scheduler = Scheduler();
        At(17, 59);
        scheduler.Check();
        At(18, 0, 5);
        scheduler.Check();

        At(19, 0, 5);
        scheduler.Check();
        scheduler.Check();

        Assert.Single(_retracted);
    }

    [Fact]
    public void Check_DaysAfterJoinNow_RetractsItBeforeTheLedgerForgetsIt()
    {
        var first = Scheduler();
        At(17, 59);
        first.Check();
        At(18, 0, 5);
        first.Check();
        var join = Assert.Single(_due, a => a.Kind == AlertKind.JoinNow);
        first.Dispose();

        // Leaf Off For Three Days: the ledger row is past its keep, and it's the only handle to the toast
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 4, 18, 0, 0, TimeSpan.Zero));
        Scheduler().Check();

        Assert.Equal([join.Tag], _retracted);
    }

    [Fact]
    public void Start_TimerTicks_RaiseOnTheirOwn()
    {
        At(17, 49, 50);
        Scheduler().Start();

        _time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(AlertKind.Reminder, Assert.Single(_due).Kind);
    }
}
