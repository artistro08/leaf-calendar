using System.Diagnostics;
using System.Text.RegularExpressions;
using LeafCalendar.Core.Events;

namespace LeafCalendar.Tests;

public partial class DescriptionHtmlTests
{
    // Only these tags may be written back to Google
    [GeneratedRegex(@"<(?!/?(b|i|u|br|ul|ol|li)>|a href=""(https://|mailto:)[^""<>]*"">|/a>)", RegexOptions.IgnoreCase)]
    private static partial Regex ForeignTag();

    static DescriptionLine Line(ListKind list, params DescriptionRun[] runs) => new(runs, list);

    static string Shape(IReadOnlyList<DescriptionLine> lines) =>
        string.Join("|", lines.Select(l => $"{l.List}:{string.Concat(l.Runs.Select(r => (r.Bold ? "*" : "") + r.Text))}"));

    [Fact]
    public void Lines_ListsAndStyles_BecomeLines()
    {
        var lines = DescriptionHtml.Lines("Agenda:<ul><li><b>Budget</b></li><li>Hiring</li></ul><ol><li>One</li><li>Two</li></ol>Thanks");

        Assert.Equal("None:Agenda:|Bullet:*Budget|Bullet:Hiring|Numbered:One|Numbered:Two|None:Thanks", Shape(lines));
    }

    [Fact]
    public void Write_LinesAndStyles_UsesOnlyAllowlistedTags()
    {
        IReadOnlyList<DescriptionLine> lines =
        [
            Line(ListKind.None, new("Read "), new("this", Bold: true, Italic: true, Underline: true), new(" <now> & then")),
            Line(ListKind.None),
            Line(ListKind.Bullet, new DescriptionRun("Docs", Link: new Uri("https://example.com/a?b=1&c=2"))),
            Line(ListKind.Numbered, new DescriptionRun("Mail", Link: new Uri("mailto:sam@example.com"))),
        ];

        Assert.Equal(
            "Read <b><i><u>this</u></i></b> &lt;now&gt; &amp; then<br><br>"
            + "<ul><li><a href=\"https://example.com/a?b=1&amp;c=2\">Docs</a></li></ul>"
            + "<ol><li><a href=\"mailto:sam@example.com\">Mail</a></li></ol>",
            DescriptionHtml.Write(lines));
    }

    [Theory]
    [InlineData("<b>Agenda</b><br>Budget")]
    [InlineData("Agenda:<ul><li>a</li><li>b</li></ul>after")]
    [InlineData("<ol><li>x</li></ol><ul><li>y</li></ul>")]
    [InlineData("a<br><br><br><br>b")]
    [InlineData("<p>one</p><div>two</div>")]
    public void Normalize_IsIdempotentAndReadsTheSame(string html)
    {
        var once = DescriptionHtml.Normalize(html);

        Assert.Equal(once, DescriptionHtml.Normalize(once));
        Assert.Equal(Shape(DescriptionHtml.Lines(html)), Shape(DescriptionHtml.Lines(once)));
    }

