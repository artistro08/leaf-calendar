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
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private string _refreshToken = refreshToken;
    private string? _accessToken;
    private DateTimeOffset _expiresAt;

    /// <summary>Returns a valid access token, refreshing it first when less than a minute is left.</summary>
    /// <exception cref="SchedulingAuthException">The provider refused the refresh token or the OAuth client.</exception>
    /// <exception cref="SchedulingUnavailableException">The provider couldn't be reached or answered with an error that may pass.</exception>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        // The hub disposes a dropped provider's tokens while a call may still be on its way here
        try
        {
            await _refreshGate.WaitAsync(ct);
        }
        catch (ObjectDisposedException ex)
        {
            throw new SchedulingUnavailableException("This sign-in has ended.", ex);
        }

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

            // An answer without expires_in gets the documented 30 minutes rather than a refresh on every call
            _accessToken = token.AccessToken!;
            _expiresAt = time.GetUtcNow() + (token.ExpiresIn is { } seconds ? TimeSpan.FromSeconds(seconds) : DefaultLifetime);
            return _accessToken;
        }
        finally
        {
            ReleaseGate();
        }
    }

    /// <summary>Disposes the refresh semaphore. A call already on its way ends with <see cref="SchedulingUnavailableException"/> or its own result.</summary>
    public void Dispose() => _refreshGate.Dispose();

    // Releases the gate unless it was disposed during the refresh (nothing waits on a disposed gate)
    private void ReleaseGate()
    {
        try
        {
            _refreshGate.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
