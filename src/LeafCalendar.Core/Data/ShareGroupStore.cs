using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;
using LeafCalendar.Core.Tray;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Data;

/// <summary>
/// A saved share: its title and message as typed, the zone its text is written in, its times (merged, in order), and
/// the guests added to it with their names (in the order added; empty for none, a name empty when Leaf doesn't know it).
/// </summary>
/// <param name="Id">The group's ID.</param>
/// <param name="Title">The title, cleaned and capped.</param>
/// <param name="Message">The message as typed, capped.</param>
/// <param name="ZoneId">The IANA zone the text is written in.</param>
/// <param name="Slots">The times, merged and in order.</param>
/// <param name="Guests">The guests, in the order added.</param>
/// <param name="ProviderId">The scheduling provider of the group's link; empty for a plain group.</param>
/// <param name="LinkId">The provider's ID for the link; empty for a plain group.</param>
/// <param name="LinkUrl">The link's booking page; empty for a plain group.</param>
/// <param name="LinkMinutes">The link's length in minutes; 0 for a plain group.</param>
/// <param name="CreatedUtc">When the group was first saved.</param>
public sealed record ShareGroup(long Id, string Title, string Message, string ZoneId, IReadOnlyList<BusyRange> Slots, IReadOnlyList<Contact> Guests,
    string ProviderId = "", string LinkId = "", string LinkUrl = "", int LinkMinutes = 0, DateTimeOffset CreatedUtc = default);

/// <summary>A saved group's scheduling link; <see cref="None"/> for a plain group.</summary>
/// <param name="ProviderId">The scheduling provider's ID.</param>
/// <param name="LinkId">The provider's ID for the link.</param>
/// <param name="LinkUrl">The link's booking page (https).</param>
/// <param name="LinkMinutes">The link's length in minutes.</param>
public sealed record ShareLink(string ProviderId, string LinkId, string LinkUrl, int LinkMinutes)
{
    /// <summary>No link: a plain group.</summary>
    public static ShareLink None { get; } = new("", "", "", 0);
}

/// <summary>
/// Reads and writes the <c>share_groups</c>, <c>share_slots</c> and <c>share_guests</c> tables: availability you copied, kept so it stays on
/// the calendar until you approve a time, delete the group, or its times pass.
/// </summary>
public static class ShareGroupStore
{
    /// <summary>The longest title kept.</summary>
    public const int MaxTitleLength = 100;

    /// <summary>The longest guest address kept (the longest an email address can be).</summary>
    public const int MaxGuestEmailLength = 254;

    /// <summary>The most guests kept for one group.</summary>
    public const int MaxGuests = 50;

