using LeafCalendar.Core.Data;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Tests.Support;

/// <summary>A migrated SQLite database in a temp folder, deleted on dispose.</summary>
public sealed class TestDatabase : IDisposable
{
    readonly TempFolder _folder = new();

    /// <summary>Creates and migrates the database.</summary>
    public TestDatabase()
    {
        Database = new LeafDatabase(Path.Combine(_folder.Path, "leaf.db"));
        Database.Migrate();
    }

    /// <summary>The database under test.</summary>
    public LeafDatabase Database { get; }

    /// <summary>The account used by fixtures (matches userinfo.json).</summary>
    public static Account SampleAccount { get; } =
        new("109876543210", "leaf.tester@gmail.com", "Leaf Tester", null, AccountStatus.Ok);

    /// <inheritdoc />
    public void Dispose()
    {
        // Clear Only This Database's Pool: ClearAllPools would dispose connections that test
        // classes running in parallel are using ("Cannot access a disposed object: sqlite3")
        using (var conn = Database.Open())
        {
            conn.Close();
            SqliteConnection.ClearPool(conn);
        }

        _folder.Dispose();
    }
}
