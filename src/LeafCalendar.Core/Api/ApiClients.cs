using System.Collections.Frozen;
using System.Xml;
using System.Xml.Linq;

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

    /// <summary>
    /// True when <paramref name="path"/> is a file inside <paramref name="folder"/> (case ignored, after resolving
    /// <c>..</c>). A program inside its own app package's folder only runs for that app, since Windows protects the folder.
    /// </summary>
    public static bool IsInFolder(string path, string folder)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder))
        {
            return false;
        }

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            return full.Length > root.Length && full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// The package's own name from its <c>AppxManifest.xml</c> (<c>Properties/DisplayName</c>), or null when it's missing,
    /// blank, or a resource reference (<c>ms-resource:</c>) that would need the package's resources to read.
    /// </summary>
    public static string? ManifestDisplayName(string manifestXml)
    {
        try
        {
            var name = XDocument.Parse(manifestXml).Root?
                .Elements().FirstOrDefault(e => e.Name.LocalName == "Properties")?
                .Elements().FirstOrDefault(e => e.Name.LocalName == "DisplayName")?
                .Value.Trim();
            return string.IsNullOrEmpty(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? null : name;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// The package's main program (its first <c>Application</c>'s <c>Executable</c>), as a full path inside
    /// <paramref name="packageFolder"/>; null when there's none or it points outside the folder. Its icon stands for the app.
    /// </summary>
    public static string? ManifestProgram(string manifestXml, string packageFolder)
    {
        try
        {
            var executable = XDocument.Parse(manifestXml).Root?
                .Elements().FirstOrDefault(e => e.Name.LocalName == "Applications")?
                .Elements().FirstOrDefault(e => e.Name.LocalName == "Application")?
                .Attribute("Executable")?.Value;
            if (string.IsNullOrEmpty(executable) || Path.IsPathRooted(executable))
            {
                return null;
            }

            var path = Path.GetFullPath(Path.Combine(packageFolder, executable));
            return IsInFolder(path, packageFolder) ? path : null;
        }
        catch (Exception ex) when (ex is XmlException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
