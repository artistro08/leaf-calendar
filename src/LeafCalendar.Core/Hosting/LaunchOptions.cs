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
/// <c>http</c> loopback address is accepted, and only with a throwaway <c>uitest-</c> profile, so a real profile's
/// secrets are never sent to the fake server.</item>
/// <item><c>--start-date yyyy-MM-dd</c> opens on that date and treats it as "today". It's honored only with <c>--fake-google</c>.</item>
/// <item><c>--now &lt;instant&gt;</c> starts Leaf's clock at an ISO 8601 instant with an offset (<c>2026-10-01T13:55:00-04:00</c>). It's honored only with <c>--fake-google</c>.
/// The accepted forms are <c>yyyy-MM-ddTHH:mm:ss±hh:mm</c> and <c>yyyy-MM-ddTHH:mm±hh:mm</c>; a value without an
/// offset is ignored, so the test clock never depends on the PC's time zone. It moves the services clock (alerts, tray, join shortcut) only; the calendar view's "now"
/// (<c>CalendarViewModel.Now</c>) still follows <c>--start-date</c> or the real clock.</item>
/// </list>
/// </remarks>
public sealed record LaunchOptions(string Profile, bool TrayProbe, Uri? FakeGoogle = null, DateOnly? StartDate = null, DateTimeOffset? Now = null)
{
    /// <summary>Parses arguments (without the executable path).</summary>
    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        var profile         = "default";
        var trayProbe       = false;
        Uri? fake           = null;
        DateOnly? date      = null;
        DateTimeOffset? now = null;

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

                case "--now" when i + 1 < args.Count:
                    now = ParseInstant(args[++i]);
                    break;
            }
        }

        // Fake Mode Only On Throwaway Profiles
        profile = IsSafeProfile(profile) ? profile : "default";
        if (!profile.StartsWith("uitest-", StringComparison.Ordinal))
        {
            fake = null;
        }

        return new LaunchOptions(profile, trayProbe, fake, fake is null ? null : date, fake is null ? null : now);
    }

    static Uri? ParseLoopback(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback)
        {
            return null;
        }

        return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    }

    // An instant with an explicit offset only, so the test clock never depends on the PC's zone
    static DateTimeOffset? ParseInstant(string value) =>
        DateTimeOffset.TryParseExact(value, ["yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mmzzz"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)
            ? instant
            : null;

    static bool IsSafeProfile(string name) =>
        name.Length is > 0 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
