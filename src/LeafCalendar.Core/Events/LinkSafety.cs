using System.Text;
using System.Text.RegularExpressions;

namespace LeafCalendar.Core.Events;

/// <summary>Video meeting services Leaf recognizes by host.</summary>
public enum MeetingProvider
{
    /// <summary>meet.google.com.</summary>
    GoogleMeet,

    /// <summary>zoom.us, zoomgov.com.</summary>
    Zoom,

    /// <summary>teams.microsoft.com, teams.live.com.</summary>
    Teams,

    /// <summary>webex.com.</summary>
    Webex,

    /// <summary>around.co.</summary>
    Around,

    /// <summary>whereby.com.</summary>
    Whereby,

    /// <summary>bluejeans.com.</summary>
    BlueJeans,

    /// <summary>doxy.me.</summary>
    DoxyMe,
}

/// <summary>
/// Which links from events may be opened (spec 4.5 and 4.8). Anyone can send an invite, so every link is hostile
/// until checked.
/// </summary>
/// <remarks>
/// Launching allows only <c>https</c> and the meeting app schemes. Descriptions allow only <c>https</c> and
/// <c>mailto</c>. Meeting hosts are matched on the parsed <see cref="Uri.IdnHost"/>, exactly or as a subdomain
/// of a registered domain, never by substring, so <c>meet.google.com.evil.example</c> and
/// <c>https://meet.google.com@evil.example</c> are not Meet.
/// </remarks>
public static partial class LinkSafety
{
    static readonly string[] LaunchSchemes = ["https", "zoommtg", "zoomus", "msteams", "webex"];

    static readonly (string Domain, MeetingProvider Provider)[] MeetingHosts =
    [
        ("meet.google.com", MeetingProvider.GoogleMeet),
        ("zoom.us", MeetingProvider.Zoom),
        ("zoomgov.com", MeetingProvider.Zoom),
        ("teams.microsoft.com", MeetingProvider.Teams),
        ("teams.live.com", MeetingProvider.Teams),
        ("webex.com", MeetingProvider.Webex),
        ("around.co", MeetingProvider.Around),
        ("whereby.com", MeetingProvider.Whereby),
        ("bluejeans.com", MeetingProvider.BlueJeans),
        ("doxy.me", MeetingProvider.DoxyMe),
    ];