    /// <summary>Every group with a time that hasn't ended by <paramref name="now"/>, oldest first, ended times left out.</summary>
    public static IReadOnlyList<ShareGroup> GetAll(SqliteConnection conn, DateTimeOffset now)
    {
        var groups = conn.Query(
            null,
            "SELECT id, title, message, zone_id, created_utc, provider_id, link_id, link_url, link_minutes FROM share_groups ORDER BY created_utc, id;",
            r => (Id: r.GetInt64(0), Title: r.GetString(1), Message: r.GetString(2), Zone: r.GetString(3), Created: DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)),
                Provider: r.GetString(5), LinkId: r.GetString(6), LinkUrl: r.GetString(7), Minutes: r.GetInt32(8)));
        var slots = conn.Query(
            null,
            "SELECT group_id, start_utc, end_utc FROM share_slots WHERE end_utc > $now;",
            r => (Group: r.GetInt64(0), Range: new BusyRange(DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)))),
            ("$now", now.ToUnixTimeMilliseconds()))
            .ToLookup(s => s.Group, s => s.Range);
        var guests = conn.Query(
            null,
            "SELECT group_id, email, name FROM share_guests ORDER BY group_id, position;",
            r => (Group: r.GetInt64(0), Guest: new Contact(r.GetString(2), r.GetString(1))))
            .ToLookup(g => g.Group, g => g.Guest);

        return [.. groups
            .Where(g => slots[g.Id].Any())
            .Select(g => new ShareGroup(g.Id, g.Title, g.Message, g.Zone, BusyMath.Merge(slots[g.Id]), [.. guests[g.Id]], g.Provider, g.LinkId, g.LinkUrl, g.Minutes, g.Created))];
    }

    /// <summary>
    /// Saves a new group and returns its ID. The title is cleaned and capped, the message capped, the times merged, and
    /// the guests' addresses trimmed and capped and their names cleaned and capped (empty addresses and repeats dropped, at most
    /// <see cref="MaxGuests"/>). The link is kept only when whole and safe, otherwise the group is plain.
    /// </summary>
    public static long Insert(SqliteConnection conn, string title, string message, string zoneId, IReadOnlyList<BusyRange> slots, DateTimeOffset now, IReadOnlyList<Contact> guests, ShareLink link)
    {
        link = Clean(link);
        using var tx = conn.BeginTransaction();
        conn.Execute(
            tx,
            "INSERT INTO share_groups (title, message, zone_id, created_utc, provider_id, link_id, link_url, link_minutes) VALUES ($title, $message, $zone, $now, $provider, $linkId, $linkUrl, $minutes);",
            ("$title", DisplayText.Clean(title, MaxTitleLength)),
            ("$message", Cap(message)),
            ("$zone", zoneId),
            ("$now", now.ToUnixTimeMilliseconds()),
            ("$provider", link.ProviderId),
            ("$linkId", link.LinkId),
            ("$linkUrl", link.LinkUrl),
            ("$minutes", link.LinkMinutes));
        var id = conn.Query(tx, "SELECT last_insert_rowid();", r => r.GetInt64(0)).Single();
        WriteSlots(conn, tx, id, slots);
        WriteGuests(conn, tx, id, guests);
        tx.Commit();
        return id;
    }

    /// <summary>Replaces a group's title, message, zone, times, guests and link. With no times left the group is deleted.</summary>
    public static void Update(SqliteConnection conn, long id, string title, string message, string zoneId, IReadOnlyList<BusyRange> slots, IReadOnlyList<Contact> guests, ShareLink link)
    {
        if (slots.Count == 0)
        {
            Delete(conn, id);
            return;
        }

        link = Clean(link);
        using var tx = conn.BeginTransaction();
        conn.Execute(
            tx,
            "UPDATE share_groups SET title = $title, message = $message, zone_id = $zone, provider_id = $provider, link_id = $linkId, link_url = $linkUrl, link_minutes = $minutes WHERE id = $id;",
            ("$id", id),
            ("$title", DisplayText.Clean(title, MaxTitleLength)),
            ("$message", Cap(message)),
            ("$zone", zoneId),
            ("$provider", link.ProviderId),
            ("$linkId", link.LinkId),
            ("$linkUrl", link.LinkUrl),
            ("$minutes", link.LinkMinutes));
        conn.Execute(tx, "DELETE FROM share_slots WHERE group_id = $id;", ("$id", id));
        conn.Execute(tx, "DELETE FROM share_guests WHERE group_id = $id;", ("$id", id));
        WriteSlots(conn, tx, id, slots);
        WriteGuests(conn, tx, id, guests);
        tx.Commit();
    }

    /// <summary>Makes every group of a provider plain (no link), for when that provider is disconnected.</summary>
    public static void ClearProvider(SqliteConnection conn, string providerId) =>
        conn.Execute(
            null,
            "UPDATE share_groups SET provider_id = '', link_id = '', link_url = '', link_minutes = 0 WHERE provider_id = $p;",
            ("$p", providerId));

    /// <summary>Deletes a group, its times and its guests.</summary>
    public static void Delete(SqliteConnection conn, long id) =>
        conn.Execute(null, "DELETE FROM share_groups WHERE id = $id;", ("$id", id));

    /// <summary>Deletes times that ended by <paramref name="now"/>, then groups with none left.</summary>
    public static void Prune(SqliteConnection conn, DateTimeOffset now)
    {
        using var tx = conn.BeginTransaction();
        conn.Execute(tx, "DELETE FROM share_slots WHERE end_utc <= $now;", ("$now", now.ToUnixTimeMilliseconds()));
        conn.Execute(tx, "DELETE FROM share_groups WHERE id NOT IN (SELECT group_id FROM share_slots);");
        tx.Commit();
    }

    private static void WriteSlots(SqliteConnection conn, SqliteTransaction tx, long id, IReadOnlyList<BusyRange> slots)
    {
        foreach (var s in BusyMath.Merge(slots))
        {
            conn.Execute(
                tx,
                "INSERT INTO share_slots (group_id, start_utc, end_utc) VALUES ($id, $start, $end);",
                ("$id", id),
                ("$start", s.Start.ToUnixTimeMilliseconds()),
                ("$end", s.End.ToUnixTimeMilliseconds()));
        }
    }

    // Addresses trimmed and capped, names cleaned and capped, empty addresses and repeats (any case) dropped, the first MaxGuests kept, in order
    private static void WriteGuests(SqliteConnection conn, SqliteTransaction tx, long id, IReadOnlyList<Contact> guests)
    {
        var kept = guests
            .Select(g => (Email: g.Email.Trim(), Name: DisplayText.Clean(g.Name, ContactSearch.MaxName)))
            .Select(g => g.Email.Length > MaxGuestEmailLength ? g with { Email = g.Email[..MaxGuestEmailLength] } : g)
            .Where(g => g.Email.Length > 0)
            .DistinctBy(g => g.Email, StringComparer.OrdinalIgnoreCase)
            .Take(MaxGuests)
            .ToList();
        for (var i = 0; i < kept.Count; i++)
        {
            conn.Execute(
                tx,
                "INSERT INTO share_guests (group_id, position, email, name) VALUES ($id, $position, $email, $name);",
                ("$id", id),
                ("$position", i),
                ("$email", kept[i].Email),
                ("$name", kept[i].Name));
        }
    }

    // A link is kept only whole and safe: an https booking page Leaf may open, a provider and link ID, a length; otherwise the group is plain
    private static ShareLink Clean(ShareLink link) =>
        link.ProviderId.Length is > 0 and <= 32 && link.LinkId.Length is > 0 and <= 64 && link.LinkMinutes is > 0 and <= 1440 && link.LinkUrl.Length <= 2048
            && Uri.TryCreate(link.LinkUrl, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps && Events.LinkSafety.CanLaunch(url)
            ? link
            : ShareLink.None;

    private static string Cap(string message) => message.Length > AvailabilityText.MaxMessageLength ? message[..AvailabilityText.MaxMessageLength] : message;
}
