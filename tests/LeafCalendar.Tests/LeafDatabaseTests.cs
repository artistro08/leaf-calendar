using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class LeafDatabaseTests : IDisposable
{
    readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Migrate_Twice_IsIdempotentAndUsesWal()
    {
        _db.Database.Migrate();

        using var conn = _db.Database.Open();
        using var mode = conn.CreateCommand();
        mode.CommandText = "PRAGMA journal_mode;";
        using var version = conn.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        using var foreignKeys = conn.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_keys;";

        Assert.Equal("wal", (string)mode.ExecuteScalar()!);
        Assert.Equal(3L, (long)version.ExecuteScalar()!);
        Assert.Equal(1L, (long)foreignKeys.ExecuteScalar()!);
    }
}
