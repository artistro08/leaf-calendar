using LeafCalendar.Core.Data;
using LeafCalendar.Tests.Support;
using Microsoft.Data.Sqlite;

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
        Assert.Equal(4L, (long)version.ExecuteScalar()!);
        Assert.Equal(1L, (long)foreignKeys.ExecuteScalar()!);
    }

    [Fact]
    public void Migrate_FromVersion3_KeepsQueuedEdits()
    {
        using var folder = new TempFolder();
        var database     = new LeafDatabase(Path.Combine(folder.Path, "leaf.db"));

        // A Version 3 Database With One Queued Edit (what the owner's install has)
        using (var conn = database.Open())
        using (var setup = conn.CreateCommand())
        {
            setup.CommandText = Schema.V1 + Schema.V2 + Schema.V3 + """
                PRAGMA user_version = 3;
                INSERT INTO accounts (id, email) VALUES ('acct', 'a@example.com');
                INSERT INTO outbox (account_id, calendar_id, event_id, operation, payload) VALUES ('acct', 'cal', 'evt', 'patch', '{}');
                """;
            setup.ExecuteNonQuery();
        }

        database.Migrate();

        using (var conn = database.Open())
        {
            var entry = Assert.Single(OutboxStore.Pending(conn, "acct"));
            Assert.Equal("evt", entry.EventId);
            Assert.Null(entry.DependsOn);
            conn.Close();
            SqliteConnection.ClearPool(conn);
        }
    }
}
