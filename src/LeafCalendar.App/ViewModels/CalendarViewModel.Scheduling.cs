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

    // Bumped when sharing starts, opens a group or stops, so links read for an earlier share are dropped
    private int _shareSession;

    // A link change or the switch turning on is waiting for open times to fit every pick to them
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

    /// <summary>The share panel switch's label: "Use {provider}" with one provider connected, "Use a booking link" with more.</summary>
    public string ShareSwitchLabel => _services.Scheduling.Connected is [var only] ? $"Use {only.Name}" : "Use a booking link";

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
                    SayShare("That link isn't on Cal.com anymore. These times will copy as plain text.");
                }
            }
            else
            {
                _shareLink ??= DefaultLink();
            }

            ShareChanged?.Invoke(this, EventArgs.Empty);
            await RefreshOpenTimesAsync();
        }, "scheduling.links.load.failed");
    }

    // The default link of the first provider that has one among the links read, else the first link
    private SchedulingLink? DefaultLink() =>
        _links.FirstOrDefault(l => l.Id == Settings.ProviderSettingsFor(l.ProviderId).DefaultLinkId) ?? (_links.Count > 0 ? _links[0] : null);

    // The provider's name for the messages, even after it was disconnected
    private string ProviderName(SchedulingLink link) =>
        _services.Scheduling.Get(link.ProviderId)?.Name ?? SchedulingProviders.Find(link.ProviderId)?.Name ?? link.ProviderId;

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
        if (open is null)
        {
            SayShare("Couldn't reach Cal.com, so times aren't limited to your openings.");
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
    // kept as dragged (and, with the link on, fitted once the openings arrive, so Copy doesn't drop it)
    private IReadOnlyList<BusyRange> FitPick(BusyRange pick)
    {
        if (!_useProvider || _shareLink is null || _openTimes is null)
        {
            _refitPending |= _useProvider && _shareLink is not null;
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
        return LeafCalendar.Core.Scheduling.ShareLinks.Build(builder, link, ranges, open, _shareGuests.ToList(), _shareText, zone, Settings.Use24HourTime);
    }

    // The first guest as the messages name them: their name, else their address
    private string FirstGuestLabel() =>
        _shareGuests.FirstOrDefault() is { } g ? (g.Name.Length > 0 ? g.Name : g.Email) : "";
}
