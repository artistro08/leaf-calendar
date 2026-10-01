using System.Diagnostics;
using LeafCalendar.Core.Events;

namespace LeafCalendar.Tests;

public class DescriptionFormatterTests
{
    static string Text(IReadOnlyList<DescriptionRun> runs) => string.Concat(runs.Select(r => r.Text));

    [Fact]
    public void Format_StylesAndLinks_BecomeRuns()
    {
        var runs = DescriptionFormatter.Format("<b>Agenda</b><br>Join <a href=\"https://example.com/x\">here</a> or <a href=\"javascript:alert(1)\">there</a>, <i><u>please</u></i>");

        (string Text, bool Bold, bool Italic, bool Underline, string? Link)[] expected =
        [
            ("Agenda", true, false, false, null),
            ("\nJoin ", false, false, false, null),
            ("here", false, false, false, "https://example.com/x"),
            (" or ", false, false, false, null),
            ("there", false, false, false, null),
            (", ", false, false, false, null),
            ("please", false, true, true, null),
        ];
        Assert.Equal(expected, runs.Select(r => (r.Text, r.Bold, r.Italic, r.Underline, r.Link?.AbsoluteUri)));
    }

    [Fact]
    public void Format_BareHttpsUrl_IsLinked()
    {
        var runs = DescriptionFormatter.Format("Notes at https://docs.example.com/a.pdf, thanks");

        Assert.Equal(["Notes at ", "https://docs.example.com/a.pdf", ", thanks"], runs.Select(r => r.Text));
        Assert.Equal(new Uri("https://docs.example.com/a.pdf"), runs[1].Link);
    }

    [Theory]
    [InlineData("<a href=\"mailto:sam@example.com\">Sam</a>", "mailto:sam@example.com")]
    [InlineData("<a href=\"file:///C:/evil.exe\">file</a>", null)]
    [InlineData("<a href='ms-msdt:/id x'>fix</a>", null)]
    [InlineData("<a href=\"http://plain.example\">plain</a>", null)]
    public void Format_OnlyHttpsAndMailtoLinksAreClickable(string html, string? expected)
    {
        Assert.Equal(expected, DescriptionFormatter.Format(html).Single().Link?.OriginalString);
    }

    [Theory]
    [InlineData("<a href=\"https://evil.example/login\">https://bank.example/login</a>", null)]
    [InlineData("<a href=\"https://evil.example/login\">www.bank.example</a>", null)]
    [InlineData("<a href=\"https://evil.example/login\">https://<b>bank.example</b>/x</a>", null)]
    [InlineData("<a href=\"https://bank.example/login\">https://bank.example/home</a>", "https://bank.example/login")]
    [InlineData("<a href=\"https://evil.example/login\">Open your bank</a>", "https://evil.example/login")]
    public void Format_LinkTextNamingAnotherHost_IsNotClickable(string html, string? expected)
    {
        Assert.All(DescriptionFormatter.Format(html), r => Assert.Equal(expected, r.Link?.AbsoluteUri));
    }

    [Theory]
    [InlineData("<a href=\"https://evil.example/x\">https://bank&#8203;.example/login</a>")]
    [InlineData("<a href=\"https://evil.example/x\">https://bank.example&zwnj;/login</a>")]
    [InlineData("<a href=\"https://evil.example/x\">\u202Ehttps://bank.example/login</a>")]
    [InlineData("<a href=\"https://evil.example/x\">\u2060www.bank.example</a>")]
    [InlineData("<a href=\"https://evil.example/x\">\uFEFFhttps://bank.example</a>")]
    [InlineData("<a href=\"https://evil.example/x\">• https://bank.example/login</a>")]
    [InlineData("<ol><a href=\"https://evil.example/x\"><li>https://bank.example/login</a></ol>")]
    [InlineData("<a href=\"https://evil.example/x\">bank.example/login</a>")]
    [InlineData("<a href=\"https://evil.example/x\">bank.example</a>")]
    [InlineData("<a href=\"https://bank.example@evil.example/x\">https://bank.example/login</a>")]
    [InlineData("<a href=\"https://bank.example@evil.example/x\">https://evil.example@bank.example</a>")]
    [InlineData("<a href=\"mailto:sam@example.com\">https://bank.example/login</a>")]
    public void Format_DisguiseBypasses_AreNotClickable(string html)
    {
        Assert.All(DescriptionFormatter.Format(html), r => Assert.Null(r.Link));
    }

    [Theory]
    [InlineData("<a href=\"https://evil.example/\">x&#xFDD0;y.z</a>")]
    [InlineData("<a href=\"https://evil.example/\">&#x2024;bank.example</a>")]
    [InlineData("<a href=\"https://evil.example/\">https://a&#xFDD0;.com/</a>")]
    [InlineData("<a href=\"https://evil.example/\">https://a&#xFFFF;.com/</a>")]
    [InlineData("<a href=\"https://a&#xFDD0;.com/\">x</a>")]
    [InlineData("<a href=\"https://a&#xFFFF;.com/\">x</a>")]
    [InlineData("see https://a﷐.com/x now")]
    [InlineData("see https://a․.com/x now")]
    public void Format_InvalidIdnHosts_NeverThrowAndAreNotClickable(string html)
    {
        Assert.All(DescriptionFormatter.Format(html), r => Assert.Null(r.Link));
    }

    [Fact]
    public void Format_MailtoValuesWithLineBreaks_AreDropped()
    {
        Assert.Equal("mailto:sam@example.com?subject=Hi", DescriptionFormatter.Format("<a href=\"mailto:sam@example.com?subject=Hi&body=a%0D%0ABcc:x@y.z&cc=q%0aw\">m</a>").Single().Link?.OriginalString);
    }

