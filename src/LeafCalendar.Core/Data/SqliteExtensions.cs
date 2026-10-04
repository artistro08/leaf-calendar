using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Data;

/// <summary>Parameterized SQL helpers. SQL text is always a constant; values always go through parameters.</summary>
internal static class SqliteExtensions
{
    /// <summary>Runs a non-query statement.</summary>
    public static int Execute(this SqliteConnection conn, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Create(conn, tx, sql, parameters);
        return command.ExecuteNonQuery();
    }

    /// <summary>Runs a query and maps each row.</summary>
    public static List<T> Query<T>(this SqliteConnection conn, SqliteTransaction? tx, string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        using var command = Create(conn, tx, sql, parameters);
        using var reader = command.ExecuteReader();

        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    /// <summary>Reads a Unix-milliseconds column as UTC, or null.</summary>
    public static DateTimeOffset? GetUnixMsOrNull(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(ordinal));

    /// <summary>Reads a nullable text column.</summary>
    public static string? GetStringOrNull(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    [SuppressMessage("Security", "CA2100", Justification = "Callers pass constant SQL from the Data folder; values are always parameters.")]
    private static SqliteCommand Create(SqliteConnection conn, SqliteTransaction? tx, string sql, (string Name, object? Value)[] parameters)
    {
        var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
