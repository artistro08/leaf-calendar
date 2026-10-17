using System.Text.Json;
using LeafCalendar.Core.Sunsama;

namespace LeafCalendar.Tests;

public sealed class SunsamaParserTests
{
    private const string Me = """
        {"user":{"email":"owner@example.com","timezone":"America/Chicago","calendars":[
          {"id":"sunsama-2c88","provider":"sunsama-calendar"},
          {"id":"work@example.com","provider":"google-calendar"}]}}
        """;

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static SunsamaProfile Profile() => SunsamaParser.Profile(Json(Me))!;

    [Fact]
    public void Profile_ReadsEmailZoneAndGoogleCalendars()
    {
        var p = Profile();

        Assert.Equal("owner@example.com", p.Email);
        Assert.Equal("America/Chicago", p.Zone.Id);
        Assert.Equal(["work@example.com"], p.GoogleCalendarIds);
    }

    [Fact]
    public void Profile_UnknownZone_ReturnsNull() =>
        Assert.Null(SunsamaParser.Profile(Json("""{"user":{"email":"a@b.c","timezone":"Mars/Olympus","calendars":[]}}""")));

    [Fact]
    public void Profile_NoUser_ReturnsNull() => Assert.Null(SunsamaParser.Profile(Json("""{"user":"x"}""")));

    [Fact]
    public void Day_ProjectedTask_ReadsEntriesEstimateTrackedAndChannel()
    {
        var tasks = Json("""
            {"tasks":[{"_id":"6ac7a44534029300011d076e","title":"Home Page Redesign","channel":"Illuminate Digital",
              "timeEstimate":"2 hours","completed":false,"timeboxEventIds":[],
              "actualTimeSpent":{"total":"1 hours and 24 minutes"},
              "projectedTimeEntries":[
                {"startDate":"2026-10-16 10:50 AM","endDate":"2026-10-16 11:30 AM"},
                {"startDate":"2026-10-16 12:30 PM","endDate":"2026-10-16 1:50 PM"}]}]}
            """);

        var task = Assert.Single(SunsamaParser.Day(tasks, Json("""{"events":[]}"""), Profile()));

        Assert.Equal("Home Page Redesign", task.Title);
        Assert.Equal("Illuminate Digital", task.Channel);
        Assert.Equal(120, task.EstimateMinutes);
        Assert.Equal(84, task.TrackedMinutes);
        Assert.False(task.Completed);
        Assert.Equal(
            [
                (new DateTimeOffset(2026, 10, 16, 15, 50, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 16, 16, 30, 0, TimeSpan.Zero)),
                (new DateTimeOffset(2026, 10, 16, 17, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 16, 18, 50, 0, TimeSpan.Zero)),
            ],
            task.Projected);
        Assert.Empty(task.Events);
    }

    [Fact]
    public void Day_TimeboxAndImported_ResolveTheirEvents()
    {
        var tasks = Json("""
            {"tasks":[
              {"_id":"aaaaaaaaaaaaaaaaaaaaaaa1","title":"Write copy","channel":"Work","completed":true,"timeboxEventIds":["ev-box"]},
              {"_id":"aaaaaaaaaaaaaaaaaaaaaaa2","title":"Lunch Break","channel":"Personal","timeboxEventIds":[],
               "importedFrom":{"service":"googleCalendar","identifier":{"sunsamaId":"ev-lunch"}}}]}
            """);
        var events = Json("""
            {"events":[
              {"eventId":"ev-box","title":"Write copy","calendarId":"sunsama-2c88",
               "start":{"dateTime":"2026-10-16T13:00:00-05:00"},"end":{"dateTime":"2026-10-16T14:00:00-05:00"}},
              {"eventId":"ev-lunch","title":"Lunch Break","calendarId":"work@example.com",
               "start":{"dateTime":"2026-10-16T11:30:00-05:00"},"end":{"dateTime":"2026-10-16T12:15:00-05:00"}}]}
            """);

        var day = SunsamaParser.Day(tasks, events, Profile());

        Assert.True(day[0].Completed);
        var box = Assert.Single(day[0].Events);
        Assert.False(box.OnGoogle);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 18, 0, 0, TimeSpan.Zero), box.Start);
        var lunch = Assert.Single(day[1].Events);
        Assert.True(lunch.OnGoogle);
        Assert.Equal("work@example.com", lunch.CalendarId);
        Assert.Equal("Lunch Break", lunch.Title);
    }

    [Fact]
    public void Day_MissingAndOddFields_KeepsWhatItCan()
    {
        var tasks = Json("""
            {"tasks":[
              {"_id":"aaaaaaaaaaaaaaaaaaaaaaa3","title":null,"channel":null,"timeEstimate":"","actualTimeSpent":null,
               "timeboxEventIds":["missing-event", 7],
               "projectedTimeEntries":[{"startDate":"bad","endDate":"2026-10-16 1:00 PM"},
                                       {"startDate":"2026-10-16 2:00 PM","endDate":"2026-10-16 1:00 PM"}]},
              "not an object",
              {"title":"no id"}]}
            """);

        var task = Assert.Single(SunsamaParser.Day(tasks, Json("""{"events":"nope"}"""), Profile()));

        Assert.Equal("", task.Title);
        Assert.Equal("", task.Channel);
        Assert.Null(task.EstimateMinutes);
        Assert.Null(task.TrackedMinutes);
        Assert.Empty(task.Projected);
        Assert.Empty(task.Events);
    }

    [Fact]
    public void Day_ZeroTracked_IsNull()
    {
        var tasks = Json("""{"tasks":[{"_id":"aaaaaaaaaaaaaaaaaaaaaaa5","actualTimeSpent":{"total":"0 minutes"}}]}""");

        Assert.Null(Assert.Single(SunsamaParser.Day(tasks, Json("""{"events":[]}"""), Profile())).TrackedMinutes);
    }

    [Fact]
    public void Day_AllDayEvent_IsSkipped()
    {
        var tasks = Json("""{"tasks":[{"_id":"aaaaaaaaaaaaaaaaaaaaaaa4","title":"x","timeboxEventIds":["ev-day"]}]}""");
        var events = Json("""{"events":[{"eventId":"ev-day","title":"x","calendarId":"sunsama-2c88","start":{"date":"2026-10-16"},"end":{"date":"2026-10-17"}}]}""");

        Assert.Empty(Assert.Single(SunsamaParser.Day(tasks, events, Profile())).Events);
    }

    [Fact]
    public void Day_NotAnObject_ReturnsEmpty() =>
        Assert.Empty(SunsamaParser.Day(Json("[]"), Json("null"), Profile()));
}
