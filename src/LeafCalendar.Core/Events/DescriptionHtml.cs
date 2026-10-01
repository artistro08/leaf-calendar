using System.Net;
using System.Text;

namespace LeafCalendar.Core.Events;

/// <summary>One line of a description: its styled runs and the list it sits in.</summary>
public sealed record DescriptionLine(IReadOnlyList<DescriptionRun> Runs, ListKind List);

/// <summary>
/// Description HTML as lines for the editor, and back to HTML for Google (spec 4.5, 7.2). Reading goes through
/// <see cref="DescriptionFormatter"/>, the one parser for untrusted description HTML. Writing emits only <c>b</c>, <c>i</c>,
/// <c>u</c>, <c>br</c>, <c>ul</c>, <c>ol</c>, <c>li</c>, and <c>a href</c> for links <see cref="LinkSafety.IsClickableInDescription"/>
/// allows (written with an ASCII host, and never when the link's text names another address), with every bit of text
/// HTML-encoded.
/// </summary>
/// <remarks>
/// Nested lists flatten to one level, and a blank line right after a list isn't kept (a list's closing tags already
/// read as a line break). <see cref="Normalize"/> is idempotent, so comparing normalized HTML tells whether a
/// description really changed.
/// </remarks>
public static class DescriptionHtml
{
    /// <summary>The description as lines (list markers removed; their kind is on the line).</summary>
    public static IReadOnlyList<DescriptionLine> Lines(string html)
    {
        var lines = new List<DescriptionLine>();
        var runs  = new List<DescriptionRun>();
        var list  = ListKind.None;

        foreach (var run in DescriptionFormatter.Format(html))
        {
            // Marker: its leading line breaks end lines, then the next line is a list item
            if (run.List != ListKind.None)
            {
                foreach (var _ in run.Text.Where(c => c == '\n'))
                {
                    EndLine();
                }

                list = run.List;
                continue;
            }

            var parts = run.Text.Split('\n');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    EndLine();
                }

                runs.Add(run with { Text = parts[i] });
            }
        }

        if (runs.Count > 0 || list != ListKind.None)
        {
            EndLine();
        }

        // Trailing blank lines don't read back, so they aren't lines
        while (lines.Count > 0 && lines[^1] is { List: ListKind.None, Runs.Count: 0 })
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;

        // Closes the current line as writing it and reading it back gives it. An empty plain line isn't kept first, right
        // after a list item (from the list's closing tags), or right after another one (the reader keeps one blank line)
        void EndLine()
        {
            var kept    = ReadBack(runs);
            var noBlank = lines.Count == 0 || lines[^1].List != ListKind.None || lines[^1].Runs.Count == 0;

            if (!(kept.Count == 0 && list == ListKind.None && noBlank))
            {
                lines.Add(new DescriptionLine(kept, list));
            }

            runs.Clear();
            list = ListKind.None;
        }
    }

    /// <summary>Lines as Google description HTML, using only the allowlisted tags.</summary>
    public static string Write(IReadOnlyList<DescriptionLine> lines)
    {
        var html          = new StringBuilder();
        var open          = ListKind.None;
        var previousPlain = false;
        var previousEmpty = false;

        foreach (var line in lines)
        {
            // Close A List That Ends Or Changes Kind
            if (open != ListKind.None && line.List != open)
            {
                html.Append(open == ListKind.Bullet ? "</ul>" : "</ol>");
                open = ListKind.None;
            }

            // List Item
            if (line.List != ListKind.None)
            {
                if (open == ListKind.None)
                {
                    // A list starts its own line, so a blank line before it needs one more break
                    if (previousPlain && previousEmpty)
                    {
                        html.Append("<br>");
                    }

                    html.Append(line.List == ListKind.Bullet ? "<ul>" : "<ol>");
                    open = line.List;
                }

                html.Append("<li>");
                AppendRuns(html, line.Runs);
                html.Append("</li>");
                previousPlain = false;
                continue;
            }

            // Plain Line (a list's closing tag already breaks the line)
            if (previousPlain)
            {
                html.Append("<br>");
            }

            var length = html.Length;
            AppendRuns(html, line.Runs);
            previousPlain = true;
            previousEmpty = html.Length == length;
        }

        if (open != ListKind.None)
        {
            html.Append(open == ListKind.Bullet ? "</ul>" : "</ol>");
        }

        return html.ToString();
    }

    /// <summary>
    /// True when reading the description cut it short (over 40,000 characters of HTML, or over 10,000 of text). Such a
    /// description can't be edited in Leaf: writing it back would delete the rest on Google.
    /// </summary>
    public static bool IsTooLong(string html)
    {
        DescriptionFormatter.Format(html, out var capped);
        return capped;
    }

    /// <summary>Google's HTML in Leaf's subset: equal results mean the same description.</summary>
    public static string Normalize(string html) => Write(Lines(html));

    static void AppendRuns(StringBuilder html, IReadOnlyList<DescriptionRun> runs)
    {
        foreach (var run in Safe(runs))
        {
            if (run.Link is not null)
            {
                html.Append("<a href=\"").Append(WebUtility.HtmlEncode(run.Link.AbsoluteUri)).Append("\">");
            }

            html.Append(run.Bold ? "<b>" : "").Append(run.Italic ? "<i>" : "").Append(run.Underline ? "<u>" : "");
            html.Append(WebUtility.HtmlEncode(run.Text));
            html.Append(run.Underline ? "</u>" : "").Append(run.Italic ? "</i>" : "").Append(run.Bold ? "</b>" : "");

            if (run.Link is not null)
            {
                html.Append("</a>");
            }
        }
    }

    // A line's runs as Write writes them: text cleaned, empty runs dropped, and links checked again (lines can come from
    // anywhere, the editor too). Neighboring runs with one target are written as neighboring links, which read back as one
    // link, so they're checked for a disguise together
    static List<DescriptionRun> Safe(IReadOnlyList<DescriptionRun> runs)
    {
        var safe = runs
            .Select(r => r with { Text = Clean(r.Text) })
            .Where(r => r.Text.Length > 0)
            .Select(r => r with { Link = SafeLink(r.Link) })
            .ToList();

        for (var start = 0; start < safe.Count;)
        {
            var link = safe[start].Link;
            var end  = start + 1;
            while (link is not null && end < safe.Count && safe[end].Link?.AbsoluteUri == link.AbsoluteUri)
            {
                end++;
            }

            if (link is not null && DescriptionFormatter.DisguisesTarget(link, string.Concat(safe.Skip(start).Take(end - start).Select(r => r.Text))))
            {
                for (var i = start; i < end; i++)
                {
                    safe[i] = safe[i] with { Link = null };
                }
            }

            start = end;
        }

        return safe;
    }

    // A line's runs as writing them and reading them back gives them: safe (above), spaces at either end trimmed (they
    // don't read back next to a line break), and unlinked text with one style joined before bare addresses are linked
    // (it's written as one piece of text, so that's how it reads back)
    static List<DescriptionRun> ReadBack(IReadOnlyList<DescriptionRun> runs)
    {
        var safe  = Safe(runs);
        var first = safe.FindIndex(r => !string.IsNullOrWhiteSpace(r.Text));
        var last  = safe.FindLastIndex(r => !string.IsNullOrWhiteSpace(r.Text));
        if (first < 0)
        {
            return [];
        }

        var kept = safe.GetRange(first, last - first + 1);
        kept[0]  = kept[0] with { Text = kept[0].Text.TrimStart() };
        kept[^1] = kept[^1] with { Text = kept[^1].Text.TrimEnd() };

        // A new bare link can sit next to a link with the same target, and the two are checked together on the next read;
        // so again until nothing changes (a second pass only unlinks, and bare links never pair up, so this ends quickly)
        var linked = WithJoinedBareLinks(kept);
        for (var pass = 0; pass < 4; pass++)
        {
            var again = WithJoinedBareLinks(Safe(linked));
            if (again.SequenceEqual(linked))
            {
                break;
            }

            linked = again;
        }

        return linked;
    }

    // Unlinked text with one style joined (it's written as one piece of text, so that's how it reads back), then bare
    // addresses linked the way reading it back links them
    static List<DescriptionRun> WithJoinedBareLinks(List<DescriptionRun> runs)
    {
        var joined = new List<DescriptionRun>();
        foreach (var run in runs)
        {
            if (joined.Count > 0 && run.Link is null && joined[^1] is { Link: null } previous
                && (previous.Bold, previous.Italic, previous.Underline) == (run.Bold, run.Italic, run.Underline))
            {
                joined[^1] = previous with { Text = previous.Text + run.Text };
                continue;
            }

            joined.Add(run);
        }

        return [.. joined.SelectMany(DescriptionFormatter.WithBareLinks).Where(r => r.Text.Length > 0)];
    }

    // The link as Google gets it: allowlisted, ASCII host with nothing hidden (LinkSafety.DisplayForm), mailto trimmed
    static Uri? SafeLink(Uri? link)
    {
        if (link is null || !LinkSafety.IsClickableInDescription(link) || LinkSafety.DisplayForm(link) is not { } shown)
        {
            return null;
        }

        if (link.Scheme == Uri.UriSchemeMailto)
        {
            return DescriptionFormatter.TrimMailto(shown);
        }

        return Uri.TryCreate(shown, UriKind.Absolute, out var ascii) && LinkSafety.IsClickableInDescription(ascii) ? ascii : null;
    }

    // Object placeholders (pasted pictures), control characters other than tab, and direction controls (embedding,
    // override, isolate) never reach Google
    static string Clean(string text) => new([.. text.Where(c => c != '￼' && (c == '\t' || !char.IsControl(c))
        && c is not ((>= '‪' and <= '‮') or (>= '⁦' and <= '⁩')))]);
}
