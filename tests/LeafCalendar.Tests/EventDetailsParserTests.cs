using LeafCalendar.Core.Events;

namespace LeafCalendar.Tests;

public class EventDetailsParserTests
{
    [Fact]
    public void Parse_ZoomLinkPastedInLocation_IsTheConference()
    {
        var details = EventDetailsParser.Parse("""{"id":"a","location":"Zoom: https://us02web.zoom.us/j/555?pwd=abc"}""");

        Assert.Equal(new Uri("https://us02web.zoom.us/j/555?pwd=abc"), details.ConferenceUri);
    }

    [Fact]
    public void Parse_TeamsLinkInDescription_OnlyWhenDescriptionIsRead()
    {
        const string json = """{"id":"a","description":"Join: <a href=\"https://teams.microsoft.com/l/meetup-join/xyz\">Teams</a>"}""";

        Assert.Equal(new Uri("https://teams.microsoft.com/l/meetup-join/xyz"), EventDetailsParser.Parse(json).ConferenceUri);
        Assert.Null(EventDetailsParser.Parse(json, includeDescription: false).ConferenceUri);
    }

    [Fact]
    public void Parse_ZoomLinkInDescription_KeepsAllParameters()
    {
        const string json = """{"id":"a","description":"Join https://zoom.us/j/1?a=1&amp;pwd=2 now"}""";

        Assert.Equal("https://zoom.us/j/1?a=1&pwd=2", EventDetailsParser.Parse(json).ConferenceUri?.AbsoluteUri);
    }

    [Fact]
    public void Parse_LookAlikeMeetingHost_Ignored()
    {
        Assert.Null(EventDetailsParser.Parse("""{"id":"a","location":"https://meet.google.com.evil.example/abc"}""").ConferenceUri);
    }

    [Fact]
    public void Parse_MinimalEvent_UsesDefaults()
    {
        var details = EventDetailsParser.Parse("""{"id":"a","status":"confirmed"}""");

        Assert.Equal(EventDetailsParser.NoTitle, details.Title);
        Assert.Equal(EventKind.Default, details.Kind);
        Assert.Equal(ResponseStatus.Accepted, details.SelfResponse);
        Assert.Equal("", details.Description);
        Assert.Null(details.ConferenceUri);
        Assert.False(details.IsFree);
        Assert.Equal(0, details.GuestCount);
    }

    [Fact]
    public void Parse_FullEvent_ReadsFields()
    {
        var details = EventDetailsParser.Parse("""
            {
              "id": "a", "summary": "Design review", "location": "Room 4", "colorId": "5",
              "eventType": "focusTime", "transparency": "transparent",
              "organizer": { "email": "boss@example.com" },
              "attendees": [
                { "email": "me@example.com", "self": true, "responseStatus": "declined" },
                { "email": "you@example.com", "responseStatus": "accepted" }
              ],
              "hangoutLink": "https://meet.google.com/abc-defg-hij"
            }
            """);

        Assert.Equal("Design review", details.Title);
        Assert.Equal("Room 4", details.Location);
        Assert.Equal("5", details.ColorId);
        Assert.Equal(EventKind.FocusTime, details.Kind);
        Assert.True(details.IsFree);
        Assert.Equal("boss@example.com", details.OrganizerEmail);
        Assert.Equal(ResponseStatus.Declined, details.SelfResponse);
        Assert.Equal(2, details.GuestCount);
        Assert.Equal(new Uri("https://meet.google.com/abc-defg-hij"), details.ConferenceUri);
    }

    [Theory]
    [InlineData("outOfOffice", EventKind.OutOfOffice)]
    [InlineData("birthday", EventKind.Birthday)]
    [InlineData("workingLocation", EventKind.WorkingLocation)]
    [InlineData("fromGmail", EventKind.Default)]
    [InlineData("somethingNew", EventKind.Default)]
    public void Parse_EventType_MapsKind(string eventType, EventKind kind)
    {
        Assert.Equal(kind, EventDetailsParser.Parse($$"""{"id":"a","eventType":"{{eventType}}"}""").Kind);
    }

    [Fact]
    public void Parse_ConferenceDataVideoEntry_PreferredOverHangoutLink()
    {
        var details = EventDetailsParser.Parse("""
            {"id":"a","hangoutLink":"https://meet.google.com/old",
             "conferenceData":{"entryPoints":[{"entryPointType":"phone","uri":"tel:+1-555"},{"entryPointType":"video","uri":"https://zoom.us/j/123"}]}}
            """);

        Assert.Equal(new Uri("https://zoom.us/j/123"), details.ConferenceUri);
    }

    [Theory]
    [InlineData("""{"id":"a","hangoutLink":"javascript:alert(1)"}""")]
    [InlineData("""{"id":"a","hangoutLink":"http://meet.example.com/x"}""")]
    [InlineData("""{"id":"a","conferenceData":{"entryPoints":[{"entryPointType":"video","uri":"file:///C:/evil.exe"}]}}""")]
    public void Parse_NonHttpsConference_Ignored(string json)
    {
        Assert.Null(EventDetailsParser.Parse(json).ConferenceUri);
    }

    [Fact]
    public void HtmlToText_GoogleDescription_KeepsStructureDropsTags()
    {
        var text = EventDetailsParser.HtmlToText(
            "<p>Agenda:</p><ul><li>Budget &amp; timeline</li><li><b>Hiring</b></li></ul>Join <a href=\"https://evil.example\">here</a><br>Thanks<script>alert(1)</script>");

        Assert.Equal("Agenda:\n• Budget & timeline\n• Hiring\nJoin here\nThanksalert(1)", text);
    }

    [Fact]
    public void HtmlToText_Huge_IsCapped()
    {
        Assert.Equal(10_001, EventDetailsParser.HtmlToText(new string('x', 50_000)).Length);
    }

    [Fact]
    public void HtmlToText_HugeUnclosedTags_FinishesAndIsCapped()
    {
        Assert.True(EventDetailsParser.HtmlToText(new string('<', 200_000)).Length <= 10_001);
        Assert.True(EventDetailsParser.HtmlToText(string.Concat(Enumerable.Repeat("<li", 70_000))).Length <= 10_001);

        // An unclosed line break followed by whitespace must stay linear
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(EventDetailsParser.HtmlToText("<br" + new string(' ', 40_000)).Length <= 10_001);
        Assert.True(timer.ElapsedMilliseconds < 500, $"Took {timer.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Parse_WithoutDescription_SkipsDescription()
    {
        var details = EventDetailsParser.Parse("""{"id":"a","summary":"Standup","description":"<p>Agenda</p>"}""", includeDescription: false);

        Assert.Equal("Standup", details.Title);
        Assert.Equal("", details.Description);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"id":"a","conferenceData":"x"}""")]
    [InlineData("""{"id":"a","attendees":[null]}""")]
    [InlineData("""{"id":"a","attendees":[1,"x"],"organizer":"me"}""")]
    public void Parse_OddShapes_TreatedAsMissing(string json)
    {
        var details = EventDetailsParser.Parse(json);

        Assert.Equal(EventDetailsParser.NoTitle, details.Title);
        Assert.Null(details.ConferenceUri);
        Assert.Equal(ResponseStatus.Accepted, details.SelfResponse);
    }
}
