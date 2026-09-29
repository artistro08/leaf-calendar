namespace LeafCalendar.Core.Auth;

/// <summary>
/// Secret storage for the OAuth client and each account's refresh token.
/// </summary>
/// <remarks>
/// Production uses <see cref="CredentialLockerTokenStore"/>; tests use an in-memory double.
/// Access tokens are never stored here: they live in memory in <see cref="AccessTokenProvider"/>.
/// </remarks>
public interface ITokenStore
{
    /// <summary>Returns the saved OAuth client, or null before setup.</summary>
    OAuthClientCredentials? GetClientCredentials();

    /// <summary>Saves (replaces) the OAuth client.</summary>
    void SetClientCredentials(OAuthClientCredentials credentials);

    /// <summary>Returns an account's refresh token, or null.</summary>
    string? GetRefreshToken(string accountId);

    /// <summary>Saves (replaces) an account's refresh token.</summary>
    void SetRefreshToken(string accountId, string refreshToken);

    /// <summary>Deletes an account's refresh token.</summary>
    void RemoveRefreshToken(string accountId);

    /// <summary>IDs of accounts that have a refresh token.</summary>
    IReadOnlyList<string> GetAccountIds();
}
