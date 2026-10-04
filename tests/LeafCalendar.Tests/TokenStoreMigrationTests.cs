using LeafCalendar.Core.Auth;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class TokenStoreMigrationTests
{
    [Fact]
    public void Migrate_FromLocker_MovesEverySecretOnce()
    {
        var from = new InMemoryTokenStore();
        from.SetClientCredentials(new("id.apps.googleusercontent.com", "secret"));
        from.SetRefreshToken("a", "1//a");
        from.SetRefreshToken("b", "1//b");
        var to = new InMemoryTokenStore();

        Assert.True(TokenStoreMigration.Migrate(from, to));
        Assert.False(TokenStoreMigration.Migrate(from, to));

        Assert.Equal(new OAuthClientCredentials("id.apps.googleusercontent.com", "secret"), to.GetClientCredentials());
        Assert.Equal("1//a", to.GetRefreshToken("a"));
        Assert.Equal("1//b", to.GetRefreshToken("b"));
        Assert.Equal(["a", "b"], to.GetAccountIds().Order(StringComparer.Ordinal));
        Assert.Empty(from.GetAccountIds());
        Assert.Null(from.GetClientCredentials());
    }

    [Fact]
    public void Migrate_NothingInTheLocker_ReturnsFalse()
    {
        var to = new InMemoryTokenStore();
        to.SetRefreshToken("a", "1//new");

        Assert.False(TokenStoreMigration.Migrate(new InMemoryTokenStore(), to));
        Assert.Equal("1//new", to.GetRefreshToken("a"));
    }

    [Fact]
    public void Migrate_WriteFails_KeepsTheLockerCopy()
    {
        var from = new InMemoryTokenStore();
        from.SetClientCredentials(new("id.apps.googleusercontent.com", "secret"));
        from.SetRefreshToken("a", "1//a");

        Assert.ThrowsAny<IOException>(() => TokenStoreMigration.Migrate(from, new ThrowingTokenStore()));
        Assert.Equal("1//a", from.GetRefreshToken("a"));
        Assert.NotNull(from.GetClientCredentials());
    }

    // A new store whose disk is full: every write fails
    private sealed class ThrowingTokenStore : ITokenStore
    {
        public OAuthClientCredentials? GetClientCredentials() => null;

        public void SetClientCredentials(OAuthClientCredentials credentials) => throw new IOException("disk full");

        public void ClearClientCredentials() => throw new IOException("disk full");

        public string? GetRefreshToken(string accountId) => null;

        public void SetRefreshToken(string accountId, string refreshToken) => throw new IOException("disk full");

        public void RemoveRefreshToken(string accountId) => throw new IOException("disk full");

        public IReadOnlyList<string> GetAccountIds() => [];
    }
}
