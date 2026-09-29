namespace LeafCalendar.Core.Hosting;

/// <summary>Per-profile folders under the app's local data root.</summary>
public sealed record LeafPaths(string Root, string Profile)
{
    /// <summary><c>{Root}\profiles\{Profile}</c>.</summary>
    public string ProfileDirectory => Path.Combine(Root, "profiles", Profile);

    /// <summary>SQLite database file.</summary>
    public string DatabasePath => Path.Combine(ProfileDirectory, "leaf.db");

    /// <summary>Log folder.</summary>
    public string LogDirectory => Path.Combine(ProfileDirectory, "Logs");
}
