using System.Collections.Concurrent;
using LeafCalendar.Core.Google;

namespace LeafCalendar.Core.Auth;

/// <summary>
/// Hands out access tokens per account, refreshing them shortly before they expire.
/// </summary>
/// <remarks>
/// Access tokens live only in memory. When Google rotates the refresh token, the new one is saved.
/// A missing or rejected refresh token, or a rejected OAuth client, becomes <see cref="AccountNeedsSignInException"/> so callers
/// can mark the account instead of retrying.
/// </remarks>
public sealed class AccessTokenProvider(GoogleOAuthClient oauth, ITokenStore store, TimeProvider time) : IDisposable
{
    static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(1);

    readonly ConcurrentDictionary<string, TokenSet> _cache = new(StringComparer.Ordinal);

    // ponytail: one lock for all accounts; switch to per-account locks if refreshes ever contend.
    readonly SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>Returns a valid access token for <paramref name="accountId"/>.</summary>
    /// <exception cref="AccountNeedsSignInException">No usable refresh token.</exception>
    public async ValueTask<string> GetAccessTokenAsync(string accountId, CancellationToken ct)
    {
        if (TryGetFresh(accountId, out var cached))
        {
            return cached;
        }

        await _refreshGate.WaitAsync(ct);
        try
        {
            // Another caller may have refreshed while we waited
            if (TryGetFresh(accountId, out cached))
            {
                return cached;
            }

            var refreshToken = store.GetRefreshToken(accountId) ?? throw new AccountNeedsSignInException(accountId);

            TokenSet tokens;
            try
            {
                tokens = await oauth.RefreshAsync(refreshToken, ct);
            }
            catch (InvalidGrantException)
            {
                throw new AccountNeedsSignInException(accountId);
            }

            // The OAuth Client Was Rejected (wrong secret, deleted, or not the one that issued the token): retrying can't help
            catch (GoogleApiException ex) when (ex.Reason is "invalid_client" or "unauthorized_client" or "deleted_client")
            {
                throw new AccountNeedsSignInException(accountId);
            }

            // Save Rotated Refresh Token
            if (tokens.RefreshToken is { } rotated)
            {
                store.SetRefreshToken(accountId, rotated);
            }

            _cache[accountId] = tokens;
            return tokens.AccessToken;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>True when the account's grant includes <paramref name="scope"/> (or Google didn't list the grant's scopes).</summary>
    /// <exception cref="AccountNeedsSignInException">No usable refresh token.</exception>
    public async ValueTask<bool> HasScopeAsync(string accountId, string scope, CancellationToken ct)
    {
        await GetAccessTokenAsync(accountId, ct);
        return !_cache.TryGetValue(accountId, out var tokens) || tokens.Scope.Length == 0 || tokens.HasScope(scope);
    }

    /// <summary>Caches tokens obtained elsewhere (right after sign-in).</summary>
    public void Seed(string accountId, TokenSet tokens) => _cache[accountId] = tokens;

    /// <summary>Drops the cached token, e.g. after Google answers 401.</summary>
    public void Forget(string accountId) => _cache.TryRemove(accountId, out _);

    bool TryGetFresh(string accountId, out string token)
    {
        if (_cache.TryGetValue(accountId, out var tokens) && tokens.ExpiresAt - time.GetUtcNow() > RefreshMargin)
        {
            token = tokens.AccessToken;
            return true;
        }

        token = "";
        return false;
    }

    /// <summary>Disposes the refresh semaphore.</summary>
    public void Dispose()
    {
        _refreshGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
