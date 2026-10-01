using System.Text.Json.Nodes;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Editing;
using LeafCalendar.Core.Events;
using LeafCalendar.LiveTests.Support;

namespace LeafCalendar.LiveTests;

public class LiveEditTests
{
    const string SkipReason = "Live account not set up. Run LiveSignInTests once (see its comment).";

    static EventEditor Editor(LiveAccount live) => new(live.Database, TimeProvider.System) { LocalZoneId = "America/New_York" };

    // Guard: Nothing Is Sent Unless Every Queued Change Targets A Calendar This Run Created
    static async Task Sync(LiveAccount live, LiveGoogle google, CancellationToken ct)
    {
        using (var conn = live.Database.Open())
        {
            foreach (var entry in OutboxStore.Pending(conn, live.AccountId))
            {
                google.RequireOwned(entry.CalendarId);
                if (entry.Operation == OutboxOperation.Move)
                {
                    google.RequireOwned(entry.Payload!);
                }
            }
        }

        // Sync Until Every Test Calendar Is Stored
        // A failed account sync is only logged, and would otherwise surface later as a foreign key error on the first edit
        for (var attempt = 1; ; attempt++)
        {
            await live.Services.Sync.SyncAccountAsync(live.AccountId, ct);

            using var conn = live.Database.Open();
            var stored = CalendarStore.GetForAccount(conn, live.AccountId).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            if (google.Owned.All(stored.Contains))
            {
                return;
            }

            if (attempt == 3)
            {
                var failure = File.Exists(live.Log.FilePath) ? File.ReadLines(live.Log.FilePath).LastOrDefault(l => l.Contains("failed", StringComparison.Ordinal)) : null;
                throw new InvalidOperationException($"Sync didn't store the test calendars after {attempt} tries. Last logged failure: {failure ?? "none"}");
            }
        }
    }

    // Deletes each calendar on its own so one failure neither skips the others nor hides the test's own error
    static async Task Cleanup(LiveGoogle google, params string?[] calendarIds)
    {
        foreach (var id in calendarIds.OfType<string>())
        {
            try
            {
                await google.DeleteCalendarAsync(id, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Only the ID and error type are logged; delete this calendar by hand if it is still there
                Console.Error.WriteLine($"Live test calendar {id} was not deleted: {ex.GetType().Name}");
            }
        }
    }

    static CalendarOccurrence Occurrence(LiveAccount live, string calendarId, string eventId, DateTimeOffset start, DateTimeOffset end, string? recurringEventId = null) =>
        new(live.AccountId, calendarId, eventId, null, recurringEventId, start, end, false, "", EventKind.Default, ResponseStatus.Accepted, "#4285F4", null, false, false);

    static CalendarOccurrence Stored(LiveAccount live, string calendarId, string eventId)
    {
        using var conn = live.Database.Open();
        var stored = EventStore.Get(conn, live.AccountId, calendarId, eventId)!;
        return Occurrence(live, calendarId, eventId, stored.Start!.Value, stored.End!.Value);
    }

    [Fact]
    public async Task Outbox_CreatePatchDelete_ReachGoogle()
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
            await Sync(live, google, ct);
            var editor = Editor(live);
            var start  = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(2).AddHours(15), TimeSpan.Zero);

            // Create (client-generated ID)
            var id = editor.Create(new EventDraft { AccountId = live.AccountId, CalendarId = calendarId, Title = "Leaf live outbox", Start = start, End = start.AddHours(1), TimeZone = "America/New_York" }, sendUpdates: false);
            await Sync(live, google, ct);
            Assert.Equal("Leaf live outbox", (string?)(await google.GetEventAsync(calendarId, id, ct))!["summary"]);

            // Patch (If-Match with the ETag Google returned for the create)
            var o      = Occurrence(live, calendarId, id, start, start.AddHours(1));
            var before = editor.Load(o);
            editor.Save(o, before, before with { Title = "Leaf live outbox (renamed)" }, EditScope.This, sendUpdates: false);
            await Sync(live, google, ct);
            Assert.Equal("Leaf live outbox (renamed)", (string?)(await google.GetEventAsync(calendarId, id, ct))!["summary"]);

