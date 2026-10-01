// Track B owns this file (Milestone 5 Tasks 7-8): the people overlay, Meet with, and share availability.
using System.Net.Mail;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;
using LeafCalendar.Core.Tray;

namespace LeafCalendar.App.ViewModels;

/// <summary>A person on the overlay: their address, the name to show, their color (0-3), and whether Google gave busy times.</summary>
public sealed record OverlayPerson(string Email, string Name, int ColorIndex, PersonBusyState State);

/// <summary>One of an overlaid person's busy stretches, with its title when their calendar is shared with details.</summary>
public sealed record OverlayBlock(string Email, int ColorIndex, DateTimeOffset Start, DateTimeOffset End, string? Title);

public sealed partial class CalendarViewModel
{
    // =========================================================================
    // PEOPLE OVERLAY AND MEET WITH
    // =========================================================================

    // Person colors cycle through this many (LeafBrushes.Person)
    const int PersonColors = 4;

    // ponytail: the overlay is fields plus one event; nothing here is stored (contacts privacy)
    List<OverlayPerson> _people = [];
    Dictionary<string, List<BusyBlock>> _blocks = new(StringComparer.OrdinalIgnoreCase);
    (DateTimeOffset From, DateTimeOffset To)? _loaded;
    bool _meetWith;

    // Bumped by every change to who is overlaid, so a lookup that finishes late is dropped
    int _overlayVersion;

    /// <summary>The people overlaid on the time grid, in the order picked.</summary>
    public IReadOnlyList<OverlayPerson> OverlayPeople => _people;

    /// <summary>True in Meet with mode: a new event gets the overlaid people as guests.</summary>
    public bool IsMeetWith => _meetWith;

    /// <summary>Who is overlaid, their busy times, or the mode changed (redraw the bar and the blocks).</summary>
    public event EventHandler? OverlayChanged;

    /// <summary>
    /// Overlays <paramref name="people"/> (P), or starts Meet with (F) when <paramref name="meetWith"/> is set, then
    /// looks up their busy times. Addresses that aren't valid are dropped, and at most <see cref="FreeBusyLookup.MaxPeople"/> are kept.
    /// </summary>
    public async Task ShowOverlayAsync(IReadOnlyList<Contact> people, bool meetWith)
    {
        // Colors Cycle; A Blank Name Shows The Address
        _people = [.. people
            .Select(p => (Email: p.Email.Trim(), Name: DisplayText.Clean(p.Name, 100)))
            .Where(p => IsAddress(p.Email))
            .DistinctBy(p => p.Email, StringComparer.OrdinalIgnoreCase)
            .Take(FreeBusyLookup.MaxPeople)
            .Select((p, i) => new OverlayPerson(p.Email, p.Name.Length > 0 ? p.Name : p.Email, i % PersonColors, PersonBusyState.Known))];

        _blocks.Clear();
        _meetWith = meetWith && _people.Count > 0;
        _loaded   = null;
        _overlayVersion++;
        OverlayChanged?.Invoke(this, EventArgs.Empty);

        await RefreshOverlayAsync();
    }

    /// <summary>Takes one person off the overlay.</summary>
    public void RemoveOverlayPerson(string email)
    {
        _people.RemoveAll(p => string.Equals(p.Email, email, StringComparison.OrdinalIgnoreCase));
        _blocks.Remove(email);
        if (_people.Count == 0)
        {
            ClearOverlay();
            return;
        }

        OverlayChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes everyone off the overlay and ends Meet with.</summary>
    public void ClearOverlay()
    {
        _people   = [];
        _blocks   = new(StringComparer.OrdinalIgnoreCase);
        _loaded   = null;
        _meetWith = false;
        _overlayVersion++;
        OverlayChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The overlaid busy stretches overlapping <c>[from, to)</c>, each with its person's color.</summary>
    public IReadOnlyList<OverlayBlock> OverlayBlocks(DateTimeOffset from, DateTimeOffset to) =>
        [.. _people.SelectMany(p => (_blocks.GetValueOrDefault(p.Email) ?? [])
            .Where(b => b.End > from && b.Start < to)
            .Select(b => new OverlayBlock(p.Email, p.ColorIndex, b.Start, b.End, b.Title)))];

    /// <summary>
    /// Looks up busy times for the visible period plus a week on each side, unless they're already loaded. A failure
    /// says so and keeps the blocks already shown.
    /// </summary>
    public async Task RefreshOverlayAsync()
    {
        if (_people.Count == 0 || PeopleAccountId() is not { } account || _services.Google is not { } google)
        {
            return;
        }

        // The Visible Period, A Week Each Side
        var from = OccurrenceQuery.LocalMidnight(PeriodStart.AddDays(-7), Zone);
        var to   = OccurrenceQuery.LocalMidnight(PeriodStart.AddDays(VisibleColumns + 7), Zone);
        if (_loaded is { } loaded && loaded.From <= from && loaded.To >= to)
        {
            return;
        }

        var version = _overlayVersion;
        IReadOnlyList<PersonBusy> found;
        try
        {
            found = await new FreeBusyLookup(google.Calendar, _services.Log).LookupAsync(account, [.. _people.Select(p => p.Email)], from, to, _life.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _services.Log.Info("freebusy.lookup.failed", $"account={account} status={Status(ex)}");
            ShowMessage("Couldn't get busy times. Check your connection.");
            return;
        }

        // Someone Changed Who Is Overlaid Meanwhile
        if (version != _overlayVersion)
        {
            return;
        }

        _blocks = found.ToDictionary(p => p.Email, p => p.Blocks.ToList(), StringComparer.OrdinalIgnoreCase);
        _people = [.. _people.Select(p => p with { State = found.FirstOrDefault(f => string.Equals(f.Email, p.Email, StringComparison.OrdinalIgnoreCase))?.State ?? PersonBusyState.Unknown })];
        _loaded = (from, to);
        OverlayChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>In Meet with mode, adds every overlaid person as a guest of the new event being edited.</summary>
    public void AddOverlayGuests()
    {
        if (!_meetWith || Editing is not { IsNew: true } editor)
        {
            return;
        }

        foreach (var p in _people)
        {
            editor.GuestInput = p.Email;
            editor.AddGuest();
        }
    }

    /// <summary>The account that asks Google about people: your main account when it's still signed in, else the first account; null with none.</summary>
    public string? PeopleAccountId() =>
        Settings.MainAccountId is { } main && AccountEmails.ContainsKey(main) ? main : AccountEmails.Keys.FirstOrDefault();

    /// <summary>Contacts matching <paramref name="text"/> for the people picker (none without an account).</summary>
    public Task<ContactResults> SearchPeopleAsync(string text, CancellationToken ct) =>
        _services.Google is { } google && PeopleAccountId() is { } account
            ? google.Contacts.SearchAsync(account, text, ct)
            : Task.FromResult(new ContactResults([], ContactAccess.Allowed));

    /// <summary>True when <paramref name="text"/> is exactly one valid email address.</summary>
    public static bool IsAddress(string text) => MailAddress.TryCreate(text, out var address) && address.Address == text;

    // A status for the log: Google's HTTP status, or the kind of failure (never a message, which could carry an address)
    static string Status(Exception ex) => ex is GoogleApiException google ? ((int)google.Status).ToString(System.Globalization.CultureInfo.InvariantCulture) : ex.GetType().Name;
}
