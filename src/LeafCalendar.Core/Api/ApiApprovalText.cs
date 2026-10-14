using LeafCalendar.Core.Tray;

namespace LeafCalendar.Core.Api;

/// <summary>
/// The approval window's words (approval window spec 7). The app's name comes from its program file, so it's cleaned
/// like event text before it's shown.
/// </summary>
public static class ApiApprovalText
{
    /// <summary>What an approved app can do.</summary>
    public const string Body = "It will be able to see your events, create and move them, and join meetings.";

    private const int MaxNameLength = 100;

    // Longer than any path Windows reports (the pipe reads 1,024 characters at most)
    private const int MaxPathLength = 2048;

    /// <summary>"{name} wants to connect to Leaf"; a name that cleans to nothing (only invisible characters) becomes the file name.</summary>
    public static string Headline(ApiCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var name = DisplayText.Clean(caller.Name, MaxNameLength);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = DisplayText.Clean(Path.GetFileNameWithoutExtension(caller.Path), MaxNameLength);
        }

        return $"{name} wants to connect to Leaf";
    }

    /// <summary>
    /// The program's path as shown, without characters that can reorder or hide text (a folder named with a right-to-left
    /// mark could make the end of the path read differently). Only for display: approval uses the path as Windows reports it.
    /// </summary>
    public static string DisplayPath(ApiCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return DisplayText.Clean(caller.Path, MaxPathLength);
    }

    /// <summary>
    /// The warning for a program that runs other scripts (Node, Python, PowerShell and the like), or null. A runtime inside
    /// its own app package's folder only runs that app's scripts, so it gets none.
    /// </summary>
    public static string? RuntimeWarning(ApiCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return !caller.Packaged && ApiClients.IsGeneralRuntime(caller.Path) ? $"Allowing {Path.GetFileName(caller.Path)} to connect will give access to all of your calendars." : null;
    }
}
