using System.Text.Json;
using LeafCalendar.Core.Data;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Settings;

/// <summary>Reads and writes <see cref="LeafSettings"/> as one JSON row in the <c>settings</c> table.</summary>
public static class SettingsStore
{
    private const string Key = "app";

    /// <summary>Loads settings; a missing, unreadable, or out-of-range row yields safe defaults.</summary>
    public static LeafSettings Load(SqliteConnection conn)
    {
        var json = conn.Query(null, "SELECT value FROM settings WHERE key = $key;", r => r.GetString(0), ("$key", Key)).SingleOrDefault();
        if (json is null)
        {
            return new LeafSettings().Normalize();
        }

        try
        {
            return (JsonSerializer.Deserialize(json, LeafJsonContext.Default.LeafSettings) ?? new LeafSettings()).Normalize();
        }
        catch (JsonException)
        {
            return new LeafSettings().Normalize();
        }
    }

    /// <summary>Saves (replaces) settings after normalizing them.</summary>
    public static void Save(SqliteConnection conn, LeafSettings settings) =>
        conn.Execute(
            null,
            "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            ("$key", Key),
            ("$value", JsonSerializer.Serialize(settings.Normalize(), LeafJsonContext.Default.LeafSettings)));
}
