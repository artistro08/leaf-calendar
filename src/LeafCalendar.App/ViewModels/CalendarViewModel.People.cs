// Track B owns this file (Milestone 5 Tasks 7-8): the people overlay, Meet with, and share availability.
using System.Net.Mail;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;
using LeafCalendar.Core.Settings;
using LeafCalendar.Core.Tray;
using LeafCalendar.Core.Views;

namespace LeafCalendar.App.ViewModels;

/// <summary>A person on the overlay: their address, the name to show, their color (0-3), and whether Google gave busy times.</summary>
public sealed record OverlayPerson(string Email, string Name, int ColorIndex, PersonBusyState State);

/// <summary>One of an overlaid person's busy stretches, with its title when their calendar is shared with details.</summary>
public sealed record OverlayBlock(string Email, int ColorIndex, DateTimeOffset Start, DateTimeOffset End, string? Title);

/// <summary>
/// A time the grid draws: a saved group's (<see cref="Title"/> set) or a pick while sharing (<see cref="Title"/> null,
/// <see cref="GroupId"/> the open group's, if any). <see cref="Index"/> is its place in its group or in the picks.
/// </summary>
public sealed record GridSlot(long? GroupId, int Index, BusyRange Range, string? Title, bool Resizable);

public sealed partial class CalendarViewModel
{
    // =========================================================================
    // PEOPLE OVERLAY AND MEET WITH
    // =========================================================================

    // Person colors cycle through this many (LeafBrushes.Person)
    private const int PersonColors = 4;

    // ponytail: the overlay is fields plus one event; nothing here is stored (contacts privacy)
    private List<OverlayPerson> _people = [];
    private Dictionary<string, List<BusyBlock>> _blocks = new(StringComparer.OrdinalIgnoreCase);
    private (DateTimeOffset From, DateTimeOffset To)? _loaded;
    private bool _meetWith;

    // Bumped by every change to who is overlaid, so a lookup that finishes late is dropped
    private int _overlayVersion;

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
        // Colors Cycle; A Blank Name Shows The Address; Nobody Counts As Free Until Google Says So
        _people = [.. people
            .Select(p => (Email: p.Email.Trim(), Name: DisplayText.Clean(p.Name, 100)))
            .Where(p => IsAddress(p.Email))
            .DistinctBy(p => p.Email, StringComparer.OrdinalIgnoreCase)
            .Take(FreeBusyLookup.MaxPeople)
            .Select((p, i) => new OverlayPerson(p.Email, p.Name.Length > 0 ? p.Name : p.Email, i % PersonColors, PersonBusyState.Unknown))];

