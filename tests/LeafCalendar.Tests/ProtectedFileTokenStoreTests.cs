using LeafCalendar.Core.Auth;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class ProtectedFileTokenStoreTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly ProtectedFileTokenStore _store;

    public ProtectedFileTokenStoreTests() => _store = new ProtectedFileTokenStore(_folder.Path);

    public void Dispose() => _folder.Dispose();

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
        using var otherFolder = new TempFolder();
        var other = new ProtectedFileTokenStore(otherFolder.Path);

        _store.SetRefreshToken("acct-1", "1//mine");

        Assert.Null(other.GetRefreshToken("acct-1"));
    }

    [Fact]
    public void ClearClientCredentials_KeepsRefreshTokens()
    {
        _store.SetClientCredentials(new("one.apps.googleusercontent.com", "secret-1"));
        _store.SetRefreshToken("acct-1", "1//a");

        _store.ClearClientCredentials();

        Assert.Null(_store.GetClientCredentials());
        Assert.Equal("1//a", _store.GetRefreshToken("acct-1"));
    }

    [Fact]
    public void ProfileFolder_Missing_IsCreatedOnWrite()
    {
        var store = new ProtectedFileTokenStore(Path.Combine(_folder.Path, "profiles", "fresh"));

        store.SetRefreshToken("acct-1", "1//a");

        Assert.Equal("1//a", store.GetRefreshToken("acct-1"));
    }

    [Fact]
    public void File_IsNotPlainText()
    {
        _store.SetClientCredentials(new("one.apps.googleusercontent.com", "GOCSPX-plain-secret"));

        var bytes = File.ReadAllBytes(Path.Combine(_folder.Path, "secrets.bin"));

        Assert.DoesNotContain("GOCSPX-plain-secret", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void File_CopiedToAnotherProfile_CannotBeRead()
    {
        _store.SetRefreshToken("acct-1", "1//mine");
        using var otherFolder = new TempFolder();
        File.Copy(Path.Combine(_folder.Path, "secrets.bin"), Path.Combine(otherFolder.Path, "secrets.bin"));

        Assert.Null(new ProtectedFileTokenStore(otherFolder.Path).GetRefreshToken("acct-1"));
    }

    [Fact]
    public void CorruptFile_ReadsAsEmpty_AndTheNextWriteReplacesIt()
    {
        File.WriteAllBytes(Path.Combine(_folder.Path, "secrets.bin"), [1, 2, 3]);

        Assert.Null(_store.GetClientCredentials());
        _store.SetRefreshToken("acc", "1//token");
        Assert.Equal("1//token", _store.GetRefreshToken("acc"));
    }

    [Fact]
    public void DeleteAll_RemovesTheFile()
    {
        _store.SetRefreshToken("acct-1", "1//a");

        _store.DeleteAll();

        Assert.False(File.Exists(Path.Combine(_folder.Path, "secrets.bin")));
        Assert.Empty(_store.GetAccountIds());
    }
}
