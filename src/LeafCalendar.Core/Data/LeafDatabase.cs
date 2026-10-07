using System.Globalization;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Data;

/// <summary>
/// Leaf's local SQLite database (one file per profile).
/// </summary>
/// <remarks>
/// Uses WAL mode so the UI can read while sync writes. Every connection turns on foreign keys,
/// so deleting an account cascades to its calendars and events. Schema changes are numbered
/// migrations tracked in <c>PRAGMA user_version</c>.
/// </remarks>
public sealed class LeafDatabase(string path)
{
    /// <summary>Database file path.</summary>
    public string Path { get; } = path;

    /// <summary>Opens a pooled connection with foreign keys enabled. Dispose it after use.</summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = true }.ToString());
        connection.Open();
        connection.Execute(null, "PRAGMA foreign_keys = ON;");
        return connection;
    }

    /// <summary>Creates the file if needed and applies pending migrations.</summary>
    public void Migrate()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        using var conn = Open();
        conn.Execute(null, "PRAGMA journal_mode = WAL;");

        var version = Convert.ToInt32(conn.Query(null, "PRAGMA user_version;", r => r.GetInt64(0)).Single(), CultureInfo.InvariantCulture);

        // Version 1
        if (version < 1)
        {
            using var tx = conn.BeginTransaction();
            conn.Execute(tx, Schema.V1);
            conn.Execute(tx, "PRAGMA user_version = 1;");
            tx.Commit();
        }

        // Version 2
        if (version < 2)
        {
            using var tx = conn.BeginTransaction();
            conn.Execute(tx, Schema.V2);
            conn.Execute(tx, "PRAGMA user_version = 2;");
            tx.Commit();
        }

        // Version 3
        if (version < 3)
        {
            using var tx = conn.BeginTransaction();
            conn.Execute(tx, Schema.V3);
            conn.Execute(tx, "PRAGMA user_version = 3;");
            tx.Commit();
        }

        // Version 4
        if (version < 4)
        {
            using var tx = conn.BeginTransaction();
            conn.Execute(tx, Schema.V4);
            conn.Execute(tx, "PRAGMA user_version = 4;");
            tx.Commit();
        }

        // Version 5
        if (version < 5)
        {
            using var tx = conn.BeginTransaction();
            conn.Execute(tx, Schema.V5);
            conn.Execute(tx, "PRAGMA user_version = 5;");
            tx.Commit();
        }

        // Version 6
        if (version < 6)
        {
            using var tx = conn.BeginTransaction();
            conn.Execute(tx, Schema.V6);
            conn.Execute(tx, "PRAGMA user_version = 6;");
            tx.Commit();
        }

        // Version 7
        if (version < 7)
        {
            using var tx = conn.BeginTransaction();
            conn.Execute(tx, Schema.V7);
            conn.Execute(tx, "PRAGMA user_version = 7;");
            tx.Commit();
        }

        // Version 8
        if (version < 8)
        {
            using var tx = conn.BeginTransaction();
            conn.Execute(tx, Schema.V8);
            conn.Execute(tx, "PRAGMA user_version = 8;");
            tx.Commit();
        }

        // Version 9
        if (version < 9)
        {
            using var tx = conn.BeginTransaction();
            conn.Execute(tx, Schema.V9);
            conn.Execute(tx, "PRAGMA user_version = 9;");
            tx.Commit();
        }
    }
}
