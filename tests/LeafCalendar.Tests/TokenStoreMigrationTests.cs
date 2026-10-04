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

        public OAuthClientCredentials? GetClientCredentials() => Read(inner.GetClientCredentials);

        public void SetClientCredentials(OAuthClientCredentials credentials) => inner.SetClientCredentials(credentials);

        public void ClearClientCredentials()
        {
            Removing();
            inner.ClearClientCredentials();
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
