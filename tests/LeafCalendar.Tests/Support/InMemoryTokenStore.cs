using LeafCalendar.Core.Auth;

namespace LeafCalendar.Tests.Support;

/// <summary>In-memory <see cref="ITokenStore"/> for logic tests.</summary>
public sealed class InMemoryTokenStore : ITokenStore
{
    readonly Dictionary<string, string> _refreshTokens = new(StringComparer.Ordinal);
    OAuthClientCredentials? _client;

    /// <inheritdoc />
    public OAuthClientCredentials? GetClientCredentials() => _client;

    /// <inheritdoc />
    public void SetClientCredentials(OAuthClientCredentials credentials) => _client = credentials;

    /// <inheritdoc />
    public string? GetRefreshToken(string accountId) => _refreshTokens.GetValueOrDefault(accountId);

    /// <inheritdoc />
    public void SetRefreshToken(string accountId, string refreshToken) => _refreshTokens[accountId] = refreshToken;

    /// <inheritdoc />
    public void RemoveRefreshToken(string accountId) => _refreshTokens.Remove(accountId);

    /// <inheritdoc />
    public IReadOnlyList<string> GetAccountIds() => [.. _refreshTokens.Keys];
}
