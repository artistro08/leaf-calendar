using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LeafCalendar.Core.Events;

/// <summary>A piece of description text with its style; <see cref="Link"/> is set only for allowlisted links.</summary>
public sealed record DescriptionRun(string Text, bool Bold = false, bool Italic = false, bool Underline = false, Uri? Link = null);

/// <summary>
/// Turns Google's description HTML into styled text runs for native rendering (spec 4.5): bold, italic,
/// underline, lists, line breaks, and links. Nothing runs and nothing loads.
/// </summary>
/// <remarks>
/// Only <c>https</c> and <c>mailto</c> links become clickable; other links keep their text as plain text. Bare
/// <c>https://</c> addresses in the text are linked too. A link whose visible text is itself a web address for a
/// different host than the real target is not clickable, so display text can't disguise the destination. Unknown
/// tags are dropped (their text stays, inert). Runs of more than one blank line collapse to one. The input is
/// bounded before any regex runs, and the output is capped at 10,000 characters plus an ellipsis.
/// </remarks>
public static partial class DescriptionFormatter
{
    const int MaxLength = 10_000;
    const int MaxInput  = MaxLength * 4;

    /// <summary>Runs for an event's <c>description</c> field (empty when there is none).</summary>
    /// <exception cref="JsonException">The JSON is invalid.</exception>
    public static IReadOnlyList<DescriptionRun> FormatEvent(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("description", out var description)
            && description.ValueKind == JsonValueKind.String
                ? Format(description.GetString()!)
                : [];
    }

    /// <summary>Runs for description HTML.</summary>
    public static IReadOnlyList<DescriptionRun> Format(string html)
    {
        if (html.Length > MaxInput)
        {
            html = html[..MaxInput];
        }

        var runs      = new List<DescriptionRun>();
        var bold      = 0;
        var italic    = 0;
        var underline = 0;
        Uri? link     = null;
        var linkStart = 0;
        var position  = 0;

        foreach (Match tag in Tag().Matches(html))
        {
            AddText(html[position..tag.Index]);
            position = tag.Index + tag.Length;

            var closing = tag.Groups[1].Value == "/";
            var step    = closing ? -1 : 1;
            switch (tag.Groups[2].Value.ToLowerInvariant())
            {
                case "b" or "strong":
                    bold = Math.Max(0, bold + step);
                    break;
                case "i" or "em":
                    italic = Math.Max(0, italic + step);
                    break;
                case "u":
                    underline = Math.Max(0, underline + step);
                    break;
                case "a":
                    EndLink();
                    if (!closing)
                    {
                        link      = SafeLink(tag.Groups[3].Value);
                        linkStart = runs.Count;
                    }

                    break;
                case "br":
                    runs.Add(new DescriptionRun("\n"));
                    break;
                case "li":
                    runs.Add(new DescriptionRun(closing ? "\n" : "• "));
                    break;
                case "p" or "div" or "ul" or "ol" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    if (closing)
                    {
                        runs.Add(new DescriptionRun("\n"));
                    }

                    break;
            }
        }

        AddText(html[position..]);
        EndLink();
        return Tidy(runs);

        // Closes the open link; if its visible text is a web address for another host, the link is stripped
        void EndLink()
        {
            if (link is not null && DisguisesTarget(link, string.Concat(runs.Skip(linkStart).Select(r => r.Text))))
            {
                for (var i = linkStart; i < runs.Count; i++)
                {
                    runs[i] = runs[i] with { Link = null };
                }
            }

            link = null;
        }

        // Text between tags: decoded, and bare https links outside an <a> become links
        void AddText(string raw)
        {
            if (raw.Length == 0)
            {
                return;
            }

            var text  = WebUtility.HtmlDecode(raw);
            var style = new DescriptionRun("", bold > 0, italic > 0, underline > 0, link);
            if (link is not null)
            {
                runs.Add(style with { Text = text });
                return;
            }

            var start = 0;
            foreach (Match match in LinkSafety.HttpsLink().Matches(text))
            {
                var url = match.Value.TrimEnd('.', ',', ')', ';', '!', '?');
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    continue;
                }

                runs.Add(style with { Text = text[start..match.Index] });
                runs.Add(style with { Text = url, Link = uri });
                start = match.Index + url.Length;
            }

            runs.Add(style with { Text = text[start..] });
        }
    }

    // True when the visible text names a web address whose host differs from where the link really goes
    static bool DisguisesTarget(Uri target, string visible)
    {
        var shown = visible.Trim();
        var www   = shown.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
        if (!www && !shown.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Anything that looks like an address but doesn't parse as http(s) is treated as disguised
        if (!Uri.TryCreate(www ? "https://" + shown : shown, UriKind.Absolute, out var shownUri)
            || (shownUri.Scheme != Uri.UriSchemeHttps && shownUri.Scheme != Uri.UriSchemeHttp))
        {
            return true;
        }

        return target.Scheme != Uri.UriSchemeHttps
            || !string.Equals(shownUri.IdnHost, target.IdnHost, StringComparison.OrdinalIgnoreCase);
    }

    static Uri? SafeLink(string attributes)
    {
        var href = Href().Match(attributes);
        if (!href.Success)
        {
            return null;
        }

        var value = WebUtility.HtmlDecode(href.Groups[2].Success ? href.Groups[2].Value : href.Groups[3].Success ? href.Groups[3].Value : href.Groups[4].Value).Trim();
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && LinkSafety.IsClickableInDescription(uri) ? uri : null;
    }

    // Drops leading and trailing line breaks, keeps at most one blank line, drops empty runs, and caps the length
    static List<DescriptionRun> Tidy(List<DescriptionRun> runs)
    {
        var result   = new List<DescriptionRun>();
        var newlines = 0;
        var started  = false;
        var total    = 0;

        foreach (var run in runs)
        {
            var text = new StringBuilder();
            foreach (var c in run.Text)
            {
                if (c == '\r')
                {
                    continue;
                }

                if (c == '\n')
                {
                    newlines += started ? 1 : 0;
                    continue;
                }

                if (newlines > 0)
                {
                    text.Append('\n', Math.Min(newlines, 2));
                    newlines = 0;
                }

                started = true;
                text.Append(c);
            }

            if (text.Length == 0)
            {
                continue;
            }

            if (total + text.Length > MaxLength)
            {
                result.Add(run with { Text = text.ToString(0, MaxLength - total) + "…" });
                return result;
            }

            total += text.Length;
            result.Add(run with { Text = text.ToString() });
        }

        return result;
    }

    // One unbounded quantifier per position ("<" then spaces, optional "/", name), so scanning stays linear
    [GeneratedRegex(@"<\s*(/?)([a-zA-Z][a-zA-Z0-9]*)\b([^<>]*)>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"href\s*=\s*(""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex Href();
}