        _blocks.Clear();
        _meetWith = meetWith && _people.Count > 0;
        _loaded = null;
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
        _people = [];
        _blocks = new(StringComparer.OrdinalIgnoreCase);
        _loaded = null;
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
    /// says so and keeps the blocks already shown, but anyone with nothing loaded for these days shows "No free/busy
    /// info" (never free). A lookup that finishes after a newer one started, or after paging moved past what it
    /// covers, is dropped.
    /// </summary>
    public async Task RefreshOverlayAsync()
    {
        if (_people.Count == 0 || PeopleAccountId() is not { } account || _services.Google is not { } google)
        {
            return;
        }

        var (from, to) = OverlayRange();
        if (Covers(_loaded, from, to))
        {
            return;
        }

        // Every Refresh Bumps The Version, So Only The Newest Lookup Lands
        var version = ++_overlayVersion;
        IReadOnlyList<PersonBusy> found;
        try
        {
            found = await new FreeBusyLookup(google.Calendar, _services.Log).LookupAsync(account, [.. _people.Select(p => p.Email)], from, to, _life.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_life.IsCancellationRequested)
        {
            // HttpClient's Own Timeout Is A Cancellation Too, But Only Shutting Down Is Silent
            _services.Log.Info("freebusy.lookup.failed", $"account={account} status={Status(ex)}");

            // Nothing Loaded For These Days: No Free/Busy Info, Never Free
            var (failFrom, failTo) = OverlayRange();
            if (version == _overlayVersion && !Covers(_loaded, failFrom, failTo))
            {
                _people = [.. _people.Select(p => p with { State = PersonBusyState.Unknown })];
                OverlayChanged?.Invoke(this, EventArgs.Empty);
            }

            ShowMessage("Couldn't get busy times. Check your connection.");
            return;
        }

        // Someone Changed Who Is Overlaid, A Newer Lookup Started, Or Paging Moved Past This One
        var (nowFrom, nowTo) = OverlayRange();
        if (version != _overlayVersion || !Covers((from, to), nowFrom, nowTo))
        {
            return;
        }

        _blocks = found.ToDictionary(p => p.Email, p => p.Blocks.ToList(), StringComparer.OrdinalIgnoreCase);
        _people = [.. _people.Select(p => p with { State = found.FirstOrDefault(f => string.Equals(f.Email, p.Email, StringComparison.OrdinalIgnoreCase))?.State ?? PersonBusyState.Unknown })];
        _loaded = (from, to);
        OverlayChanged?.Invoke(this, EventArgs.Empty);
    }

    // The visible period, a week each side
    private (DateTimeOffset From, DateTimeOffset To) OverlayRange() =>
        (OccurrenceQuery.LocalMidnight(PeriodStart.AddDays(-7), Zone), OccurrenceQuery.LocalMidnight(PeriodStart.AddDays(VisibleColumns + 7), Zone));

    private static bool Covers((DateTimeOffset From, DateTimeOffset To)? range, DateTimeOffset from, DateTimeOffset to) =>
        range is { } r && r.From <= from && r.To >= to;

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

    /// <summary>
    /// Google contacts matching <paramref name="text"/> for the people picker and the share panel's guest box, from
    /// every signed-in account at once (your main account's first), each address once (none without an account). The
    /// access state is the main account's.
    /// </summary>
    public async Task<ContactResults> SearchPeopleAsync(string text, CancellationToken ct)
    {
        if (_services.Google is not { } google)
        {
            return new([], ContactAccess.Allowed);
        }

        var answers = await Task.WhenAll(PeopleAccounts().Select(account => google.Contacts.SearchAsync(account, text, ct)));
        return new(
            [.. answers.SelectMany(a => a.Contacts).DistinctBy(c => c.Email, StringComparer.OrdinalIgnoreCase).Take(ContactSearch.MaxResults)],
            answers.FirstOrDefault()?.Access ?? ContactAccess.Allowed);
    }

    // People from your own events, per account: read once off the UI thread, read again after the events change or
    // after a read that failed
    private readonly Dictionary<string, Task<IReadOnlyList<Contact>>> _localPeople = new(StringComparer.Ordinal);

    /// <summary>
    /// People from your own calendar events in every signed-in account (the guests you meet with, as the editor's guest
    /// box suggests them; your main account's first) who match <paramref name="text"/>, for the people picker and the
    /// share panel's guest box, each address once. Read from the local database only; none without an account.
    /// </summary>
    public async Task<IReadOnlyList<Contact>> LocalPeopleAsync(string text)
    {
        var now = Now;
        var loads = PeopleAccounts().Select(account =>
        {
            // A read that failed isn't kept, so the next call tries again
            if (!_localPeople.TryGetValue(account, out var load) || load.IsFaulted)
            {
                _localPeople[account] = load = Task.Run(() => ReadLocal(conn => FrequentPeople.Load(conn, account, now)));
            }

            return load;
        }).ToList();

        var people = await Task.WhenAll(loads);
        return [.. people.SelectMany(p => FrequentPeople.Match(p, text)).DistinctBy(c => c.Email, StringComparer.OrdinalIgnoreCase).Take(ContactSearch.MaxResults)];
    }

    // Every signed-in account, the one that asks Google about people first
    private List<string> PeopleAccounts() => [.. AccountEmails.Keys.OrderBy(id => id == PeopleAccountId() ? 0 : 1)];

    // =========================================================================
    // SHARE AVAILABILITY
    // =========================================================================

    private bool _sharing;
    private List<BusyRange> _slots = [];
    private string _shareZoneId = "";

    /// <summary>True while you pick times to share: a drag on the time grid adds a slot instead of a new event.</summary>
    public bool IsSharing => _sharing;

    /// <summary>The picked times, merged and in order.</summary>
    public IReadOnlyList<BusyRange> ShareSlots => _slots;

    /// <summary>The IANA ID of the zone the copied text is written in (the zone on screen when sharing starts).</summary>
    public string ShareZoneId
    {
        get => _shareZoneId;
        set => _shareZoneId = value;
    }

    /// <summary>Sharing started or stopped, the slots changed, or the saved groups were read again (redraw the share panel).</summary>
    public event EventHandler? ShareChanged;

    /// <summary>What a group with no title is called.</summary>
    public const string GenericShareTitle = "Held times";

    private long? _openGroupId;
    private string _shareTitle = "";
    private string _shareText = "";
    private List<Contact> _shareGuests = [];
    private List<ShareGroup> _savedGroups = [];
    private int _groupsGeneration;

    // The saved group whose times can be resized on the grid (the one being approved), or null. ApproveSlot sets it;
    // the approve editor saving deletes the group, and the editor closing any other way clears it
    private long? _approvingGroupId;

    /// <summary>The saved groups with times still to come, oldest first.</summary>
    public IReadOnlyList<ShareGroup> SavedGroups => _savedGroups;

    /// <summary>The saved group the share panel shows, or null while picking new times.</summary>
    public long? OpenGroupId => _openGroupId;

    /// <summary>This share's title (the panel's Title box). An open group keeps its saved title until Save or Copy.</summary>
    public string ShareTitle
    {
        get => _shareTitle;
        set => _shareTitle = value;
    }

    /// <summary>
    /// This share's message (<c>{times}</c> marks where the proposed times go). Each new share starts from the default in
    /// Settings › Calendars; a change here is for this share (and its saved group, once saved) only.
    /// </summary>
    public string ShareText
    {
        get => _shareText;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _shareText = value;
        }
    }

