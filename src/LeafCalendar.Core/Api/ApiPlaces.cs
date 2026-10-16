using System.Text.Json;
using LeafCalendar.Core.Places;

namespace LeafCalendar.Core.Api;

/// <summary><c>places.search</c> (local API spec 4): the places the editor's Location box suggests, for an app's location field.</summary>
internal static class ApiPlaces
{
    // Off (or no key) is an error an app can hide its box on; Google's own failures are an empty list, as in the editor
    public static async Task<JsonElement> SearchAsync(ApiContext context, PlacesParams p, CancellationToken ct)
    {
        if (context.Host.Places is not { } places || !places.IsOn(context.Host.Settings))
        {
            throw new ApiException(ApiErrors.NotEnabled, "Location suggestions are off.");
        }

        var query = (p.Query ?? "").Trim();
        if (query.Length > PlaceSearch.MaxQuery)
        {
            throw ApiTime.Invalid("query is at most 100 characters.");
        }

        var found = await places.SearchAsync(query, ct);

        List<PlaceResult> result = [.. found.Select(f => new PlaceResult(f.Name, f.Address.Length > 0 ? f.Address : null, f.Text))];
        return ApiParams.Write(result, ApiJsonContext.Default.ListPlaceResult);
    }
}
