using System.Security.Cryptography;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class TokenStoreMigrationTests : IDisposable
{
    private static readonly OAuthClientCredentials Client = new("id.apps.googleusercontent.com", "secret");

    private readonly TempFolder _folder = new();
    private readonly AppLog _log;
    private readonly ProtectedFileTokenStore _file;

    public TokenStoreMigrationTests()
    {
        _log = new AppLog(Path.Combine(_folder.Path, "Logs"), TimeProvider.System);
        _file = new ProtectedFileTokenStore(Path.Combine(_folder.Path, "profiles", "default"));
    }

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Open_FromLocker_MovesEverySecretOnce()
    {
        var locker = Seeded();

        Assert.Same(_file, TokenStoreMigration.Open(_file, () => locker, _log));
        Assert.Same(_file, TokenStoreMigration.Open(_file, () => locker, _log));

        Assert.Equal(Client, _file.GetClientCredentials());
        Assert.Equal("1//a", _file.GetRefreshToken("a"));
        Assert.Equal("1//b", _file.GetRefreshToken("b"));
        Assert.Empty(locker.GetAccountIds());
        Assert.Null(locker.GetClientCredentials());
        Assert.Single(LogLines("auth.secrets.migrated"));
    }

    [Fact]
    public void Open_NothingInTheLocker_UsesTheFileStore_AndWritesNothing()
    {
        Assert.Same(_file, TokenStoreMigration.Open(_file, () => new InMemoryTokenStore(), _log));

        Assert.False(_file.Exists);
        Assert.Empty(LogLines("auth.secrets."));
    }

    [Fact]
    public void Open_ARemovalFailsHalfway_KeepsTheFileStoreWithEverySecret()
    {
        var locker = new FlakyLocker(Seeded()) { RemovalsBeforeFailure = 1 };

        Assert.Same(_file, TokenStoreMigration.Open(_file, () => locker, _log));

        Assert.Equal(Client, _file.GetClientCredentials());
        Assert.Equal(["a", "b"], _file.GetAccountIds().Order(StringComparer.Ordinal));
        Assert.Single(LogLines("auth.secrets.cleanup.failed error=IOException"));

        // The next start only finishes the cleanup
        locker.RemovalsBeforeFailure = int.MaxValue;
        Assert.Same(_file, TokenStoreMigration.Open(_file, () => locker, _log));
        Assert.Empty(locker.GetAccountIds());
        Assert.Null(locker.GetClientCredentials());
        Assert.Single(LogLines("auth.secrets.migrated"));
    }

    [Fact]
    public void Open_LockerUnreadable_OnAMigratedProfile_UsesTheFileStore()
    {
        _file.SetRefreshToken("a", "1//a");
        var locker = new FlakyLocker(Seeded()) { ReadsFail = true };

        Assert.Same(_file, TokenStoreMigration.Open(_file, () => locker, _log));
        Assert.Same(_file, TokenStoreMigration.Open(_file, () => throw new InvalidOperationException("no vault"), _log));

        Assert.Equal("1//a", _file.GetRefreshToken("a"));
    }

    [Fact]
    public void Open_LockerUnreadable_BeforeMigrating_UsesTheFileStore()
    {
        var locker = new FlakyLocker(Seeded()) { ReadsFail = true };

        Assert.Same(_file, TokenStoreMigration.Open(_file, () => locker, _log));
        Assert.Same(_file, TokenStoreMigration.Open(_file, () => throw new InvalidOperationException("no vault"), _log));

        Assert.False(_file.Exists);
        Assert.Equal(2, LogLines("auth.secrets.locker.unreadable").Count);
    }

    [Fact]
    public void Open_FileIsBadData_RebuildsItFromTheLockerLeftovers()
    {
        // A cleanup failed after the move, then secrets.bin was damaged
        var path = Path.Combine(_folder.Path, "profiles", "default", "secrets.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        var locker = Seeded();

        Assert.Same(_file, TokenStoreMigration.Open(_file, () => locker, _log));

        Assert.Equal(Client, _file.GetClientCredentials());
        Assert.Equal(["a", "b"], _file.GetAccountIds().Order(StringComparer.Ordinal));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(path + ".unreadable"));
        Assert.Empty(locker.GetAccountIds());
        Assert.Null(locker.GetClientCredentials());
        Assert.Single(LogLines("auth.secrets.rebuilt"));
        Assert.Empty(LogLines("auth.secrets.migrated"));
    }

    [Fact]
    public void Open_FileUnavailableRightNow_KeepsTheLockerLeftovers()
    {
        _file.SetRefreshToken("a", "1//a");
        var file = new ProtectedFileTokenStore(Path.Combine(_folder.Path, "profiles", "default"), (_, _) => throw new CryptographicException(unchecked((int)0x8009000B)));
        var locker = Seeded();

        Assert.Same(file, TokenStoreMigration.Open(file, () => locker, _log));

        Assert.Equal(["a", "b"], locker.GetAccountIds().Order(StringComparer.Ordinal));
        Assert.Equal(Client, locker.GetClientCredentials());
        Assert.Equal("1//a", _file.GetRefreshToken("a"));
    }

    [Fact]
    public void Open_LockerUnreadableOnTheFirstStart_ThenSetUpAgain_NextStartKeepsEveryAccount()
    {
        // First Start After The Update: the Locker can't be read for a moment, so the empty file store is used
        var locker = new FlakyLocker(Seeded()) { ReadsFail = true };
        Assert.Same(_file, TokenStoreMigration.Open(_file, () => locker, _log));

        // Sent To Setup, the user saves the client again and signs one account in again
        _file.SetClientCredentials(Client);
        _file.SetRefreshToken("a", "1//a-new");

        // Next Start: the Locker reads, and what secrets.bin lacks comes over before the Locker is emptied
        locker.ReadsFail = false;
        Assert.Same(_file, TokenStoreMigration.Open(_file, () => locker, _log));

        Assert.Equal(Client, _file.GetClientCredentials());
        Assert.Equal("1//a-new", _file.GetRefreshToken("a"));
        Assert.Equal("1//b", _file.GetRefreshToken("b"));
        Assert.Empty(locker.GetAccountIds());
        Assert.Null(locker.GetClientCredentials());
    }

    [Fact]
    public void Open_AddingTheLockerLeftoversFails_KeepsTheFileStore_AndTheLeftovers()
    {
        _file.SetClientCredentials(Client);
        _file.SetRefreshToken("a", "1//a-new");
        var locker = Seeded();

        // The temp file is held open, so the write fails
        using (new FileStream(Path.Combine(_folder.Path, "profiles", "default", "secrets.bin.tmp"), FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Assert.Same(_file, TokenStoreMigration.Open(_file, () => locker, _log));
        }

        Assert.Equal(["a"], _file.GetAccountIds());
        Assert.Equal(["a", "b"], locker.GetAccountIds().Order(StringComparer.Ordinal));
        Assert.Single(LogLines("auth.secrets.migrate.failed error=IOException"));
    }

    [Fact]
    public void Open_FileHasTokensButNoClient_TakesTheLockerClient()
    {
        _file.SetRefreshToken("a", "1//a-new");
        var locker = Seeded();

        Assert.Same(_file, TokenStoreMigration.Open(_file, () => locker, _log));

        Assert.Equal(Client, _file.GetClientCredentials());
        Assert.Equal("1//a-new", _file.GetRefreshToken("a"));
        Assert.Equal("1//b", _file.GetRefreshToken("b"));
        Assert.Empty(locker.GetAccountIds());
    }

    [Fact]
    public void Open_LockerHoldsHalfAClient_ClearsIt()
    {
        // Only the client secret is left, which the Locker reports as no client
        var locker = new FlakyLocker(new InMemoryTokenStore());

        Assert.Same(_file, TokenStoreMigration.Open(_file, () => locker, _log));

        Assert.True(locker.ClientCleared);
    }

    [Fact]
    public void Open_WriteFails_UsesTheLocker_AndLeavesItIntact()
    {
        // The profile "folder" is a file, so the secrets can't be written
        var blocked = Path.Combine(_folder.Path, "blocked");
        File.WriteAllBytes(blocked, []);
        var file = new ProtectedFileTokenStore(blocked);
        var locker = Seeded();

        Assert.Same(locker, TokenStoreMigration.Open(file, () => locker, _log));

        Assert.Equal(Client, locker.GetClientCredentials());
        Assert.Equal(["a", "b"], locker.GetAccountIds().Order(StringComparer.Ordinal));
        Assert.Single(LogLines("auth.secrets.migrate.failed error=IOException"));
    }

    private static InMemoryTokenStore Seeded()
    {
        var locker = new InMemoryTokenStore();
        locker.SetClientCredentials(Client);
        locker.SetRefreshToken("a", "1//a");
        locker.SetRefreshToken("b", "1//b");
        return locker;
    }

    private List<string> LogLines(string text) =>
        File.Exists(_log.FilePath) ? [.. File.ReadAllLines(_log.FilePath).Where(l => l.Contains(text, StringComparison.Ordinal))] : [];

    // A Credential Locker that can fail to read, or fail after some removals
    private sealed class FlakyLocker(InMemoryTokenStore inner) : ITokenStore
    {
        private int _removals;

        public bool ReadsFail { get; set; }

        public int RemovalsBeforeFailure { get; set; } = int.MaxValue;

        public bool ClientCleared { get; private set; }

        public OAuthClientCredentials? GetClientCredentials() => Read(inner.GetClientCredentials);

        public void SetClientCredentials(OAuthClientCredentials credentials) => inner.SetClientCredentials(credentials);

        public void ClearClientCredentials()
        {
            Removing();
            inner.ClearClientCredentials();
            ClientCleared = true;
        }

        public string? GetRefreshToken(string accountId) => Read(() => inner.GetRefreshToken(accountId));

        public void SetRefreshToken(string accountId, string refreshToken) => inner.SetRefreshToken(accountId, refreshToken);

        public void RemoveRefreshToken(string accountId)
        {
            Removing();
            inner.RemoveRefreshToken(accountId);
        }

        public IReadOnlyList<string> GetAccountIds() => Read(inner.GetAccountIds);

        private T Read<T>(Func<T> read) => ReadsFail ? throw new UnauthorizedAccessException("vault locked") : read();

        private void Removing()
        {
            if (_removals++ >= RemovalsBeforeFailure)
            {
                throw new IOException("vault busy");
            }
        }
    }
}
