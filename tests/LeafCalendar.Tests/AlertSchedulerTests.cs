using System.Text.Json;
using LeafCalendar.Core.Alerts;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;
using Microsoft.Data.Sqlite;
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
    public void Check_Replan_ReportsWhatsAhead()
    {
        var scheduler = Scheduler();
        var plans     = new List<PlanSummary>();
        scheduler.Planned += (_, plan) => plans.Add(plan);

        At(17, 45);
        scheduler.Check();
        scheduler.Check();

        // The 17:50 reminder and the 18:00 "Join now"; a pass on the cached plan reports nothing
        var plan = Assert.Single(plans);
        Assert.Equal(2, plan.Ahead);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 17, 50, 0, TimeSpan.Zero), plan.Next);
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
    public void Check_WokeAfterMeetingEndedOutsideTheLookBack_SkipsIt()
    {
        var scheduler = Scheduler();
        At(17, 40);
        scheduler.Check();

        // Asleep From 17:40 To 19:30: the reminder and the join are both past the look-back, and the meeting is over
        At(19, 30);
        scheduler.Check();

        Assert.Empty(_due);
    }

    [Fact]
    public void Check_WokeNinetyMinutesIntoALongMeeting_ShowsJoinNowPastTheLookBack()
    {
        // A Two-Hour Meeting (18:00-20:00Z): its join came due 90 minutes before the wake, and it's still running
        Store(Meeting.Replace("T19:00", "T20:00", StringComparison.Ordinal));
        var scheduler = Scheduler();
        At(17, 40);
        scheduler.Check();

        At(19, 30);
        scheduler.Check();

        Assert.Equal(AlertKind.JoinNow, Assert.Single(_due).Kind);
    }

    [Fact]
    public void Check_MeetingLongerThanADay_KeepsItsJoinNow()
    {
        // Sep 30 12:00Z To Oct 2 12:00Z: started more than a day before the pass
        Store(Meeting.Replace("2026-10-01T18:00:00Z", "2026-09-30T12:00:00Z", StringComparison.Ordinal).Replace("2026-10-01T19:00:00Z", "2026-10-02T12:00:00Z", StringComparison.Ordinal));
        var scheduler = Scheduler();
        At(17, 0);
        scheduler.Check();
        scheduler.Check();

        Assert.Equal(AlertKind.JoinNow, Assert.Single(_due).Kind);
        Assert.Empty(_retracted);
    }

    [Fact]
    public void Check_MeetingReacceptedAfterRetract_ShowsJoinNowAgain()
    {
        var scheduler = Scheduler();
        At(17, 59);
        scheduler.Check();
        At(18, 0, 5);
        scheduler.Check();

        // Declined, Then Accepted Again
        Store(Meeting.Replace("\"hangoutLink\"", "\"attendees\":[{\"email\":\"leaf.tester@gmail.com\",\"self\":true,\"responseStatus\":\"declined\"}],\"hangoutLink\"", StringComparison.Ordinal));
        scheduler.Invalidate();
        At(18, 5);
        scheduler.Check();
        Store(Meeting.Replace("\"hangoutLink\"", "\"attendees\":[{\"email\":\"leaf.tester@gmail.com\",\"self\":true,\"responseStatus\":\"accepted\"}],\"hangoutLink\"", StringComparison.Ordinal));
        scheduler.Invalidate();
        At(18, 10);
        scheduler.Check();

        Assert.Equal(2, _due.Count(a => a.Kind == AlertKind.JoinNow));
        Assert.Single(_retracted);
    }

    [Fact]
    public void Check_MeetingMovedBack_ShowsJoinNowAgain()
    {
        var scheduler = Scheduler();
        At(17, 59);
        scheduler.Check();
        At(18, 0, 5);
        scheduler.Check();

        // Moved To 20:00, Then Back To 18:00
        Store(Meeting.Replace("T18:00", "T20:00", StringComparison.Ordinal).Replace("T19:00", "T21:00", StringComparison.Ordinal));
        scheduler.Invalidate();
        At(18, 5);
        scheduler.Check();
        Store(Meeting);
        scheduler.Invalidate();
        At(18, 10);
        scheduler.Check();

        Assert.Equal(2, _due.Count(a => a.Kind == AlertKind.JoinNow));
        Assert.Single(_retracted);
    }

    [Fact]
    public void Check_MeetingCanceledAfterJoinNow_RetractsItsToast()
    {
        var scheduler = Scheduler();
        At(17, 59);
        scheduler.Check();
        At(18, 0, 5);
        scheduler.Check();
        var join = Assert.Single(_due, a => a.Kind == AlertKind.JoinNow);

        Store(Meeting.Replace("\"confirmed\"", "\"cancelled\"", StringComparison.Ordinal));
        scheduler.Invalidate();
        scheduler.Check();

        Assert.Equal([join.Tag], _retracted);
    }

    [Fact]
    public void Check_MeetingDeletedAfterJoinNow_RetractsItsToast()
    {
        var scheduler = Scheduler();
        At(17, 59);
        scheduler.Check();
        At(18, 0, 5);
        scheduler.Check();
        var join = Assert.Single(_due, a => a.Kind == AlertKind.JoinNow);

        using (var conn = _db.Database.Open())
        {
            EventStore.Remove(conn, null, Account, Primary, "evt-meet");
        }

        scheduler.Invalidate();
        scheduler.Check();

        Assert.Equal([join.Tag], _retracted);
    }

    [Fact]
    public void Check_ZoneChanged_RePlansInTheNewZone()
    {
        // An All-Day Offsite On Oct 2 With A 60-Minute Popup: 23:00Z Oct 1 in UTC, 03:00Z Oct 2 in New York
        Store("""
            {"id":"evt-offsite","status":"confirmed","summary":"Offsite","start":{"date":"2026-10-02"},"end":{"date":"2026-10-03"},
             "reminders":{"useDefault":false,"overrides":[{"method":"popup","minutes":60}]}}
            """);
        var tz        = TimeZoneInfo.Utc;
        var scheduler = new AlertScheduler(_db.Database, _time, () => tz);
        scheduler.AlertDue += (_, alert) => _due.Add(alert);
        _schedulers.Add(scheduler);
        At(22, 0);
        scheduler.Check();

        // Travelled To New York Overnight
        tz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 2, 3, 0, 5, TimeSpan.Zero));
        scheduler.Check();

        Assert.Equal("evt-offsite", Assert.Single(_due).Occurrence.EventId);
    }

    [Fact]
    public void Check_ClockSetBack_RePlansAndAsksForSync()
    {
        // A Meeting On Sep 29, Two Days Before The First Pass (FakeTimeProvider can't go back, so a settable clock)
        Store(Meeting.Replace("evt-meet", "evt-earlier", StringComparison.Ordinal).Replace("2026-10-01", "2026-09-29", StringComparison.Ordinal));
        var clock     = new SettableClock { Now = new DateTimeOffset(2026, 10, 1, 17, 0, 0, TimeSpan.Zero) };
        var scheduler = new AlertScheduler(_db.Database, clock, () => TimeZoneInfo.Utc);
        scheduler.AlertDue += (_, alert) => _due.Add(alert);
        scheduler.SyncSoon += (_, _) => _syncSoon++;
        _schedulers.Add(scheduler);
        scheduler.Check();

        // Clock Corrected Back To Sep 29
        clock.Now = new DateTimeOffset(2026, 9, 29, 17, 49, 5, TimeSpan.Zero);
        scheduler.Check();
        Assert.Equal(1, _syncSoon);

        clock.Now = new DateTimeOffset(2026, 9, 29, 17, 50, 5, TimeSpan.Zero);
        scheduler.Check();

        Assert.Equal("evt-earlier", Assert.Single(_due).Occurrence.EventId);
    }

    [Fact]
    public void Check_HandlerThrows_TheRestOfThePassStillRaises()
    {
        // A Second Meeting At The Same Time, So One Pass Has Two Alerts And Two Retracts
        Store(Meeting.Replace("evt-meet", "evt-other", StringComparison.Ordinal));
        var failures  = new List<Exception>();
        var raised    = new List<string>();
        var scheduler = new AlertScheduler(_db.Database, _time, () => TimeZoneInfo.Utc);
        scheduler.Failed += (_, ex) => failures.Add(ex);
        _schedulers.Add(scheduler);
        At(17, 59);
        scheduler.Check();
        At(18, 0, 5);
        scheduler.Check();

        // Both Meetings Moved To 17:00, And Toasts Now Fail To Show
        scheduler.AlertDue += (_, alert) =>
        {
            raised.Add(alert.Occurrence.EventId);
            throw new NotSupportedException("Toast failed");
        };
        scheduler.AlertRetracted += (_, tag) => raised.Add(tag);
        Store(Meeting.Replace("T18:00", "T17:00", StringComparison.Ordinal));
        Store(Meeting.Replace("evt-meet", "evt-other", StringComparison.Ordinal).Replace("T18:00", "T17:00", StringComparison.Ordinal));
        scheduler.Invalidate();
        At(18, 1);
        scheduler.Check();

        Assert.Equal(2, failures.Count);
        Assert.Equal(4, raised.Count);
    }

    [Fact]
    public async Task Invalidate_WhileAPassIsRunning_DoesNotWaitForIt()
    {
        // A Pass Held Inside Its State Lock (the zone is read there, like the database)
        using var inPass  = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var scheduler     = new AlertScheduler(_db.Database, _time, () =>
        {
            inPass.Set();
            release.Wait();
            return TimeZoneInfo.Utc;
        });
        _schedulers.Add(scheduler);
        var ct   = TestContext.Current.CancellationToken;
        var pass = Task.Run(scheduler.Check, ct);
        inPass.Wait(ct);

        // An Edit On The UI Thread
        var invalidate = Task.Run(scheduler.Invalidate, ct);
        var first      = await Task.WhenAny(invalidate, Task.Delay(TimeSpan.FromSeconds(2), ct));
        release.Set();
        await pass;

        Assert.Same(invalidate, first);
    }

    [Fact]
    public void Check_RePlanFails_PlansAgainOnTheNextPass()
    {
        // The Meeting A Day Later (Oct 2 18:00Z, reminder 17:50Z): past the end of the first plan
        Store(Meeting.Replace("2026-10-01", "2026-10-02", StringComparison.Ordinal));
        var scheduler = Scheduler();
        At(17, 45);
        scheduler.Check();

        // The Plan Runs Out While The Events Can't Be Read
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 2, 17, 44, 0, TimeSpan.Zero));
        using (var conn = _db.Database.Open())
        {
            conn.Execute(null, "ALTER TABLE events RENAME TO events_away;");
        }

        Assert.Throws<SqliteException>(scheduler.Check);

        using (var conn = _db.Database.Open())
        {
            conn.Execute(null, "ALTER TABLE events_away RENAME TO events;");
        }

        _time.SetUtcNow(new DateTimeOffset(2026, 10, 2, 17, 50, 5, TimeSpan.Zero));
        scheduler.Check();

        Assert.Equal(AlertKind.Reminder, Assert.Single(_due).Kind);
    }

    [Fact]
    public void Check_FailsAfterRecordingAnAlert_StillShowsIt()
    {
        // An Old Row For The Pass To Prune, And Deletes From The Ledger Fail
        using (var conn = _db.Database.Open())
        {
            var ended = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
            AlertLedger.TryAdd(conn, "Reminder|old", AlertKind.Reminder, "old", ended, ended);
            conn.Execute(null, "CREATE TRIGGER ledger_delete_fails BEFORE DELETE ON alert_ledger BEGIN SELECT RAISE(ABORT, 'delete failed'); END;");
        }

        var scheduler = Scheduler();
        At(17, 50, 5);
        Assert.Throws<SqliteException>(scheduler.Check);

        using (var conn = _db.Database.Open())
        {
            conn.Execute(null, "DROP TRIGGER ledger_delete_fails;");
        }

        scheduler.Check();

        // The Ledger Has The Reminder, So This Was Its Only Chance To Show
        Assert.Equal(AlertKind.Reminder, Assert.Single(_due).Kind);
    }

    [Fact]
    public void PlanFrom_LongRunningEventWithoutALink_StaysADayBack()
    {
        // A Timed "Project phase" Block Since May, Still Running, With No Meeting Link (so no "Join now" to look for)
        Store("""
            {"id":"evt-phase","status":"confirmed","summary":"Project phase",
             "start":{"dateTime":"2026-05-01T09:00:00Z"},"end":{"dateTime":"2026-11-01T09:00:00Z"}}
            """);
        var now        = new DateTimeOffset(2026, 10, 1, 17, 0, 0, TimeSpan.Zero);
        using var conn = _db.Database.Open();

        Assert.Equal(now - TimeSpan.FromDays(1), AlertScheduler.PlanFrom(conn, now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void PlanFrom_LongRunningMeetingWithALink_GoesBackToItsStart()
    {
        Store(Meeting.Replace("2026-10-01T18:00:00Z", "2026-05-01T09:00:00Z", StringComparison.Ordinal).Replace("2026-10-01T19:00:00Z", "2026-11-01T09:00:00Z", StringComparison.Ordinal));
        var now        = new DateTimeOffset(2026, 10, 1, 17, 0, 0, TimeSpan.Zero);
        using var conn = _db.Database.Open();

        Assert.True(AlertScheduler.PlanFrom(conn, now, TimeZoneInfo.Utc) < new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Start_TimerTicks_RaiseOnTheirOwn()
    {
        At(17, 49, 50);
        Scheduler().Start();

        _time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(AlertKind.Reminder, Assert.Single(_due).Kind);
    }

    sealed class SettableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
