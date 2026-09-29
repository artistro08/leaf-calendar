using LeafCalendar.Core.Data;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class AccountStoreTests : IDisposable
{
    readonly TestDatabase _db = new();

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
}
