using System.Globalization;

namespace LeafCalendar.Core.Hosting;

/// <summary>
/// Command-line options.
/// </summary>
/// <remarks>
/// Recognized options:
/// <list type="bullet">
/// <item><c>--profile &lt;name&gt;</c> isolates data. Only <c>[A-Za-z0-9_-]{1,64}</c> is accepted; anything else means <c>default</c>.</item>
/// <item><c>--tray-probe</c> closes the window after it renders and trims memory (memory budget test).</item>
/// <item><c>--fake-google &lt;uri&gt;</c> sends every Google request to a fake server (UI tests). Only an absolute
/// <c>http</c> loopback address is accepted, so it can never point at the network. It exposes nothing new: anyone who
/// can launch Leaf with arguments already runs as this Windows user, and that user can read the Credential Locker.</item>
/// <item><c>--start-date yyyy-MM-dd</c> opens on that date and treats it as "today". It's honored only with <c>--fake-google</c>.</item>
/// </list>
/// </remarks>
public sealed record LaunchOptions(string Profile, bool TrayProbe, Uri? FakeGoogle = null, DateOnly? StartDate = null)
{
    /// <summary>Parses arguments (without the executable path).</summary>
    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        var profile    = "default";
        var trayProbe  = false;
        Uri? fake      = null;
        DateOnly? date = null;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--profile" when i + 1 < args.Count:
                    profile = args[++i];
                    break;

                case "--tray-probe":
                    trayProbe = true;
                    break;

                case "--fake-google" when i + 1 < args.Count:
                    fake = ParseLoopback(args[++i]);
                    break;

                case "--start-date" when i + 1 < args.Count:
                    date = DateOnly.TryParseExact(args[++i], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
                    break;
            }
        }

        return new LaunchOptions(IsSafeProfile(profile) ? profile : "default", trayProbe, fake, fake is null ? null : date);
    }

    static Uri? ParseLoopback(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback)
        {
            return null;
        }

        return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    }

    static bool IsSafeProfile(string name) =>
        name.Length is > 0 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
