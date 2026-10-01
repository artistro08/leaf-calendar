using LeafCalendar.Core.Editing;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.People;

/// <summary>A room you've booked before: its cleaned name and its checked address.</summary>
public sealed record Room(string Name, string Email);

/// <summary>
/// The rooms offered in a Workspace account's editor: the ones you've booked before, read from your synced events.
/// </summary>
/// <remarks>
/// Listing every room in a Workspace needs an admin-only scope, so Leaf only knows rooms from events. A room is an
/// attendee with Google's resource address (<c>@resource.calendar.google.com</c>). Room names are untrusted text,
/// cleaned and capped like contact names. Computed each time and never stored.
/// </remarks>
public static class Rooms
{
    /// <summary>The account's rooms, one per address (ignoring case), sorted by name.</summary>
    /// <exception cref="SqliteException">The database couldn't be read.</exception>
    public static IReadOnlyList<Room> Load(SqliteConnection conn, string accountId)
    {
        var rooms = new Dictionary<string, Room>(StringComparer.OrdinalIgnoreCase);

        // Only Rows That Mention A Resource Are Parsed
        foreach (var (_, email, name, isRoom, _) in FrequentPeople.Attendees(conn, "AND raw_json LIKE '%resource%'", ("$account", accountId)))
        {
            // Google's Resource Address Is Required: a resource flag on any other address could be spoofed by an invite
            if (!isRoom || !email.EndsWith(EventJson.RoomDomain, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var known = rooms.GetValueOrDefault(email);
            rooms[email] = new Room(name.Length > 0 ? name : known?.Name ?? email[..email.IndexOf('@', StringComparison.Ordinal)], known?.Email ?? email);
        }

        return [.. rooms.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The rooms whose name contains <paramref name="query"/> (ignoring case), in their order, at most <paramref name="max"/>.</summary>
    public static IReadOnlyList<Room> Match(IReadOnlyList<Room> rooms, string query, int max = 8)
    {
        ArgumentNullException.ThrowIfNull(rooms);

        var text = query?.Trim() ?? "";
        return text.Length == 0 ? [] : [.. rooms.Where(r => r.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(max)];
    }
}
