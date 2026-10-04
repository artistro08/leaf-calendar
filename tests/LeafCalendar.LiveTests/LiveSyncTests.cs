using System.Text.Json;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Recurrence;
using LeafCalendar.LiveTests.Support;

namespace LeafCalendar.LiveTests;

public class LiveSyncTests
{
    [Fact]
    public async Task Sync_CreateUpdateDelete_MirrorsGoogle()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var live = LiveAccount.TryLoad();
        if (live is null)
        {
            Assert.Skip("Live account not set up. Run LiveSignInTests once (see its comment).");
            return;
        }

        var google = new LiveGoogle(live);
        var calendarId = await google.CreateTestCalendarAsync(ct);
        try
        {
            // Create
            var eventId = await google.InsertEventAsync(calendarId, "Leaf live create", ct);
            await live.Services.Sync.SyncAccountAsync(live.AccountId, ct);
            Assert.Contains("Leaf live create", Get(live, calendarId, eventId)!.RawJson, StringComparison.Ordinal);

            // Update (incremental)
            var token = SyncToken(live, calendarId);
            await google.PatchSummaryAsync(calendarId, eventId, "Leaf live update", ct);
            await live.Services.Sync.SyncAccountAsync(live.AccountId, ct);
            Assert.Contains("Leaf live update", Get(live, calendarId, eventId)!.RawJson, StringComparison.Ordinal);
            Assert.NotEqual(token, SyncToken(live, calendarId));

            // Delete
            await google.DeleteEventAsync(calendarId, eventId, ct);
            await live.Services.Sync.SyncAccountAsync(live.AccountId, ct);
            Assert.Null(Get(live, calendarId, eventId));
        }
        finally
        {
            await google.DeleteCalendarAsync(calendarId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Expand_WeeklyAcrossDstWithExdate_MatchesGoogleInstances()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var live = LiveAccount.TryLoad();
        if (live is null)
        {
            Assert.Skip("Live account not set up. Run LiveSignInTests once (see its comment).");
            return;
        }

        var google = new LiveGoogle(live);
        var calendarId = await google.CreateTestCalendarAsync(ct);
        try
        {
            var eventId = await google.InsertRecurringEventAsync(
                calendarId,
                "2026-10-26T09:00:00",
                "2026-10-26T09:30:00",
                "America/New_York",
                ["RRULE:FREQ=WEEKLY;BYDAY=MO,WE;COUNT=6", "EXDATE;TZID=America/New_York:20261104T090000"],
                ct);
            var expected = await google.ListInstanceStartsAsync(calendarId, eventId, ct);

            // Expand from what Leaf synced, exactly as the views will
            await live.Services.Sync.SyncAccountAsync(live.AccountId, ct);
            using var raw = JsonDocument.Parse(Get(live, calendarId, eventId)!.RawJson);
            var start = raw.RootElement.GetProperty("start");
            var recurrence = raw.RootElement.GetProperty("recurrence").EnumerateArray().Select(r => r.GetString()!).ToList();

            var actual = RecurrenceExpander.ExpandTimed(
                recurrence,
                start.GetProperty("dateTime").GetDateTimeOffset(),
                start.GetProperty("timeZone").GetString(),
                DateTimeOffset.MinValue,
                DateTimeOffset.MaxValue);

            Assert.Equal(expected, actual.Select(a => a.ToUniversalTime()));
        }
        finally
        {
            await google.DeleteCalendarAsync(calendarId, CancellationToken.None);
        }
    }

    private static StoredEvent? Get(LiveAccount live, string calendarId, string eventId)
    {
        using var conn = live.Database.Open();
        return EventStore.Get(conn, live.AccountId, calendarId, eventId);
    }

    private static string? SyncToken(LiveAccount live, string calendarId)
    {
        using var conn = live.Database.Open();
        return CalendarStore.GetForAccount(conn, live.AccountId).Single(c => c.Id == calendarId).SyncToken;
    }
}
