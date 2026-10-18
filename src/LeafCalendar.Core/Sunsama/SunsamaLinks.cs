namespace LeafCalendar.Core.Sunsama;

/// <summary>The Open in Sunsama link (spec 2026-10-16, section 6.5).</summary>
public static class SunsamaLinks
{
    /// <summary>Where Windows goes when no app handles <c>sunsama://</c>: Sunsama's home page.</summary>
    /// <remarks>Sunsama's web task link needs a workspace ID the MCP server doesn't give.</remarks>
    public static Uri Fallback { get; } = new("https://app.sunsama.com/");

    /// <summary>
    /// Builds <c>sunsama://action/details/&lt;id&gt;</c> from fixed text and the task ID, only when the ID is Sunsama's
    /// 24-hex shape; any other ID gives null, so nothing from Sunsama can change the link's shape.
    /// </summary>
    /// <param name="taskId">The task's ID.</param>
    /// <returns>The link, or null.</returns>
    public static Uri? TaskLink(string? taskId) => SunsamaText.IsTaskId(taskId) ? new Uri($"sunsama://action/details/{taskId}") : null;
}
