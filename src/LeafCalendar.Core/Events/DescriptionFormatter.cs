using System.Net;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LeafCalendar.Core.Events;

/// <summary>The kind of list a description line or list marker belongs to.</summary>
public enum ListKind
{
    /// <summary>Not in a list.</summary>
    None,

    /// <summary>A bulleted list (<c>ul</c>).</summary>
    Bullet,

    /// <summary>A numbered list (<c>ol</c>).</summary>
    Numbered,
}

/// <summary>
/// A piece of description text with its style; <see cref="Link"/> is set only for allowlisted links.
/// <see cref="List"/> other than <see cref="ListKind.None"/> marks a list marker run, whose text is "• " or "N. " and may
/// start with line breaks.
/// </summary>
public sealed record DescriptionRun(string Text, bool Bold = false, bool Italic = false, bool Underline = false, Uri? Link = null, ListKind List = ListKind.None);

/// <summary>
/// Turns Google's description HTML into styled text runs for native rendering (spec 4.5): bold, italic,
/// underline, lists, line breaks, and links. Nothing runs and nothing loads.
/// </summary>
/// <remarks>
/// Only <c>https</c> and <c>mailto</c> links become clickable; other links keep their text as plain text. Bare
/// <c>https://</c> addresses in the text are linked too. A link whose visible text is itself a web address for a
/// different host than the real target is not clickable (a best-effort check; the UI also shows the real URL). Unknown
/// tags are dropped (their text stays, inert). Lists and list items start on a new line, and numbered list markers count.
/// Source line breaks right next to a tag that breaks the line aren't extra lines. Runs of more than one blank line collapse to one. The input is
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
    public static IReadOnlyList<DescriptionRun> Format(string html) => Format(html, out _);

    /// <summary>Runs for description HTML; <paramref name="capped"/> says whether the input or the text was cut short.</summary>
    internal static IReadOnlyList<DescriptionRun> Format(string html, out bool capped)
    {
        var tooLong = html.Length > MaxInput;
        if (tooLong)
        {
            html = html[..MaxInput];
        }

        var runs           = new List<DescriptionRun>();
        var lists          = new Stack<(ListKind Kind, int Count)>();
        var bold           = 0;
        var italic         = 0;
        var underline      = 0;
        Uri? link          = null;
        var linkStart      = 0;
        var position       = 0;
        var lineStart      = true;
        var previousBreaks = false;
        Uri? closedLink    = null;
        var closedStart    = 0;
        var closedEnd      = 0;

        foreach (Match tag in Tag().Matches(html))
        {
            var name    = tag.Groups[2].Value.ToLowerInvariant();
            var closing = tag.Groups[1].Value == "/";
            var step    = closing ? -1 : 1;

            // Tags that end or start a line
            var breaks = name is "br" or "li" or "ul" or "ol" || (closing && name is "p" or "div" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6");
            AddText(SourceText(html[position..tag.Index], previousBreaks, breaks));
            position       = tag.Index + tag.Length;
            previousBreaks = breaks;

            switch (name)
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
                    CloseLink();
                    if (!closing)
                    {
                        // Reopened on the same target with no text between: it reads as one link, so it's checked as one
                        var opened = SafeLink(tag.Groups[3].Value);
                        if (opened is not null && closedLink is not null && closedEnd == runs.Count && closedLink.AbsoluteUri == opened.AbsoluteUri)
                        {
                            (link, linkStart, closedLink) = (opened, closedStart, null);
                            break;
                        }

                        CheckClosedLink();
                        (link, linkStart) = (opened, runs.Count);
                    }

                    break;
                case "br":
                    Add(new DescriptionRun("\n"));
                    break;
                case "ul" or "ol" when closing:
                    lists.TryPop(out _);
                    Add(new DescriptionRun("\n"));
                    break;
                case "ul" or "ol":
                    lists.Push((name == "ul" ? ListKind.Bullet : ListKind.Numbered, 0));
                    NewLine();
                    break;
                case "li" when closing:
                    Add(new DescriptionRun("\n"));
                    break;
                case "li":
                    // A stray item outside any list reads as a bullet
                    var kind  = ListKind.Bullet;
                    var count = 0;
                    if (lists.TryPop(out var top))
                    {
                        (kind, count) = (top.Kind, top.Count + 1);
                        lists.Push((kind, count));
                    }

                    NewLine();
                    Add(new DescriptionRun(kind == ListKind.Numbered ? $"{count}. " : "• ", List: kind));
                    break;
                case "p" or "div" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    if (closing)
                    {
                        Add(new DescriptionRun("\n"));
                    }

                    break;
            }
        }

        AddText(SourceText(html[position..], previousBreaks, before: false));
        CloseLink();
        CheckClosedLink();
        var result = Tidy(runs, out var cut);
        capped = tooLong || cut;
        return result;

        // Adds a run, tracking whether the text so far ends at the start of a line (list markers don't count as text)
        void Add(DescriptionRun run)
        {
            if (run.Text.Length == 0)
            {
                return;
            }

            CheckClosedLink();
            runs.Add(run);
            var text = run.Text.AsSpan().TrimEnd(" \t\r");
            if (run.List == ListKind.None && text.Length > 0)
            {
                lineStart = text[^1] == '\n';
            }
        }

        // Lists and their items start on a new line
        void NewLine()
        {
            if (!lineStart)
            {
                Add(new DescriptionRun("\n"));
            }
        }

        // Closes the open link. Its check waits until something else follows, since the same link reopened right away reads
        // as one link with it
        void CloseLink()
        {
            if (link is not null)
            {
                (closedLink, closedStart, closedEnd) = (link, linkStart, runs.Count);
            }

            link = null;
        }

        // Checks the closed link: if its visible text is a web address for another host, the link is stripped
        void CheckClosedLink()
        {
            // List markers aren't part of the link's visible text
            if (closedLink is not null && DisguisesTarget(closedLink, string.Concat(runs.Skip(closedStart).Take(closedEnd - closedStart).Where(r => r.List == ListKind.None).Select(r => r.Text))))
            {
                for (var i = closedStart; i < closedEnd; i++)
                {
                    runs[i] = runs[i] with { Link = null };
                }
            }

            closedLink = null;
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
                Add(style with { Text = text });
                return;
            }

            foreach (var piece in WithBareLinks(style with { Text = text }))
            {
                Add(piece);
            }
        }
    }

    /// <summary>An unlinked run split so its bare <c>https://</c> addresses become links (a linked run comes back as is).</summary>
    internal static IEnumerable<DescriptionRun> WithBareLinks(DescriptionRun run)
    {
        if (run.Link is not null)
        {
            yield return run;
            yield break;
        }

        var text  = run.Text;
        var start = 0;
        foreach (Match match in LinkSafety.HttpsLink().Matches(text))
        {
            var url = match.Value.TrimEnd('.', ',', ')', ';', '!', '?');
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !LinkSafety.IsClickableInDescription(uri))
            {
                continue;
            }

            yield return run with { Text = text[start..match.Index] };
            yield return run with { Text = url, Link = uri };
            start = match.Index + url.Length;
        }

        yield return run with { Text = text[start..] };
    }

    // Source formatting next to a tag that breaks the line isn't a line of its own: whitespace-only text there is
    // dropped, and so is one line break right after or right before the tag ("<b>a</b>\n<br>\nb" reads "a", "b")
    static string SourceText(string raw, bool after, bool before)
    {
        if (!after && !before)
        {
            return raw;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        var start = 0;
        var end   = raw.Length;

        // One Leading Line Break
        if (after)
        {
            var i = 0;
            while (i < end && raw[i] is ' ' or '\t')
            {
                i++;
            }

            var broke = i < end && raw[i] is '\r' or '\n';
            i += i + 1 < end && raw[i] == '\r' && raw[i + 1] == '\n' ? 2 : broke ? 1 : 0;
            start = broke ? i : 0;
        }

        // One Trailing Line Break
        if (before)
        {
            var i = end;
            while (i > start && raw[i - 1] is ' ' or '\t')
            {
                i--;
            }

            var broke = i > start && raw[i - 1] is '\r' or '\n';
            i -= i - 1 > start && raw[i - 1] == '\n' && raw[i - 2] == '\r' ? 2 : broke ? 1 : 0;
            end = broke ? i : end;
        }

        return raw[start..end];
    }

    // True when the visible text names a web address whose host differs from where the link really goes
    // True when the text is the mail link's own address (name ignoring case, host compared in ASCII form)
    static bool SameAddress(Uri target, string shown) =>
        Uri.TryCreate("mailto:" + shown, UriKind.Absolute, out var shownUri)
            && string.Equals(Uri.UnescapeDataString(shownUri.UserInfo), Uri.UnescapeDataString(target.UserInfo), StringComparison.OrdinalIgnoreCase)
            && LinkSafety.TryIdnHost(shownUri, out var shownHost)
            && LinkSafety.TryIdnHost(target, out var targetHost)
            && string.Equals(shownHost, targetHost, StringComparison.OrdinalIgnoreCase);

    internal static bool DisguisesTarget(Uri target, string visible)
    {
        // Look-alike forms (fullwidth letters, other dots) read as what they look like; text that can't be normalized isn't trusted
        string folded;
        try
        {
            folded = visible.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            return true;
        }

        // Invisible characters (zero-width, bidi controls, BOM), a leading bullet, and leading slashes or dots ("//bank.example") can't dodge the check
        var shown = new string(folded.Where(c => char.GetUnicodeCategory(c) is not (UnicodeCategory.Format or UnicodeCategory.Control))
                .Select(c => c is '。' or '｡' or '．' or '․' ? '.' : c).ToArray())
            .Trim().TrimStart('•', ' ').Trim().TrimStart('/', '.');
        var web = shown.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        if (!web && shown.Any(char.IsWhiteSpace))
        {
            return false;
        }

        // A mailto link is disguised by web-address text, or by text that's another email address
        if (target.Scheme == Uri.UriSchemeMailto)
        {
            return web || shown.StartsWith("www.", StringComparison.OrdinalIgnoreCase) || (shown.Contains('@', StringComparison.Ordinal) && !SameAddress(target, shown));
        }

        // Scheme-less text counts as an address when its host part has an interior dot ("bank.example/login")
        var host = shown.Split('/', '?', '#')[0];
        var dot  = host.IndexOf('.');
        if (!web && (dot < 1 || dot >= host.Length - 1))
        {
            return false;
        }

        // Anything that looks like an address but doesn't parse as http(s), or hides userinfo, is treated as disguised
        if (!Uri.TryCreate(web ? shown : "https://" + shown, UriKind.Absolute, out var shownUri)
            || (shownUri.Scheme != Uri.UriSchemeHttps && shownUri.Scheme != Uri.UriSchemeHttp)
            || shownUri.UserInfo.Length > 0)
        {
            return true;
        }

        // A host that can't be normalized is never trusted
        return !LinkSafety.TryIdnHost(shownUri, out var shownHost)
            || !LinkSafety.TryIdnHost(target, out var targetHost)
            || !string.Equals(shownHost, targetHost, StringComparison.OrdinalIgnoreCase);
    }

    static Uri? SafeLink(string attributes)
    {
        // Attributes are read left to right, so "href=" inside another attribute's quoted value is never a name
        string? value = null;
        foreach (Match attribute in Attribute().Matches(attributes))
        {
            if (attribute.Groups[1].Value.Equals("href", StringComparison.OrdinalIgnoreCase))
            {
                value = attribute.Groups[2].Success ? attribute.Groups[2].Value : attribute.Groups[3].Success ? attribute.Groups[3].Value : attribute.Groups[4].Value;
                break;
            }
        }

        if (value is null)
        {
            return null;
        }

        value = WebUtility.HtmlDecode(value).Trim();
        if (value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return TrimMailto(value);
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && LinkSafety.IsClickableInDescription(uri) ? uri : null;
    }

    // Keeps only the subject from a mailto link's query, so an invite can't add cc, bcc, a body, or attachments
    // Recipients must be plain addresses, so one that hides "?bcc=" or another header behind encoding makes the link unclickable.
    // A kept value can't hold "&", "=", or ";" once decoded either (a client that decodes before splitting would read a new field)
    internal static Uri? TrimMailto(string value)
    {
        var parts = value.Split('?', 2);
        if (!parts[0].StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || !AreAddresses(parts[0]["mailto:".Length..], allowEmpty: true))
        {
            return null;
        }

        var kept = parts.Length < 2
            ? []
            : parts[1].Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .Where(p => p.Length == 2 && p[0].Equals("subject", StringComparison.OrdinalIgnoreCase)
                    && Uri.UnescapeDataString(p[1]) is var decoded && !decoded.Any(c => char.IsControl(c) || c is '&' or '=' or ';'))
                .Select(p => $"{p[0]}={p[1]}")
                .ToList();
        // Several recipients: Uri takes only one "@" in the address part, so all but the last "@" and the commas are
        // percent-encoded (mail apps decode them back to "a@b.example,c@d.example")
        var to    = Uri.UnescapeDataString(parts[0]["mailto:".Length..]);
        var at    = to.LastIndexOf('@');
        var clean = "mailto:" + (to.Contains(',', StringComparison.Ordinal) ? to[..at].Replace("@", "%40", StringComparison.Ordinal).Replace(",", "%2C", StringComparison.Ordinal) + to[at..] : to)
            + (kept.Count > 0 ? "?" + string.Join("&", kept) : "");
        if (!Uri.TryCreate(clean, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeMailto)
        {
            return null;
        }

        // Backstop: no encoded line break may survive in the final link
        return uri.AbsoluteUri.Contains("%0D", StringComparison.OrdinalIgnoreCase) || uri.AbsoluteUri.Contains("%0A", StringComparison.OrdinalIgnoreCase)
            ? null
            : uri;
    }

    // A comma-separated list of plain addresses ("name@host": letters, digits, ".", "-", "_", "+"), checked once decoded
    static bool AreAddresses(string encoded, bool allowEmpty)
    {
        if (encoded.Length == 0)
        {
            return allowEmpty;
        }

        return Uri.UnescapeDataString(encoded).Split(',').All(address =>
        {
            var at = address.IndexOf('@', StringComparison.Ordinal);
            return at > 0 && at == address.LastIndexOf('@') && at < address.Length - 1
                && address.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '+' or '@');
        });
    }

    // Drops leading and trailing line breaks, keeps at most one blank line, drops empty runs, and caps the length
    static List<DescriptionRun> Tidy(List<DescriptionRun> runs, out bool cut)
    {
        var result   = new List<DescriptionRun>();
        cut          = false;
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
                cut = true;
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

    // name=value pairs; the lookbehind starts matches only at the beginning of a name (keeps it linear and skips "data-href")
    [GeneratedRegex(@"(?<![\w:-])([\w:-]+)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>][^\s>]*)|[""'][^<>]*)")]
    private static partial Regex Attribute();
}