    // Review Focus 1: Hostile Markup Never Survives A Round Trip
    [Theory]
    [InlineData("<script>alert(1)</script>Hi")]
    [InlineData("<img src=x onerror=alert(1)>Hi")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>Hi")]
    [InlineData("<a href=\"javascript:alert(1)\">Hi</a>")]
    [InlineData("<a href=\"data:text/html,<b>x</b>\">Hi</a>")]
    [InlineData("<a href=\"https://evil.example\" onclick=\"x()\">https://bank.example</a>")]
    [InlineData("<style>b{}</style><b onmouseover=x>Hi")]
    [InlineData("&lt;script&gt;alert(1)&lt;/script&gt;")]
    [InlineData("<a href=\"https://ok.example/\u202Egnp.exe\">Hi</a>")]
    public void Normalize_HostileMarkup_OnlyAllowlistedTagsSurvive(string html)
    {
        var output = DescriptionHtml.Normalize(html);

        // A tag the parser can't read (a quoted "<" inside it) stays as encoded, inert text, so only links are checked
        Assert.DoesNotMatch(ForeignTag(), output);
        Assert.DoesNotContain("href=\"javascript:", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"data:", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", output, StringComparison.OrdinalIgnoreCase);
    }

    // Review Focus 1: Random Tag Soup Stays Inside The Allowlist, And A Second Pass Changes Nothing
    [Fact]
    public void Normalize_RandomTagSoup_IsAllowlistedAndIdempotent()
    {
        string[] pieces = ["<b>", "</b>", "<i>", "</u>", "<ul>", "<li>", "</li>", "</ol>", "<ol>", "<br>", "<a href=\"https://x.example/\">", "</a>",
            "<a href='mailto:a@b.example?bcc=c@d.example'>", "<script>", "&amp;", "&lt;b&gt;", "text", " ", "\n", "\u202E", "\uFFFC", "<p>", "</div>", "<", ">", "\""];
        var random = new Random(6);

        for (var i = 0; i < 2_000; i++)
        {
            var html   = string.Concat(Enumerable.Range(0, random.Next(1, 40)).Select(_ => pieces[random.Next(pieces.Length)]));
            var output = DescriptionHtml.Normalize(html);

            Assert.DoesNotMatch(ForeignTag(), output);
            Assert.DoesNotContain("bcc", output, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(output, DescriptionHtml.Normalize(output));
        }
    }

    // Review Focus 3: Pasted Objects And Control Characters Never Reach Google
    [Fact]
    public void Write_ObjectPlaceholdersAndControlCharacters_AreDropped()
    {
        var html = DescriptionHtml.Write([Line(ListKind.None, new DescriptionRun("a\uFFFCb\u0007c\td"))]);

        Assert.Equal("abc\td", html);
    }

    [Fact]
    public void Normalize_HugeInput_IsCapped()
    {
        var output = DescriptionHtml.Normalize(string.Concat(Enumerable.Repeat("<b>word</b> ", 20_000)));

        Assert.True(output.Length < 30_000);
        Assert.EndsWith("…</b>", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Lines_BlankLineRightAfterAList_IsDropped()
    {
        // A list's closing tags read as a line break; Leaf writes lists that way, so the extra blank line isn't kept
        Assert.Equal("Bullet:x|None:y", Shape(DescriptionHtml.Lines("<ul><li>x</li></ul>y")));
    }

    // Review Focus 1: Lines From Anywhere (The Editor Too) Only Write Allowlisted Links
    [Fact]
    public void Write_UnsafeLinks_AreDroppedOrTrimmed()
    {
        var html = DescriptionHtml.Write(
        [
            Line(ListKind.None, new("Run", Link: new Uri("javascript:alert(1)")), new(" "), new("Page", Link: new Uri("data:text/html,x"))),
            Line(ListKind.None, new DescriptionRun("Mail", Link: new Uri("mailto:a@b.example?subject=Hi&bcc=c@d.example"))),
        ]);

        Assert.Equal("Run Page<br><a href=\"mailto:a@b.example?subject=Hi\">Mail</a>", html);
    }

    // Review Focus 1: A Link Whose Text Names Another Address Is Never Written, Even Once Split Into Lines Or Runs
    [Theory]
    [InlineData("<a href=\"https://evil.example/\">￼<b>https://bank.example/login</b></a>")]
    [InlineData("<a href=\"https://evil.example/\">Sign in\nhttps://bank.example/login</a>")]
    [InlineData("<a href=\"https://evil.example/\">Sign in <b>https://bank.example/login</b></a>")]
    public void Normalize_DisguisedLinkPieces_AreNotLinkedToTheirTarget(string html)
    {
        var output = DescriptionHtml.Normalize(html);

        Assert.DoesNotContain("evil.example\"><b>https", output, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.example\">https", output, StringComparison.Ordinal);
        Assert.Equal(output, DescriptionHtml.Normalize(output));
    }

    [Theory]
    [InlineData("https://bank.example/login")]
    [InlineData("bank\uFF0Eexample")]
    [InlineData("bank\u3002example")]
    [InlineData("//bank.example")]
    public void Write_DisguisedLink_IsWrittenAsText(string text)
    {
        var html = DescriptionHtml.Write([Line(ListKind.None, new DescriptionRun(text, Link: new Uri("https://evil.example/")))]);

        Assert.DoesNotContain("evil.example", html, StringComparison.Ordinal);
    }

    // Adjacent runs with one target are written as adjacent links, which read back as one link, so they're checked together
    [Fact]
    public void Write_DisguisedLinkSplitAcrossRuns_IsWrittenAsText()
    {
        var html = DescriptionHtml.Write([Line(ListKind.None, new DescriptionRun("bank", Link: new Uri("https://evil.example")), new DescriptionRun(".example", Bold: true, Link: new Uri("https://evil.example")))]);

        Assert.DoesNotContain("evil.example", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_DisguisedPieceNextToAnotherLink_IsWrittenAsText()
    {
        var html = DescriptionHtml.Write([Line(ListKind.None, new DescriptionRun("https://bank", Link: new Uri("https://bank.evil.example")), new DescriptionRun(".example", Link: new Uri("https://evil.example")))]);

        Assert.DoesNotContain("bank.evil.example", html, StringComparison.Ordinal);
    }

    // Review Focus 1: Hrefs Go To Google With ASCII Hosts And Nothing Hidden
    [Theory]
    [InlineData("<a href=\"https://\u0430pple.com/\">apple</a>", "https://xn--pple-43d.com/")]
    [InlineData("<a href=\"https://ba\u200Bnk.example/\">x</a>", "https://bank.example/")]
    public void Normalize_UnicodeHosts_AreWrittenAscii(string html, string href)
    {
        var output = DescriptionHtml.Normalize(html);

        Assert.Contains($"href=\"{href}\"", output, StringComparison.Ordinal);
        Assert.Equal(output, DescriptionHtml.Normalize(output));
    }

    [Fact]
    public void Write_UnicodeHost_IsWrittenAscii()
    {
        var html = DescriptionHtml.Write([Line(ListKind.None, new DescriptionRun("apple", Link: new Uri("https://\u0430pple.com/login")))]);

        Assert.Equal("<a href=\"https://xn--pple-43d.com/login\">apple</a>", html);
    }

    [Theory]
    [InlineData(10_000, "x", false)]
    [InlineData(10_001, "x", true)]
    [InlineData(9_000, "&amp;", true)]
    [InlineData(5_001, "<b></b>x", true)]
    [InlineData(0, "x", false)]
    public void IsTooLong_WhenReadingCutTheDescriptionShort(int count, string piece, bool expected)
    {
        Assert.Equal(expected, DescriptionHtml.IsTooLong(string.Concat(Enumerable.Repeat(piece, count))));
    }

    // Review Focus 1: Direction Controls Never Reach Google In Body Text
    [Fact]
    public void Normalize_BidiControlsInText_AreDropped()
    {
        Assert.Equal("abcdefghi", DescriptionHtml.Normalize("a\u202Ab\u202Bc\u202Cd\u202De\u202Ef\u2066g\u2067h\u2068\u2069i"));
    }

    // Text split into runs reads back joined, so bare addresses are linked per line, not per run
    [Theory]
    [InlineData("x https:<span>//bank.example</span>")]
    [InlineData("<span>https://</span>bank.example")]
    [InlineData("<b>https:</b><b>//bank.example</b>")]
    [InlineData("<a href=\"https://evil.example\">https://bank</a>.example")]
    [InlineData("<a href=\"javascript:x\">https://bank</a>.example/login")]
    [InlineData("https://a\uFFFC.example")]
    [InlineData("https://bank.example\u202E")]
    public void Normalize_BareLinksAcrossRuns_IsIdempotent(string html)
    {
        var once = DescriptionHtml.Normalize(html);

        Assert.Equal(once, DescriptionHtml.Normalize(once));
    }

    // The reviewer's probe: every input stays inside the allowlist, carries no hidden header or hidden character, and is stable
    [Theory]
    [InlineData("<a href=\"https://evil.example\">bank\uFF0Eexample</a>")]
    [InlineData("<a href=\"https://evil.example\">bank\u3002example/login</a>")]
    [InlineData("<a href=\"https://evil.example\">bank\u2024example</a>")]
    [InlineData("<a href=\"https://evil.example\">//bank.example/login</a>")]
    [InlineData("<a href=\"https://evil.example\">Log in at bank.example</a>")]
    [InlineData("<a href=\"https://evil.example\">bank</a><a href=\"https://evil.example\">.example</a>")]
    [InlineData("<a href=\"https://evil.example\">https://</a><a href=\"https://evil.example\">bank.example</a>")]
    [InlineData("<a href=\"https://evil.example\">bank.example\u00A0</a>")]
    [InlineData("<a href=\"https://evil.example\">\u202Eelpmaxe.knab</a>")]
    [InlineData("<a href=\"https://ok.example/\u202Egnp.exe\">x</a>")]
    [InlineData("<a href=\"https://ok.example/a&#x0A;b&#x09;c\">x</a>")]
    [InlineData("<a href=\"https://ba\u200Bnk.example/\">x</a>")]
    [InlineData(@"<a href=""https:\evil.example\x"">x</a>")]
    [InlineData("<a href=\"//evil.example/x\">x</a>")]
    [InlineData("<a href=\" java&#x09;script:alert(1)\">x</a>")]
    [InlineData("<a href=\"&#106;avascript:alert(1)\">x</a>")]
    [InlineData("<a href=\"file:///c:/x\">x</a>")]
    [InlineData("<a href=\"https://bank.example@evil.example/\">x</a>")]
    [InlineData("<a href='https://ok.example/\"onmouseover=x'>x</a>")]
    [InlineData("<a href=\"https://\u0430pple.com/\">apple</a>")]
    [InlineData("<a href=\"mailto:a@b.example?%62cc=c@d.example&amp;body=hi\">mail</a>")]
    [InlineData("<a href=\"mailto:a@b.example?BCC=c@d.example\">mail</a>")]
    [InlineData("<a href=\"mailto:a@b.example,c@d.example?cc=e@f.example&amp;bcc=g@h.example&amp;to=i@j.example\">mail</a>")]
    [InlineData("<a href=\"mailto:a@b.example%3Fbcc=c@d.example\">mail</a>")]
    [InlineData("<a href=\"mailto:a@b.example?subject=x%26bcc%3Dc@d.example\">mail</a>")]
    [InlineData("<a href=\"mailto:a@b.example?subject=x;bcc=c@d.example\">mail</a>")]
    [InlineData("<a href=\"mailto:a@b.example?attach=C:x&amp;body=%0Dhi\">mail</a>")]
    [InlineData("<a href=\"mailto:a@b.example?%20bcc=c@d.example\">mail</a>")]
    [InlineData("<b><i>x</b>y</i><u>z<ol><li>q")]
    [InlineData("<ul><li>a<ol><li>b</li></ol>c</li></ul>")]
    [InlineData("<a href=\"https://evil.example\"><li>bank.example</a>")]
    [InlineData("https://bank.example\u202E")]
    [InlineData("<a href=\"https://evil.example\">bank<ul><li>.example</li></ul></a>")]
    public void Normalize_ReviewerProbe_StaysSafeAndStable(string html)
    {
        var output = DescriptionHtml.Normalize(html);

        Assert.DoesNotMatch(ForeignTag(), output);
        Assert.Equal(output, DescriptionHtml.Normalize(output));
        Assert.DoesNotContain("href=\"https://evil.example/\">bank", output, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"https://evil.example/\">https", output, StringComparison.Ordinal);
        Assert.DoesNotContain("@evil.example", output, StringComparison.Ordinal);
        Assert.False(Regex.IsMatch(output, @"href=""(?!https://|mailto:)"), output);
        Assert.False(Regex.IsMatch(output, @"href=""[^""]*(bcc|%62cc|to=|attach|;|%3F)", RegexOptions.IgnoreCase), output);
        Assert.False(output.Any(c => char.IsControl(c) || c is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069') or '\u200B'), output);
    }

    [Fact]
    public void Write_ReviewerProbeLinks_StaySafe()
    {
        Assert.Equal("<a href=\"mailto:a@b.example\">mail</a>", WriteLink("mail", "mailto:a@b.example?bcc=c@d.example&cc=x@y.example"));
        Assert.Equal("<a href=\"https://ok.example/%E2%80%AEgnp.exe\">x</a>", WriteLink("x", "https://ok.example/\u202Egnp.exe"));
        Assert.Equal("x", WriteLink("x", "data:text/html,hi"));
        Assert.Equal("x", WriteLink("x", "file:///c:/x"));
        Assert.Equal("x", WriteLink("x", "https://bank.example@evil.example/"));

        static string WriteLink(string text, string link) => DescriptionHtml.Write([Line(ListKind.None, new DescriptionRun(text, Link: new Uri(link)))]);
    }

    [Fact]
    public void Normalize_BidiControlInHref_IsNeverWrittenRaw()
    {
        var output = DescriptionHtml.Normalize("<a href=\"https://ok.example/‮gnp.exe\">Hi</a>");

        Assert.DoesNotContain('‮', output);
    }

    [Theory]
    [InlineData("<ul><li>a<ul><li>b</ul>c", "Bullet:a|Bullet:b|None:c")]
    [InlineData("<b><i>a</b>b</i>c<li>d", "None:*abc|Bullet:d")]
    [InlineData("<ul>\n    <li>a</li>\n    <li>b</li>\n</ul>\nc", "Bullet:a|Bullet:b|None:c")]
    [InlineData("<b>a</b>\r\n<br>\r\nb", "None:*a|None:b")]
    public void Lines_NestedUnclosedAndPrettyPrinted_ReadAsWritten(string html, string shape)
    {
        var once = DescriptionHtml.Normalize(html);

        Assert.Equal(shape, Shape(DescriptionHtml.Lines(html)));
        Assert.Equal(once, DescriptionHtml.Normalize(once));
    }

    [Fact]
    public void Normalize_EntityTricks_StayText()
    {
        const string html = "&lt;script&gt;alert(1)&lt;/script&gt; &amp;lt;b&amp;gt;";

        Assert.Equal(html, DescriptionHtml.Normalize(html));
    }

    [Fact]
    public void Normalize_Empty_IsEmpty()
    {
        Assert.Equal("", DescriptionHtml.Normalize(""));
        Assert.Equal("", DescriptionHtml.Normalize("<script></script><br><p></p>"));
    }

    [Theory]
    [InlineData("<a href='")]
    [InlineData("<ul><li><b><i>")]
    [InlineData("<")]
    [InlineData("&#")]
    public void Normalize_HugeHostileInput_StaysFast(string filler)
    {
        var html      = string.Concat(Enumerable.Repeat(filler, 1_000_000 / filler.Length));
        var stopwatch = Stopwatch.StartNew();

        var output = DescriptionHtml.Normalize(html);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), stopwatch.Elapsed.ToString());
        Assert.True(output.Length < 200_000);
    }

    // RTF Field Syntax In A Description Is Plain Text: Every Character Reaches The Editor (no field, no hidden link target)
    [Fact]
    public void Lines_RtfFieldText_KeepsEveryCharacter()
    {
        const string rtf = """{\rtf1{\field{\*\fldinst HYPERLINK "https://x"}{\fldrslt Click}}}""";

        var text = string.Concat(DescriptionHtml.Lines(rtf).SelectMany(l => l.Runs).Select(r => r.Text));

        Assert.Equal(rtf, text);
    }
}