using System.Text.Json;
using LeafCalendar.Core.Alerts;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class AlertPlannerTests : IDisposable
{
    const string Primary = "leaf.tester@gmail.com";
    static readonly string Account = TestDatabase.SampleAccount.Id;
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    readonly TestDatabase _db = new();

    public AlertPlannerTests()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        CalendarStore.ReplaceForAccount(conn, Account, JsonSerializer.Deserialize(Fixture.Read("calendar-list.json"), GoogleJsonContext.Default.CalendarListPage)!.Items);
        foreach (var fixture in new[] { "events-page1.json", "events-page2.json" })
        {
            foreach (var item in JsonSerializer.Deserialize(Fixture.Read(fixture), GoogleJsonContext.Default.EventsPage)!.Items)
            {
                EventStore.Apply(conn, null, Account, Primary, item);
            }
        }
    }

    public void Dispose() => _db.Dispose();

    void Insert(string json)
    {
        using var conn = _db.Database.Open();
        using var doc  = JsonDocument.Parse(json);
        EventStore.Apply(conn, null, Account, Primary, doc.RootElement);
    }

    IReadOnlyList<Alert> Plan(DateTimeOffset from, DateTimeOffset to)
    {
        using var conn = _db.Database.Open();
        return AlertPlanner.Plan(conn, from, to, NewYork);
    }

    static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void Plan_EventOnDefaults_UsesTheCalendarsPopup()
    {
        // Dentist, Oct 1 9:00 EDT (13:00Z), no reminders field; the primary calendar's default is a 10-minute popup
        var alert = Assert.Single(Plan(Utc(10, 1, 12), Utc(10, 1, 14)));

        Assert.Equal(AlertKind.Reminder, alert.Kind);
        Assert.Equal("evt-single", alert.Occurrence.EventId);
        Assert.Equal(Utc(10, 1, 12, 50), alert.FireAt);
        Assert.Equal(10, alert.MinutesBefore);
        Assert.Null(alert.MeetingLink);
    }

    // One shared calendar in two more accounts: shown under "222" with no reminder, and under "333" with a 30-minute one
    const string Shared = "jazmin@group.calendar.google.com";

    void AddSharedCalendar(bool hiddenUnder333)
    {
        using var conn = _db.Database.Open();
        foreach (var (id, reminders) in new[] { ("222", new List<ReminderOverride>()), ("333", [new ReminderOverride { Method = "popup", Minutes = 30 }]) })
        {
            AccountStore.Upsert(conn, new Account(id, $"{id}@example.com", null, null, AccountStatus.Ok));
            CalendarStore.ReplaceForAccount(conn, id, [new CalendarListEntry { Id = Shared, Summary = "Jazmin", AccessRole = "reader", Selected = true, DefaultReminders = reminders }]);
        }

        CalendarStore.SetHidden(conn, "333", Shared, hiddenUnder333);
    }

    void InsertShared(string accountId)
    {
        using var conn = _db.Database.Open();
        EventStore.ApplyJson(conn, null, accountId, Shared, """
            {"id":"evt-shared","status":"confirmed","summary":"Dinner","start":{"dateTime":"2026-10-02T23:00:00Z"},"end":{"dateTime":"2026-10-03T00:00:00Z"},
             "reminders":{"useDefault":true}}
            """);
    }

    [Fact]
    public void Plan_SharedCalendar_RemindsFromTheShownCopyWithTheHiddenCopysDefault()
    {
        AddSharedCalendar(hiddenUnder333: true);
        InsertShared("222");
        InsertShared("333");

        var alert = Assert.Single(Plan(Utc(10, 2, 22), Utc(10, 2, 23)));

        Assert.Equal("222", alert.Occurrence.AccountId);
        Assert.Equal(Utc(10, 2, 22, 30), alert.FireAt);
        Assert.Equal(30, alert.MinutesBefore);
    }

    [Fact]
    public void Plan_SharedCalendarShownTwice_RemindsOnce()
    {
        AddSharedCalendar(hiddenUnder333: false);
        InsertShared("222");
        InsertShared("333");

        Assert.Single(Plan(Utc(10, 2, 22), Utc(10, 2, 23)));
    }

    [Fact]
    public void Plan_Overrides_ReplaceTheDefaultsAndSkipEmail()
    {
        Insert("""
            {"id":"evt-ovr","status":"confirmed","summary":"Overrides","start":{"dateTime":"2026-10-02T15:00:00Z"},"end":{"dateTime":"2026-10-02T16:00:00Z"},
             "reminders":{"useDefault":false,"overrides":[{"method":"popup","minutes":5},{"method":"email","minutes":30}]}}
            """);

        var alert = Assert.Single(Plan(Utc(10, 2, 14), Utc(10, 2, 16)));

        Assert.Equal(Utc(10, 2, 14, 55), alert.FireAt);
        Assert.Equal(5, alert.MinutesBefore);
    }

    [Fact]
    public void Plan_NoDefaultAndNoOverrides_NoReminder()
    {
        Insert("""
            {"id":"evt-quiet","status":"confirmed","summary":"Quiet","start":{"dateTime":"2026-10-02T15:00:00Z"},"end":{"dateTime":"2026-10-02T16:00:00Z"},
             "reminders":{"useDefault":false}}
            """);

        Assert.Empty(Plan(Utc(10, 2, 0), Utc(10, 2, 23)));
    }

    [Fact]
    public void Plan_AllDayOverride_CountsBackFromLocalMidnight()
    {
        // 900 minutes before Oct 3 00:00 EDT (04:00Z) is Oct 2 9:00 AM EDT (13:00Z), Google's "1 day before at 9 AM"
        Insert("""
            {"id":"evt-allday-ovr","status":"confirmed","summary":"Trip","start":{"date":"2026-10-03"},"end":{"date":"2026-10-04"},
             "reminders":{"useDefault":false,"overrides":[{"method":"popup","minutes":900}]}}
            """);

        var alert = Assert.Single(Plan(Utc(10, 2, 0), Utc(10, 3, 0)));

        Assert.Equal(Utc(10, 2, 13), alert.FireAt);
        Assert.True(alert.Occurrence.IsAllDay);
    }

    [Fact]
    public void Plan_AllDayOnDefaults_NoReminder()
    {
        // Company holiday, Oct 12, no reminders field: Google's API doesn't expose all-day defaults (the Monday standup still reminds)
        var alerts = Plan(Utc(10, 11, 0), Utc(10, 13, 0));

        Assert.DoesNotContain(alerts, a => a.Occurrence.EventId == "evt-allday");
        Assert.Contains(alerts, a => a.Occurrence.EventId == "evt-weekly");
    }

    [Fact]
    public void Plan_RepeatingAcrossDaylightSavingEnd_KeepsTheLocalTime()
    {
        // Team standup, 9:30 New York on Mon/Wed/Fri; DST ends Sunday Nov 1, 2026
        var standups = Plan(Utc(10, 30, 0), Utc(11, 3, 0)).Where(a => a.Occurrence.EventId == "evt-weekly").Select(a => a.FireAt).ToList();

        Assert.Equal([Utc(10, 30, 13, 20), Utc(11, 2, 14, 20)], standups);
    }

    [Fact]
    public void Plan_MeetingLink_AddsJoinNowAtStartAndTheLinkOnTheReminder()
    {
        Insert("""
            {"id":"evt-meet","status":"confirmed","summary":"Design review","hangoutLink":"https://meet.google.com/abc-defg-hij",
             "start":{"dateTime":"2026-10-02T18:00:00Z"},"end":{"dateTime":"2026-10-02T19:00:00Z"}}
            """);

        var alerts = Plan(Utc(10, 2, 17), Utc(10, 2, 19));

        Assert.Equal([AlertKind.Reminder, AlertKind.JoinNow], alerts.Select(a => a.Kind));
        Assert.Equal(Utc(10, 2, 17, 50), alerts[0].FireAt);
        Assert.Equal(Utc(10, 2, 18), alerts[1].FireAt);
        Assert.All(alerts, a => Assert.Equal("https://meet.google.com/abc-defg-hij", a.MeetingLink!.AbsoluteUri));
    }

    [Fact]
    public void Plan_UnknownMeetingHost_NoJoinNowAndNoJoinButton()
    {
        // A toast's Join opens blind (no window, no address), so only a known meeting host's link rides on an alert
        Insert("""
            {"id":"evt-odd","status":"confirmed","summary":"Odd","hangoutLink":"https://login-micros0ft.example/meet",
             "conferenceData":{"entryPoints":[{"entryPointType":"video","uri":"https://login-micros0ft.example/meet"}]},
             "start":{"dateTime":"2026-10-02T18:00:00Z"},"end":{"dateTime":"2026-10-02T19:00:00Z"}}
            """);

        var alert = Assert.Single(Plan(Utc(10, 2, 17), Utc(10, 2, 19)));

        Assert.Equal(AlertKind.Reminder, alert.Kind);
        Assert.Null(alert.MeetingLink);
    }

    [Fact]
    public void Plan_ColleaguesMeeting_NoJoinNow()
    {
        // On a colleague's calendar you can see, Google marks the colleague as "self"
        AddSharedCalendar(hiddenUnder333: true);
        using (var conn = _db.Database.Open())
        {
            EventStore.ApplyJson(conn, null, "222", Shared, """
                {"id":"evt-theirs","status":"confirmed","summary":"Theirs","hangoutLink":"https://meet.google.com/abc-defg-hij",
                 "attendees":[{"email":"jazmin@example.com","self":true,"responseStatus":"accepted"}],
                 "start":{"dateTime":"2026-10-02T18:00:00Z"},"end":{"dateTime":"2026-10-02T19:00:00Z"},"reminders":{"useDefault":false,"overrides":[{"method":"popup","minutes":10}]}}
                """);
        }

        var alerts = Plan(Utc(10, 2, 17), Utc(10, 2, 19));

        Assert.Equal([AlertKind.Reminder], alerts.Select(a => a.Kind));
    }

    [Fact]
    public void Plan_MissedRemindersOfARunningEvent_IncludedOnRequest()
    {
        // A timed block since May (10-minute default reminder, due long before the window); one that ended is left out
        Insert("""{"id":"evt-phase","status":"confirmed","summary":"Project phase","start":{"dateTime":"2026-05-01T09:00:00Z"},"end":{"dateTime":"2026-11-01T09:00:00Z"}}""");
        Insert("""{"id":"evt-done","status":"confirmed","summary":"Done","start":{"dateTime":"2026-05-01T09:00:00Z"},"end":{"dateTime":"2026-09-30T09:00:00Z"}}""");

        using var conn = _db.Database.Open();
        var missed = AlertPlanner.Plan(conn, Utc(10, 1, 0), Utc(10, 1, 1), NewYork, includeMissed: true);

        Assert.Equal(Utc(5, 1, 8, 50), Assert.Single(missed, a => a.Occurrence.EventId == "evt-phase").FireAt);
        Assert.DoesNotContain(missed, a => a.Occurrence.EventId == "evt-done");
        Assert.DoesNotContain(Plan(Utc(10, 1, 0), Utc(10, 1, 1)), a => a.Occurrence.EventId == "evt-phase");
    }

    [Fact]
    public void Plan_Declined_NoAlerts()
    {
        Insert("""
            {"id":"evt-no","status":"confirmed","summary":"Declined","hangoutLink":"https://meet.google.com/abc-defg-hij",
             "attendees":[{"email":"leaf.tester@gmail.com","self":true,"responseStatus":"declined"}],
             "start":{"dateTime":"2026-10-02T18:00:00Z"},"end":{"dateTime":"2026-10-02T19:00:00Z"}}
            """);

        Assert.Empty(Plan(Utc(10, 2, 17), Utc(10, 2, 19)));
    }

    [Fact]
    public void Plan_ChangedInstance_UsesItsOwnReminders()
    {
        Insert("""
            {"id":"evt-weekly_20261009T133000Z","status":"confirmed","recurringEventId":"evt-weekly","summary":"Standup",
             "originalStartTime":{"dateTime":"2026-10-09T09:30:00-04:00","timeZone":"America/New_York"},
             "start":{"dateTime":"2026-10-09T09:30:00-04:00"},"end":{"dateTime":"2026-10-09T10:00:00-04:00"},
             "reminders":{"useDefault":false,"overrides":[{"method":"popup","minutes":30}]}}
            """);

        var alert = Assert.Single(Plan(Utc(10, 9, 12), Utc(10, 9, 14)));

        Assert.Equal(Utc(10, 9, 13), alert.FireAt);
        Assert.Equal(30, alert.MinutesBefore);
    }

    [Fact]
    public void Plan_MinutesOutsideGooglesRange_Ignored()
    {
        Insert("""
            {"id":"evt-odd","status":"confirmed","summary":"Odd","start":{"dateTime":"2026-10-20T15:00:00Z"},"end":{"dateTime":"2026-10-20T16:00:00Z"},
             "reminders":{"useDefault":false,"overrides":[{"method":"popup","minutes":-5},{"method":"popup","minutes":50000},{"method":"popup","minutes":15},{"method":"popup","minutes":"x"}]}}
            """);

        var alert = Assert.Single(Plan(Utc(10, 20, 0), Utc(10, 21, 0)));

        Assert.Equal(15, alert.MinutesBefore);
    }

    [Fact]
    public void Plan_WindowIsExclusiveAtTheStartAndInclusiveAtTheEnd()
    {
        Assert.Empty(Plan(Utc(10, 1, 12, 50), Utc(10, 1, 13)));
        Assert.Single(Plan(Utc(10, 1, 12, 49), Utc(10, 1, 12, 50)));
    }

    [Fact]
    public void Plan_FourWeekReminder_IsFoundFromAWindowBeforeIt()
    {
        Insert("""
            {"id":"evt-far","status":"confirmed","summary":"Far","start":{"dateTime":"2026-11-20T15:00:00Z"},"end":{"dateTime":"2026-11-20T16:00:00Z"},
             "reminders":{"useDefault":false,"overrides":[{"method":"popup","minutes":40320}]}}
            """);

        var alert = Assert.Single(Plan(Utc(10, 23, 0), Utc(10, 24, 0)), a => a.Occurrence.EventId == "evt-far");

        Assert.Equal(Utc(10, 23, 15), alert.FireAt);
    }

    [Fact]
    public void Tag_IsShortStableHex()
    {
        Assert.Equal(Alert.TagFor("x"), Alert.TagFor("x"));
        Assert.NotEqual(Alert.TagFor("x"), Alert.TagFor("y"));
        Assert.Matches("^[0-9A-F]{16}$", Alert.TagFor("Reminder|a|b|c|1|10"));
    }
}
