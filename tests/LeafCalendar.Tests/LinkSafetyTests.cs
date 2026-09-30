using System.Diagnostics;
using LeafCalendar.Core.Events;

namespace LeafCalendar.Tests;

public class LinkSafetyTests
{
    [Theory]
    [InlineData("https://example.com/a", true)]
    [InlineData("HTTPS://example.com/a", true)]
    [InlineData("zoommtg://zoom.us/join?confno=123", true)]
    [InlineData("zoomus://zoom.us/join?confno=123", true)]
    [InlineData("msteams:/l/meetup-join/19%3ameeting", true)]
    [InlineData("webex://meet?sip=abc", true)]
    [InlineData("http://example.com/a", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("ms-msdt:/id%20PCWDiagnostic", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("search-ms:query=secret", false)]
    [InlineData("mailto:a@example.com", false)]
    public void CanLaunch_OnlyHttpsAndMeetingApps(string link, bool expected)
    {
        Assert.Equal(expected, LinkSafety.CanLaunch(new Uri(link)));
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("mailto:sam@example.com", true)]
    [InlineData("zoommtg://zoom.us/join", false)]
    [InlineData("http://example.com", false)]
    public void IsClickableInDescription_HttpsAndMailtoOnly(string link, bool expected)
    {
        Assert.Equal(expected, LinkSafety.IsClickableInDescription(new Uri(link)));
    }

    [Theory]
    [InlineData("https://meet.google.com/abc-defg-hij", MeetingProvider.GoogleMeet)]
    [InlineData("https://us02web.zoom.us/j/123", MeetingProvider.Zoom)]
    [InlineData("https://teams.microsoft.com/l/meetup-join/x", MeetingProvider.Teams)]
    [InlineData("https://acme.webex.com/meet/sam", MeetingProvider.Webex)]
    [InlineData("https://whereby.com/room", MeetingProvider.Whereby)]
    [InlineData("https://doxy.me/drsmith", MeetingProvider.DoxyMe)]
    public void ProviderOf_KnownHosts(string link, MeetingProvider provider)
    {
        Assert.Equal(provider, LinkSafety.ProviderOf(new Uri(link)));
    }

    [Theory]
    [InlineData("https://meet.google.com.evil.example/abc")]
    [InlineData("https://evilzoom.us/j/123")]
    [InlineData("https://zoom.us.evil.example/j/123")]
    [InlineData("https://meet.google.com@evil.example/abc")]
    [InlineData("http://meet.google.com/abc")]
    [InlineData("https://example.com/?next=https://meet.google.com/abc")]
    public void ProviderOf_LookAlikes_AreNotMeetings(string link)
    {
        Assert.Null(LinkSafety.ProviderOf(new Uri(link)));
    }

    [Fact]
    public void JoinUri_Meet_AddsAuthUserReplacingAnyExisting()
    {
        var join = LinkSafety.JoinUri(new Uri("https://meet.google.com/abc-defg-hij?authuser=0&hs=1"), "sam@example.com");

        Assert.Equal("https://meet.google.com/abc-defg-hij?hs=1&authuser=sam%40example.com", join.AbsoluteUri);
    }

    [Fact]
    public void JoinUri_Zoom_Unchanged()
    {
        var zoom = new Uri("https://zoom.us/j/123?pwd=x");

        Assert.Same(zoom, LinkSafety.JoinUri(zoom, "sam@example.com"));
    }

    [Theory]
    [InlineData("Dial in or join https://zoom.us/j/123?pwd=x.", "https://zoom.us/j/123?pwd=x")]
    [InlineData("Docs: https://example.com/a then https://teams.microsoft.com/l/meetup-join/abc", "https://teams.microsoft.com/l/meetup-join/abc")]
    [InlineData("Nothing to join here", null)]
    [InlineData("https://zoom.us.evil.example/j/1", null)]
    public void FindMeetingLink_FirstKnownHost(string text, string? expected)
    {
        Assert.Equal(expected, LinkSafety.FindMeetingLink(text)?.AbsoluteUri);
    }

    [Fact]
    public void FindMeetingLink_HugeInput_StaysFast()
    {
        var stopwatch = Stopwatch.StartNew();

        Assert.Null(LinkSafety.FindMeetingLink(string.Concat(Enumerable.Repeat("https://", 200_000))));
        Assert.Null(LinkSafety.FindMeetingLink("https://" + new string('a', 1_000_000)));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), stopwatch.Elapsed.ToString());
    }

    [Fact]
    public void MailtoGuests_EscapesAddressesAndSubject()
    {
        var mailto = LinkSafety.MailtoGuests(["boss@example.com", "you+cal@example.com"], "Design review & budget");

        Assert.Equal("mailto:boss@example.com?to=you%2Bcal%40example.com&subject=Design%20review%20%26%20budget", mailto.OriginalString);
    }

    [Fact]
    public void MailtoGuests_HostileAddress_CannotInjectFields()
    {
        var mailto = LinkSafety.MailtoGuests(["a@example.com", "x@example.com?bcc=evil@example.com"], "Hi");

        Assert.DoesNotContain("?bcc", mailto.OriginalString, StringComparison.Ordinal);
        Assert.Equal(1, mailto.OriginalString.Count(c => c == '?'));
    }
}
