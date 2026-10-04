using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LeafCalendar.Core.Settings;

namespace LeafCalendar.Core.Auth;

/// <summary>
/// Stores Leaf's secrets in <c>secrets.bin</c> in the profile folder, encrypted with Windows DPAPI for the current user.
/// </summary>
/// <remarks>
/// The profile folder is under the package's <c>LocalState</c>, so Windows deletes the secrets when Leaf is uninstalled.
/// The file is one JSON document (client ID, client secret, and a refresh token per account ID), encrypted with the
/// profile name (lowercase, as profiles are case-insensitive) as extra entropy, so a copy in another profile folder
/// doesn't open. Writes go to <c>secrets.bin.tmp</c> and replace the file in one move, so a crash never leaves half a file.
/// A damaged file, or one made for another user or profile, reads as empty; the next write first moves it aside to
/// <c>secrets.bin.unreadable</c>. Any other failure to read it (DPAPI failing for a passing reason, a locked file) throws
/// <see cref="InvalidDataException"/>, which sync treats as "try later", so nobody is marked signed out and nothing
/// overwrites the file. Every call takes one lock, because the sync loop and the UI both use tokens.
/// </remarks>
/// <seealso href="https://learn.microsoft.com/windows/win32/api/dpapi/nf-dpapi-cryptunprotectdata"/>
public sealed class ProtectedFileTokenStore : ITokenStore
{
    // DPAPI's answers for data it can never decrypt: not DPAPI data at all, or made for another user or entropy
    private const int ErrorInvalidData = 13;
    private const int ErrorInvalidParameter = 87;
    private const int NteBadData = unchecked((int)0x80090005);

    private readonly Lock _gate = new();
    private readonly string _directory;
    private readonly string _path;
    private readonly byte[] _entropy;
    private readonly Func<byte[], byte[], byte[]> _unprotect;

    /// <summary>Creates the store for a profile folder (<c>…\LocalState\profiles\{profile}</c>); the folder is created on the first write.</summary>
    public ProtectedFileTokenStore(string profileDirectory)
        : this(profileDirectory, Dpapi.Unprotect)
    {
    }

    // Tests: a decrypt that fails the way a real one can
    internal ProtectedFileTokenStore(string profileDirectory, Func<byte[], byte[], byte[]> unprotect)
    {
        _directory = Path.TrimEndingDirectorySeparator(profileDirectory);
        _path = Path.Combine(_directory, "secrets.bin");
        _entropy = Encoding.UTF8.GetBytes("LeafCalendar/" + Path.GetFileName(_directory).ToLowerInvariant());
        _unprotect = unprotect;
    }

    /// <summary>True once <c>secrets.bin</c> has been written (secrets from the Credential Locker were moved, or saved since).</summary>
    public bool Exists => File.Exists(_path);

    /// <inheritdoc />
    public OAuthClientCredentials? GetClientCredentials()
    {
        lock (_gate)
        {
            var file = Load(out _);
            return file.ClientId is null || file.ClientSecret is null ? null : new OAuthClientCredentials(file.ClientId, file.ClientSecret);
        }
    }

    /// <inheritdoc />
    public void SetClientCredentials(OAuthClientCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        lock (_gate)
        {
            var file = Load(out var unreadable);
            Save(file with { ClientId = credentials.ClientId, ClientSecret = credentials.ClientSecret }, unreadable);
        }
    }

    /// <inheritdoc />
    public void ClearClientCredentials()
    {
        lock (_gate)
        {
            var file = Load(out var unreadable);
            Save(file with { ClientId = null, ClientSecret = null }, unreadable);
        }
    }

    /// <inheritdoc />
    public string? GetRefreshToken(string accountId)
    {
        lock (_gate)
        {
            return Load(out _).Refresh.GetValueOrDefault(accountId);
        }
    }

