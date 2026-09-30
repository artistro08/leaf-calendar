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

    [Fact]
    public void Format_ListsEntitiesAndBlankLines()
    {
        var runs = DescriptionFormatter.Format("<p>Agenda:</p><ul><li>Budget &amp; timeline</li><li>Hiring</li></ul>\n\n\n\nThanks<script>alert(1)</script>");

        Assert.Equal("Agenda:\n• Budget & timeline\n• Hiring\n\nThanks" + "alert(1)", Text(runs));
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
