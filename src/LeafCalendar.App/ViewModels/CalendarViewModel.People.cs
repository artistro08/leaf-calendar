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

    /// <summary>Contacts matching <paramref name="text"/> for the people picker (none without an account).</summary>
    public Task<ContactResults> SearchPeopleAsync(string text, CancellationToken ct) =>
        _services.Google is { } google && PeopleAccountId() is { } account
            ? google.Contacts.SearchAsync(account, text, ct)
            : Task.FromResult(new ContactResults([], ContactAccess.Allowed));

    // =========================================================================
    // SHARE AVAILABILITY
    // =========================================================================

    private bool _sharing;
    private List<BusyRange> _slots = [];
    private string _shareZoneId = "";
    private HashSet<CalendarRef> _shareCalendars = [];

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
    private List<ShareGroup> _savedGroups = [];
    private int _groupsGeneration;

    // The saved group whose times can be resized on the grid (the one being approved), or null. The approve editor
    // (not built yet) sets it; until then only the picks can be resized
    private long? _approvingGroupId;

    /// <summary>The saved groups with times still to come, oldest first.</summary>
    public IReadOnlyList<ShareGroup> SavedGroups => _savedGroups;

    /// <summary>The saved group the share panel shows, or null while picking new times.</summary>
    public long? OpenGroupId => _openGroupId;

    /// <summary>This share's title (the panel's Title box). With a group open, a change saves it.</summary>
    public string ShareTitle
    {
        get => _shareTitle;
        set
        {
            if (value == _shareTitle)
            {
                return;
            }

            _shareTitle = value;
            SaveOpenGroup();
        }
    }

    /// <summary>
    /// This share's message (<c>{times}</c> marks where the free times go). Each new share starts from the default in
    /// Settings › Calendars; a change here is for this share (and its saved group) only.
    /// </summary>
    public string ShareText
    {
        get => _shareText;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value == _shareText)
            {
                return;
            }

            _shareText = value;
            SaveOpenGroup();
        }
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
        _shareCalendars = [.. ShareableCalendars().Select(c => new CalendarRef(c.AccountId, c.Id))];
        _openGroupId = null;
        _shareTitle = "";
        _shareText = Settings.ShareMessage;
        _sharing = true;
        ShareChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Opens a saved group in the share panel: its times (editable, each change saved), title, message and zone, checked
    /// against the calendars that can be shared now. New picks not yet copied are dropped first, like Cancel.
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
        _shareZoneId = group.ZoneId;
        _shareCalendars = [.. ShareableCalendars().Select(c => new CalendarRef(c.AccountId, c.Id))];
        _sharing = true;
        ShareChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Deletes the open saved group and stops sharing.</summary>
    public void DeleteOpenGroup()
    {
        if (_openGroupId is not { } id)
        {
            return;
        }

        WriteGroups("share.group.delete.failed", conn => ShareGroupStore.Delete(conn, id));
        StopSharing();
    }

    /// <summary>Stops sharing and forgets the picked times (a saved group stays saved).</summary>
    public void StopSharing()
    {
        _sharing = false;
        _openGroupId = null;
        _slots = [];
        _shareCalendars = [];
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
        SaveOpenGroup();
        ShareChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes the picked time at <paramref name="index"/> (an open group's last time deletes the group and stops sharing).</summary>
    public void RemoveShareSlot(int index)
    {
        if (index < 0 || index >= _slots.Count)
        {
            return;
        }

        _slots.RemoveAt(index);
        SaveOpenGroup();
        if (_openGroupId is not null && _slots.Count == 0)
        {
            StopSharing();
            return;
        }

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
        SaveOpenGroup();
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

    /// <summary>A drawn time resized on the grid: a pick changes (and its open group saves), or a group being approved saves.</summary>
    public void ResizeGridSlot(GridSlot slot, DateTimeOffset start, DateTimeOffset end)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (slot.Title is null)
        {
            UpdateShareSlot(slot.Index, start, end);
            return;
        }

        if (slot.GroupId is { } id && slot.Resizable)
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
        WriteGroups("share.group.save.failed", conn => ShareGroupStore.Update(conn, group.Id, group.Title, group.Message, group.ZoneId, merged));
    }

    // An open saved group follows every change (no times left deletes it)
    private void SaveOpenGroup()
    {
        if (_openGroupId is not { } id)
        {
            return;
        }

        var (title, text, zone, slots) = (_shareTitle, _shareText, _shareZoneId, _slots.ToList());
        WriteGroups("share.group.save.failed", conn => ShareGroupStore.Update(conn, id, title, text, zone, slots));
    }

    // A write to the saved groups, then a fresh read of them; a failure is logged (never its content) and says so
    private void WriteGroups(string failure, Action<Microsoft.Data.Sqlite.SqliteConnection> write)
    {
        try
        {
            using var conn = _services.Database.Open();
            write(conn);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            _services.Log.Error(failure, ex);
            ShowMessage("Couldn't save that. Try again.");
        }

        ReloadGroups();
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

    /// <summary>The calendars whose busy times can block shared times: the visible ones you own or can write to.</summary>
    public IReadOnlyList<CalendarInfo> ShareableCalendars() => [.. Calendars.Where(c => c.IsVisible && c.AccessRole is "owner" or "writer")];

    /// <summary>
    /// The picked times minus everything busy on the visible calendars (Google free/busy): as text in the chosen zone,
    /// and the free ranges themselves. <c>("", [])</c> when none is free, null when Google couldn't be asked or had no
    /// answer for a calendar.
    /// </summary>
    public async Task<(string Text, IReadOnlyList<BusyRange> Free)?> BuildAvailabilityAsync(CancellationToken ct)
    {
        if (_slots.Count == 0)
        {
            return ("", []);
        }

        if (_services.Google is not { } google)
        {
            return null;
        }

        var from = _slots.Min(s => s.Start);
        var to = _slots.Max(s => s.End);

        try
        {
            // One Query Per Account (At Most 50 Calendars Each), Accounts In Parallel
            var queries = _shareCalendars
                .GroupBy(c => c.AccountId)
                .SelectMany(g => g.Select(c => c.CalendarId).Chunk(GoogleCalendarClient.MaxFreeBusyIds).Select(ids => google.Calendar.QueryFreeBusyAsync(g.Key, ids, from, to, ct)))
                .ToList();
            var answers = await Task.WhenAll(queries);

            if (answers.SelectMany(a => a.Values).Any(r => r.Error is not null))
            {
                return null;
            }

            var busy = answers.SelectMany(a => a.Values).SelectMany(r => r.Busy);
            var free = BusyMath.Subtract(_slots, busy);
            return (AvailabilityText.Format(free, TimeZoneInfo.TryFindSystemTimeZoneById(_shareZoneId, out var shareZone) ? shareZone : Zone, Settings.Use24HourTime), free);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _services.Log.Info("share.build.failed", $"status={Status(ex)}");
            return null;
        }
    }

    /// <summary>
    /// Copies the free picked times as text, in this share's message (never logged), saves them as a group (or updates the
    /// open one) so they stay on the calendar, then stops sharing and says so in the notice; or says why it couldn't
    /// (sharing goes on).
    /// </summary>
    public async Task CopyAvailabilityAsync()
    {
        if (await BuildAvailabilityAsync(_life.Token) is not { } availability)
        {
            ShowMessage("Couldn't check your calendars. Check your connection.");
            return;
        }

        var (text, free) = availability;

        if (text.Length == 0)
        {
            ShowMessage("None of those times are free");
            return;
        }

        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(AvailabilityText.Compose(_shareText, text));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            // Another app holds the clipboard (a remote session, a clipboard manager): say so, and sharing goes on
            Fail("share.copy.failed", ex);
            return;
        }

        // Saved As A Group (or the open group updated) with the free times that went into the text
        var saved = true;
        try
        {
            using var conn = _services.Database.Open();
            if (_openGroupId is { } id)
            {
                ShareGroupStore.Update(conn, id, _shareTitle, _shareText, _shareZoneId, free);
            }
            else
            {
                ShareGroupStore.Insert(conn, _shareTitle, _shareText, _shareZoneId, free, Now);
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            _services.Log.Error("share.group.save.failed", ex);
            saved = false;
        }

        _services.Log.Info("share.copy", $"slots={_slots.Count} calendars={_shareCalendars.Count}");
        StopSharing();
        ReloadGroups();
        ShowMessage(saved ? "Availability copied" : "Copied, but couldn't save these times.");
    }

    /// <summary>True when <paramref name="text"/> is exactly one valid email address.</summary>
    public static bool IsAddress(string text) => MailAddress.TryCreate(text, out var address) && address.Address == text;

    // A status for the log: Google's HTTP status, or the kind of failure (never a message, which could carry an address)
    private static string Status(Exception ex) => ex is GoogleApiException google ? ((int)google.Status).ToString(System.Globalization.CultureInfo.InvariantCulture) : ex.GetType().Name;
}
