using System.Collections.Frozen;

namespace LeafCalendar.Core.Api;

/// <summary>Who connects to the local API: the pipe's name, and programs that run other people's scripts (local API spec 3.3, 3.5).</summary>
public static class ApiClients
{
    // Programs that run whatever script they're given, so approving one approves every script it runs
    private static readonly FrozenSet<string> Runtimes = new[]
    {
        "node", "deno", "bun", "python", "pythonw", "py", "pyw", "powershell", "pwsh", "cmd", "wscript", "cscript",
        "mshta", "rundll32", "java", "javaw", "ruby", "perl", "php", "dotnet", "conhost",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The pipe's name: <c>leaf-calendar-{user}</c> for the default profile, <c>leaf-calendar-{user}-{profile}</c> for
    /// others, lowercase. The user name keeps two people signed in to one PC apart; the profile keeps test profiles apart.
    /// </summary>
    public static string PipeName(string userName, string profile) =>
        ("leaf-calendar-" + userName + (string.Equals(profile, "default", StringComparison.OrdinalIgnoreCase) ? "" : "-" + profile)).ToLowerInvariant();

    /// <summary>
    /// The name the approval window and Settings show for a program: its file description, else its product name, else
    /// its file name. The description comes first because Windows' own programs carry the OS as their product name
    /// ("Microsoft® Windows® Operating System" for PowerShell), while their description names the program.
    /// </summary>
    public static string DisplayName(string? fileDescription, string? productName, string path)
    {
        var name = !string.IsNullOrWhiteSpace(fileDescription) ? fileDescription : productName;
        return !string.IsNullOrWhiteSpace(name) ? name.Trim() : Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>True when the program runs other scripts (Node, Python, PowerShell and the like).</summary>
    public static bool IsGeneralRuntime(string path) => Runtimes.Contains(Path.GetFileNameWithoutExtension(path));
}