            // Delete (held for the undo window first)
            editor.Delete([o], EditScope.This, sendUpdates: false);
            await Task.Delay(EventEditor.UndoWindow + TimeSpan.FromSeconds(1), ct);
            await Sync(live, google, ct);
            Assert.Equal("cancelled", (string?)(await google.GetEventAsync(calendarId, id, ct))?["status"] ?? "cancelled");
        }
        finally
        {
            await Cleanup(google, calendarId);
        }
    }

    [Fact]
    public async Task Create_WithMeet_GetsAMeetLink()
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
            await Sync(live, google, ct);
            var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(2).AddHours(15), TimeSpan.Zero);

            // Create With Meet (conferenceData.createRequest goes out with conferenceDataVersion=1)
            var id = Editor(live).Create(new EventDraft { AccountId = live.AccountId, CalendarId = calendarId, Title = "Leaf live meet", Start = start, End = start.AddHours(1), TimeZone = "America/New_York", HasConference = true }, sendUpdates: false);
            await Sync(live, google, ct);

            var link = (string?)(await google.GetEventAsync(calendarId, id, ct))!["hangoutLink"];
            Assert.StartsWith("https://meet.google.com/", link, StringComparison.Ordinal);
        }
        finally
        {
            await Cleanup(google, calendarId);
        }
    }

    // Ruling R15: a late undo never sends the old conference ID; a deleted event that had Meet comes back with a new link
    [Fact]
    public async Task LateUndo_OfAMeetEvent_ComesBackWithANewMeetLink()
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
            await Sync(live, google, ct);
            var editor = Editor(live);
            var start  = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(2).AddHours(15), TimeSpan.Zero);

            // Create With Meet
            var id = editor.Create(new EventDraft { AccountId = live.AccountId, CalendarId = calendarId, Title = "Leaf live late undo", Start = start, End = start.AddHours(1), TimeZone = "America/New_York", HasConference = true }, sendUpdates: false);
            await Sync(live, google, ct);
            var oldLink = (string?)(await google.GetEventAsync(calendarId, id, ct))!["hangoutLink"];
            Assert.StartsWith("https://meet.google.com/", oldLink, StringComparison.Ordinal);

            // Delete, Past The Undo Window, Sent
            var receipt = editor.Delete([Stored(live, calendarId, id)], EditScope.This, sendUpdates: false);
            await Task.Delay(EventEditor.UndoWindow + TimeSpan.FromSeconds(1), ct);
            await Sync(live, google, ct);
            Assert.Equal("cancelled", (string?)(await google.GetEventAsync(calendarId, id, ct))?["status"] ?? "cancelled");

            // Late Undo: a quiet copy with a new ID
            Assert.Equal(UndoResult.Recreated, editor.Undo(receipt));
            string copyId;
            using (var conn = live.Database.Open())
            {
                copyId = OutboxStore.Pending(conn, live.AccountId).Single(e => e.Operation == OutboxOperation.Create).EventId;
            }

            await Sync(live, google, ct);
            var copy = (await google.GetEventAsync(calendarId, copyId, ct))!;
            Assert.NotEqual(id, copyId);
            Assert.Equal("Leaf live late undo", (string?)copy["summary"]);
            var newLink = (string?)copy["hangoutLink"];
            Assert.StartsWith("https://meet.google.com/", newLink, StringComparison.Ordinal);
            Assert.NotEqual(oldLink, newLink);
        }
        finally
        {
            await Cleanup(google, calendarId);
        }
    }

    [Fact]
    public async Task StaleEtag_BecomesConflict_KeepMineWins()
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
            var id = await google.InsertEventAsync(calendarId, "Leaf live conflict", ct);
            await Sync(live, google, ct);
            var o = Stored(live, calendarId, id);

            // Google changes it behind Leaf's back, then Leaf edits its older copy
            await google.PatchSummaryAsync(calendarId, id, "Changed on Google", ct);
            var editor = Editor(live);
            var before = editor.Load(o);
            editor.Save(o, before, before with { Title = "Mine" }, EditScope.This, sendUpdates: false);
            await Sync(live, google, ct);

            var resolver = new ConflictResolver(live.Database, TimeProvider.System);
            var conflict = Assert.Single(resolver.GetAll(), c => c.Entry.EventId == id);
            Assert.Contains("Changed on Google", conflict.GoogleJson!, StringComparison.Ordinal);

            resolver.KeepMine(conflict);
            await Sync(live, google, ct);

            Assert.Equal("Mine", (string?)(await google.GetEventAsync(calendarId, id, ct))!["summary"]);
        }
        finally
        {
            await Cleanup(google, calendarId);
        }
    }

    [Fact]
    public async Task RepeatingSeries_ThisEventAndThisAndFollowing_MatchGoogle()
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
            // Mondays and Wednesdays, 9:00 New York (14:00 UTC after the DST change), six times: Nov 2, 4, 9, 11, 16, 18
            var masterId = await google.InsertRecurringEventAsync(calendarId, "2026-11-02T09:00:00", "2026-11-02T09:30:00", "America/New_York", ["RRULE:FREQ=WEEKLY;BYDAY=MO,WE;COUNT=6"], ct);
            await Sync(live, google, ct);
            var editor = Editor(live);
            static DateTimeOffset At(int day) => new(2026, 11, day, 14, 0, 0, TimeSpan.Zero);

            // This Event: Nov 9 an hour later
            var third  = Occurrence(live, calendarId, masterId, At(9), At(9).AddMinutes(30), masterId);
            var before = editor.Load(third);
            editor.Save(third, before, before with { Start = At(9).AddHours(1), End = At(9).AddHours(1.5) }, EditScope.This, sendUpdates: false);
            await Sync(live, google, ct);
            Assert.Contains(At(9).AddHours(1), await google.ListInstanceStartsAsync(calendarId, masterId, ct));

            // This And Following: from Nov 16, renamed
            var fifth = Occurrence(live, calendarId, masterId, At(16), At(16).AddMinutes(30), masterId);
            before = editor.Load(fifth);
            editor.Save(fifth, before, before with { Title = "Leaf live split" }, EditScope.Following, sendUpdates: false);
            string newSeries;
            using (var conn = live.Database.Open())
            {
                newSeries = OutboxStore.Pending(conn, live.AccountId).Single(e => e.Operation == OutboxOperation.Create).EventId;
            }

            await Sync(live, google, ct);

            Assert.Equal(4, (await google.ListInstanceStartsAsync(calendarId, masterId, ct)).Count);
            Assert.Equal([At(16), At(18)], await google.ListInstanceStartsAsync(calendarId, newSeries, ct));
            Assert.Equal("Leaf live split", (string?)(await google.GetEventAsync(calendarId, newSeries, ct))!["summary"]);
        }
        finally
        {
            await Cleanup(google, calendarId);
        }
    }

    [Fact]
    public async Task ReplyAndMoveToAnotherCalendar_ReachGoogle()
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
        string? guestId = null;
        string? otherId = null;
        try
        {
            guestId = await google.CreateTestCalendarAsync(ct);
            otherId = await google.CreateTestCalendarAsync(ct);
            var id = await google.InsertInviteAsync(calendarId, guestId, "Leaf live reply", ct);
            await Sync(live, google, ct);
            var editor = Editor(live);

            // Reply With A Note From The Guest's Copy (sent against Google's latest copy)
            editor.Respond(Stored(live, guestId, id), ResponseStatus.Tentative, "Live note", sendUpdates: false, EditScope.This);
            await Sync(live, google, ct);
            var self = (await google.GetEventAsync(guestId, id, ct))!["attendees"]!.AsArray().Single(a => (bool?)a!["self"] == true)!;
            Assert.Equal("tentative", (string?)self["responseStatus"]);
            Assert.Equal("Live note", (string?)self["comment"]);

            // Move The Organizer's Copy To The Other Calendar (events.move)
            var o      = Stored(live, calendarId, id);
            var before = editor.Load(o);
            editor.Save(o, before, before with { CalendarId = otherId }, EditScope.This, sendUpdates: false);
            await Sync(live, google, ct);
            Assert.Equal("Leaf live reply", (string?)(await google.GetEventAsync(otherId, id, ct))!["summary"]);
        }
        finally
        {
            await Cleanup(google, calendarId, guestId, otherId);
        }
    }
}
