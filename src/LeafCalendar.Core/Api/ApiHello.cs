using System.Buffers.Binary;
using System.Collections.Frozen;

namespace LeafCalendar.Core.Api;

/// <summary>
/// What an app sends with its first <c>leaf.hello</c> for the approval window (approval window spec 3): its icon, and
/// the link that takes you back to it after Allow.
/// </summary>
/// <remarks>
/// Both are read only from a connection's first line, since Leaf Calendar asks before it runs any request, and both are untrusted:
/// the icon must be a small PNG (checked by its signature and header here; Windows decodes it in the App) and the link
/// an app's own scheme, never the web, files, scripts, data or Windows' own. Anything that breaks a rule reads as
/// nothing, so the window falls back to the program's own icon and to bringing the app's window forward. Never throws.
/// </remarks>
/// <param name="Icon">The checked PNG, or empty.</param>
/// <param name="ReturnUrl">The checked return link, or null.</param>
/// <param name="Log">For the log: whether each was <c>ok</c>, <c>none</c> (not sent) or <c>refused</c> (broke a rule); never the values.</param>
public sealed record ApiHello(ReadOnlyMemory<byte> Icon, Uri? ReturnUrl, string Log = ApiHello.NoneLog)
{
    private const string NoneLog = "icon=none returnUrl=none";

    /// <summary>The most bytes an icon may have, decoded.</summary>
    public const int MaxIconBytes = 65_536;

    /// <summary>The widest and tallest an icon may be, in pixels.</summary>
    public const int MaxIconSize = 256;

    /// <summary>The longest return link.</summary>
    public const int MaxReturnUrlLength = 2048;

    /// <summary>Nothing sent (or nothing usable).</summary>
    public static readonly ApiHello None = new(ReadOnlyMemory<byte>.Empty, null);

    // Schemes a return link may not use (and every ms- scheme): the web (Edge's own scheme opens web pages too), files,
    // scripts, data, and Windows' own
    private static readonly FrozenSet<string> RefusedSchemes = new[]
    {
        "http", "https", "microsoft-edge", "file", "ftp", "javascript", "data", "shell", "search-ms", "search",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The icon and return link a connection's first line carries; <see cref="None"/> when it isn't a usable <c>leaf.hello</c> request.</summary>
    public static ApiHello Read(string line)
    {
        HelloParams p;
        try
        {
            var request = ApiMessage.Parse(line);
            if (request.Method != "leaf.hello")
            {
                return None;
            }

            p = ApiParams.Read(request.Params, ApiJsonContext.Default.HelloParams);
        }
        catch (ApiProtocolException)
        {
            return None;
        }

        var icon = ReadIcon(p.Icon);
        var link = ReadReturnUrl(p.ReturnUrl);
        var log = $"icon={Status(p.Icon, !icon.IsEmpty)} returnUrl={Status(p.ReturnUrl, link is not null)}";
        return log == NoneLog ? None : new ApiHello(icon, link, log);
    }

    // What the log says about one param: ok, none (not sent), or refused (sent, but broke a rule)
    private static string Status(string? sent, bool kept) => kept ? "ok" : string.IsNullOrEmpty(sent) ? "none" : "refused";

    /// <summary>The PNG a base64 icon decodes to, or empty when it breaks a rule (too big, not a PNG, wider or taller than <see cref="MaxIconSize"/>).</summary>
    public static ReadOnlyMemory<byte> ReadIcon(string? base64)
    {
        if (string.IsNullOrEmpty(base64) || base64.Length > (MaxIconBytes + 2) / 3 * 4)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        byte[] png;
        try
        {
            png = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        // Signature, Then The IHDR Chunk's Width And Height (big-endian, bytes 16 to 23)
        if (png.Length < 24 || png.Length > MaxIconBytes || !png.AsSpan(0, 8).SequenceEqual(PngSignature) || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
        return width is >= 1 and <= MaxIconSize && height is >= 1 and <= MaxIconSize ? png : ReadOnlyMemory<byte>.Empty;
    }

    /// <summary>The return link, or null when it isn't an absolute link to an app's own scheme (at most <see cref="MaxReturnUrlLength"/> characters, no user info).</summary>
    public static Uri? ReadReturnUrl(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxReturnUrlLength || !Uri.TryCreate(text, UriKind.Absolute, out var link))
        {
            return null;
        }

        var refused = RefusedSchemes.Contains(link.Scheme) || link.Scheme.StartsWith("ms-", StringComparison.OrdinalIgnoreCase) || link.UserInfo.Length > 0;
        return refused ? null : link;
    }
}
