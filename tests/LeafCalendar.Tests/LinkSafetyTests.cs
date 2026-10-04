using System.Diagnostics;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Settings;

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
    [InlineData("https://meet.google.com@evil.example/abc", false)]
    [InlineData("https://user:pass@example.com/a", false)]
    public void CanLaunch_OnlyHttpsAndMeetingApps(string link, bool expected)
    {
        Assert.Equal(expected, LinkSafety.CanLaunch(new Uri(link)));
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("mailto:sam@example.com", true)]
    [InlineData("zoommtg://zoom.us/join", false)]
    [InlineData("http://example.com", false)]
    [InlineData("https://bank.example@evil.example/", false)]
    [InlineData("https://user:pass@example.com/", false)]
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
        Assert.Equal(join.AbsoluteUri, join.OriginalString);
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

    // A Closing Parenthesis That Pairs With One In The Address Is Part Of It
    [Theory]
    [InlineData("Join (https://zoom.us/j/123?pwd=x).", "https://zoom.us/j/123?pwd=x")]
    [InlineData("Join https://zoom.us/my/room_(east) now", "https://zoom.us/my/room_(east)")]
    public void FindMeetingLink_TrailingParenthesis_KeptOnlyWhenPaired(string text, string expected)
    {
        Assert.Equal(expected, LinkSafety.FindMeetingLink(text)?.AbsoluteUri);
    }

    [Theory]
    [InlineData("․")]
    [InlineData("﷐")]
    [InlineData("￿")]
    public void InvalidIdnHosts_NeverThrow_AndAreNotAllowlisted(string bad)
    {
        Assert.Null(LinkSafety.FindMeetingLink($"join https://a{bad}.zoom.us/j/1 now"));
        if (Uri.TryCreate($"https://a{bad}.zoom.us/j/1", UriKind.Absolute, out var uri))
        {
            Assert.Null(LinkSafety.ProviderOf(uri));
            Assert.False(LinkSafety.IsClickableInDescription(uri));
            Assert.False(LinkSafety.TryIdnHost(uri, out _));
        }
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

        Assert.Equal("mailto:boss@example.com?to=you%2Bcal%40example.com&subject=Design%20review%20%26%20budget", mailto?.OriginalString);
    }

    // An Apostrophe Is Part Of A Valid Address And Can't Add A Mail Field
    [Fact]
    public void MailtoGuests_AddressWithAnApostrophe_IsKept()
    {
        var mailto = LinkSafety.MailtoGuests(["sam@example.com", "mary.o'brien@example.com"], "Hi");

        Assert.Equal("mailto:sam@example.com?to=mary.o%27brien%40example.com&subject=Hi", mailto?.OriginalString);
        Assert.Equal("mailto:mary.o%27brien@example.com?subject=Hi", LinkSafety.MailtoGuests(["mary.o'brien@example.com"], "Hi")?.OriginalString);
    }

    [Theory]
    [InlineData("a@b@c.com")]
    [InlineData("a@b.com?bcc=e@x&body=x")]
    [InlineData("a@b.com\r\nBcc: e@x")]
    [InlineData("a@[1.2.3.4]")]
    [InlineData("user@bücher.example")]
    [InlineData("")]
    public void MailtoGuests_HostileAddress_NeverThrowsOrInjects(string hostile)
    {
        var mailto = LinkSafety.MailtoGuests(["a@example.com", hostile], "Hi");

        Assert.NotNull(mailto);
        Assert.Equal(1, mailto.OriginalString.Count(c => c == '?'));
        Assert.DoesNotContain("bcc=", mailto.OriginalString, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%0D", mailto.OriginalString, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%0A", mailto.OriginalString, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("a@b@c.com")]
    [InlineData("a@b.com?bcc=e@x&body=x")]
    [InlineData("a@b.com\r\nBcc: e@x")]
    [InlineData("a@[1.2.3.4]")]
    public void MailtoGuests_HostileFirstAddress_NeverThrows(string hostile)
    {
        Assert.Null(LinkSafety.MailtoGuests([hostile], "Hi"));
        Assert.NotNull(LinkSafety.MailtoGuests([hostile, "ok@example.com"], "Hi"));
    }

    [Fact]
    public void MailtoGuests_IdnAddress_DoesNotThrow()
    {
        Assert.NotNull(LinkSafety.MailtoGuests(["user@bücher.example"], "Hi"));
    }

    [Fact]
    public void MailtoGuests_FirstAddressHostile_StillWorksOrNull()
    {
        Assert.Null(LinkSafety.MailtoGuests(["a@b@c.com"], "Hi"));
        Assert.Null(LinkSafety.MailtoGuests([], "Hi"));
        Assert.StartsWith("mailto:", LinkSafety.MailtoGuests(["a@b@c.com", "ok@example.com"], "Hi")?.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public void MailtoGuests_SubjectControlCharacters_AreStripped()
    {
        var mailto = LinkSafety.MailtoGuests(["a@example.com"], "Hi\r\nBcc: evil@example.com\tthere\0");

        Assert.Equal("mailto:a@example.com?subject=HiBcc%3A%20evil%40example.comthere", mailto?.OriginalString);
    }

    [Fact]
    public void MailtoGuests_HugeInput_StaysFast()
    {
        var stopwatch = Stopwatch.StartNew();

        _ = LinkSafety.MailtoGuests(Enumerable.Repeat("a@" + new string('b', 1000) + ".com", 500).ToList(), new string('x', 100_000));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), stopwatch.Elapsed.ToString());
    }

    [Theory]
    [InlineData("https://example.com/agenda?a=1#top", "https://example.com/agenda?a=1#top")]
    [InlineData("https://аpple.com/login", "https://xn--pple-43d.com/login")]
    [InlineData("https://exämple.com:8443/x", "https://xn--exmple-cua.com:8443/x")]
    [InlineData("mailto:sam@exämple.com?subject=Hi", "mailto:sam@xn--exmple-cua.com?subject=Hi")]
    [InlineData("https://meet.google.com/abc?authuser=leaf%40gmail.com", "https://meet.google.com/abc?authuser=leaf%40gmail.com")]
    public void DisplayForm_ShowsAsciiHosts(string link, string expected) =>
        Assert.Equal(expected, LinkSafety.DisplayForm(new Uri(link)));

    // Review Focus 1: The Details Panel Names Where A Link Really Goes Unless Its Text Already Does
    [Theory]
    [InlineData("https://evil.example/x", "Log in at bank.example", "evil.example")]
    [InlineData("https://evil.example/x", "here", "evil.example")]
    [InlineData("https://аpple.com/", "apple", "xn--pple-43d.com")]
    [InlineData("https://evil.example/", "bank</a><a>.example", "evil.example")]
    [InlineData("mailto:sam@example.com", "Sam", "sam@example.com")]
    [InlineData("https://аpple.com/", "https://аpple.com/", "xn--pple-43d.com")]
    [InlineData("https://аpple.com/", "аpple.com", "xn--pple-43d.com")]
    [InlineData("https://xn--pple-43d.com/", "xn--pple-43d.com", null)]
    [InlineData("https://example.com/x", "https://example.com/x", null)]
    [InlineData("https://example.com/x", "https://example.com/other", null)]
    [InlineData("https://example.com/x", "EXAMPLE.COM", null)]
    [InlineData("https://www.example.com/", "www.example.com", null)]
    [InlineData("mailto:sam@example.com", "sam@example.com", null)]
    public void HostNote_OnlyWhenTheTextDoesNotShowTheTarget(string link, string text, string? expected)
    {
        Assert.Equal(expected, LinkSafety.HostNote(new Uri(link), text));
    }

    [Fact]
    public void DisplayForm_StripsInvisibleAndDirectionCharacters()
    {
        var hidden = "‪‮⁦⁩‎‏؜​‌‍⁠﻿";
        var link = new Uri("https://example.com/a", UriKind.Absolute);

        var shown = LinkSafety.DisplayForm(new Uri(link.OriginalString + hidden, UriKind.Absolute)) ?? "";

        Assert.DoesNotContain(shown, c => hidden.Contains(c, StringComparison.Ordinal));
        Assert.StartsWith("https://example.com/a", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void DisplayForm_HttpsWithAUserName_IsNull() =>
        Assert.Null(LinkSafety.DisplayForm(new Uri("https://meet.google.com@evil.example/abc")));

    [Fact]
    public void DisplayForm_HostWithoutAnAsciiForm_IsNull() =>
        Assert.Null(LinkSafety.DisplayForm(new Uri("https://a․b.com/")));

    [Fact]
    public void MapsSearch_Bing() =>
        Assert.Equal("https://www.bing.com/maps?q=Room%204", LinkSafety.MapsSearch("Room 4", MapProvider.Bing).AbsoluteUri);

    [Fact]
    public void MapsSearch_GoogleStaysTheDefault() =>
        Assert.StartsWith("https://www.google.com/maps/search/", LinkSafety.MapsSearch("Room 4").AbsoluteUri, StringComparison.Ordinal);
}
