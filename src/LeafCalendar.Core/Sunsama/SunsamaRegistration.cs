using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LeafCalendar.Core.Scheduling;

namespace LeafCalendar.Core.Sunsama;

/// <summary>
/// Registers Leaf Calendar's own public OAuth client with Sunsama the first time you sign in (OAuth dynamic client
/// registration), so you never enter a client ID (spec 2026-10-16, section 4.2).
/// </summary>
/// <remarks>
/// The client is public (<c>token_endpoint_auth_method: none</c>): it has no secret, and PKCE proves each sign-in.
/// See <see href="https://datatracker.ietf.org/doc/html/rfc7591">RFC 7591</see>.
/// </remarks>
public static class SunsamaRegistration
{
    /// <summary>Registers a client for <paramref name="redirectUri"/> and returns its client ID.</summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="register">Sunsama's registration endpoint.</param>
    /// <param name="redirectUri">The fixed loopback redirect the sign-in listens on.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The new client ID.</returns>
    /// <exception cref="SchedulingAuthException">Sunsama refused the registration (400, 401, 403) or sent no client ID.</exception>
    /// <exception cref="SchedulingUnavailableException">Offline, a timeout, 429, another error status, or an unreadable answer.</exception>
    public static async Task<string> RegisterAsync(HttpClient http, Uri register, string redirectUri, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);

        using var content = new ByteArrayContent(Body(redirectUri));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(register, content, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new SchedulingUnavailableException("Sunsama couldn't be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SchedulingUnavailableException("Sunsama didn't answer in time.", ex);
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    var why = SchedulingTokenRequest.ErrorCodes(await response.Content.ReadAsStringAsync(ct));
                    throw new SchedulingAuthException(string.Create(CultureInfo.InvariantCulture, $"Sunsama refused the client registration ({(int)response.StatusCode} {why})."));
                case (HttpStatusCode)429:
                    throw new SchedulingUnavailableException("Sunsama asked Leaf Calendar to slow down.", response.Headers.RetryAfter?.Delta);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SchedulingUnavailableException(string.Create(CultureInfo.InvariantCulture, $"Sunsama's registration endpoint answered {(int)response.StatusCode}."));
            }

            string? clientId;
            try
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                clientId = doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("client_id", out var id)
                    && id.ValueKind == JsonValueKind.String
                        ? id.GetString()
                        : null;
            }
            catch (JsonException ex)
            {
                throw new SchedulingUnavailableException("Sunsama's registration answer couldn't be read.", ex);
            }

            return clientId is { Length: > 0 } ? clientId : throw new SchedulingAuthException("Sunsama's registration answer had no client ID.");
        }
    }

    // The registration request: a public client for the loopback redirect, asking for read-only scopes
    private static byte[] Body(string redirectUri)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("client_name", "Leaf Calendar");
            json.WriteStartArray("redirect_uris");
            json.WriteStringValue(redirectUri);
            json.WriteEndArray();
            json.WriteStartArray("grant_types");
            json.WriteStringValue("authorization_code");
            json.WriteStringValue("refresh_token");
            json.WriteEndArray();
            json.WriteStartArray("response_types");
            json.WriteStringValue("code");
            json.WriteEndArray();
            json.WriteString("token_endpoint_auth_method", "none");
            json.WriteString("scope", "read offline_access");
            json.WriteEndObject();
        }

        return stream.ToArray();
    }
}
