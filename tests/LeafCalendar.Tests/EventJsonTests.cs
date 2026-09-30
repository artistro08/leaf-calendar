using System.Text.Json.Nodes;
using LeafCalendar.Core.Editing;
using LeafCalendar.Core.Events;

namespace LeafCalendar.Tests;

public class EventJsonTests
{
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    const string Meeting = """
        {
          "id": "evt-1", "etag": "\"7\"", "status": "confirmed", "summary": "Design review", "location": "Room 4",
          "description": "<b>Agenda</b><br>Budget", "colorId": "5",
          "start": { "dateTime": "2026-10-01T14:00:00-04:00", "timeZone": "America/New_York" },
          "end": { "dateTime": "2026-10-01T15:00:00-04:00", "timeZone": "America/New_York" },
          "organizer": { "email": "boss@example.com" },
          "attendees": [
            { "email": "boss@example.com", "organizer": true, "responseStatus": "accepted" },
            { "email": "me@example.com", "self": true, "responseStatus": "needsAction" },
            { "email": "you@example.com", "optional": true, "responseStatus": "tentative", "comment": "Late" }
          ],
          "reminders": { "useDefault": false, "overrides": [ { "method": "email", "minutes": 60 }, { "method": "popup", "minutes": 10 } ] },
          "hangoutLink": "https://meet.google.com/abc-defg-hij"
        }
        """;

    static readonly DateTimeOffset Start = new(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);

    static EventDraft Read(string json = Meeting) => EventJson.ReadDraft("acct", "cal", json, Start, Start.AddHours(1), isAllDay: false);

    [Fact]
    public void ReadDraft_FullEvent_ReadsEveryField()
    {
        var draft = Read();

        Assert.Equal("Design review", draft.Title);
        Assert.Equal("Room 4", draft.Location);
        Assert.Equal("Agenda\nBudget", draft.Description);
        Assert.Equal("5", draft.ColorId);
        Assert.Equal("America/New_York", draft.TimeZone);
        Assert.False(draft.UseDefaultReminders);
        Assert.Equal([10], draft.ReminderMinutes);
        Assert.Equal(3, draft.Guests.Count);
        Assert.True(draft.Guests[0].IsOrganizer);
        Assert.True(draft.Guests[1].IsSelf);
        Assert.Equal(new Guest("you@example.com", Optional: true, Response: ResponseStatus.Tentative, Comment: "Late"), draft.Guests[2]);
        Assert.Equal(new Uri("https://meet.google.com/abc-defg-hij"), draft.ConferenceUri);
    }

    [Fact]
    public void BuildPatch_Unchanged_IsEmpty()
    {
        var draft = Read();

        Assert.Empty(EventJson.BuildPatch(draft, draft with { Guests = [.. draft.Guests] }));
    }

    [Fact]
    public void BuildPatch_TitleOnly_SendsOnlySummary()
    {
        var draft = Read();

        var patch = EventJson.BuildPatch(draft, draft with { Title = "Budget review" });

        Assert.Equal("""{"summary":"Budget review"}""", patch.ToJsonString());
    }

    [Fact]
    public void BuildPatch_MovedTimed_WritesZoneOffsetAndZone()
    {
        var draft = Read();

        var patch = EventJson.BuildPatch(draft, draft with { Start = Start.AddHours(1), End = Start.AddHours(2) });

        Assert.Equal("2026-10-01T15:00:00-04:00", (string?)patch["start"]!["dateTime"]);
        Assert.Equal("America/New_York", (string?)patch["start"]!["timeZone"]);
        Assert.Equal("2026-10-01T16:00:00-04:00", (string?)patch["end"]!["dateTime"]);
    }

    [Fact]
    public void BuildPatch_TimedToAllDay_ClearsDateTime()
    {
        var draft    = Read();
        var midnight = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        var patch = EventJson.BuildPatch(draft, draft with { IsAllDay = true, Start = midnight, End = midnight.AddDays(1) });

        Assert.Equal("""{"date":"2026-10-01","dateTime":null,"timeZone":null}""", patch["start"]!.ToJsonString());
        Assert.Equal("2026-10-02", (string?)patch["end"]!["date"]);
    }

    [Fact]
    public void BuildPatch_Description_EncodesTextAsHtml()
    {
        var draft = Read();

        var patch = EventJson.BuildPatch(draft, draft with { Description = "Bring <notes>\rand snacks" });

        Assert.Equal("Bring &lt;notes&gt;<br>and snacks", (string?)patch["description"]);
    }

    [Fact]
    public void BuildPatch_ColorCleared_SendsNull()
    {
        var draft = Read();

        Assert.Equal("""{"colorId":null}""", EventJson.BuildPatch(draft, draft with { ColorId = null }).ToJsonString());
    }

    [Fact]
    public void BuildPatch_GuestAdded_SendsWholeAttendeeList()
    {
        var draft = Read();

        var patch = EventJson.BuildPatch(draft, draft with { Guests = [.. draft.Guests, new Guest("new@example.com")] });

        var attendees = patch["attendees"]!.AsArray();
        Assert.Equal(4, attendees.Count);
        Assert.Equal("new@example.com", (string?)attendees[3]!["email"]);
        Assert.Equal(true, (bool?)attendees[2]!["optional"]);
    }

