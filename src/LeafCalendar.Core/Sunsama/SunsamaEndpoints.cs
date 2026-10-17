namespace LeafCalendar.Core.Sunsama;

/// <summary>Where Leaf Calendar talks to Sunsama: the MCP server, the OAuth endpoints and the loopback redirect port.</summary>
/// <param name="Mcp">The MCP server URL (also the OAuth <c>resource</c>).</param>
/// <param name="Authorize">The consent page.</param>
/// <param name="Token">The token endpoint.</param>
/// <param name="Register">The dynamic client registration endpoint.</param>
/// <param name="RedirectPort">The fixed loopback port the sign-in listens on; the registered redirect uses it.</param>
public sealed record SunsamaEndpoints(Uri Mcp, Uri Authorize, Uri Token, Uri Register, int RedirectPort = SunsamaEndpoints.DefaultRedirectPort)
{
    /// <summary>Sunsama's loopback redirect port (Cal.com keeps 53817).</summary>
    public const int DefaultRedirectPort = 53818;

    /// <summary>Sunsama's real servers.</summary>
    public static SunsamaEndpoints Default { get; } = ForRoot(new Uri("https://api.sunsama.com/"), "");

    /// <summary>The fake Sunsama inside the UI tests' fake Google server, under <c>sunsama/</c>.</summary>
    /// <param name="root">The fake server's root, ending in <c>/</c>.</param>
    /// <returns>The fake's endpoints.</returns>
    public static SunsamaEndpoints ForFake(Uri root) => ForRoot(root, "sunsama/");

    private static SunsamaEndpoints ForRoot(Uri root, string prefix) =>
        new(new Uri(root, prefix + "mcp"), new Uri(root, prefix + "oauth/authorize"), new Uri(root, prefix + "oauth/token"), new Uri(root, prefix + "oauth/register"));
}
