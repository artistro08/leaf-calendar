namespace LeafCalendar.Core.Scheduling;

/// <summary>A provider Leaf knows how to sign in to: its name, its OAuth endpoints, and how to build it.</summary>
/// <param name="Id">The provider's ID (<see cref="ISchedulingProvider.Id"/>).</param>
/// <param name="Name">The name shown in the UI.</param>
/// <param name="Description">One sentence for its Settings card.</param>
/// <param name="OAuth">Its OAuth endpoints, scopes and fixed redirect, which Settings › Accounts draws its section from.</param>
/// <param name="Create">Builds it from the shared client, a function that hands out a fresh access token, and the API address.</param>
/// <param name="DefaultBase">Its hosted API address.</param>
public sealed record SchedulingProviderInfo(string Id, string Name, string Description, SchedulingOAuth OAuth,
    Func<HttpClient, Func<CancellationToken, Task<string>>, Uri, ISchedulingProvider> Create, Uri DefaultBase);

/// <summary>The one place that knows every scheduling provider (spec 3.3).</summary>
public static class SchedulingProviders
{
    /// <summary>Every provider, in the order Settings lists them.</summary>
    public static IReadOnlyList<SchedulingProviderInfo> All { get; } =
    [
        new SchedulingProviderInfo(
            CalComProvider.ProviderId,
            "Cal.com",
            "Share times as booking links from your Cal.com account.",
            CalComProvider.OAuth,
            (http, accessToken, baseUri) => new CalComProvider(http, accessToken, baseUri),
            CalComProvider.DefaultApiBase),
    ];

    /// <summary>The provider with this ID, or null when Leaf doesn't know it.</summary>
    public static SchedulingProviderInfo? Find(string id) => All.FirstOrDefault(p => p.Id == id);
}
