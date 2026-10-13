// Sharing with a booking link (scheduling providers spec sections 5-7): the panel's switch and link, the link's open
// times for the days on screen, fitting picks to them, and the messages that say what fitting did.
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;
using LeafCalendar.Core.Scheduling;

namespace LeafCalendar.App.ViewModels;

public sealed partial class CalendarViewModel
{
    // =========================================================================
    // SHARING WITH A BOOKING LINK
    // =========================================================================

    // The most messages stacked over the calendar at once (the oldest goes first)
    private const int MaxShareMessages = 4;

    // The panel's switch: picks are fitted to the link's openings and Copy writes a link per time
    private bool _useProvider;

    // The panel's link (kept while the switch is off, so turning it back on picks the same one)
    private SchedulingLink? _shareLink;

    // Every connected provider's links, read when sharing starts
    private IReadOnlyList<SchedulingLink> _links = [];

    // The link's open times for _openRange; null when unknown (offline, not fetched yet, or the switch is off)
    private IReadOnlyList<BusyRange>? _openTimes;
    private (DateTimeOffset From, DateTimeOffset To)? _openRange;

    // The range a fetch still on its way covers, so paging inside it doesn't ask again
    private (DateTimeOffset From, DateTimeOffset To)? _openAsked;

    // Bumped by every open-times fetch and every switch, link or sharing change, so only the newest answer lands
    private int _openGeneration;

    // Bumped when sharing starts, opens a group or stops, or its provider is signed out, so links read before are dropped
    private int _shareSession;

    // A link change, the switch turning on, or a pick made before the link or its openings were known is waiting for
    // open times to fit every pick to them
    private bool _refitPending;

    // Newest first, at most MaxShareMessages
    private readonly List<string> _shareMessages = [];

    /// <summary>True while at least one scheduling provider is connected (the share panel shows its switch and link).</summary>
    public bool HasScheduling => _services.Scheduling.Connected.Count > 0;

    /// <summary>The share panel's switch: on, picks are fitted to <see cref="ShareLink"/>'s openings and Copy writes a booking link per time.</summary>
    public bool UseProvider => _useProvider;

    /// <summary>The link this share books on, or null with the switch off or no link to pick.</summary>
    public SchedulingLink? ShareLink => _useProvider ? _shareLink : null;

    /// <summary>Every connected provider's links, for the share panel's Link box.</summary>
    public IReadOnlyList<SchedulingLink> ShareLinks => _links;

    /// <summary>The link's open times for the days around the screen, or null when unknown (the grid shades the rest).</summary>
    public IReadOnlyList<BusyRange>? OpenTimes => _openTimes;

    /// <summary>The stretch <see cref="OpenTimes"/> covers (outside it the openings aren't known, so nothing is shaded there), or null.</summary>
    public (DateTimeOffset From, DateTimeOffset To)? OpenTimesRange => _openRange;

    /// <summary>The share panel switch's label: "Schedule with {provider}" with one provider connected, "Schedule with a booking link" with more.</summary>
    public string ShareSwitchLabel => _services.Scheduling.Connected is [var only] ? $"Schedule with {only.Name}" : "Schedule with a booking link";

    /// <summary>The page on the share link's provider for checking why times aren't open; null without a link.</summary>
    public Uri? ShareAvailabilityPage => ShareLink is { } link ? SchedulingProviders.Find(link.ProviderId)?.AvailabilityPage : null;

    /// <summary>The messages over the calendar while sharing, newest first.</summary>
    public IReadOnlyList<string> ShareMessages => _shareMessages;

    /// <summary>
    /// How many times <see cref="SayShare"/> has run. The newest that many messages (at most all of them) were just said,
    /// so a card already on screen for one starts its time over.
    /// </summary>
    public int ShareMessagesSaid { get; private set; }

    /// <summary>A message was added to or removed from <see cref="ShareMessages"/>.</summary>
    public event EventHandler? ShareMessagesChanged;

