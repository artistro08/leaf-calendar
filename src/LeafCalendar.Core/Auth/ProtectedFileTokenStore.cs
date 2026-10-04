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
/// profile name as extra entropy, so a copy in another profile folder doesn't open. Writes go to <c>secrets.bin.tmp</c>
/// and replace the file in one move, so a crash never leaves half a file. A damaged or unreadable file reads as empty,
/// and the next write replaces it. Every call takes one lock, because the sync loop and the UI both use tokens.
/// </remarks>
/// <seealso href="https://learn.microsoft.com/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata"/>
public sealed class ProtectedFileTokenStore : ITokenStore
{
    private readonly Lock _gate = new();
    private readonly string _directory;
    private readonly string _path;
    private readonly byte[] _entropy;

    /// <summary>Creates the store for a profile folder (<c>…\LocalState\profiles\{profile}</c>); the folder is created on the first write.</summary>
    public ProtectedFileTokenStore(string profileDirectory)
    {
        _directory = Path.TrimEndingDirectorySeparator(profileDirectory);
        _path = Path.Combine(_directory, "secrets.bin");
        _entropy = Encoding.UTF8.GetBytes("LeafCalendar/" + Path.GetFileName(_directory));
    }

    /// <inheritdoc />
    public OAuthClientCredentials? GetClientCredentials()
    {
        lock (_gate)
        {
            var file = Load();
            return file.ClientId is null || file.ClientSecret is null ? null : new OAuthClientCredentials(file.ClientId, file.ClientSecret);
        }
    }

    /// <inheritdoc />
    public void SetClientCredentials(OAuthClientCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        lock (_gate)
        {
            Save(Load() with { ClientId = credentials.ClientId, ClientSecret = credentials.ClientSecret });
        }
    }

    /// <inheritdoc />
    public void ClearClientCredentials()
    {
        lock (_gate)
        {
            Save(Load() with { ClientId = null, ClientSecret = null });
        }
    }

    /// <inheritdoc />
    public string? GetRefreshToken(string accountId)
    {
        lock (_gate)
        {
            return Load().Refresh.GetValueOrDefault(accountId);
        }
    }

    /// <inheritdoc />
    public void SetRefreshToken(string accountId, string refreshToken)
    {
        lock (_gate)
        {
            var file = Load();
            file.Refresh[accountId] = refreshToken;
            Save(file);
        }
    }

    /// <inheritdoc />
    public void RemoveRefreshToken(string accountId)
    {
        lock (_gate)
        {
            var file = Load();
            if (file.Refresh.Remove(accountId))
            {
                Save(file);
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetAccountIds()
    {
        lock (_gate)
        {
            return [.. Load().Refresh.Keys];
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

    // Missing, damaged, or made for another user or profile: empty. Other IO errors (a locked file) are thrown, so a
    // passing failure can't make the next write drop every other secret.
    private SecretsFile Load()
    {
        try
        {
            var json = Dpapi.Unprotect(File.ReadAllBytes(_path), _entropy);
            var file = JsonSerializer.Deserialize(json, LeafJsonContext.Default.SecretsFile);
            return file is null ? Empty() : file with { Refresh = new(file.Refresh ?? [], StringComparer.Ordinal) };
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or CryptographicException or JsonException)
        {
            return Empty();
        }
    }

    // Temp File, Then One Move: a crash leaves the old file or the new one, never half of either
    private void Save(SecretsFile file)
    {
        var bytes = Dpapi.Protect(JsonSerializer.SerializeToUtf8Bytes(file, LeafJsonContext.Default.SecretsFile), _entropy);
        var temp = _path + ".tmp";

        Directory.CreateDirectory(_directory);
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
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
