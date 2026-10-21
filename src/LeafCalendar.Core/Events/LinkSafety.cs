using System.Text;
using System.Text.RegularExpressions;
using LeafCalendar.Core.Settings;

namespace LeafCalendar.Core.Events;

/// <summary>Video meeting services Leaf Calendar recognizes by host.</summary>
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

/// <summary>What the details panel's location button does with a location (<see cref="LinkSafety.LocationTarget"/>).</summary>
public enum LocationAction
{
    /// <summary>
    /// Opens a map: a search on the map site picked in Settings › General (an address, a room, anything else), or a
    /// Google Maps or Bing Maps link in the location.
    /// </summary>
    Map,

    /// <summary>Opens the web link in the location.</summary>
    Link,

    /// <summary>Calls the phone number in the location (<c>tel:</c>).</summary>
    Call,

    /// <summary>Writes an email to the address in the location's <c>mailto:</c> link.</summary>
    Email,
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
    private static readonly string[] LaunchSchemes = ["https", "zoommtg", "zoomus", "msteams", "webex"];

    private static readonly (string Domain, MeetingProvider Provider)[] MeetingHosts =
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

    /// <summary>
    /// True for <c>https</c> (without a user name, which can pose as a host: <c>https://meet.google.com@evil.example</c>
    /// opens evil.example) and the meeting app schemes (<c>zoommtg</c>, <c>zoomus</c>, <c>msteams</c>, <c>webex</c>).
    /// </summary>
    public static bool CanLaunch(Uri uri) =>
        uri.IsAbsoluteUri && LaunchSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase)
            && (uri.Scheme != Uri.UriSchemeHttps || (uri.UserInfo.Length == 0 && TryIdnHost(uri, out _)));

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
    /// A link's address as Leaf Calendar shows it (tooltips, "Video call:"), matching what <c>LaunchAsync</c> opens. A non-ASCII
    /// host is shown in its ASCII (punycode) form, so a look-alike such as Cyrillic "аpple.com" can't pass for the real
    /// site, and invisible or direction-changing characters are removed. Null when the host has no ASCII form (such a
    /// link is never allowlisted, so it shouldn't be shown as clickable).
    /// </summary>
    public static string? DisplayForm(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        // No ASCII Host, Or An https User Name Posing As One: never allowlisted, so never shown as a link
        if (!TryIdnHost(uri, out var asciiHost) || (uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length > 0))
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
        var at = host.Length > 0 && host != asciiHost ? text.IndexOf(host, StringComparison.Ordinal) : -1;
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
        var same = Uri.TryCreate(asText, UriKind.Absolute, out var shownUri)
            && (shownUri.Scheme == Uri.UriSchemeHttps || shownUri.Scheme == Uri.UriSchemeHttp)
            && shownUri.UserInfo.Length == 0
            && TryIdnHost(shownUri, out var shownHost)
            && string.Equals(shownHost, host, StringComparison.OrdinalIgnoreCase)
            && Ascii.IsValid(shownUri.Host);

        // A non-ASCII host in the text (a look-alike such as Cyrillic "аpple.com") still gets the note, in ASCII
        return same ? null : host;
    }

    // Direction controls (U+202A-202E, U+2066-2069, U+200E/F, U+061C) and zero-width characters (U+200B-200D, U+2060, U+FEFF)
    private static bool IsHiddenCharacter(char c) =>
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
        var query = builder.Query.TrimStart('?')
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
            if (Uri.TryCreate(TrimLinkEnd(match.Value), UriKind.Absolute, out var uri) && ProviderOf(uri) is not null)
            {
                return uri;
            }
        }

        return null;
    }

    /// <summary>
    /// A link found in text without the punctuation that ends the sentence around it. A closing parenthesis goes only
    /// when the link has no opening one for it: "(see https://a.example/x)." loses both, and
    /// "https://a.example/Mercury_(planet)" keeps its own.
    /// </summary>
    internal static string TrimLinkEnd(string link)
    {
        var end = link.Length;
        var unpaired = link.AsSpan().Count(')') - link.AsSpan().Count('(');

        while (end > 0)
        {
            var last = link[end - 1];
            if (last == ')' && unpaired > 0)
            {
                unpaired--;
            }
            else if (last is not ('.' or ',' or ';' or '!' or '?'))
            {
                break;
            }

            end--;
        }

        return link[..end];
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
        var first = Uri.EscapeDataString(safe[0]).Replace("%40", "@", StringComparison.Ordinal);
        var others = safe.Count > 1 ? "to=" + string.Join(",", safe.Skip(1).Select(Uri.EscapeDataString)) + "&" : "";
        if (Uri.TryCreate($"mailto:{first}?{others}subject={cleanSubject}", UriKind.Absolute, out var mailto))
        {
            return mailto;
        }

        // Fallback: every address in the to= field
        return Uri.TryCreate($"mailto:?to={string.Join(",", safe.Select(Uri.EscapeDataString))}&subject={cleanSubject}", UriKind.Absolute, out var fallback)
            ? fallback
            : null;
    }

    // One "@", something on both sides, and nothing that could split, add, or inject mail fields (an apostrophe can't:
    // "o'brien@a.example" is a valid address, and it's escaped like the rest)
    private static bool IsPlainAddress(string email)
    {
        var at = email.IndexOf('@');
        return at > 0
            && at < email.Length - 1
            && email.IndexOf('@', at + 1) < 0
            && !email.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || ",;<>()[]?&%=#\"\\".Contains(c, StringComparison.Ordinal));
    }

    /// <summary>A map search for a location, in Google Maps or Bing Maps (Settings › General). The location is escaped, so it never changes the address.</summary>
    public static Uri MapsSearch(string location, MapProvider provider = MapProvider.Google) => provider == MapProvider.Bing
        ? new("https://www.bing.com/maps?q=" + Uri.EscapeDataString(location))
        : new("https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString(location));

    /// <summary>
    /// Where a location goes (issue #49): the first web link in it (<see cref="IsWebLink"/>), opened as a map when it's
    /// a Google Maps or Bing Maps link (<see cref="MapSiteOf"/>); a <c>mailto:</c> link with one plain address; a
    /// <c>tel:</c> link (a vanity one's letters dialed as keypad digits), or a location that's only a phone number (10 to 15 digits, or 7 or more after a "+", after a
    /// label such as "Tel:" and before an extension such as "ext. 12"); otherwise a map search. Ten digits keeps dates
    /// and ZIP+4 codes on the map. Call and email links are rebuilt from the digits or the address alone
    /// (<see cref="IsCallLink"/>), so nothing else in the location reaches them.
    /// </summary>
    public static (Uri Link, LocationAction Action) LocationTarget(string location, MapProvider provider)
    {
        ArgumentNullException.ThrowIfNull(location);
        var text = location.Length > 40_000 ? location[..40_000] : location;

        // A Web Link (the same bound and trimming as meeting links); a map site's own link opens as a map
        foreach (Match match in WebLink().Matches(text))
        {
            if (Uri.TryCreate(TrimLinkEnd(match.Value), UriKind.Absolute, out var uri) && IsWebLink(uri))
            {
                return (uri, MapSiteOf(uri) is null ? LocationAction.Link : LocationAction.Map);
            }
        }

        // An Email Link
        // (an address Uri can't read, such as "a@b|c", is no email link, never an exception)
        if (MailtoLink().Match(text) is { Success: true } mail && TrimLinkEnd(mail.Groups["to"].Value) is var to && IsPlainAddress(to)
            && Uri.TryCreate("mailto:" + to, UriKind.Absolute, out var mailto))
        {
            return (mailto, LocationAction.Email);
        }

        // A Phone Number: a tel: link is one whatever its length; a location needs to be only a number
        if (TelLink().Match(text) is { Success: true } tel
            && CallLink(tel.Groups["number"].Value, 3, vanity: tel.Groups["vanity"].Success) is { } dial)
        {
            return (dial, LocationAction.Call);
        }

        if (CallLink(PhoneLabel().Replace(text.Trim(), ""), 10) is { } call)
        {
            return (call, LocationAction.Call);
        }

        return (MapsSearch(location, provider), LocationAction.Map);
    }

    /// <summary>
    /// The map site a web link opens on: Google Maps (google.com/maps, maps.google.com, maps.app.goo.gl, goo.gl/maps)
    /// or Bing Maps (bing.com/maps), or null for any other link. Hosts are matched exactly, never by substring.
    /// </summary>
    public static MapProvider? MapSiteOf(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!IsWebLink(uri) || !TryIdnHost(uri, out var idnHost))
        {
            return null;
        }

        var host = idnHost.TrimEnd('.').ToLowerInvariant();
        var maps = uri.AbsolutePath.StartsWith("/maps", StringComparison.OrdinalIgnoreCase);
        return host switch
        {
            "maps.google.com" or "maps.app.goo.gl" => MapProvider.Google,
            "google.com" or "www.google.com" or "goo.gl" when maps => MapProvider.Google,
            "bing.com" or "www.bing.com" when maps => MapProvider.Bing,
            _ => null,
        };
    }

    /// <summary>True for an <c>http</c> or <c>https</c> link without a user name (which can pose as a host) and with an ASCII host form.</summary>
    public static bool IsWebLink(Uri uri) =>
        uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && uri.UserInfo.Length == 0 && TryIdnHost(uri, out _);

    /// <summary>True for a <c>tel:</c> link that is only an optional "+", digits, and an optional extension, as <see cref="LocationTarget"/> builds.</summary>
    public static bool IsCallLink(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == "tel" && CallNumber().IsMatch(uri.OriginalString.AsSpan(4));

    // A tel: link from a phone number as people write it ("+1 (555) 123-4567 ext. 12"), or null when it isn't one.
    // Without a "+" it needs minDigits digits (with one, 7 at most)
    private static Uri? CallLink(string number, int minDigits, bool vanity = false)
    {
        var text = number.Trim();
        var extension = "";
        if (PhoneExtension().Match(text) is { Success: true } ext)
        {
            extension = ";ext=" + ext.Groups["ext"].Value;
            text = text[..ext.Index].TrimEnd();
        }

        // A vanity number's letters are dialed as their keypad digits (its extension was split off first)
        if (vanity)
        {
            text = Keypad(text);
        }

        var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
        var plus = text.StartsWith('+');
        var isNumber = digits.Length <= 15 && digits.Length >= (plus ? Math.Min(minDigits, 7) : minDigits) && text.LastIndexOf('+') <= 0
            && text.All(c => char.IsAsciiDigit(c) || char.IsWhiteSpace(c) || "+-.()\u2013".Contains(c, StringComparison.Ordinal));
        return isNumber ? new Uri("tel:" + (plus ? "+" : "") + digits + extension) : null;
    }

    // A vanity number's letters as the keypad digits they're on ("1-800-FLOWERS" is 1-800-3569377)
    private static string Keypad(string number) => string.Concat(number.Select(c => char.ToUpperInvariant(c) switch
    {
        >= 'A' and <= 'C' => '2',
        >= 'D' and <= 'F' => '3',
        >= 'G' and <= 'I' => '4',
        >= 'J' and <= 'L' => '5',
        >= 'M' and <= 'O' => '6',
        >= 'P' and <= 'S' => '7',
        >= 'T' and <= 'V' => '8',
        >= 'W' and <= 'Z' => '9',
        var other => other,
    }));

    [GeneratedRegex(@"^\+?[0-9]{3,15}(;ext=[0-9]{1,6})?$")]
    private static partial Regex CallNumber();

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex WebLink();

    [GeneratedRegex(@"\bmailto:(?<to>[^\s?<>""']+)", RegexOptions.IgnoreCase)]
    private static partial Regex MailtoLink();

    [GeneratedRegex(@"\btel:\s*(?<number>(?<vanity>\+?[0-9][0-9\-]*(?!ext)[A-Za-z]{2}[A-Za-z0-9\-]*(?:\s*(?:ext\.?|x)\s*[0-9]{1,6})?)|[+0-9(][0-9\s().+\-\u2013]*(?:(?:ext\.?|x|;ext=)\s*[0-9]{1,6})?)(?![\w\-])", RegexOptions.IgnoreCase)]
    private static partial Regex TelLink();

    [GeneratedRegex(@"^(?:tel|phone|ph|call|mobile|cell)\b\s*[:.]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex PhoneLabel();

    [GeneratedRegex(@"(?:ext\.?|extension|x|;ext=)\s*(?<ext>[0-9]{1,6})$", RegexOptions.IgnoreCase)]
    private static partial Regex PhoneExtension();

    [GeneratedRegex(@"https://[^\s<>""']+", RegexOptions.IgnoreCase)]
    internal static partial Regex HttpsLink();
}
