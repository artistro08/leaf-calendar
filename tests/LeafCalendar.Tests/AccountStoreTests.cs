using LeafCalendar.Core.Data;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class AccountStoreTests : IDisposable
{
    private readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Upsert_SameIdTwice_UpdatesSingleRow()
    {
        using var conn = _db.Database.Open();

        AccountStore.Upsert(conn, TestDatabase.SampleAccount with { Status = AccountStatus.NeedsSignIn });
        AccountStore.Upsert(conn, TestDatabase.SampleAccount with { DisplayName = "Renamed" });

        var account = Assert.Single(AccountStore.GetAll(conn));
        Assert.Equal("Renamed", account.DisplayName);
        Assert.Equal(AccountStatus.Ok, account.Status);
    }

    [Fact]
    public void SetStatus_NeedsSignIn_Persists()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);

        AccountStore.SetStatus(conn, TestDatabase.SampleAccount.Id, AccountStatus.NeedsSignIn);

        Assert.Equal(AccountStatus.NeedsSignIn, AccountStore.GetAll(conn).Single().Status);
    }

    [Fact]
    public void Delete_Existing_RemovesRow()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);

        AccountStore.Delete(conn, TestDatabase.SampleAccount.Id);

        Assert.Empty(AccountStore.GetAll(conn));
    }

    [Fact]
    public void SetHostedDomain_RoundTrips_AndIsWorkspace()
    {
        using var conn = _db.Database.Open();
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);

        // Not Known Yet
        var unknown = AccountStore.GetAll(conn).Single();
        Assert.Null(unknown.HostedDomain);
        Assert.False(AccountStore.IsWorkspace(unknown));

        // Workspace (a later sign-in that doesn't know the domain keeps it)
        AccountStore.SetHostedDomain(conn, TestDatabase.SampleAccount.Id, "example.com");
        AccountStore.Upsert(conn, TestDatabase.SampleAccount);
        var workspace = AccountStore.GetAll(conn).Single();
        Assert.Equal("example.com", workspace.HostedDomain);
        Assert.True(AccountStore.IsWorkspace(workspace));

        // Personal
        AccountStore.Upsert(conn, TestDatabase.SampleAccount with { HostedDomain = "" });
        var personal = AccountStore.GetAll(conn).Single();
        Assert.Equal("", personal.HostedDomain);
        Assert.False(AccountStore.IsWorkspace(personal));
    }
}
