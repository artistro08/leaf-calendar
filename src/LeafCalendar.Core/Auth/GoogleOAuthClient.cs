using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LeafCalendar.Core.Google;

namespace LeafCalendar.Core.Auth;

/// <summary>
/// Google OAuth 2.0 calls for an installed (desktop) app.
/// </summary>
/// <remarks>
/// Uses the Authorization Code flow with PKCE and a loopback redirect. The client secret goes only
/// to Google's token endpoint over HTTPS; it never appears in URLs or logs.
/// </remarks>
/// <seealso href="https://developers.google.com/identity/protocols/oauth2/native-app"/>
public sealed class GoogleOAuthClient(HttpClient http, OAuthClientCredentials credentials, TimeProvider time, GoogleEndpoints? endpoints = null)
{
    /// <summary>Full read/write calendar access.</summary>
    public const string CalendarScope = "https://www.googleapis.com/auth/calendar";

    /// <summary>Read-only access to the user's contacts (guest autocomplete).</summary>
    public const string ContactsScope = "https://www.googleapis.com/auth/contacts.readonly";

    /// <summary>Read-only access to "other contacts", people the user has emailed (guest autocomplete).</summary>
    public const string OtherContactsScope = "https://www.googleapis.com/auth/contacts.other.readonly";

    /// <summary>Every scope Leaf requests.</summary>
    public static readonly IReadOnlyList<string> Scopes =
    [
        "openid",
        "email",
        "profile",
        CalendarScope,
        ContactsScope,
        OtherContactsScope,
        "https://www.googleapis.com/auth/directory.readonly",
    ];

    readonly GoogleEndpoints _endpoints = endpoints ?? GoogleEndpoints.Default;

    /// <summary>Builds the consent page address to open in the user's browser.</summary>
    public Uri BuildAuthorizationUrl(Uri redirectUri, string state, string codeChallenge, string? loginHint = null)
    {
        // Build Query
        List<KeyValuePair<string, string>> query =
        [
            new("client_id", credentials.ClientId),
            new("redirect_uri", redirectUri.AbsoluteUri),
            new("response_type", "code"),
            new("scope", string.Join(' ', Scopes)),
            new("state", state),
            new("code_challenge", codeChallenge),
            new("code_challenge_method", "S256"),
            new("access_type", "offline"),
            new("prompt", "consent"),
            new("include_granted_scopes", "true"),
        ];

        if (!string.IsNullOrEmpty(loginHint))
        {
            query.Add(new("login_hint", loginHint));
        }

        var encoded = string.Join('&', query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        return new Uri($"{_endpoints.Authorization.AbsoluteUri}?{encoded}");
    }

    /// <summary>Exchanges an authorization code for tokens.</summary>
    public Task<TokenSet> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken ct) =>
        RequestTokenAsync(
            [
                new("grant_type", "authorization_code"),
                new("code", code),
                new("code_verifier", codeVerifier),
                new("redirect_uri", redirectUri.AbsoluteUri),
            ],
            ct);

    /// <summary>Gets a new access token.</summary>
    /// <exception cref="InvalidGrantException">The refresh token was revoked or expired.</exception>
    public Task<TokenSet> RefreshAsync(string refreshToken, CancellationToken ct) =>
        RequestTokenAsync(
            [
                new("grant_type", "refresh_token"),
                new("refresh_token", refreshToken),
            ],
            ct);

    /// <summary>Revokes a token. A token Google already considers invalid counts as revoked.</summary>
    public async Task RevokeAsync(string token, CancellationToken ct)
    {
        using var content  = new FormUrlEncodedContent([new KeyValuePair<string?, string?>("token", token)]);
        using var response = await http.PostAsync(_endpoints.Revoke, content, ct);

        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.BadRequest)
        {
            throw new GoogleApiException(response.StatusCode, null, "Google token revoke failed.");
        }
    }

    /// <summary>Reads the signed-in user's ID, email, name, and picture.</summary>
    public async Task<GoogleUserInfo> GetUserInfoAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _endpoints.UserInfo);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new GoogleApiException(response.StatusCode, null, "Google user info request failed.");
        }

        return await response.Content.ReadFromJsonAsync(GoogleJsonContext.Default.GoogleUserInfo, ct)
            ?? throw new InvalidDataException("Google returned empty user info.");
    }

    async Task<TokenSet> RequestTokenAsync(List<KeyValuePair<string?, string?>> form, CancellationToken ct)
    {
        // Add Client Credentials
        form.Add(new("client_id", credentials.ClientId));
        form.Add(new("client_secret", credentials.ClientSecret));

        using var content  = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(_endpoints.Token, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        // Map Errors
        if (!response.IsSuccessStatusCode)
        {
            var error = GoogleJson.TryParse(body, GoogleJsonContext.Default.OAuthErrorResponse);
            if (error?.Error == "invalid_grant")
            {
                throw new InvalidGrantException();
            }

            throw new GoogleApiException(response.StatusCode, error?.Error, "Google token request failed.");
        }

        var token = JsonSerializer.Deserialize(body, GoogleJsonContext.Default.TokenResponse)
            ?? throw new InvalidDataException("Google returned an empty token response.");

        return new TokenSet(token.AccessToken, time.GetUtcNow().AddSeconds(token.ExpiresIn), token.RefreshToken, token.Scope ?? "");
    }
}