    /// <summary>
    /// Shows a scheduling message over the calendar, newest on top. The same text already shown moves to the top
    /// instead of showing twice, and only the newest four are kept.
    /// </summary>
    public void SayShare(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _shareMessages.Remove(text);
        _shareMessages.Insert(0, text);
        ShareMessagesSaid++;
        if (_shareMessages.Count > MaxShareMessages)
        {
            _shareMessages.RemoveRange(MaxShareMessages, _shareMessages.Count - MaxShareMessages);
        }

        ShareMessagesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes a message off the calendar (its card faded out).</summary>
    public void DismissShareMessage(string text)
    {
        if (_shareMessages.Remove(text))
        {
            ShareMessagesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Turns the share panel's switch on or off. On fits every pick to the link's openings once they're known; off keeps
    /// the picks as they are, and Save then keeps the group as plain times.
    /// </summary>
    public void SetUseProvider(bool on)
    {
        if (!_sharing || on == _useProvider)
        {
            return;
        }

        _useProvider = on;
        _shareLink ??= DefaultLink();
        ClearOpenTimes();
        _refitPending = on;
        ShareChanged?.Invoke(this, EventArgs.Empty);
        if (on)
        {
            Fire(RefreshOpenTimesAsync, "scheduling.open.refresh.failed");
        }
    }

    /// <summary>Picks the link this share books on (one of <see cref="ShareLinks"/>) and fits every pick to its openings once they're known.</summary>
    public void SetShareLink(SchedulingLink? link)
    {
        if (!_sharing || link is null || link == _shareLink || !_links.Contains(link))
        {
            return;
        }

        _shareLink = link;
        ClearOpenTimes();
        _refitPending = _useProvider;
        ShareChanged?.Invoke(this, EventArgs.Empty);
        Fire(RefreshOpenTimesAsync, "scheduling.open.refresh.failed");
    }

    /// <summary>
    /// A pick moved on the grid: it lands where dropped, merged with any pick it now overlaps (fitted to the link's
    /// openings with the switch on). Nothing happens when that place no longer holds the dragged time.
    /// </summary>
    public void MoveShareSlot(GridSlot slot, DateTimeOffset start, DateTimeOffset end)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (slot.Title is null && slot.Index < _slots.Count && _slots[slot.Index] == slot.Range)
        {
            UpdateShareSlot(slot.Index, start, end);
        }
    }

    // Sharing started: the switch starts on with the default link while a provider is connected
    private void StartScheduling()
    {
        ResetScheduling();
        _useProvider = HasScheduling;
        if (_useProvider)
        {
            LoadLinks(null);
        }
    }

    // A saved group opened with its own switch and link. Until the links are read (or when the provider can't be asked)
    // the group's saved link stands in, so Save keeps it and Copy can still build links offline
    private void OpenScheduling(ShareGroup group)
    {
        ResetScheduling();
        _useProvider = group.ProviderId.Length > 0;
        if (_useProvider && Uri.TryCreate(group.LinkUrl, UriKind.Absolute, out var url))
        {
            // ponytail: the stand-in doesn't know whether the link takes extra guests, so it says no (only the first goes in)
            _shareLink = new SchedulingLink(group.ProviderId, group.LinkId, "", group.LinkMinutes, url, AllowsGuests: false);
        }

        // Its provider already refused the sign-in (OnSchedulingChanged says so when that happens while it's open)
        if (_useProvider && _services.Scheduling.StateOf(group.ProviderId) == ProviderState.NeedsReconnect)
        {
            SayShare(NeedsReconnectMessage(group.ProviderId));
        }

        if (HasScheduling || _useProvider)
        {
            LoadLinks(group);
        }
    }

    // Sharing stopped: nothing provider-related is kept (the messages fade on their own)
    private void ResetScheduling()
    {
        _shareSession++;
        _useProvider = false;
        _shareLink = null;
        _links = [];
        _refitPending = false;
        ClearOpenTimes();
    }

    private void ClearOpenTimes()
    {
        _openGeneration++;
        _openTimes = null;
        _openRange = null;
        _openAsked = null;
    }

    // Reads every connected provider's links off the UI thread, then picks the group's link (or the default) and asks
    // for its open times. A group's link that its provider no longer lists turns the switch off; when the provider gave
    // no links at all (offline, or it needs reconnecting) the saved link stays
    private void LoadLinks(ShareGroup? group)
    {
        var session = _shareSession;
        Fire(async () =>
        {
            var links = await Task.Run(() => _services.Scheduling.GetLinksAsync(_life.Token));
            if (session != _shareSession)
            {
                return;
            }

            _links = links;
            if (group is { ProviderId.Length: > 0 })
            {
                var saved = links.FirstOrDefault(l => l.ProviderId == group.ProviderId && l.Id == group.LinkId);
                if (saved is not null)
                {
                    _shareLink = saved;
                }
                else if (links.Any(l => l.ProviderId == group.ProviderId))
                {
                    _useProvider = false;
                    _shareLink = DefaultLink();
                    SayShare($"That link isn't on {ProviderName(group.ProviderId)} anymore. These times will copy as plain text.");
                }
            }
            else
            {
                // A link already picked swaps to the provider's listed copy of it (it may be a stand-in)
                _shareLink = _shareLink is { } picked
                    ? links.FirstOrDefault(l => l.ProviderId == picked.ProviderId && l.Id == picked.Id) ?? picked
                    : DefaultLink();
            }

            ShareChanged?.Invoke(this, EventArgs.Empty);
            await RefreshOpenTimesAsync();
        }, "scheduling.links.load.failed");
    }

    // The default link of the first provider that has one among the links read, else the first link
    private SchedulingLink? DefaultLink() =>
        _links.FirstOrDefault(l => l.Id == Settings.ProviderSettingsFor(l.ProviderId).DefaultLinkId) ?? (_links.Count > 0 ? _links[0] : null);

    // The provider's name for the messages, even after it was disconnected
    private string ProviderName(SchedulingLink link) => ProviderName(link.ProviderId);

    private string ProviderName(string providerId) =>
        _services.Scheduling.Get(providerId)?.Name ?? SchedulingProviders.Find(providerId)?.Name ?? providerId;

    // The needs-reconnecting text, as Settings › Accounts shows it
    private string NeedsReconnectMessage(string providerId) =>
        $"{ProviderName(providerId)} signed Leaf out. Sign in again to keep sharing booking links.";

    // A provider signed in, out, was removed or needs reconnecting (raised on any thread). Signed out or removed: a share
    // on it turns plain, so Save and Copy & Save write no link from it. Needs reconnecting: the share keeps its switch and
    // link as a stand-in (like a group opened offline), so Save keeps the group's link and booking check (spec 8), and a
    // message says to sign in again. Signed in while the share's link isn't among the links read: they're read again
    // (with the group when its link is unchanged), so fitting resumes. The panel refreshes
    private void OnSchedulingChanged(object? sender, EventArgs e) => _dispatcher.TryEnqueue(() =>
    {
        if (!_sharing)
        {
            return;
        }

        var hub = _services.Scheduling;
        if (_shareLink is { } link && hub.Get(link.ProviderId) is null)
        {
            _shareSession++;
            _links = [.. _links.Where(l => hub.Get(l.ProviderId) is not null)];
            _refitPending = false;
            ClearOpenTimes();
            if (hub.StateOf(link.ProviderId) == ProviderState.NeedsReconnect)
            {
                if (_useProvider)
                {
                    SayShare(NeedsReconnectMessage(link.ProviderId));
                }
            }
            else
            {
                _useProvider = false;
                _shareLink = DefaultLink();
            }
        }
        else if (HasScheduling && (_links.Count == 0 || (_shareLink is { } current && !_links.Contains(current))))
        {
            // Signed in during a new share: the switch turns on, as it starts for a share begun while signed in
            if (_links.Count == 0 && !_useProvider && _openGroupId is null)
            {
                _useProvider = true;
                _refitPending = _slots.Count > 0;
            }

            var group = _savedGroups.FirstOrDefault(g => g.Id == _openGroupId);
            LoadLinks(group is not null && _shareLink is { } l && l.ProviderId == group.ProviderId && l.Id == group.LinkId ? group : null);
        }

        ShareChanged?.Invoke(this, EventArgs.Empty);
    });

    // Paging: the new days' open times, when sharing with the switch on
    private void OnSchedulingPeriodChanged()
    {
        if (_sharing && _useProvider && _shareLink is not null)
        {
            Fire(RefreshOpenTimesAsync, "scheduling.open.refresh.failed");
        }
    }

    // Asks the link's provider for its open times around the days on screen (the people overlay's range), unless
    // they're already known. Not reachable: no shade, picks stay as dragged, and a message says so; a refused key also
    // marks the provider as needing reconnecting. Only the newest answer lands, and a pending refit applies then
    private async Task RefreshOpenTimesAsync()
    {
        if (!_sharing || !_useProvider || _shareLink is not { } link)
        {
            return;
        }

        var (from, to) = OverlayRange();
        if (_openTimes is not null && Covers(_openRange, from, to))
        {
            Refit();
            return;
        }

        if (_services.Scheduling.Get(link.ProviderId) is not IOpenTimesSource source)
        {
            return;
        }

        // Scrolling reports every new first day; one fetch that covers them is enough
        if (Covers(_openAsked, from, to))
        {
            return;
        }

        var generation = ++_openGeneration;
        _openAsked = (from, to);
        IReadOnlyList<BusyRange>? open = null;
        var refused = false;

        // In A Rate-Limit Back-Off It Counts As Not Reachable
        try
        {
            if (!_services.Scheduling.IsWaiting(link.ProviderId))
            {
                open = await Task.Run(() => source.GetOpenTimesAsync(link.Id, from.ToUniversalTime(), to.ToUniversalTime(), _life.Token));
            }
        }
        catch (Exception ex) when (ex is SchedulingUnavailableException or SchedulingAuthException)
        {
            _services.Log.Info("scheduling.open.failed", $"provider={link.ProviderId} error={ex.GetType().Name}");
            if (ex is SchedulingAuthException)
            {
                refused = true;
                _services.Scheduling.MarkNeedsReconnect(link.ProviderId);
            }
            else if (ex is SchedulingUnavailableException { RetryAfter: { } retryAfter })
            {
                _services.Scheduling.WaitFor(link.ProviderId, retryAfter);
            }
        }
        finally
        {
            // Any other failure too: the next paging asks again
            if (generation == _openGeneration)
            {
                _openAsked = null;
            }
        }

        // A newer fetch started, or the switch, the link or the share changed meanwhile
        if (generation != _openGeneration)
        {
            return;
        }

        _openTimes = open;
        _openRange = open is null ? null : (from, to);
        // A refused sign-in says so instead, once the share turns plain (OnSchedulingChanged)
        if (open is null && !refused)
        {
            SayShare($"Couldn't reach {ProviderName(link)}, so times aren't limited to your openings.");
        }

        Refit();
        ShareChanged?.Invoke(this, EventArgs.Empty);
    }

    // A link change, the switch turning on, or picks made before the openings were known: every pick inside the
    // fetched days is fitted at once, with the same messages. Picks on other days are kept as they are (their openings
    // aren't known, so fitting would drop them)
    private void Refit()
    {
        if (!_refitPending || _openTimes is null || _shareLink is null || _openRange is not { } range)
        {
            return;
        }

        _refitPending = false;
        var inside = _slots.Where(s => s.Start >= range.From && s.End <= range.To).ToList();
        if (inside.Count == 0)
        {
            return;
        }

        var fit = SlotFitting.FitAll(inside, _openTimes);
        Say(fit.Summary, _shareLink);
        _slots = [.. BusyMath.Merge([.. _slots.Except(inside), .. fit.Ranges])];
        ShareChanged?.Invoke(this, EventArgs.Empty);
    }

    // With a link on and its openings known, a pick is fitted to them and the messages say what happened; otherwise it's
    // kept as dragged (and, with the switch on, fitted once the link and its openings arrive, so Copy doesn't drop it;
    // a pick made before the links were even read waits for them too)
    private IReadOnlyList<BusyRange> FitPick(BusyRange pick)
    {
        if (!_useProvider || _shareLink is null || _openTimes is null)
        {
            _refitPending |= _useProvider;
            return [pick];
        }

        var fit = SlotFitting.Fit(pick, _openTimes);
        Say(fit.Summary, _shareLink);
        return fit.Ranges;
    }

    // One message per fact, each pushed on top of the stack in turn
    private void Say(FitSummary summary, SchedulingLink link)
    {
        foreach (var message in FitMessages.For(summary, ProviderName(link)))
        {
            SayShare(message);
        }
    }

    // The link saved with the group: the panel's with the switch on, else none (a plain group)
    private LeafCalendar.Core.Data.ShareLink CurrentLink() =>
        _useProvider && _shareLink is { } l
            ? new LeafCalendar.Core.Data.ShareLink(l.ProviderId, l.Id, l.BookingUrl.AbsoluteUri, l.Minutes)
            : LeafCalendar.Core.Data.ShareLink.None;

    // The copied text with a booking link per time, or null to copy plain times (switch off, or the provider can't build
    // links). Uses the open times only when they cover every pick, else splits by the link's length
    private LinkedTimes? BuildLinkedAvailability(TimeZoneInfo zone)
    {
        if (!_useProvider || _shareLink is not { } link || _services.Scheduling.Get(link.ProviderId) is not ITimeLinkBuilder builder)
        {
            return null;
        }

        var ranges = BusyMath.Merge(_slots);
        var open = _openRange is { } r && ranges.All(s => s.Start >= r.From && s.End <= r.To) ? _openTimes : null;
        return LeafCalendar.Core.Scheduling.ShareLinks.Build(builder, link, ranges, open, _shareGuests.ToList(), _shareText, Settings.BookingLinkNote, zone, Settings.Use24HourTime);
    }

    // The first guest as the messages name them: their name, else their address
    private string FirstGuestLabel() =>
        _shareGuests.FirstOrDefault() is { } g ? (g.Name.Length > 0 ? g.Name : g.Email) : "";

    // The zone the share is written in, else the zone on screen
    private TimeZoneInfo ShareZone() => TimeZoneInfo.TryFindSystemTimeZoneById(_shareZoneId, out var zone) ? zone : Zone;

    // =========================================================================
    // APPROVE ON A BOOKING LINK (spec 5.1)
    // =========================================================================

    /// <summary>
    /// True when Approve... books on the provider instead of opening the event editor: a saved group is open with the
    /// switch on, and its link's provider can book.
    /// </summary>
    public bool ApprovesThroughProvider =>
        _openGroupId is not null && ShareLink is { } link && _services.Scheduling.Get(link.ProviderId) is IBookingCreator;

    /// <summary>The name of the provider the share's link is on (such as "Cal.com"), or empty with the switch off.</summary>
    public string ShareProviderName => ShareLink is { } link ? ProviderName(link) : "";

    /// <summary>
    /// The guests a booking on the share's link invites: every guest when the link allows extra guests, else only the
    /// first (the booker), as the provider sends them. The confirmation lists these, and <see cref="BookAsync"/> books them.
    /// </summary>
    public IReadOnlyList<Contact> BookingGuests =>
        ShareLink is { AllowsGuests: false } ? [.. _shareGuests.Take(1)] : [.. _shareGuests];

    /// <summary>Logs a failure a view caught while booking, by its exception type name only.</summary>
    public void LogBookFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _services.Log.Info("scheduling.book.failed", exception.GetType().Name);
    }

    /// <summary>Each time as one line in the share's zone, such as "Tue Oct 13, 10-10:30 AM CT".</summary>
    public IReadOnlyList<string> BookableTimeLabels(IReadOnlyList<BusyRange> times) =>
        AvailabilityText.FormatTimes(times, ShareZone(), Settings.Use24HourTime);

    /// <summary>
    /// The bookable times inside picked time <paramref name="index"/>, from the link's open times asked fresh. Empty when
    /// none can be booked, after a message says why: the provider couldn't be reached (nothing is booked offline), none
    /// of the time is open anymore (the shade refreshes too), or the sign-in was refused (the provider needs reconnecting).
    /// </summary>
    public async Task<IReadOnlyList<BusyRange>> BookableTimesAsync(int index, CancellationToken ct)
    {
        if (index < 0 || index >= _slots.Count || ShareLink is not { } link)
        {
            return [];
        }

        var range = _slots[index];
        var name = ProviderName(link);
        if (!IsListedLink(link, name))
        {
            return [];
        }

        IReadOnlyList<BusyRange>? open = null;

        // In A Rate-Limit Back-Off It Counts As Not Reachable
        try
        {
            if (!_services.Scheduling.IsWaiting(link.ProviderId) && _services.Scheduling.Get(link.ProviderId) is IOpenTimesSource source)
            {
                open = await Task.Run(() => source.GetOpenTimesAsync(link.Id, range.Start.ToUniversalTime(), range.End.ToUniversalTime(), ct), ct);
            }
        }
        catch (SchedulingAuthException ex)
        {
            _services.Log.Info("scheduling.book.times.failed", $"provider={link.ProviderId} error={ex.GetType().Name}");
            _services.Scheduling.MarkNeedsReconnect(link.ProviderId);
            return [];
        }
        catch (SchedulingUnavailableException ex)
        {
            _services.Log.Info("scheduling.book.times.failed", $"provider={link.ProviderId} error={ex.GetType().Name}");
            if (ex.RetryAfter is { } retryAfter)
            {
                _services.Scheduling.WaitFor(link.ProviderId, retryAfter);
            }
        }

        if (open is null)
        {
            SayShare($"Couldn't reach {name}. Nothing was booked.");
            return [];
        }

        var times = SlotFitting.Split(range, open, link.Minutes);
        if (times.Count == 0)
        {
            SayShare("That time is not open per your availability. Pick a different time");
            RefreshOpenTimesNow();
        }

        return times;
    }

    /// <summary>
    /// Books <paramref name="time"/> on the open group's link for its guests (the provider sends its confirmation emails).
    /// Booked: the group is deleted, sharing stops and a message says so (the event reaches Google through the provider's
    /// own calendar connection, so Leaf creates none). Taken: a message asks for another time and the open times refresh.
    /// Not reachable: a message says nothing was booked; it's never tried again on its own, since a server error may
    /// still have booked it (the booking check finds that). Sign-in refused: the provider needs reconnecting.
    /// </summary>
    public async Task<BookResult> BookAsync(BusyRange time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(time);
        if (ShareLink is not { } link)
        {
            return BookResult.Unavailable;
        }

        var name = ProviderName(link);
        if (!IsListedLink(link, name))
        {
            return BookResult.Unavailable;
        }

        if (_openGroupId is not { } id || _shareGuests.Count == 0 || _services.Scheduling.Get(link.ProviderId) is not IBookingCreator creator)
        {
            SayShare($"Couldn't reach {name}. Nothing was booked.");
            return BookResult.Unavailable;
        }

        var guests = BookingGuests;
        var zone = ShareZone();
        try
        {
            await Task.Run(() => creator.BookAsync(link, time.Start.ToUniversalTime(), guests, zone, ct), ct);
        }
        catch (SchedulingSlotTakenException ex)
        {
            _services.Log.Info("scheduling.book.taken", $"provider={link.ProviderId} error={ex.GetType().Name}");
            SayShare($"That time was just taken on {name}. Pick another.");
            RefreshOpenTimesNow();
            return BookResult.Taken;
        }
        catch (SchedulingAuthException ex)
        {
            _services.Log.Info("scheduling.book.auth", $"provider={link.ProviderId} error={ex.GetType().Name}");
            _services.Scheduling.MarkNeedsReconnect(link.ProviderId);
            return BookResult.NeedsReconnect;
        }
        catch (SchedulingUnavailableException ex)
        {
            _services.Log.Info("scheduling.book.unavailable", $"provider={link.ProviderId} error={ex.InnerException?.GetType().Name ?? ex.GetType().Name}");
            if (ex.RetryAfter is { } retryAfter)
            {
                _services.Scheduling.WaitFor(link.ProviderId, retryAfter);
            }

            SayShare($"Couldn't reach {name}. Nothing was booked.");
            return BookResult.Unavailable;
        }

        // Deleted at once, so the booking check finds no group for this booking and shows no second notification
        _services.Log.Info("scheduling.book.booked", $"provider={link.ProviderId} guests={guests.Count}");
        WriteGroups("share.group.delete.failed", conn => ShareGroupStore.Delete(conn, id));
        if (_openGroupId == id)
        {
            StopSharing();
        }

        ShowMessage($"Booked on {name}.");
        return BookResult.Booked;
    }

    // A link the provider listed. The group's saved stand-in (links not read yet, or reading them failed) doesn't know
    // whether the link takes extra guests, so nothing is booked on it: it counts as not reachable and the links are read again
    private bool IsListedLink(SchedulingLink link, string name)
    {
        if (_links.Contains(link))
        {
            return true;
        }

        SayShare($"Couldn't reach {name}. Nothing was booked.");
        LoadLinks(_savedGroups.FirstOrDefault(g => g.Id == _openGroupId));
        return false;
    }

    // The open times asked again even when they cover the days on screen (a time was just taken)
    private void RefreshOpenTimesNow()
    {
        ClearOpenTimes();
        Fire(RefreshOpenTimesAsync, "scheduling.open.refresh.failed");
    }
}

/// <summary>How booking a time through Approve... ended.</summary>
public enum BookResult
{
    /// <summary>The provider booked it; the group is deleted and sharing stopped.</summary>
    Booked,

    /// <summary>The time isn't open anymore; the group stays.</summary>
    Taken,

    /// <summary>The provider couldn't be reached or failed; nothing was booked as far as Leaf knows, and the group stays.</summary>
    Unavailable,

    /// <summary>The provider refused the sign-in and needs reconnecting; the group stays.</summary>
    NeedsReconnect,
}