    /// <summary>
    /// The guests added to this share, in order (a picked contact's name, empty when only the address is known). An open
    /// group keeps its saved guests until Save or Copy &amp; Save; new picks start with none.
    /// </summary>
    public IReadOnlyList<Contact> ShareGuests => _shareGuests;

    /// <summary>
    /// Adds a guest to this share (<paramref name="name"/> is a picked contact's, shown on its row; without one, the name
    /// Leaf knows for that address fills in once found). False when
    /// <paramref name="email"/> isn't one valid address; an address already added (any case) isn't added again.
    /// </summary>
    public bool AddShareGuest(string email, string name = "")
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(name);
        email = email.Trim();
        if (!_sharing || !IsAddress(email))
        {
            return false;
        }

        if (!_shareGuests.Any(g => string.Equals(g.Email, email, StringComparison.OrdinalIgnoreCase)))
        {
            _shareGuests.Add(new Contact(name.Trim(), email));
            ShareChanged?.Invoke(this, EventArgs.Empty);
            if (name.Trim().Length == 0)
            {
                FillShareGuestName(email, askGoogle: true);
            }
        }

        return true;
    }

    /// <summary>Removes the guest at <paramref name="index"/> from this share (saved with Save or Copy &amp; Save).</summary>
    public void RemoveShareGuest(int index)
    {
        if (index < 0 || index >= _shareGuests.Count)
        {
            return;
        }

        _shareGuests.RemoveAt(index);
        ShareChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A group's title, or the generic one when it has none.</summary>
    public static string GroupTitle(ShareGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return group.Title.Length > 0 ? group.Title : GenericShareTitle;
    }

    /// <summary>
    /// Starts picking times to share (nothing happens while already sharing). From Month view it switches to the last
    /// time-grid view you used (Day, Week, or a number of days), since times are picked on the grid.
    /// </summary>
    public void StartSharing()
    {
        if (_sharing)
        {
            return;
        }

        if (Mode == CalendarViewMode.Month)
        {
            SetMode(Settings.LastGridView ?? CalendarViewMode.Week);
        }

        _slots = [];
        _shareZoneId = TimeZoneCatalog.IanaId(Zone);
        _openGroupId = null;
        _shareTitle = "";
        _shareText = Settings.ShareMessage;
        _shareGuests = [];
        _sharing = true;
        ShareChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Opens a saved group in the share panel: its times (editable; changes wait for Save or Copy &amp; Save), title,
    /// message, guests and zone. Only opens when not already sharing (a click on a saved time is refused while sharing).
    /// </summary>
    public void OpenGroup(long id)
    {
        if (_savedGroups.FirstOrDefault(g => g.Id == id) is not { } group)
        {
            return;
        }

        if (Mode == CalendarViewMode.Month)
        {
            SetMode(Settings.LastGridView ?? CalendarViewMode.Week);
        }

        _openGroupId = group.Id;
        _slots = [.. group.Slots];
        _shareTitle = group.Title;
        _shareText = group.Message;
        // Only real addresses (0.1.302 could save unchecked text, and migration 11 copied it)
        _shareGuests = [.. group.Guests.Where(g => IsAddress(g.Email))];
        _shareZoneId = group.ZoneId;
        _sharing = true;
        ShareChanged?.Invoke(this, EventArgs.Empty);
        foreach (var guest in _shareGuests.Where(g => g.Name.Length == 0).ToList())
        {
            FillShareGuestName(guest.Email, askGoogle: false);
        }
    }

    // A guest added by address alone gets the name Leaf knows for that exact address: from the people in your own
    // events, else (only for a guest just typed or added, never on reopening a saved group) Google's contacts.
    // Applied only while that guest is still listed without a name, so a late answer is dropped
    private async void FillShareGuestName(string email, bool askGoogle)
    {
        try
        {
            static bool Same(Contact c, string email) => string.Equals(c.Email, email, StringComparison.OrdinalIgnoreCase) && c.Name.Length > 0;

            var name = (await LocalPeopleAsync(email)).FirstOrDefault(c => Same(c, email))?.Name
                ?? (askGoogle ? (await SearchPeopleAsync(email, _life.Token)).Contacts.FirstOrDefault(c => Same(c, email))?.Name : null);
            var index = _shareGuests.FindIndex(g => string.Equals(g.Email, email, StringComparison.OrdinalIgnoreCase) && g.Name.Length == 0);
            if (name is null || index < 0)
            {
                return;
            }

            _shareGuests[index] = _shareGuests[index] with { Name = name };
            ShareChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _services.Log.Info("share.guest.name.failed", ex.GetType().Name);
        }
    }

    /// <summary>Deletes the open saved group at once (the title bar's Delete saved times) and stops sharing.</summary>
    public void DeleteOpenGroup()
    {
        if (_openGroupId is not { } id)
        {
            return;
        }

        WriteGroups("share.group.delete.failed", conn => ShareGroupStore.Delete(conn, id));
        StopSharing();
    }

    /// <summary>
    /// Save: keeps the picked times as a group (new picks become a new group; an open group gets its times, title,
    /// message, guests and zone updated), then stops sharing. Needs a time. When the database write fails it says so and
    /// sharing goes on, so nothing typed is lost.
    /// </summary>
    public void SaveShare()
    {
        if (!_sharing || _slots.Count == 0)
        {
            return;
        }

        var (group, title, text, zone, slots, now, guests) = (_openGroupId, _shareTitle, _shareText, _shareZoneId, _slots.ToList(), Now, _shareGuests.ToList());
        var saved = WriteGroups("share.group.save.failed", conn =>
        {
            if (group is { } id)
            {
                ShareGroupStore.Update(conn, id, title, text, zone, slots, guests);
            }
            else
            {
                ShareGroupStore.Insert(conn, title, text, zone, slots, now, guests);
            }
        });

        if (saved)
        {
            StopSharing();
        }
    }

    /// <summary>
    /// Books one time of the open group: sharing stops (the editor needs the right panel), and the event editor opens on
    /// that time with the group's title and every guest added. Needs a guest. The group is deleted only once the event
    /// saves; until then its times stay on the grid, resizable.
    /// </summary>
    public void ApproveSlot(int index)
    {
        if (_openGroupId is not { } id || index < 0 || index >= _slots.Count || _shareGuests.Count == 0)
        {
            return;
        }

        var slot = _slots[index];
        var guests = _shareGuests.ToList();
        var cleaned = DisplayText.Clean(_shareTitle, ShareGroupStore.MaxTitleLength);
        var title = cleaned.Length > 0 ? cleaned : GenericShareTitle;
        StopSharing();
        var before = Editing;
        BeginCreate(slot.Start, slot.End, isAllDay: false);

        // The editor didn't open on this time (another edit it couldn't leave stayed): nothing is approved
        if (ReferenceEquals(Editing, before) || Editing is not { IsNew: true } editor)
        {
            return;
        }

        // Set after the editor opens, so opening it doesn't clear it
        _approvingGroupId = id;
        editor.Title = title;
        foreach (var guest in guests)
        {
            editor.GuestInput = guest.Email;
            editor.AddGuest(guest.Name);
        }

        ShareChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops sharing and forgets the picked times (a saved group stays saved).</summary>
    public void StopSharing()
    {
        _sharing = false;
        _openGroupId = null;
        _slots = [];
        _shareGuests = [];
        ShareChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds a picked time (merged with any it overlaps or touches).</summary>
    public void AddShareSlot(DateTimeOffset start, DateTimeOffset end)
    {
        if (!_sharing || end <= start)
        {
            return;
        }

        _slots = [.. BusyMath.Merge([.. _slots, new BusyRange(start, end)])];
        ShareChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Removes the picked time at <paramref name="index"/>. An open group keeps its saved times until Save; with none
    /// left, Save is off and Delete is how the group goes.
    /// </summary>
    public void RemoveShareSlot(int index)
    {
        if (index < 0 || index >= _slots.Count)
        {
            return;
        }

        _slots.RemoveAt(index);
        ShareChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Changes the picked time at <paramref name="index"/> (the right panel's times; merged again with any it now overlaps or touches).</summary>
    public void UpdateShareSlot(int index, DateTimeOffset start, DateTimeOffset end)
    {
        if (index < 0 || index >= _slots.Count || end <= start)
        {
            return;
        }

        _slots[index] = new BusyRange(start, end);
        _slots = [.. BusyMath.Merge(_slots)];
        ShareChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The times the grid draws: every saved group's (oldest first; not the open group's, which are the picks), then the
    /// picks while sharing. The last drawn is on top, so the picks win a click. Only the picks, and a group being
    /// approved, can be resized.
    /// </summary>
    public IReadOnlyList<GridSlot> GridSlots()
    {
        var list = new List<GridSlot>();
        foreach (var g in _savedGroups.Where(g => !_sharing || g.Id != _openGroupId))
        {
            var title = GroupTitle(g);
            list.AddRange(g.Slots.Select((s, i) => new GridSlot(g.Id, i, s, title, Resizable: g.Id == _approvingGroupId)));
        }

        if (_sharing)
        {
            list.AddRange(_slots.Select((s, i) => new GridSlot(_openGroupId, i, s, null, Resizable: true)));
        }

        return list;
    }

    /// <summary>
    /// A drawn time resized on the grid: a pick changes (an open group's waits for Save), or a group being approved saves.
    /// Nothing happens when the times were read again during the drag and that place no longer holds the dragged time.
    /// </summary>
    public void ResizeGridSlot(GridSlot slot, DateTimeOffset start, DateTimeOffset end)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (slot.Title is null)
        {
            if (slot.Index < _slots.Count && _slots[slot.Index] == slot.Range)
            {
                UpdateShareSlot(slot.Index, start, end);
            }

            return;
        }

        if (slot.GroupId is { } id && slot.Resizable
            && _savedGroups.FirstOrDefault(g => g.Id == id) is { } group && slot.Index < group.Slots.Count && group.Slots[slot.Index] == slot.Range)
        {
            UpdateSavedSlot(id, slot.Index, start, end);
        }
    }

    /// <summary>Changes one time of a saved group (the one being approved) and saves it, merged again.</summary>
    public void UpdateSavedSlot(long groupId, int index, DateTimeOffset start, DateTimeOffset end)
    {
        if (_savedGroups.FirstOrDefault(g => g.Id == groupId) is not { } group || index < 0 || index >= group.Slots.Count || end <= start)
        {
            return;
        }

        var slots = group.Slots.ToList();
        slots[index] = new BusyRange(start, end);
        var merged = BusyMath.Merge(slots);
        WriteGroups("share.group.save.failed", conn => ShareGroupStore.Update(conn, group.Id, group.Title, group.Message, group.ZoneId, merged, group.Guests));
    }

    // A write to the saved groups, then a fresh read of them; a failure is logged (never its content), says so, and
    // returns false
    private bool WriteGroups(string failure, Action<Microsoft.Data.Sqlite.SqliteConnection> write)
    {
        var written = true;
        try
        {
            using var conn = _services.Database.Open();
            write(conn);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            _services.Log.Error(failure, ex);
            ShowMessage("Couldn't save that. Try again.");
            written = false;
        }

        ReloadGroups();
        return written;
    }

    /// <summary>Reads the saved groups again off the UI thread, dropping ended times first; only the newest read lands.</summary>
    public void ReloadGroups()
    {
        var generation = ++_groupsGeneration;
        var now = Now;

        // Fire's continuation resumes on the UI thread (the dispatcher's context it was started on)
        Fire(async () =>
        {
            var groups = await Task.Run(() =>
            {
                using var conn = _services.Database.Open();
                ShareGroupStore.Prune(conn, now);
                return ShareGroupStore.GetAll(conn, now);
            });

            if (generation != _groupsGeneration)
            {
                return;
            }

            _savedGroups = [.. groups];

            // The Open Group Is Gone (its times all passed): the panel goes on as new picks, so Copy saves a new group
            if (_openGroupId is { } open && !_savedGroups.Any(g => g.Id == open))
            {
                _openGroupId = null;
            }

            // The Group Being Approved Is Gone Too: its times can't be resized any more
            if (_approvingGroupId is { } approving && !_savedGroups.Any(g => g.Id == approving))
            {
                _approvingGroupId = null;
            }

            ShareChanged?.Invoke(this, EventArgs.Empty);
        }, "share.groups.load.failed");
    }

    /// <summary>
    /// The picked times exactly as dragged (merged, in order), as text in the share's zone, wrapped in this share's
    /// message. Nothing is asked of Google, so it works offline. Empty with no times.
    /// </summary>
    public string BuildAvailability()
    {
        if (_slots.Count == 0)
        {
            return "";
        }

        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(_shareZoneId, out var shareZone) ? shareZone : Zone;
        return AvailabilityText.Compose(_shareText, AvailabilityText.Format(BusyMath.Merge(_slots), zone, Settings.Use24HourTime));
    }

    /// <summary>
    /// Copy &amp; Save: copies the picked times as text, in this share's message (never logged), saves them as a group
    /// (or updates the open one) with the title and guests so they stay on the calendar, then stops sharing and says so in
    /// the notice. When another app holds the clipboard it says so and sharing goes on.
    /// </summary>
    public void CopyAndSaveShare()
    {
        if (!_sharing || _slots.Count == 0)
        {
            return;
        }

        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(BuildAvailability());
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            // Another app holds the clipboard (a remote session, a clipboard manager): say so, and sharing goes on
            Fail("share.copy.failed", ex);
            return;
        }

        // Saved As A Group (or the open group updated) with the times that went into the text
        var saved = true;
        try
        {
            using var conn = _services.Database.Open();
            if (_openGroupId is { } id)
            {
                ShareGroupStore.Update(conn, id, _shareTitle, _shareText, _shareZoneId, _slots, _shareGuests.ToList());
            }
            else
            {
                ShareGroupStore.Insert(conn, _shareTitle, _shareText, _shareZoneId, _slots, Now, _shareGuests.ToList());
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            _services.Log.Error("share.group.save.failed", ex);
            saved = false;
        }

        _services.Log.Info("share.copy", $"slots={_slots.Count} guests={_shareGuests.Count}");
        StopSharing();
        ReloadGroups();
        ShowMessage(saved ? "Availability copied" : "Copied, but couldn't save these times.");
    }

    /// <summary>True when <paramref name="text"/> is exactly one valid email address.</summary>
    public static bool IsAddress(string text) => MailAddress.TryCreate(text, out var address) && address.Address == text;

    // A status for the log: Google's HTTP status, or the kind of failure (never a message, which could carry an address)
    private static string Status(Exception ex) => ex is GoogleApiException google ? ((int)google.Status).ToString(System.Globalization.CultureInfo.InvariantCulture) : ex.GetType().Name;
}