    [Fact]
    public void BuildCreate_NewTimedEvent_HasIdTimesAndDefaultReminders()
    {
        var draft = new EventDraft { AccountId = "acct", CalendarId = "cal", Title = "Coffee", Start = Start, End = Start.AddMinutes(30), TimeZone = "America/New_York" };

        var body = EventJson.BuildCreate("abcde12345", draft);

        Assert.Equal("abcde12345", (string?)body["id"]);
        Assert.Equal("Coffee", (string?)body["summary"]);
        Assert.Equal("2026-10-01T14:00:00-04:00", (string?)body["start"]!["dateTime"]);
        Assert.Equal("""{"useDefault":true}""", body["reminders"]!.ToJsonString());
        Assert.Null(body["attendees"]);
        Assert.Null(body["recurrence"]);
    }

    [Fact]
    public void ApplyPatch_MergesNestedObjectsAndRemovesNulls()
    {
        var merged = EventJson.ApplyPatch(
            """{"id":"a","summary":"Old","start":{"dateTime":"2026-10-01T10:00:00Z","timeZone":"UTC"},"colorId":"5"}""",
            new JsonObject { ["summary"] = "New", ["start"] = new JsonObject { ["date"] = "2026-10-01", ["dateTime"] = null, ["timeZone"] = null }, ["colorId"] = null });

        Assert.Equal("""{"id":"a","summary":"New","start":{"date":"2026-10-01"}}""", merged);
    }

    [Fact]
    public void MaterializeInstance_CopiesMasterWithoutRecurrence()
    {
        var master = """{"id":"evt-weekly","etag":"\"2\"","summary":"Team standup","start":{"dateTime":"2026-10-05T09:30:00-04:00","timeZone":"America/New_York"},"end":{"dateTime":"2026-10-05T10:00:00-04:00","timeZone":"America/New_York"},"recurrence":["RRULE:FREQ=WEEKLY"]}""";
        var original = new DateTimeOffset(2026, 10, 9, 13, 30, 0, TimeSpan.Zero);

        var instance = JsonNode.Parse(EventJson.MaterializeInstance(master, "evt-weekly_20261009T133000Z", original, false, original, original.AddMinutes(30)))!;

        Assert.Equal("evt-weekly_20261009T133000Z", (string?)instance["id"]);
        Assert.Equal("evt-weekly", (string?)instance["recurringEventId"]);
        Assert.Equal("2026-10-09T09:30:00-04:00", (string?)instance["originalStartTime"]!["dateTime"]);
        Assert.Null(instance["recurrence"]);
        Assert.Null(instance["etag"]);
        Assert.Equal("Team standup", (string?)instance["summary"]);
    }

    [Fact]
    public void CloneForCreate_DropsGoogleOwnedFields()
    {
        var clone = JsonNode.Parse(EventJson.CloneForCreate(Meeting, "newid12345", keepRecurrence: false))!.AsObject();

        Assert.Equal("newid12345", (string?)clone["id"]);
        Assert.False(clone.ContainsKey("etag"));
        Assert.False(clone.ContainsKey("organizer"));
        Assert.False(clone.ContainsKey("hangoutLink"));
        Assert.Equal("Design review", (string?)clone["summary"]);
    }

    [Fact]
    public void WithResponse_ChangesOnlySelf()
    {
        var json = JsonNode.Parse(EventJson.WithResponse(Meeting, ResponseStatus.Tentative, "Running late"))!;

        Assert.Equal("tentative", (string?)json["attendees"]![1]!["responseStatus"]);
        Assert.Equal("Running late", (string?)json["attendees"]![1]!["comment"]);
        Assert.Equal("accepted", (string?)json["attendees"]![0]!["responseStatus"]);
    }

    [Theory]
    [InlineData("owner", Meeting, false)]
    [InlineData("reader", """{"id":"a"}""", false)]
    [InlineData("writer", """{"id":"a"}""", true)]
    [InlineData("owner", """{"id":"a","organizer":{"email":"me@example.com","self":true},"attendees":[{"email":"x@example.com"}]}""", true)]
    [InlineData("owner", """{"id":"a","organizer":{"email":"boss@example.com"},"guestsCanModify":true,"attendees":[{"email":"x@example.com"}]}""", true)]
    public void CanEdit_FollowsRoleAndOrganizer(string role, string json, bool expected)
    {
        Assert.Equal(expected, EventJson.CanEdit(json, role));
    }

    [Fact]
    public void CanRespond_GuestYes_OrganizerNo()
    {
        Assert.True(EventJson.CanRespond(Meeting));
        Assert.False(EventJson.CanRespond("""{"organizer":{"self":true},"attendees":[{"email":"me@example.com","self":true}]}"""));
    }

    [Fact]
    public void ApplyBirthdayRule_TimedTitleWithBirthday_BecomesYearlyAllDay()
    {
        var draft = new EventDraft { AccountId = "acct", CalendarId = "cal", Title = "Sam's Birthday", Start = new DateTimeOffset(2026, 10, 2, 2, 0, 0, TimeSpan.Zero), End = new DateTimeOffset(2026, 10, 2, 3, 0, 0, TimeSpan.Zero) };

        var result = EventJson.ApplyBirthdayRule(draft, NewYork);

        Assert.True(result.IsAllDay);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), result.Start);
        Assert.Equal(result.Start.AddDays(1), result.End);
        Assert.Equal(["RRULE:FREQ=YEARLY"], result.Recurrence);
    }

    [Fact]
    public void ApplyBirthdayRule_OtherTitle_Unchanged()
    {
        var draft = new EventDraft { AccountId = "acct", CalendarId = "cal", Title = "Lunch" };

        Assert.Same(draft, EventJson.ApplyBirthdayRule(draft, NewYork));
    }
}
