using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Controls;

/// <summary>
/// Time zone suggestions for a search box (time travel, Settings' extra zones): one row per match, the zone you're in
/// shown but disabled. Rows are <see cref="ListViewItem"/>s holding plain strings (a list of Core records can't go to
/// WinRT under Native AOT), each its own container, so a disabled row is never recycled onto another zone.
/// </summary>
public static class ZoneSuggestions
{
    /// <summary>A row per choice; the one that is <paramref name="current"/> can't be picked.</summary>
    public static List<ListViewItem> Rows(IReadOnlyList<TimeZoneChoice> choices, TimeZoneInfo current)
    {
        ArgumentNullException.ThrowIfNull(choices);

        return [.. choices.Select(c => new ListViewItem { Content = c.ToString(), IsEnabled = !TimeZoneCatalog.IsSameZone(c.Id, current) })];
    }

    /// <summary>The choice behind a picked row (matched by reference to our own rows, never read back), or null for a disabled or unknown one.</summary>
    public static TimeZoneChoice? Chosen(List<ListViewItem> rows, IReadOnlyList<TimeZoneChoice> choices, object? picked)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(choices);

        var index = rows.FindIndex(r => ReferenceEquals(r, picked));
        return index >= 0 && index < choices.Count && rows[index].IsEnabled ? choices[index] : null;
    }

    /// <summary>The first choice that isn't <paramref name="current"/> (what Enter takes with nothing picked), or null.</summary>
    public static TimeZoneChoice? FirstPickable(IReadOnlyList<TimeZoneChoice> choices, TimeZoneInfo current) =>
        choices.FirstOrDefault(c => !TimeZoneCatalog.IsSameZone(c.Id, current));
}
