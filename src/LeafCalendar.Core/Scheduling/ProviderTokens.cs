namespace LeafCalendar.Core.Scheduling;

/// <summary>
/// Hands out a provider's access token, refreshing it with the saved refresh token a minute before it ends.
/// </summary>
/// <remarks>
/// The access token lives only in memory. Callers that arrive while a refresh runs wait for it and share its token. A
/// rotated refresh token in the answer goes to <paramref name="saveRotatedRefreshToken"/>. A refused refresh (400/401,
/// such as <c>invalid_grant</c>) throws <see cref="SchedulingAuthException"/> once, so the provider can be marked as
/// needing reconnecting; offline, 429 and 5xx throw <see cref="SchedulingUnavailableException"/>.
/// </remarks>
public sealed class ProviderTokens(
    HttpClient http,
    SchedulingOAuth oauth,
    ProviderClient client,
    string refreshToken,
    TimeProvider time,
    Action<string> saveRotatedRefreshToken) : IDisposable
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private string _refreshToken = refreshToken;
    private string? _accessToken;
    private DateTimeOffset _expiresAt;

    /// <summary>Returns a valid access token, refreshing it first when less than a minute is left.</summary>
    /// <exception cref="SchedulingAuthException">The provider refused the refresh token or the OAuth client.</exception>
    /// <exception cref="SchedulingUnavailableException">The provider couldn't be reached or answered with an error that may pass.</exception>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            // Another caller may have refreshed while this one waited
            if (_accessToken is not null && _expiresAt - time.GetUtcNow() > RefreshMargin)
            {
                return _accessToken;
            }

            var token = await SchedulingTokenRequest.SendAsync(
                http,
                oauth,
                client,
                [new("grant_type", "refresh_token"), new("refresh_token", _refreshToken)],
                ct);

            // Save Rotated Refresh Token
            if (token.RefreshToken is { Length: > 0 } rotated && rotated != _refreshToken)
            {
                _refreshToken = rotated;
                saveRotatedRefreshToken(rotated);
            }

            // An answer without expires_in counts as already ending, so the next call refreshes again
            _accessToken = token.AccessToken!;
            _expiresAt = time.GetUtcNow().AddSeconds(token.ExpiresIn ?? 0);
            return _accessToken;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Disposes the refresh semaphore.</summary>
    public void Dispose() => _refreshGate.Dispose();
}
