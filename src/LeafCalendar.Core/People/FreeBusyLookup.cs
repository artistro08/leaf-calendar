using System.Text.Json;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.Tray;

namespace LeafCalendar.Core.People;

/// <summary>Whether Google gave busy times for a person.</summary>
public enum PersonBusyState
{
    /// <summary>Google answered: the blocks are their busy times (none means free).</summary>
    Known,

    /// <summary>Google had nothing to give (not found, outside your domain, not shared). Never shown as free.</summary>
    Unknown,
}

/// <summary>One busy stretch of a person, with the event's title when their calendar is shared with details.</summary>
public sealed record BusyBlock(DateTimeOffset Start, DateTimeOffset End, string? Title);

/// <summary>A person's busy times over the looked-up range.</summary>
public sealed record PersonBusy(string Email, IReadOnlyList<BusyBlock> Blocks, PersonBusyState State);

/// <summary>
/// Busy times of teammates for the people overlay and Meet with: Google free/busy for everyone, plus event titles
/// for anyone whose calendar is shared with you with details.
/// </summary>
/// <remarks>
/// Free/busy is the authority: it always supplies the busy stretches, and a person it has an error for is
/// <see cref="PersonBusyState.Unknown"/> even when their details came back. Titles from the details are attached by
/// time to the busy stretches they cover; busy time no readable event covers stays untitled. Titles are untrusted
/// and cleaned. The result lives in memory for the caller's overlay and is never stored; the log carries the
/// account ID and counts only, never an address or a title.
/// </remarks>
/// <seealso href="https://developers.google.com/workspace/calendar/api/v3/reference/freebusy/query"/>
public sealed class FreeBusyLookup(GoogleCalendarClient client, AppLog log)
{
    /// <summary>Most people looked up at once (extra ones are dropped).</summary>
    public const int MaxPeople = 20;

    const int MaxTitle = 200;

    /// <summary>Busy times for each distinct address in <paramref name="emails"/> (trimmed, first spelling kept, at most <see cref="MaxPeople"/>).</summary>
    /// <param name="accountId">The account asking.</param>
    /// <param name="emails">Addresses to look up.</param>
    /// <param name="from">Range start.</param>
    /// <param name="to">Range end (exclusive).</param>
    /// <param name="ct">Cancels the lookup.</param>
    /// <returns>One entry per address, in the order given.</returns>
    /// <exception cref="GoogleApiException">Google refused the free/busy query.</exception>
    /// <exception cref="HttpRequestException">Google couldn't be reached.</exception>
    public async Task<IReadOnlyList<PersonBusy>> LookupAsync(string accountId, IReadOnlyList<string> emails, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        // Trimmed, Deduped, Capped
        var people = emails
            .Select(e => e.Trim())
            .Where(e => e.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxPeople)
            .ToList();

        if (people.Count == 0)
        {
            return [];
        }

        // One Free/Busy Query, One Details Read Per Person, In Parallel
        var freeBusy = client.QueryFreeBusyAsync(accountId, people, from, to, ct);
        var details  = people.Select(e => DetailsAsync(accountId, e, from, to, ct)).ToList();

        await Task.WhenAll([freeBusy, .. details]);

        var answers = await freeBusy;
        var result  = people.Select((email, i) => Person(email, answers[email], details[i].Result)).ToList();

        log.Info("freebusy.lookup", $"account={accountId} count={result.Count} unknown={result.Count(p => p.State == PersonBusyState.Unknown)}");
        return result;
    }

    // Free/Busy Supplies The Blocks; Details Only Name Them
    static PersonBusy Person(string email, FreeBusyResult answer, IReadOnlyList<BusyBlock> titled)
    {
        if (answer.Error is not null)
        {
            return new PersonBusy(email, [], PersonBusyState.Unknown);
        }

        var blocks = new List<BusyBlock>();
        foreach (var busy in BusyMath.Merge(answer.Busy))
        {
            // Titled Events Inside This Stretch, Clipped To It
            var inside = titled
                .Where(t => t.End > busy.Start && t.Start < busy.End)
                .Select(t => new BusyBlock(t.Start > busy.Start ? t.Start : busy.Start, t.End < busy.End ? t.End : busy.End, t.Title))
                .ToList();

            // The Rest Of The Stretch Stays Untitled
            var rest = BusyMath.Subtract([busy], inside.Select(t => new BusyRange(t.Start, t.End)));

            blocks.AddRange(inside);
            blocks.AddRange(rest.Select(r => new BusyBlock(r.Start, r.End, null)));
        }

        return new PersonBusy(email, [.. blocks.OrderBy(b => b.Start)], PersonBusyState.Known);
    }

    // Busy, Timed, Confirmed Events With Clean Titles; Empty When The Calendar Isn't Shared With Details Or The Read Fails
    async Task<IReadOnlyList<BusyBlock>> DetailsAsync(string accountId, string email, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        EventsPage? page;
        try
        {
            page = await client.ListEventsInRangeAsync(accountId, email, from, to, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return [];
        }

        if (page is null)
        {
            return [];
        }

        var blocks = new List<BusyBlock>();
        foreach (var item in page.Items)
        {
            if (Text(item, "status") != "confirmed" || Text(item, "transparency") == "transparent")
            {
                continue;
            }

            if (Time(item, "start") is not { } start || Time(item, "end") is not { } end || end <= start)
            {
                continue;
            }

            var title = DisplayText.Clean(Text(item, "summary"), MaxTitle);
            blocks.Add(new BusyBlock(start, end, title.Length > 0 ? title : null));
        }

        return blocks;
    }

    static string? Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // A Timed Start Or End (All-Day Events Have Only A Date)
    static DateTimeOffset? Time(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(name, out var when)
        && when.ValueKind == JsonValueKind.Object
        && when.TryGetProperty("dateTime", out var value)
        && value.ValueKind == JsonValueKind.String
        && value.TryGetDateTimeOffset(out var at)
            ? at
            : null;
}
