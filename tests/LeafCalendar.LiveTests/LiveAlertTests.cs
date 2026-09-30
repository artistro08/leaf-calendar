using System.Text.Json.Nodes;
using LeafCalendar.Core.Alerts;
using LeafCalendar.Core.Data;
using LeafCalendar.LiveTests.Support;

namespace LeafCalendar.LiveTests;

public class LiveAlertTests
{
    const string SkipReason = "Live account not set up. Run LiveSignInTests once (see its comment).";

    [Fact]
    public async Task Plan_RealGoogle_UsesTheCalendarDefaultAndTheEventsOverrides()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var live = LiveAccount.TryLoad();
        if (live is null)
        {
            Assert.Skip(SkipReason);
            return;
        }

        var google     = new LiveGoogle(live);
        var calendarId = await google.CreateTestCalendarAsync(ct);
        try
        {
            // Arrange On Google: a 7-minute calendar default, one event on it, one with a 3-minute override
            await google.SetDefaultRemindersAsync(calendarId, 7, ct);
            var start      = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(2).AddHours(15), TimeSpan.Zero);
            var onDefault  = await google.InsertEventWithRemindersAsync(calendarId, start, new JsonObject { ["useDefault"] = true }, ct);
            var overridden = await google.InsertEventWithRemindersAsync(calendarId, start.AddHours(2), new JsonObject
            {
                ["useDefault"] = false,
                ["overrides"]  = new JsonArray(new JsonObject { ["method"] = "popup", ["minutes"] = 3 }),
            }, ct);

            // Sync, And Show The Test Calendar In Leaf
            await live.Services.Sync.SyncAccountAsync(live.AccountId, ct);
            using var conn = live.Database.Open();
            CalendarStore.SetHidden(conn, live.AccountId, calendarId, false);

            var alerts = AlertPlanner.Plan(conn, start.AddHours(-1), start.AddHours(3), TimeZoneInfo.Utc)
                .Where(a => a.Occurrence.CalendarId == calendarId)
                .ToList();

            Assert.Contains(alerts, a => a.Occurrence.EventId == onDefault && a.FireAt == start.AddMinutes(-7));
            Assert.Contains(alerts, a => a.Occurrence.EventId == overridden && a.FireAt == start.AddHours(2).AddMinutes(-3));
            Assert.DoesNotContain(alerts, a => a.Occurrence.EventId == overridden && a.MinutesBefore == 7);
        }
        finally
        {
            await google.DeleteCalendarAsync(calendarId, CancellationToken.None);
        }
    }
}
