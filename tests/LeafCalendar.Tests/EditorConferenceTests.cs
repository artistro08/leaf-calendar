using LeafCalendar.Core.Editing;

namespace LeafCalendar.Tests;

public class EditorConferenceTests
{
    private static readonly Uri Meet = new("https://meet.google.com/abc-defg-hij");

    [Fact]
    public void Text_NoCall_SaysSo()
    {
        Assert.Equal("No video call", EditorConference.Text(hadConference: false, hasConference: false, conferenceUri: null));
    }

    [Fact]
    public void Text_NewlyAdded_SaysTheLinkComesOnSave()
    {
        Assert.Equal("Google Meet link is added when you save", EditorConference.Text(hadConference: false, hasConference: true, conferenceUri: null));
    }

    [Fact]
    public void Text_Existing_ShowsTheHost()
    {
        Assert.Equal("Video call: meet.google.com", EditorConference.Text(hadConference: true, hasConference: true, conferenceUri: Meet));
    }

    [Fact]
    public void Text_ExistingWithoutALink_SaysVideoCall()
    {
        Assert.Equal("Video call", EditorConference.Text(hadConference: true, hasConference: true, conferenceUri: null));
    }

    [Fact]
    public void Text_ExistingRemoved_SaysNoCall()
    {
        Assert.Equal("No video call", EditorConference.Text(hadConference: true, hasConference: false, conferenceUri: Meet));
    }

    [Fact]
    public void Text_LookAlikeHost_ShowsTheAsciiForm()
    {
        var lookAlike = new Uri("https://zoоm.us/j/123");

        Assert.Equal("Video call: xn--zom-ted.us", EditorConference.Text(hadConference: false, hasConference: false, conferenceUri: lookAlike));
    }

    [Fact]
    public void Text_LinkOnlyInTheLocation_StillShowsIt()
    {
        var zoom = new Uri("https://us02web.zoom.us/j/123");

        Assert.Equal("Video call: us02web.zoom.us", EditorConference.Text(hadConference: false, hasConference: false, conferenceUri: zoom));
    }
}
