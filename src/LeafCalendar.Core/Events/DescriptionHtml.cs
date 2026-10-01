using System.Net;
using System.Text;

namespace LeafCalendar.Core.Events;

/// <summary>One line of a description: its styled runs and the list it sits in.</summary>
public sealed record DescriptionLine(IReadOnlyList<DescriptionRun> Runs, ListKind List);

/// <summary>
/// Description HTML as lines for the editor, and back to HTML for Google (spec 4.5, 7.2). Reading goes through
/// <see cref="DescriptionFormatter"/>, the one parser for untrusted description HTML. Writing emits only <c>b</c>, <c>i</c>,
/// <c>u</c>, <c>br</c>, <c>ul</c>, <c>ol</c>, <c>li</c>, and <c>a href</c> for links <see cref="LinkSafety.IsClickableInDescription"/>
/// allows, with every bit of text HTML-encoded.
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

            // Text: cleaned and its link checked the way Write does it, then bare addresses linked the way reading it back
            // links them, so a line holds exactly what writing it and reading it back gives
            var parts = run.Text.Split('\n');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    EndLine();
                }

                var piece = run with { Text = Clean(parts[i]) };
                runs.AddRange(DescriptionFormatter.WithBareLinks(piece with { Link = SafeLink(piece) }).Where(r => r.Text.Length > 0));
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

        // Closes the current line. Spaces at either end of a line don't read back next to a line break, so they're trimmed.
        // An empty plain line isn't kept first, right after a list item (from the list's closing tags), or right after
        // another one (the reader keeps at most one blank line)
        void EndLine()
        {
            var first   = runs.FindIndex(r => !string.IsNullOrWhiteSpace(r.Text));
            var last    = runs.FindLastIndex(r => !string.IsNullOrWhiteSpace(r.Text));
            var kept    = first < 0 ? [] : runs.GetRange(first, last - first + 1);
            var noBlank = lines.Count == 0 || lines[^1].List != ListKind.None || lines[^1].Runs.Count == 0;

            if (kept.Count > 0)
            {
                kept[0]  = kept[0] with { Text = kept[0].Text.TrimStart() };
                kept[^1] = kept[^1] with { Text = kept[^1].Text.TrimEnd() };
            }

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

    /// <summary>Google's HTML in Leaf's subset: equal results mean the same description.</summary>
    public static string Normalize(string html) => Write(Lines(html));

    static void AppendRuns(StringBuilder html, IReadOnlyList<DescriptionRun> runs)
    {
        foreach (var run in runs)
        {
            var text = Clean(run.Text);
            if (text.Length == 0)
            {
                continue;
            }

            var link = SafeLink(run with { Text = text });
            if (link is not null)
            {
                html.Append("<a href=\"").Append(WebUtility.HtmlEncode(link.AbsoluteUri)).Append("\">");
            }

            html.Append(run.Bold ? "<b>" : "").Append(run.Italic ? "<i>" : "").Append(run.Underline ? "<u>" : "");
            html.Append(WebUtility.HtmlEncode(text));
            html.Append(run.Underline ? "</u>" : "").Append(run.Italic ? "</i>" : "").Append(run.Bold ? "</b>" : "");

            if (link is not null)
            {
                html.Append("</a>");
            }
        }
    }

    // Lines can come from anywhere (the editor too), so a run's link gets the parser's checks again, against the run's own
    // text (each run is written as its own link, and that's how it reads back)
    static Uri? SafeLink(DescriptionRun run) =>
        run.Link is not { } link || !LinkSafety.IsClickableInDescription(link) || DescriptionFormatter.DisguisesTarget(link, run.Text) ? null
        : link.Scheme == Uri.UriSchemeMailto ? DescriptionFormatter.TrimMailto(link.OriginalString)
        : link;

    // Object placeholders (pasted pictures) and control characters other than tab never reach Google
    static string Clean(string text) => new([.. text.Where(c => c != '￼' && (c == '\t' || !char.IsControl(c)))]);
}
