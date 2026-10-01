using System.Globalization;

namespace LeafCalendar.Core.Editing;

/// <summary>The popup reminder times the editor offers, and how each one reads ("10 min", "1 hr", "1 day").</summary>
public static class ReminderTimes
{
    static readonly int[] Presets = [0, 5, 10, 15, 30, 60, 1440];

    /// <summary>The presets plus any loaded time that isn't one, in minutes, shortest first.</summary>
    public static IReadOnlyList<int> Choices(IEnumerable<int> loaded) => [.. Presets.Union(loaded).Order()];

    /// <summary>"At start", "10 min", "1 hr", "1 day", or "2 days".</summary>
    public static string Label(int minutes) => minutes switch
    {
        0                          => "At start",
        < 60                       => string.Create(CultureInfo.InvariantCulture, $"{minutes} min"),
        _ when minutes % 1440 == 0 => minutes == 1440 ? "1 day" : string.Create(CultureInfo.InvariantCulture, $"{minutes / 1440} days"),
        _ when minutes % 60 == 0   => string.Create(CultureInfo.InvariantCulture, $"{minutes / 60} hr"),
        _                          => string.Create(CultureInfo.InvariantCulture, $"{minutes} min"),
    };
}
