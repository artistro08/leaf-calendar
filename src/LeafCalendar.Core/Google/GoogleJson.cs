using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace LeafCalendar.Core.Google;

/// <summary>Shared JSON helpers for Google responses.</summary>
internal static class GoogleJson
{
    /// <summary>Parses <paramref name="body"/>, returning null when it isn't valid JSON for <typeparamref name="T"/>.</summary>
    public static T? TryParse<T>(string body, JsonTypeInfo<T> info)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(body, info);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Builds a <see cref="GoogleApiException"/> from a failed response, keeping only status and reason.</summary>
    public static async Task<GoogleApiException> ToExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        var reason = TryParse(body, GoogleJsonContext.Default.ApiErrorEnvelope)?.Error?.Errors?.FirstOrDefault()?.Reason;

        return new GoogleApiException(response.StatusCode, reason, $"Google API request failed with status {(int)response.StatusCode}.");
    }
}
