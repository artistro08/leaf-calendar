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
        Assert.Equal(5L, (long)version.ExecuteScalar()!);
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

    [Fact]
    public void Migrate_FromVersion4_KeepsDataAndAddsTheAlertLedger()
    {
        using var folder = new TempFolder();
        var database     = new LeafDatabase(Path.Combine(folder.Path, "leaf.db"));

        // A Version 4 Database With A Calendar, An Event, And Outbox Rows (One Waiting On Another)
        using (var conn = database.Open())
        using (var setup = conn.CreateCommand())
        {
            setup.CommandText = Schema.V1 + Schema.V2 + Schema.V3 + Schema.V4 + """
                PRAGMA user_version = 4;
                INSERT INTO accounts (id, email) VALUES ('acct', 'a@example.com');
                INSERT INTO calendars (account_id, id, summary, access_role) VALUES ('acct', 'cal', 'Work', 'owner');
                INSERT INTO events (account_id, calendar_id, id, status, raw_json) VALUES ('acct', 'cal', 'evt', 'confirmed', '{}');
                INSERT INTO outbox (account_id, calendar_id, event_id, operation, payload) VALUES ('acct', 'cal', 'evt', 'patch', '{}');
                INSERT INTO outbox (account_id, calendar_id, event_id, operation, payload, depends_on) VALUES ('acct', 'cal', 'evt2', 'insert', '{}', 1);
                """;
            setup.ExecuteNonQuery();
        }

        database.Migrate();

        using (var conn = database.Open())
        {
            var pending = OutboxStore.Pending(conn, "acct");
            Assert.Equal(2, pending.Count);
            Assert.Null(pending[0].DependsOn);
            Assert.Equal(1L, pending[1].DependsOn);
            Assert.Equal(1L, conn.Query(null, "SELECT COUNT(*) FROM calendars;", r => r.GetInt64(0)).Single());
            Assert.Equal(1L, conn.Query(null, "SELECT COUNT(*) FROM events;", r => r.GetInt64(0)).Single());
            Assert.Equal(5L, conn.Query(null, "PRAGMA user_version;", r => r.GetInt64(0)).Single());
            Assert.True(AlertLedger.TryAdd(conn, "k", LeafCalendar.Core.Alerts.AlertKind.Reminder, "t", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            conn.Close();
            SqliteConnection.ClearPool(conn);
        }
    }
}