    [Theory]
    [InlineData("<a href=\"mailto:a@b.com?subject=Hi&body=a&#13;&#10;Bcc:x@y.z\">m</a>")]
    [InlineData("<a href=\"mailto:a@b.com?subject=Hi&body=a\r\nBcc:x@y.z\">m</a>")]
    [InlineData("<a href=\"mailto:a@b.com?subject=Hi&body=a\nBcc:x@y.z\">m</a>")]
    [InlineData("<a href=\"mailto:a@b.com?subject=Hi&body=a&#x0A;Bcc:x@y.z\">m</a>")]
    public void Format_MailtoRawLineBreaks_AreDropped(string html)
    {
        var link = DescriptionFormatter.Format(html).Single().Link;

        Assert.Equal("mailto:a@b.com?subject=Hi", link?.OriginalString);
        Assert.DoesNotContain("%0", link!.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Format_MailtoText_StaysClickable()
    {
        Assert.Equal("mailto:sam@example.com", DescriptionFormatter.Format("<a href=\"mailto:sam@example.com\">sam@example.com</a>").Single().Link?.OriginalString);
    }

    [Theory]
    [InlineData("mailto:sam@example.com?subject=Hi&attach=C:/secret.txt", "mailto:sam@example.com?subject=Hi")]
    [InlineData("mailto:sam@example.com?bcc=evil@example.com&body=Yo&cc=a@example.com", "mailto:sam@example.com?body=Yo&cc=a@example.com")]
    [InlineData("mailto:sam@example.com?ATTACH=x&BCC=y", "mailto:sam@example.com")]
    public void Format_MailtoLinks_KeepOnlySubjectBodyCc(string href, string expected)
    {
        Assert.Equal(expected, DescriptionFormatter.Format($"<a href=\"{href}\">mail</a>").Single().Link?.OriginalString);
    }

    [Theory]
    [InlineData("<a data-href=\"https://evil.example\">x</a>")]
    [InlineData("<a title=\"see href=https://evil.example\">x</a>")]
    [InlineData("<a title='a' data-x-href='https://evil.example'>x</a>")]
    public void Format_HrefMustBeAnAttributeName(string html)
    {
        Assert.Null(DescriptionFormatter.Format(html).Single().Link);
    }

    [Fact]
    public void Format_RealHrefAfterOtherAttributes_IsFound()
    {
        Assert.Equal("https://ok.example/", DescriptionFormatter.Format("<a title=\"t\" class=x HREF='https://ok.example/'>x</a>").Single().Link?.AbsoluteUri);
    }

    [Theory]
    [InlineData("<a ", "a=b ")]
    [InlineData("<a x=\"", "href=")]
    [InlineData("<a ", "-")]
    [InlineData("<a x=", "'")]
    public void Format_HostileAttributeInput_StaysFast(string prefix, string filler)
    {
        var html      = prefix + string.Concat(Enumerable.Repeat(filler, 200_000 / filler.Length)) + ">x</a>";
        var stopwatch = Stopwatch.StartNew();

        _ = DescriptionFormatter.Format(html);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), stopwatch.Elapsed.ToString());
    }

    [Fact]
    public void Format_ListsEntitiesAndBlankLines()
    {
        var runs = DescriptionFormatter.Format("<p>Agenda:</p><ul><li>Budget &amp; timeline</li><li>Hiring</li></ul>\n\n\n\nThanks<script>alert(1)</script>");

        Assert.Equal("Agenda:\n• Budget & timeline\n• Hiring\n\nThanks" + "alert(1)", Text(runs));
    }

    [Fact]
    public void Format_ListMarkers_CarryTheirKind()
    {
        var runs = DescriptionFormatter.Format("Agenda<ul><li>a</li></ul><ol><li>b</li><li>c</li></ol>");

        Assert.Equal("Agenda\n• a\n\n1. b\n2. c", Text(runs));
        Assert.Equal([ListKind.Bullet, ListKind.Numbered, ListKind.Numbered], runs.Where(r => r.List != ListKind.None).Select(r => r.List));
    }

    [Fact]
    public void Format_PrettyPrintedList_HasNoExtraBlankLines()
    {
        Assert.Equal("Agenda\n• a\n• b\n\nThanks", Text(DescriptionFormatter.Format("Agenda\n<ul>\n  <li>a</li>\n  <li>b</li>\n</ul>\nThanks")));
    }

    [Fact]
    public void Format_Huge_IsCapped()
    {
        Assert.Equal(10_001, Text(DescriptionFormatter.Format(new string('x', 50_000))).Length);
    }

    [Theory]
    [InlineData("<", " ")]
    [InlineData("<a href=\"", "x")]
    [InlineData("<a ", "href ")]
    [InlineData("< ", "< ")]
    [InlineData("<b", " ")]
    [InlineData("<a href=x", "https://")]
    public void Format_HostileHugeInput_StaysFast(string prefix, string filler)
    {
        var html      = prefix + string.Concat(Enumerable.Repeat(filler, 200_000 / filler.Length));
        var stopwatch = Stopwatch.StartNew();

        _ = DescriptionFormatter.Format(html);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), stopwatch.Elapsed.ToString());
    }

    [Fact]
    public void FormatEvent_ReadsDescriptionField()
    {
        Assert.Equal("Hi", Text(DescriptionFormatter.FormatEvent("""{"description":"<b>Hi</b>"}""")));
        Assert.Empty(DescriptionFormatter.FormatEvent("""{"summary":"x"}"""));
    }
}
