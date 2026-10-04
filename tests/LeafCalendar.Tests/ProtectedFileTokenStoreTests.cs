using System.Security.Cryptography;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.Sync;
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
    public void CorruptFile_IsMovedAsideBeforeTheNextWrite()
    {
        File.WriteAllBytes(Path.Combine(_folder.Path, "secrets.bin.unreadable"), [9]);
        File.WriteAllBytes(Path.Combine(_folder.Path, "secrets.bin"), [1, 2, 3]);

        Assert.Empty(_store.GetAccountIds());
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(_folder.Path, "secrets.bin")));

        _store.SetRefreshToken("acc", "1//token");

        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(_folder.Path, "secrets.bin.unreadable")));
    }

    [Fact]
    public void Dpapi_BadData_FailsWithTheCodesTheStoreReadsAsEmpty()
    {
        // Not DPAPI data at all: ERROR_INVALID_PARAMETER
        Assert.Equal(87, Assert.Throws<CryptographicException>(() => Dpapi.Unprotect([1, 2, 3], [1])).HResult);

        // Another profile's (or user's) data: ERROR_INVALID_DATA
        var data = Dpapi.Protect([1, 2, 3], [1]);
        Assert.Equal(13, Assert.Throws<CryptographicException>(() => Dpapi.Unprotect(data, [2])).HResult);
    }

    [Fact]
    public void DecryptFails_ForAnotherReason_ThrowsAndKeepsTheFile()
    {
        // NTE_BAD_KEY_STATE: the user's key isn't available yet (a password change not synced, for one)
        _store.SetRefreshToken("acct-1", "1//a");
        var path = Path.Combine(_folder.Path, "secrets.bin");
        var before = File.ReadAllBytes(path);
        var store = new ProtectedFileTokenStore(_folder.Path, (_, _) => throw new CryptographicException(unchecked((int)0x8009000B)));

        Assert.True(SecretsUnavailable.Is(Assert.Throws<InvalidDataException>(() => store.GetRefreshToken("acct-1"))));
        Assert.True(SecretsUnavailable.Is(Assert.Throws<InvalidDataException>(() => store.SetRefreshToken("acct-2", "1//b"))));
        Assert.True(SecretsUnavailable.Is(Assert.Throws<InvalidDataException>(() => store.GetClientCredentials())));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".unreadable"));
        Assert.Equal("1//a", _store.GetRefreshToken("acct-1"));
    }

    [Fact]
    public void FileLocked_ThrowsSecretsUnavailable_AndKeepsTheFile()
    {
        _store.SetRefreshToken("acct-1", "1//a");

        using (new FileStream(Path.Combine(_folder.Path, "secrets.bin"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(SecretsUnavailable.Is(Assert.Throws<InvalidDataException>(() => _store.GetRefreshToken("acct-1"))));
        }

        Assert.Equal("1//a", _store.GetRefreshToken("acct-1"));
    }

    [Fact]
    public async Task DecryptFails_DuringSync_IsRetriedLater_NeverSignedOut()
    {
        using var harness = new SyncHarness();
        harness.RouteStandardGoogle();
        _store.SetRefreshToken(SyncHarness.AccountId, "1//test-refresh-token");
        var broken = true;
        var store = new ProtectedFileTokenStore(_folder.Path, (data, entropy) => broken ? throw new CryptographicException(unchecked((int)0x8009000B)) : Dpapi.Unprotect(data, entropy));
        var http = new HttpClient(harness.Google);
        using var provider = new AccessTokenProvider(new GoogleOAuthClient(http, new("id.apps.googleusercontent.com", "GOCSPX-test"), harness.Time), store, harness.Time);
        using var engine = new SyncEngine(new GoogleCalendarClient(http, provider), harness.Db.Database, harness.Log, harness.Time);
        var signIns = new List<string>();
        engine.SignInNeeded += (_, account) => signIns.Add(account);

        // Secrets Unavailable: a failed sync, not a sign-out
        await engine.SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Empty(signIns);
        Assert.Contains("sync.account.failed", File.ReadAllText(harness.LogPath), StringComparison.Ordinal);
        Assert.Contains("InvalidDataException", File.ReadAllText(harness.LogPath), StringComparison.Ordinal);
        using (var conn = harness.Db.Database.Open())
        {
            Assert.Equal(AccountStatus.Ok, Assert.Single(AccountStore.GetAll(conn)).Status);
            Assert.Empty(CalendarStore.GetForAccount(conn, SyncHarness.AccountId));
        }

        // Readable Again: the next sync goes through
        broken = false;
        await engine.SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Empty(signIns);
        using (var conn = harness.Db.Database.Open())
        {
            Assert.NotEmpty(CalendarStore.GetForAccount(conn, SyncHarness.AccountId));
        }
    }

    [Fact]
    public void ProfileName_DifferentCase_ReadsTheSameFile()
    {
        new ProtectedFileTokenStore(Path.Combine(_folder.Path, "Default")).SetRefreshToken("acct-1", "1//a");

        Assert.Equal("1//a", new ProtectedFileTokenStore(Path.Combine(_folder.Path, "default")).GetRefreshToken("acct-1"));
    }

    [Fact]
    public void Import_WritesClientAndTokensInOneFile()
    {
        Assert.False(_store.Exists);

        _store.Import(new("one.apps.googleusercontent.com", "secret-1"), new Dictionary<string, string> { ["a"] = "1//a", ["b"] = "1//b" });

        Assert.True(_store.Exists);
        Assert.Equal(new OAuthClientCredentials("one.apps.googleusercontent.com", "secret-1"), _store.GetClientCredentials());
        Assert.Equal(["a", "b"], _store.GetAccountIds().Order(StringComparer.Ordinal));
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
