namespace LeafCalendar.Core.Scheduling;

/// <summary>One box Settings draws when you connect a provider (spec 3.2).</summary>
/// <param name="Key">The value's key, such as <c>api_key</c> (a secret's key in <see cref="Auth.ITokenStore"/>).</param>
/// <param name="Label">The box's label.</param>
/// <param name="Secret">True for a password box saved through <see cref="Auth.ITokenStore"/>; false for a text box saved in settings.</param>
/// <param name="Required">Whether the box can be left empty.</param>
/// <param name="Placeholder">The hint shown in the empty box.</param>
/// <param name="IsValid">Whether a value is acceptable (only asked for non-empty values of optional fields).</param>
/// <param name="InvalidMessage">What to show when <paramref name="IsValid"/> says no.</param>
public sealed record SetupField(string Key, string Label, bool Secret, bool Required, string Placeholder, Func<string, bool> IsValid, string InvalidMessage);

/// <summary>A provider Leaf knows how to connect: its name, the boxes it needs, and how to build it.</summary>
/// <param name="Id">The provider's ID (<see cref="ISchedulingProvider.Id"/>).</param>
/// <param name="Name">The name shown in the UI.</param>
/// <param name="Description">One sentence for its Settings card.</param>
/// <param name="Fields">The boxes it needs, in order.</param>
/// <param name="Create">Builds it from the field values (secrets included) and the API address to talk to.</param>
/// <param name="DefaultBase">Its hosted API address, used when no server is given.</param>
public sealed record SchedulingProviderInfo(string Id, string Name, string Description, IReadOnlyList<SetupField> Fields,
    Func<HttpClient, IReadOnlyDictionary<string, string>, Uri, ISchedulingProvider> Create, Uri DefaultBase);

/// <summary>The one place that knows every scheduling provider (spec 3.3).</summary>
public static class SchedulingProviders
{
    /// <summary>The longest API key accepted.</summary>
    private const int MaxKey = 200;

    /// <summary>The longest server address accepted.</summary>
    private const int MaxServer = 2048;

    /// <summary>Every provider, in the order Settings lists them.</summary>
    public static IReadOnlyList<SchedulingProviderInfo> All { get; } =
    [
        new SchedulingProviderInfo(
            CalComProvider.ProviderId,
            "Cal.com",
            "Share times as booking links from your Cal.com account.",
            [
                new SetupField("api_key", "API key", Secret: true, Required: true, "cal_live_…", IsCalComKey,
                    "Cal.com didn't accept that API key. Check it in Cal.com › Settings › Developer › API keys."),
                new SetupField("server", "Server address", Secret: false, Required: false, "Leave empty for cal.com", IsServer,
                    "Enter an https:// address, or leave it empty for cal.com."),
            ],
            (http, values, baseUri) => new CalComProvider(http, values["api_key"].Trim(), baseUri),
            CalComProvider.DefaultApiBase),
    ];

    /// <summary>The provider with this ID, or null when Leaf doesn't know it.</summary>
    public static SchedulingProviderInfo? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// A server value as the API address to use: trimmed and ending with "/", so a path such as <c>https://host/api</c>
    /// keeps its path when relative endpoints are added to it.
    /// </summary>
    internal static Uri ServerBase(string value)
    {
        var text = value.Trim();
        return new Uri(text.EndsWith('/') ? text : text + "/");
    }

    // Trimmed: starts with cal_, at most 200 characters, and nothing that could split a header (no space or control character)
    private static bool IsCalComKey(string value)
    {
        var text = value.Trim();
        return text.StartsWith("cal_", StringComparison.Ordinal) && text.Length <= MaxKey
            && !text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
    }

    // Empty, or an absolute https address with no user info, query or fragment (a bare "?" or "#" counts as one)
    private static bool IsServer(string value)
    {
        var text = value.Trim();
        return text.Length == 0
            || (text.Length <= MaxServer && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 && text.IndexOfAny(['?', '#']) < 0);
    }
}