    /// <summary>True for <c>https</c> and the meeting app schemes (<c>zoommtg</c>, <c>zoomus</c>, <c>msteams</c>, <c>webex</c>).</summary>
    public static bool CanLaunch(Uri uri) =>
        uri.IsAbsoluteUri && LaunchSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase)
            && (uri.Scheme != Uri.UriSchemeHttps || TryIdnHost(uri, out _));

    /// <summary>True for links a description may make clickable: <c>https</c> (without a user name, which can pose as a host) and <c>mailto</c>.</summary>
    public static bool IsClickableInDescription(Uri uri) =>
        uri.IsAbsoluteUri && ((uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && TryIdnHost(uri, out _)) || uri.Scheme == Uri.UriSchemeMailto);

    /// <summary>The meeting service an <c>https</c> link belongs to, or null.</summary>
    public static MeetingProvider? ProviderOf(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        if (!TryIdnHost(uri, out var idnHost))
        {
            return null;
        }

        var host = idnHost.TrimEnd('.').ToLowerInvariant();
        foreach (var (domain, provider) in MeetingHosts)
        {
            if (host == domain || host.EndsWith("." + domain, StringComparison.Ordinal))
            {
                return provider;
            }
        }

        return null;
    }

    /// <summary>
    /// The link's host in IDN (ASCII) form. <see cref="Uri.IdnHost"/> throws for hosts with characters that are invalid
    /// in internationalized names (U+2024, U+FDD0, U+FFFF) even though <see cref="Uri"/> accepted the link; this returns
    /// false instead, and callers treat that as "not allowlisted".
    /// </summary>
    public static bool TryIdnHost(Uri uri, out string host)
    {
        host = "";
        if (!uri.IsAbsoluteUri)
        {
            return false;
        }

        try
        {
            host = uri.IdnHost;
            return true;
        }
        catch (Exception e) when (e is UriFormatException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// A link's address as Leaf shows it (tooltips, "Video call:"), matching what <c>LaunchAsync</c> opens. A non-ASCII
    /// host is shown in its ASCII (punycode) form, so a look-alike such as Cyrillic "аpple.com" can't pass for the real
    /// site, and invisible or direction-changing characters are removed. Null when the host has no ASCII form (such a
    /// link is never allowlisted, so it shouldn't be shown as clickable).
    /// </summary>
    public static string? DisplayForm(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!TryIdnHost(uri, out var asciiHost))
        {
            return null;
        }

        string text;
        try
        {
            text = uri.AbsoluteUri;
        }
        catch (Exception e) when (e is UriFormatException or InvalidOperationException)
        {
            return null;
        }

        // Swap A Unicode Host For Its ASCII Form (the host comes before anything else that could match)
        var host = uri.Host;
        var at   = host.Length > 0 && host != asciiHost ? text.IndexOf(host, StringComparison.Ordinal) : -1;
        if (at >= 0)
        {
            text = string.Concat(text.AsSpan(0, at), asciiHost, text.AsSpan(at + host.Length));
        }

        return new string(text.Where(c => !IsHiddenCharacter(c)).ToArray());
    }

    /// <summary>
    /// Where a description link really goes, to show after its text: the ASCII host (the address for <c>mailto</c>).
    /// Null when the text already shows that host or address, or the link has no ASCII form.
    /// </summary>
    public static string? HostNote(Uri link, string text)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(text);

        if (!TryIdnHost(link, out var host) || host.Length == 0)
        {
            return null;
        }

        var shown = new string(text.Where(c => !IsHiddenCharacter(c)).ToArray()).Trim();

        // Mail: The Text Is The Address
        if (link.Scheme == Uri.UriSchemeMailto)
        {
            var address = link.UserInfo.Length > 0 ? $"{Uri.UnescapeDataString(link.UserInfo)}@{host}" : host;
            return string.Equals(shown, address, StringComparison.OrdinalIgnoreCase) ? null : address;
        }

        // Web: The Text Is The Host, Or An Address On It
        var asText = shown.Contains("://", StringComparison.Ordinal) ? shown : "https://" + shown;
        var same   = Uri.TryCreate(asText, UriKind.Absolute, out var shownUri)
            && (shownUri.Scheme == Uri.UriSchemeHttps || shownUri.Scheme == Uri.UriSchemeHttp)
            && shownUri.UserInfo.Length == 0
            && TryIdnHost(shownUri, out var shownHost)
            && string.Equals(shownHost, host, StringComparison.OrdinalIgnoreCase)
            && Ascii.IsValid(shownUri.Host);

        // A non-ASCII host in the text (a look-alike such as Cyrillic "аpple.com") still gets the note, in ASCII
        return same ? null : host;
    }

    // Direction controls (U+202A-202E, U+2066-2069, U+200E/F, U+061C) and zero-width characters (U+200B-200D, U+2060, U+FEFF)
    static bool IsHiddenCharacter(char c) =>
        c is (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩') or (>= '​' and <= '‏') or '؜' or '⁠' or '﻿';

    /// <summary>The link to open for Join: Meet gets <c>authuser=&lt;email&gt;</c> so the right Google account joins (spec 8.5); others open as-is.</summary>
    public static Uri JoinUri(Uri conference, string accountEmail)
    {
        if (ProviderOf(conference) != MeetingProvider.GoogleMeet || accountEmail.Length == 0)
        {
            return conference;
        }

        // Port -1 keeps UriBuilder from writing ":443" into the address it hands to the browser
        var builder = new UriBuilder(conference) { Port = conference.IsDefaultPort ? -1 : conference.Port };
        var query  = builder.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith("authuser=", StringComparison.OrdinalIgnoreCase))
            .Append("authuser=" + Uri.EscapeDataString(accountEmail));
        builder.Query = string.Join("&", query);
        return builder.Uri;
    }

    /// <summary>The first link in <paramref name="text"/> on a known meeting host (pasted Zoom, Teams, Webex, ... links).</summary>
    public static Uri? FindMeetingLink(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        // Bounded input, so a huge invite can't stall the UI thread
        foreach (Match match in HttpsLink().Matches(text.Length > 40_000 ? text[..40_000] : text))
        {
            if (Uri.TryCreate(match.Value.TrimEnd('.', ',', ')', ';', '!', '?'), UriKind.Absolute, out var uri) && ProviderOf(uri) is not null)
            {
                return uri;
            }
        }

        return null;
    }

    /// <summary>
    /// A <c>mailto:</c> link to all the addresses with the event title as the subject ("Email guests"). The first address
    /// is the link's own; the rest go in a <c>to=</c> field (RFC 6068), because <see cref="Uri"/> reads the part before
    /// the query as one address.
    /// </summary>
    /// <remarks>
    /// Guest data comes from the invite, so it never throws: addresses that aren't a plain single <c>local@domain</c>
    /// (extra <c>@</c>, whitespace, control characters, or characters that could add mail fields) are dropped, control
    /// characters are stripped from the subject, and if <see cref="Uri"/> won't accept the tidy form the addresses go
    /// in a <c>to=</c> field instead. Returns null when no usable address remains (the UI hides the action).
    /// </remarks>
    public static Uri? MailtoGuests(IReadOnlyList<string> emails, string subject)
    {
        var safe = emails.Where(IsPlainAddress).ToList();
        if (safe.Count == 0)
        {
            return null;
        }

        var cleanSubject = Uri.EscapeDataString(new string(subject.Where(c => !char.IsControl(c)).ToArray()));
        var first        = Uri.EscapeDataString(safe[0]).Replace("%40", "@", StringComparison.Ordinal);
        var others       = safe.Count > 1 ? "to=" + string.Join(",", safe.Skip(1).Select(Uri.EscapeDataString)) + "&" : "";
        if (Uri.TryCreate($"mailto:{first}?{others}subject={cleanSubject}", UriKind.Absolute, out var mailto))
        {
            return mailto;
        }

        // Fallback: every address in the to= field
        return Uri.TryCreate($"mailto:?to={string.Join(",", safe.Select(Uri.EscapeDataString))}&subject={cleanSubject}", UriKind.Absolute, out var fallback)
            ? fallback
            : null;
    }

    // One "@", something on both sides, and nothing that could split, add, or inject mail fields
    static bool IsPlainAddress(string email)
    {
        var at = email.IndexOf('@');
        return at > 0
            && at < email.Length - 1
            && email.IndexOf('@', at + 1) < 0
            && !email.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || ",;<>()[]?&%=#\"'\\".Contains(c, StringComparison.Ordinal));
    }

    /// <summary>A Google Maps search for a location. The Bing Maps choice (spec 9) arrives with the settings page in Milestone 5.</summary>
    public static Uri MapsSearch(string location) =>
        new("https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString(location));

    [GeneratedRegex(@"https://[^\s<>""']+", RegexOptions.IgnoreCase)]
    internal static partial Regex HttpsLink();
}
