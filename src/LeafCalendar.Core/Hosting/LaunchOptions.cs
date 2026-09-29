namespace LeafCalendar.Core.Hosting;

/// <summary>
/// Command-line options.
/// </summary>
/// <remarks>
/// Recognized options:
/// <list type="bullet">
/// <item><c>--profile &lt;name&gt;</c> isolates data (UI tests use throwaway profiles). The name becomes a folder
/// and a Credential Locker prefix, so only <c>[A-Za-z0-9_-]{1,64}</c> is accepted; anything else falls back to
/// <c>default</c>.</item>
/// <item><c>--tray-probe</c> closes the window after it renders and trims memory, for the memory budget test.</item>
/// </list>
/// </remarks>
public sealed record LaunchOptions(string Profile, bool TrayProbe)
{
    /// <summary>Parses arguments (without the executable path).</summary>
    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        var profile   = "default";
        var trayProbe = false;

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
            }
        }

        return new LaunchOptions(IsSafeProfile(profile) ? profile : "default", trayProbe);
    }

    static bool IsSafeProfile(string name) =>
        name.Length is > 0 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
