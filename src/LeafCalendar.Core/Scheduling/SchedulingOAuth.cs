using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LeafCalendar.Core.Scheduling;

/// <summary>A provider's OAuth endpoints and scopes, as data, so every provider signs in the same way.</summary>
/// <param name="Authorize">The consent page opened in the browser.</param>
/// <param name="Token">The token endpoint (code exchange and refresh).</param>
/// <param name="Scopes">The scopes asked for, sent space separated.</param>
/// <param name="RedirectPort">The fixed loopback port the redirect comes back on (providers match redirect URIs exactly).</param>
/// <param name="RedirectPath">The redirect path, starting with <c>/</c>.</param>
/// <param name="ClientPage">Where you create your own OAuth client on the provider's site.</param>
public sealed record SchedulingOAuth(Uri Authorize, Uri Token, IReadOnlyList<string> Scopes, int RedirectPort, string RedirectPath, Uri ClientPage);

/// <summary>Your own OAuth client for a provider (a confidential client: ID and secret).</summary>
/// <param name="ClientId">The client ID.</param>
/// <param name="ClientSecret">The client secret; sent only in the token request body, never in a URL or a log.</param>
public sealed record ProviderClient(string ClientId, string ClientSecret)
{
    /// <summary>Names the type only, so printing a client (in a log or an exception) never shows its ID or secret, which are never logged.</summary>
    public override string ToString() => nameof(ProviderClient);
}

/// <summary>The fixed redirect port is in use by another app, so the sign-in can't receive the provider's reply.</summary>
public sealed class SchedulingPortBusyException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SchedulingPortBusyException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public SchedulingPortBusyException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public SchedulingPortBusyException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

// A token endpoint's answer. Every field is nullable, since nothing from a provider is trusted
internal sealed record OAuthTokenResponse(string? AccessToken, string? RefreshToken, int? ExpiresIn, string? Scope);

/// <summary>Source-generated JSON for OAuth token answers, which use snake_case names (AOT-safe).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(OAuthTokenResponse))]
internal sealed partial class SchedulingOAuthJsonContext : JsonSerializerContext;

// The token request both the sign-in and the refresh send, with the provider's errors mapped
internal static class SchedulingTokenRequest
{
    // Posts the form plus the client's ID and secret (body only). 400/401 and an answer without an access token are
    // refusals; offline, a timeout, 429, 5xx and an unreadable answer may pass. Cancellation propagates.
    public static async Task<OAuthTokenResponse> SendAsync(HttpClient http, SchedulingOAuth oauth, ProviderClient client, List<KeyValuePair<string?, string?>> form, CancellationToken ct)
    {
        form.Add(new("client_id", client.ClientId));
        form.Add(new("client_secret", client.ClientSecret));

        using var content = new FormUrlEncodedContent(form);
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(oauth.Token, content, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new SchedulingUnavailableException("The provider couldn't be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SchedulingUnavailableException("The provider didn't answer in time.", ex);
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized:
                    var why = ErrorCodes(await response.Content.ReadAsStringAsync(ct));
                    throw new SchedulingAuthException(string.Create(CultureInfo.InvariantCulture, $"The provider refused the sign-in ({(int)response.StatusCode} {why})."));
                case (HttpStatusCode)429:
                    throw new SchedulingUnavailableException("The provider asked Leaf to slow down.", response.Headers.RetryAfter?.Delta);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SchedulingUnavailableException(string.Create(CultureInfo.InvariantCulture, $"The provider's token endpoint answered {(int)response.StatusCode}."));
            }

            OAuthTokenResponse? token;
            try
            {
                token = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(ct), SchedulingOAuthJsonContext.Default.OAuthTokenResponse);
            }
            catch (JsonException ex)
            {
                throw new SchedulingUnavailableException("The provider's token answer couldn't be read.", ex);
            }

            return token is { AccessToken.Length: > 0 } ? token : throw new SchedulingAuthException("The provider's token answer had no access token.");
        }
    }

    // The OAuth error code and description from a refusal (top level, or under an "error" object), kept to letters,
    // digits, '_' and '-' so the log gets the reason but nothing else the provider sent
    internal static string ErrorCodes(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return "";
            }

            var scope = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object ? e : root;
            return $"{Code(scope, "error")}{Code(scope, "code")} {Code(scope, "error_description")}".Trim();
        }
        catch (JsonException)
        {
            return "";
        }

        static string Code(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? new string([.. (v.GetString() ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').Take(64)])
                : "";
    }
}