    /// <inheritdoc />
    public void SetRefreshToken(string accountId, string refreshToken)
    {
        lock (_gate)
        {
            var file = Load(out var unreadable);
            file.Refresh[accountId] = refreshToken;
            Save(file, unreadable);
        }
    }

    /// <inheritdoc />
    public void RemoveRefreshToken(string accountId)
    {
        lock (_gate)
        {
            var file = Load(out var unreadable);
            if (file.Refresh.Remove(accountId))
            {
                Save(file, unreadable);
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetAccountIds()
    {
        lock (_gate)
        {
            return [.. Load(out _).Refresh.Keys];
        }
    }

    /// <summary>
    /// Saves an OAuth client (when not null) and refresh tokens in one write, so a move from the Credential Locker lands
    /// whole or not at all.
    /// </summary>
    public void Import(OAuthClientCredentials? client, IReadOnlyDictionary<string, string> refreshTokens)
    {
        ArgumentNullException.ThrowIfNull(refreshTokens);

        lock (_gate)
        {
            var file = Load(out var unreadable);
            if (client is not null)
            {
                file = file with { ClientId = client.ClientId, ClientSecret = client.ClientSecret };
            }

            foreach (var (accountId, token) in refreshTokens)
            {
                file.Refresh[accountId] = token;
            }

            Save(file, unreadable);
        }
    }

    /// <summary>Deletes every secret for this profile (tests and profile resets).</summary>
    public void DeleteAll()
    {
        lock (_gate)
        {
            // File.Delete throws when the folder itself is gone
            if (Directory.Exists(_directory))
            {
                File.Delete(_path);
                File.Delete(_path + ".tmp");
            }
        }
    }

    // Missing: empty. Damaged, or made for another user or profile: empty, and unreadable, so the next write moves it aside.
    // Anything else might pass, so it throws instead: an empty answer would mark accounts signed out, and a write over it
    // would drop every other secret.
    private SecretsFile Load(out bool unreadable)
    {
        unreadable = false;

        byte[] data;
        try
        {
            data = File.ReadAllBytes(_path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Empty();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("The saved sign-in secrets couldn't be read right now.", ex);
        }

        try
        {
            var file = JsonSerializer.Deserialize(_unprotect(data, _entropy), LeafJsonContext.Default.SecretsFile);
            if (file is not null)
            {
                return file with { Refresh = new(file.Refresh ?? [], StringComparer.Ordinal) };
            }
        }
        catch (CryptographicException ex) when (ex.HResult is not (ErrorInvalidData or ErrorInvalidParameter or NteBadData))
        {
            throw new InvalidDataException("The saved sign-in secrets couldn't be decrypted right now.", ex);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            // Bad data: it can never be read, so it reads as empty
        }

        unreadable = true;
        return Empty();
    }

    // Temp File, Then One Move: a crash leaves the old file or the new one, never half of either
    private void Save(SecretsFile file, bool replacesUnreadable)
    {
        var bytes = Dpapi.Protect(JsonSerializer.SerializeToUtf8Bytes(file, LeafJsonContext.Default.SecretsFile), _entropy);
        var temp = _path + ".tmp";

        Directory.CreateDirectory(_directory);
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        // An Unreadable File Is Kept Aside (the latest one), never just overwritten
        if (replacesUnreadable && File.Exists(_path))
        {
            File.Move(_path, _path + ".unreadable", overwrite: true);
        }

        File.Move(temp, _path, overwrite: true);
    }

    private static SecretsFile Empty() => new(null, null, new(StringComparer.Ordinal));
}

/// <summary>A profile's secrets, as stored (encrypted) in <c>secrets.bin</c>.</summary>
/// <param name="ClientId">The OAuth client ID, or null before setup.</param>
/// <param name="ClientSecret">The OAuth client secret, or null before setup.</param>
/// <param name="Refresh">Refresh tokens by account ID (an empty dictionary when there are none).</param>
internal sealed record SecretsFile(string? ClientId, string? ClientSecret, Dictionary<string, string> Refresh);
