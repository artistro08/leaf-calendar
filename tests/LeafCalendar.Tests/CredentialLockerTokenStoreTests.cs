using LeafCalendar.Core.Auth;

namespace LeafCalendar.Tests;

public sealed class CredentialLockerTokenStoreTests : IDisposable
{
    readonly CredentialLockerTokenStore _store = new("test-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() => _store.DeleteAll();

    [Fact]
    public void GetClientCredentials_Empty_ReturnsNull()
    {
        Assert.Null(_store.GetClientCredentials());
        Assert.Null(_store.GetRefreshToken("missing"));
        Assert.Empty(_store.GetAccountIds());
    }

    [Fact]
    public void SetClientCredentials_Twice_KeepsLatest()
    {
        _store.SetClientCredentials(new("one.apps.googleusercontent.com", "secret-1"));
        _store.SetClientCredentials(new("two.apps.googleusercontent.com", "secret-2"));

        Assert.Equal(new OAuthClientCredentials("two.apps.googleusercontent.com", "secret-2"), _store.GetClientCredentials());
    }

    [Fact]
    public void RefreshTokens_SetReplaceRemove_RoundTrip()
    {
        _store.SetRefreshToken("acct-1", "1//a");
        _store.SetRefreshToken("acct-2", "1//b");
        _store.SetRefreshToken("acct-1", "1//a2");

        Assert.Equal("1//a2", _store.GetRefreshToken("acct-1"));
        Assert.Equal(["acct-1", "acct-2"], _store.GetAccountIds().Order(StringComparer.Ordinal));

        _store.RemoveRefreshToken("acct-1");

        Assert.Null(_store.GetRefreshToken("acct-1"));
        Assert.Equal(["acct-2"], _store.GetAccountIds());
    }

    [Fact]
    public void Profiles_Different_AreIsolated()
    {
        var other = new CredentialLockerTokenStore("test-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            _store.SetRefreshToken("acct-1", "1//mine");

            Assert.Null(other.GetRefreshToken("acct-1"));
        }
        finally
        {
            other.DeleteAll();
        }
    }
}
