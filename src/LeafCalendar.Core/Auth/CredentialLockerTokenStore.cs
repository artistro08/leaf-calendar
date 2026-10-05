using Windows.Security.Credentials;

namespace LeafCalendar.Core.Auth;

/// <summary>
/// Stores Leaf's secrets in Windows Credential Locker (<see cref="PasswordVault"/>).
/// </summary>
/// <remarks>
/// Entries are encrypted with the current Windows user's login and namespaced by profile:
/// <c>LeafCalendar/{profile}/client</c> holds the client ID and secret, and
/// <c>LeafCalendar/{profile}/refresh</c> holds one entry per account, keyed by account ID.
/// Earlier builds kept secrets here; the app now uses <see cref="ProtectedFileTokenStore"/> and only reads this store to
/// move them out (<see cref="TokenStoreMigration"/>), or for one run when that move fails. Live tests still use it.
/// </remarks>
/// <seealso href="https://learn.microsoft.com/windows/apps/develop/security/credential-locker"/>
public sealed class CredentialLockerTokenStore(string profile) : ITokenStore
{
    private const string ClientIdUser = "client-id";
    private const string ClientSecretUser = "client-secret";
    private const int ElementNotFound = unchecked((int)0x80070490);

    private readonly PasswordVault _vault = new();

    private string ClientResource => $"LeafCalendar/{profile}/client";
    private string RefreshResource => $"LeafCalendar/{profile}/refresh";

    /// <inheritdoc />
    public OAuthClientCredentials? GetClientCredentials()
    {
        var id = Read(ClientResource, ClientIdUser);
        var secret = Read(ClientResource, ClientSecretUser);

        return id is null || secret is null ? null : new OAuthClientCredentials(id, secret);
    }

    /// <inheritdoc />
    public void SetClientCredentials(OAuthClientCredentials credentials)
    {
        Write(ClientResource, ClientIdUser, credentials.ClientId);
        Write(ClientResource, ClientSecretUser, credentials.ClientSecret);
    }

    /// <inheritdoc />
    public void ClearClientCredentials()
    {
        Remove(ClientResource, ClientIdUser);
        Remove(ClientResource, ClientSecretUser);
    }

    /// <inheritdoc />
    public string? GetRefreshToken(string accountId) => Read(RefreshResource, accountId);

    /// <inheritdoc />
    public void SetRefreshToken(string accountId, string refreshToken) => Write(RefreshResource, accountId, refreshToken);

    /// <inheritdoc />
    public void RemoveRefreshToken(string accountId) => Remove(RefreshResource, accountId);

    /// <inheritdoc />
    public IReadOnlyList<string> GetAccountIds() => [.. FindAll(RefreshResource).Select(c => c.UserName)];

    /// <summary>Deletes every secret for this profile (tests and profile resets).</summary>
    public void DeleteAll()
    {
        foreach (var credential in FindAll(ClientResource).Concat(FindAll(RefreshResource)))
        {
            _vault.Remove(credential);
        }
    }

    private string? Read(string resource, string user)
    {
        var credential = FindAll(resource).FirstOrDefault(c => c.UserName == user);
        if (credential is null)
        {
            return null;
        }

        credential.RetrievePassword();
        return credential.Password;
    }

    private void Write(string resource, string user, string value)
    {
        Remove(resource, user);
        _vault.Add(new PasswordCredential(resource, user, value));
    }

    private void Remove(string resource, string user)
    {
        var credential = FindAll(resource).FirstOrDefault(c => c.UserName == user);
        if (credential is not null)
        {
            _vault.Remove(credential);
        }
    }

    // FindAllByResource throws "Element not found" (0x80070490) when a resource has no entries.
    private List<PasswordCredential> FindAll(string resource)
    {
        try
        {
            return [.. _vault.FindAllByResource(resource)];
        }
        catch (Exception ex) when (ex.HResult == ElementNotFound)
        {
            return [];
        }
    }
}
