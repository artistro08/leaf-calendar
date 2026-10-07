# Saved Share Groups Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Copied availability is saved as a titled group that stays on the calendar, picked and saved times resize by their edges, a group's time is approved into a real event with a guest, Esc leaves scheduling, and the default share message is set in Settings › Calendars.

**Architecture:** Groups live in two new SQLite tables (migration 9) behind a static `ShareGroupStore` in Core, like the other stores. The pure edge math (`DragMath.ResizeRange`, `ShareSlotHit.At`) lives in Core with logic tests. `CalendarViewModel.People.cs` owns sharing state (open picks, open group, saved groups, approval); `ShareSlotsPanel`, `DayColumn` and `TimeGridView` draw and drive it.

**Tech Stack:** C# 14, .NET 10, WinUI 3 (Windows App SDK), Microsoft.Data.Sqlite, xunit v3, FlaUI.

**Spec:** `docs/superpowers/specs/2026-10-06-saved-share-groups-design.md` (read it with this plan; it is the authority on behavior).

## Global Constraints

- Read `CLAUDE.md` first. The build treats every warning as an error: 0 warnings or the task isn't done.
- Run commands from the repo root `D:\leaf-calendar`.
- Build: `dotnet build LeafCalendar.slnx -c Debug`. Logic tests: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug` (one class: `--filter-class "*ClassName"`).
- UI tests drive the **installed** package: `pwsh tools/sign-install.ps1` first, then `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj -c Debug --filter-class "*ClassName"`. Never run two UI test processes at once.
- Native AOT rules: `x:Bind` only; `ItemsSource` = concrete `List<T>`; no reflection; JSON only through source-generated contexts.
- Store instants in UTC as Unix milliseconds (`ToUnixTimeMilliseconds`), like `AlertLedger`. Every SQL statement parameterized.
- Never log titles, messages, emails or times. Log names, IDs, counts, exception type names.
- Database reads that could block the UI run on the thread pool with a generation counter.
- Namespaces match folders. Doc comments on every public member. No `=` alignment. CRLF line endings: edit files in place.
- US English, sentence case. Approved wording (use verbatim): `Title`, `Held times`, `Guest email`, `name@example.com`, `Approve…`, `Save this time as an event with the guest`, `Delete`, `Saved times`, `Close`, `Copied, but couldn't save these times.`, `{title}, {time range}`, `Approve {time range}`, `Share availability message`, `{times} is replaced with your free times.`, `Use Leaf's message`. Existing strings stay: `Times to share`, `Use the default message`, `Only the times`, `Copy`, `Cancel`.
- Spacing scale 2, 4, 8, 12, 16, 24, 32. Every element a UI test touches gets an `AutomationId`; icon-only controls get a tooltip and `AutomationProperties.Name`.
- Commits: never commit unless the owner asks (standing rule covers merging a verified milestone). The "Commit" steps below mean **stage and stop**; leave committing to the owner's go-ahead.

## Review Focus

1. **Approve editor canceled or save fails** → the group must stay saved. Pinned in Task 7 (UI test `Approve_CancelEditor_KeepsGroup`) and by deleting the group only after `SaveEditorAsync` reaches `Editing = null` on the create path.
2. **Copy succeeds but the database write fails** → the clipboard still holds the text, sharing stops, the notice says `Copied, but couldn't save these times.` Pinned in Task 4 by the try/catch shape around `ShareGroupStore` calls (App layer has no unit harness; noted in report).
3. **A saved group whose times all pass while Leaf is open** → it disappears without a restart. Pinned in Task 2 (`GetAll_HidesEndedTimes`, `Prune_DropsEndedTimesAndEmptyGroups`) and Task 4 (minute-tick reload).
4. **Resize dragged past the other edge or across midnight/DST** → keeps at least 15 minutes and lands on a real wall-clock time. Pinned in Task 3 (`ResizeRange_*` tests).
5. **Two groups overlapping on the grid** → a click opens the topmost (the later-created) group; edges of the open picks win over saved ones. Pinned in Task 3 (`ShareSlotHit_*` tests: order of the list decides).

---

### Task 1: Esc leaves scheduling wherever focus is

**Files:**
- Modify: `src/LeafCalendar.App/Views/CalendarPage.xaml.cs:343-370` (or wherever the diagnosis points)
- Test: `tests/LeafCalendar.UITests/ShareAvailabilityTests.cs`

**Interfaces:** none new.

- [ ] **Step 1: Write the failing UI tests**

Add to `ShareAvailabilityTests`:

```csharp
[Fact]
public void Escape_OnTheGrid_StopsSharing()
{
    using var leaf = Launch();
    StartSharing(leaf);
    DragHours(leaf, 14, 15);
    leaf.WaitFor("ShareSlot_0");

    leaf.Press(VirtualKeyShort.ESCAPE);

    Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel") && leaf.WaitFor("ShareSlotsPanel").IsOffscreen == false, TimeSpan.FromSeconds(5)).Success);
}

[Theory]
[InlineData("ShareMessageBox")]
[InlineData("SharePanelStart_0")]
[InlineData("ShareCopyButton")]
public void Escape_WithFocusInThePanel_StopsSharing(string focusId)
{
    using var leaf = Launch();
    StartSharing(leaf);
    DragHours(leaf, 14, 15);
    leaf.WaitFor(focusId).Focus();

    leaf.Press(VirtualKeyShort.ESCAPE);

    Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel") && leaf.WaitFor("ShareSlotsPanel").IsOffscreen == false, TimeSpan.FromSeconds(5)).Success);
}
```

(The panel is collapsed, not removed, when sharing stops; check how the existing Cancel test asserts the panel is gone and use the same check if it differs from `IsOffscreen`.)

- [ ] **Step 2: Install and run to see which fail**

Run: `pwsh tools/sign-install.ps1`, then `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj -c Debug --filter-class "*ShareAvailabilityTests"`
Expected: at least one `Escape_*` case FAILS (the owner sees Esc not working). Note exactly which focus targets fail.

- [ ] **Step 3: Diagnose (systematic-debugging skill)**

Trace the key for each failing target. Candidates, in order:
1. `MainWindow.xaml.cs:89` `RootGrid.PreviewKeyDown` → `CalendarPage.HandleShortcut(e)`: check whether it marks Escape handled (a pending key sequence, the `_editorFromE` path) before the page's `KeyboardAccelerator` runs.
2. The `TimePicker`/`TextBox`/`TimeZoneComboBox` swallowing Escape (`TimeZoneComboBox.OnPreviewKeyDown:147` only when the selection differs).
3. Focus leaving the page's tree (accelerators only fire for focus inside the page).

Log temporarily with `_services.Log.Info("esc.trace", ...)` if needed; remove it before Step 5.

- [ ] **Step 4: Fix at the layer that eats the key**

Expected shape if (1) or (3): handle Escape in `CalendarPage.HandleShortcut` before other shortcut logic, unless a dropdown, picker or suggestion list is open:

```csharp
// Esc While Scheduling Stops It (wherever focus is in the window; an open dropdown or picker closes first, by itself)
if (e.Key == VirtualKey.Escape && ViewModel.IsSharing && !e.Handled && _sheet is null)
{
    if (_view is Controls.TimeGridView grid && grid.CancelDrag())
    {
        e.Handled = true;
        return true;
    }

    ViewModel.StopSharing();
    e.Handled = true;
    return true;
}
```

Keep `OnEscapeInvoked` as is. Open dropdowns and picker flyouts live in popups whose keys don't reach `RootGrid.PreviewKeyDown`; confirm by hand that Esc in an open time picker flyout closes only the flyout.

- [ ] **Step 5: Build, install, re-run**

Run: `dotnet build LeafCalendar.slnx -c Debug` (0 warnings), `pwsh tools/sign-install.ps1`, then the `ShareAvailabilityTests` class again.
Expected: all PASS.

- [ ] **Step 6: Stage**

```bash
git add src/LeafCalendar.App tests/LeafCalendar.UITests/ShareAvailabilityTests.cs
```

Proposed message: `fix(share): Esc leaves scheduling wherever focus is in the window`

---

### Task 2: Saved groups in the database (migration 9 and `ShareGroupStore`)

**Files:**
- Modify: `src/LeafCalendar.Core/Data/Schema.cs` (add `V9`)
- Modify: `src/LeafCalendar.Core/Data/LeafDatabase.cs` (apply V9)
- Create: `src/LeafCalendar.Core/Data/ShareGroupStore.cs`
- Create: `tests/LeafCalendar.Tests/ShareGroupStoreTests.cs`
- Modify: `tests/LeafCalendar.Tests/LeafDatabaseTests.cs` (every `Assert.Equal(8L, … user_version …)` → `9L`, plus one new test)

**Interfaces:**
- Produces:
  - `public sealed record ShareGroup(long Id, string Title, string Message, string ZoneId, IReadOnlyList<BusyRange> Slots)` (namespace `LeafCalendar.Core.Data`)
  - `public static class ShareGroupStore` with
    - `const int MaxTitleLength = 100`
    - `IReadOnlyList<ShareGroup> GetAll(SqliteConnection conn, DateTimeOffset now)` — oldest group first; slots merged, in order, ended ones left out; groups with no slots left out
    - `long Insert(SqliteConnection conn, string title, string message, string zoneId, IReadOnlyList<BusyRange> slots, DateTimeOffset now)`
    - `void Update(SqliteConnection conn, long id, string title, string message, string zoneId, IReadOnlyList<BusyRange> slots)` — no slots deletes the group
    - `void Delete(SqliteConnection conn, long id)`
    - `void Prune(SqliteConnection conn, DateTimeOffset now)`

- [ ] **Step 1: Write the failing tests**

`tests/LeafCalendar.Tests/ShareGroupStoreTests.cs`:

```csharp
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class ShareGroupStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private static BusyRange At(int startHour, int endHour) => new(Now.AddHours(startHour), Now.AddHours(endHour));

    [Fact]
    public void Insert_ThenGetAll_ReadsTheGroupBack()
    {
        using var conn = _db.Database.Open();

        var id = ShareGroupStore.Insert(conn, "Coffee", "Here:\r\n{times}", "America/New_York", [At(3, 4), At(1, 2)], Now);

        var group = Assert.Single(ShareGroupStore.GetAll(conn, Now));
        Assert.Equal(id, group.Id);
        Assert.Equal("Coffee", group.Title);
        Assert.Equal("Here:\r\n{times}", group.Message);
        Assert.Equal("America/New_York", group.ZoneId);
        Assert.Equal([At(1, 2), At(3, 4)], group.Slots);
    }

    [Fact]
    public void Insert_CleansAndCapsTheTitle()
    {
        using var conn = _db.Database.Open();

        ShareGroupStore.Insert(conn, "  Lunch\r\n" + new string('x', 200), "", "UTC", [At(1, 2)], Now);

        var title = Assert.Single(ShareGroupStore.GetAll(conn, Now)).Title;
        Assert.True(title.Length <= ShareGroupStore.MaxTitleLength);
        Assert.StartsWith("Lunch", title, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', title);
    }

    [Fact]
    public void Insert_CapsTheMessage()
    {
        using var conn = _db.Database.Open();

        ShareGroupStore.Insert(conn, "", new string('m', 5000), "UTC", [At(1, 2)], Now);

        Assert.Equal(LeafCalendar.Core.People.AvailabilityText.MaxMessageLength, Assert.Single(ShareGroupStore.GetAll(conn, Now)).Message.Length);
    }

    [Fact]
    public void Insert_OverlappingTimes_AreMerged()
    {
        using var conn = _db.Database.Open();

        ShareGroupStore.Insert(conn, "", "", "UTC", [At(1, 3), At(2, 4)], Now);

        Assert.Equal([At(1, 4)], Assert.Single(ShareGroupStore.GetAll(conn, Now)).Slots);
    }

    [Fact]
    public void Update_ReplacesTitleMessageZoneAndTimes()
    {
        using var conn = _db.Database.Open();
        var id = ShareGroupStore.Insert(conn, "Old", "old", "UTC", [At(1, 2)], Now);

        ShareGroupStore.Update(conn, id, "New", "new", "Europe/Paris", [At(5, 6)]);

        var group = Assert.Single(ShareGroupStore.GetAll(conn, Now));
        Assert.Equal(("New", "new", "Europe/Paris"), (group.Title, group.Message, group.ZoneId));
        Assert.Equal([At(5, 6)], group.Slots);
    }

    [Fact]
    public void Update_NoTimesLeft_DeletesTheGroup()
    {
        using var conn = _db.Database.Open();
        var id = ShareGroupStore.Insert(conn, "", "", "UTC", [At(1, 2)], Now);

        ShareGroupStore.Update(conn, id, "", "", "UTC", []);

        Assert.Empty(ShareGroupStore.GetAll(conn, Now));
        Assert.Equal(0L, conn.Query(null, "SELECT COUNT(*) FROM share_groups;", r => r.GetInt64(0)).Single());
    }

    [Fact]
    public void Delete_RemovesItsTimesToo()
    {
        using var conn = _db.Database.Open();
        var id = ShareGroupStore.Insert(conn, "", "", "UTC", [At(1, 2), At(3, 4)], Now);

        ShareGroupStore.Delete(conn, id);

        Assert.Empty(ShareGroupStore.GetAll(conn, Now));
        Assert.Equal(0L, conn.Query(null, "SELECT COUNT(*) FROM share_slots;", r => r.GetInt64(0)).Single());
    }

    [Fact]
    public void GetAll_HidesEndedTimes()
    {
        using var conn = _db.Database.Open();
        ShareGroupStore.Insert(conn, "", "", "UTC", [At(-3, -2), At(-1, 1), At(2, 3)], Now);

        // A time still running stays (only an end at or before now is past)
        Assert.Equal([At(-1, 1), At(2, 3)], Assert.Single(ShareGroupStore.GetAll(conn, Now)).Slots);
    }

    [Fact]
    public void GetAll_GroupWithOnlyEndedTimes_IsLeftOut()
    {
        using var conn = _db.Database.Open();
        ShareGroupStore.Insert(conn, "", "", "UTC", [At(-3, -2)], Now);

        Assert.Empty(ShareGroupStore.GetAll(conn, Now));
    }

    [Fact]
    public void GetAll_OldestGroupFirst()
    {
        using var conn = _db.Database.Open();
        var first = ShareGroupStore.Insert(conn, "A", "", "UTC", [At(5, 6)], Now);
        var second = ShareGroupStore.Insert(conn, "B", "", "UTC", [At(1, 2)], Now.AddMinutes(1));

        Assert.Equal([first, second], ShareGroupStore.GetAll(conn, Now).Select(g => g.Id));
    }

    [Fact]
    public void Prune_DropsEndedTimesAndEmptyGroups()
    {
        using var conn = _db.Database.Open();
        ShareGroupStore.Insert(conn, "Gone", "", "UTC", [At(-3, -2)], Now);
        ShareGroupStore.Insert(conn, "Kept", "", "UTC", [At(-3, -2), At(1, 2)], Now);

        ShareGroupStore.Prune(conn, Now);

        Assert.Equal(1L, conn.Query(null, "SELECT COUNT(*) FROM share_groups;", r => r.GetInt64(0)).Single());
        Assert.Equal(1L, conn.Query(null, "SELECT COUNT(*) FROM share_slots;", r => r.GetInt64(0)).Single());
    }
}
```

In `LeafDatabaseTests.cs` change every `Assert.Equal(8L, conn.Query(null, "PRAGMA user_version;" …` to `9L`, and add:

```csharp
[Fact]
public void Migrate_FromVersion8_AddsEmptyShareGroups()
{
    using var folder = new TempFolder();
    var database = new LeafDatabase(Path.Combine(folder.Path, "leaf.db"));

    // A Version 8 Database With One Account (what the deferred-items release installs)
    using (var conn = database.Open())
    using (var setup = conn.CreateCommand())
    {
        setup.CommandText = Schema.V1 + Schema.V2 + Schema.V3 + Schema.V4 + Schema.V5 + Schema.V6 + Schema.V7 + Schema.V8 + """
            PRAGMA user_version = 8;
            INSERT INTO accounts (id, email, display_name) VALUES ('acct', 'a@example.com', 'A');
            """;
        setup.ExecuteNonQuery();
    }

    database.Migrate();

    using (var conn = database.Open())
    {
        Assert.Equal(9L, conn.Query(null, "PRAGMA user_version;", r => r.GetInt64(0)).Single());
        Assert.Single(AccountStore.GetAll(conn));
        Assert.Empty(ShareGroupStore.GetAll(conn, DateTimeOffset.UtcNow));
        conn.Close();
        SqliteConnection.ClearPool(conn);
    }
}
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug --filter-class "*ShareGroupStoreTests"`
Expected: build FAILS (`ShareGroupStore` not defined).

- [ ] **Step 3: Add the migration**

`Schema.cs`, after `V8`:

```csharp
    /// <summary>
    /// Version 9: shared availability saved when it's copied (owner request 2026-10-06). <c>share_groups</c> holds the
    /// title and message as typed and the IANA zone the text was written in; <c>share_slots</c> its free times, in UTC.
    /// Local only: never sent anywhere.
    /// </summary>
    public const string V9 = """
        CREATE TABLE share_groups (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            title       TEXT NOT NULL,
            message     TEXT NOT NULL,
            zone_id     TEXT NOT NULL,
            created_utc INTEGER NOT NULL
        );

        CREATE TABLE share_slots (
            group_id  INTEGER NOT NULL REFERENCES share_groups(id) ON DELETE CASCADE,
            start_utc INTEGER NOT NULL,
            end_utc   INTEGER NOT NULL
        );

        CREATE INDEX ix_share_slots_group ON share_slots (group_id);
        """;
```

`LeafDatabase.Migrate`, after the version 8 block:

```csharp
        // Version 9
        if (version < 9)
        {
            using var tx = conn.BeginTransaction();
            conn.Execute(tx, Schema.V9);
            conn.Execute(tx, "PRAGMA user_version = 9;");
            tx.Commit();
        }
```

Update the `CLAUDE.md` line "tracked in `PRAGMA user_version` (currently 8)" to 9.

- [ ] **Step 4: Write the store**

`src/LeafCalendar.Core/Data/ShareGroupStore.cs`:

```csharp
using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;
using LeafCalendar.Core.Tray;
using Microsoft.Data.Sqlite;

namespace LeafCalendar.Core.Data;

/// <summary>A saved share: its title and message as typed, the zone its text is written in, and its free times (merged, in order).</summary>
public sealed record ShareGroup(long Id, string Title, string Message, string ZoneId, IReadOnlyList<BusyRange> Slots);

/// <summary>
/// Reads and writes the <c>share_groups</c> and <c>share_slots</c> tables: availability you copied, kept so it stays on
/// the calendar until you approve a time, delete the group, or its times pass.
/// </summary>
public static class ShareGroupStore
{
    /// <summary>The longest title kept.</summary>
    public const int MaxTitleLength = 100;

    /// <summary>Every group with a time that hasn't ended by <paramref name="now"/>, oldest first, ended times left out.</summary>
    public static IReadOnlyList<ShareGroup> GetAll(SqliteConnection conn, DateTimeOffset now)
    {
        var groups = conn.Query(null, "SELECT id, title, message, zone_id FROM share_groups ORDER BY created_utc, id;", r => (Id: r.GetInt64(0), Title: r.GetString(1), Message: r.GetString(2), Zone: r.GetString(3)));
        var slots = conn.Query(
            null,
            "SELECT group_id, start_utc, end_utc FROM share_slots WHERE end_utc > $now;",
            r => (Group: r.GetInt64(0), Range: new BusyRange(DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2))))),
            ("$now", now.ToUnixTimeMilliseconds()))
            .ToLookup(s => s.Group, s => s.Range);

        return [.. groups
            .Where(g => slots[g.Id].Any())
            .Select(g => new ShareGroup(g.Id, g.Title, g.Message, g.Zone, BusyMath.Merge(slots[g.Id])))];
    }

    /// <summary>Saves a new group and returns its ID. The title is cleaned and capped, the message capped, the times merged.</summary>
    public static long Insert(SqliteConnection conn, string title, string message, string zoneId, IReadOnlyList<BusyRange> slots, DateTimeOffset now)
    {
        using var tx = conn.BeginTransaction();
        conn.Execute(
            tx,
            "INSERT INTO share_groups (title, message, zone_id, created_utc) VALUES ($title, $message, $zone, $now);",
            ("$title", DisplayText.Clean(title, MaxTitleLength)),
            ("$message", Cap(message)),
            ("$zone", zoneId),
            ("$now", now.ToUnixTimeMilliseconds()));
        var id = conn.Query(tx, "SELECT last_insert_rowid();", r => r.GetInt64(0)).Single();
        WriteSlots(conn, tx, id, slots);
        tx.Commit();
        return id;
    }

    /// <summary>Replaces a group's title, message, zone and times. With no times left the group is deleted.</summary>
    public static void Update(SqliteConnection conn, long id, string title, string message, string zoneId, IReadOnlyList<BusyRange> slots)
    {
        if (slots.Count == 0)
        {
            Delete(conn, id);
            return;
        }

        using var tx = conn.BeginTransaction();
        conn.Execute(
            tx,
            "UPDATE share_groups SET title = $title, message = $message, zone_id = $zone WHERE id = $id;",
            ("$id", id),
            ("$title", DisplayText.Clean(title, MaxTitleLength)),
            ("$message", Cap(message)),
            ("$zone", zoneId));
        conn.Execute(tx, "DELETE FROM share_slots WHERE group_id = $id;", ("$id", id));
        WriteSlots(conn, tx, id, slots);
        tx.Commit();
    }

    /// <summary>Deletes a group and its times.</summary>
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

    private static string Cap(string message) => message.Length > AvailabilityText.MaxMessageLength ? message[..AvailabilityText.MaxMessageLength] : message;
}
```

Check `DisplayText.Clean` collapses newlines and trims (read `src/LeafCalendar.Core/Tray/DisplayText.cs:20`); if it doesn't trim, `Insert_CleansAndCapsTheTitle` tells you. Check `BusyMath.Merge` also sorts (read `BusyMath.cs:11`).

- [ ] **Step 5: Run the tests**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug --filter-class "*ShareGroupStoreTests"`, then `--filter-class "*LeafDatabaseTests"`, then the full suite.
Expected: all PASS, 0 build warnings.

- [ ] **Step 6: Stage**

```bash
git add src/LeafCalendar.Core/Data tests/LeafCalendar.Tests/ShareGroupStoreTests.cs tests/LeafCalendar.Tests/LeafDatabaseTests.cs CLAUDE.md
```

Proposed message: `feat(share): copied availability can be saved as a group (database version 9)`

---

### Task 3: Edge math (`DragMath.ResizeRange`, `ShareSlotHit`)

**Files:**
- Modify: `src/LeafCalendar.Core/Views/DragMath.cs` (add `ResizeRange` after `ResizeEnd`, ~line 85)
- Create: `src/LeafCalendar.Core/Views/ShareSlotHit.cs`
- Test: `tests/LeafCalendar.Tests/DragMathTests.cs`, create `tests/LeafCalendar.Tests/ShareSlotHitTests.cs`

**Interfaces:**
- Produces:
  - `public static (DateTimeOffset Start, DateTimeOffset End) DragMath.ResizeRange(DateTimeOffset start, DateTimeOffset end, bool topEdge, DateTimeOffset pointerAt, TimeZoneInfo zone)`
  - `public enum SlotEdge { Inside, Top, Bottom }` and `public readonly record struct SlotHitResult(int Index, SlotEdge Edge)`
  - `public static SlotHitResult? ShareSlotHit.At(IReadOnlyList<BusyRange> slots, DateTimeOffset at, TimeSpan edge)` — searches from the **end** of the list (the last drawn is on top); an edge within `edge` of a slot's start or end wins over an inside hit of a slot earlier in the list only by list order.

- [ ] **Step 1: Write the failing tests**

Add to `DragMathTests` (use the class's existing zone helpers if it has them; this uses New York, which has a DST change on 2026-11-01):

```csharp
private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

private static DateTimeOffset Ny(int month, int day, int hour, int minute = 0) =>
    DragMath.ToInstant(new DateTime(2026, month, day, hour, minute, 0), NewYork);

[Fact]
public void ResizeRange_BottomEdge_SnapsTheEnd()
{
    var (start, end) = DragMath.ResizeRange(Ny(10, 1, 9), Ny(10, 1, 10), topEdge: false, Ny(10, 1, 11, 7), NewYork);

    Assert.Equal((Ny(10, 1, 9), Ny(10, 1, 11)), (start, end));
}

[Fact]
public void ResizeRange_TopEdge_SnapsTheStart()
{
    var (start, end) = DragMath.ResizeRange(Ny(10, 1, 9), Ny(10, 1, 10), topEdge: true, Ny(10, 1, 8, 22), NewYork);

    Assert.Equal((Ny(10, 1, 8, 15), Ny(10, 1, 10)), (start, end));
}

[Fact]
public void ResizeRange_TopDraggedPastTheEnd_KeepsFifteenMinutes()
{
    var (start, end) = DragMath.ResizeRange(Ny(10, 1, 9), Ny(10, 1, 10), topEdge: true, Ny(10, 1, 12), NewYork);

    Assert.Equal((Ny(10, 1, 9, 45), Ny(10, 1, 10)), (start, end));
}

[Fact]
public void ResizeRange_BottomDraggedAboveTheStart_KeepsFifteenMinutes()
{
    var (start, end) = DragMath.ResizeRange(Ny(10, 1, 9), Ny(10, 1, 10), topEdge: false, Ny(10, 1, 7), NewYork);

    Assert.Equal((Ny(10, 1, 9), Ny(10, 1, 9, 15)), (start, end));
}

[Fact]
public void ResizeRange_OnTheFallBackDay_EndIsARealInstant()
{
    // Nov 1 2026: 1:00-2:00 AM happens twice in New York; 3 AM is 4 real hours after midnight
    var (start, end) = DragMath.ResizeRange(Ny(11, 1, 0), Ny(11, 1, 1), topEdge: false, Ny(11, 1, 3), NewYork);

    Assert.Equal(Ny(11, 1, 0), start);
    Assert.Equal(TimeSpan.FromHours(4), end - start);
}
```

`tests/LeafCalendar.Tests/ShareSlotHitTests.cs`:

```csharp
using LeafCalendar.Core.Google;
using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public sealed class ShareSlotHitTests
{
    private static readonly DateTimeOffset Nine = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Edge = TimeSpan.FromMinutes(5);

    private static BusyRange Hours(double from, double to) => new(Nine.AddHours(from), Nine.AddHours(to));

    [Fact]
    public void At_NearTheTop_IsTheTopEdge() =>
        Assert.Equal(new SlotHitResult(0, SlotEdge.Top), ShareSlotHit.At([Hours(0, 1)], Nine.AddMinutes(3), Edge));

    [Fact]
    public void At_NearTheBottom_IsTheBottomEdge() =>
        Assert.Equal(new SlotHitResult(0, SlotEdge.Bottom), ShareSlotHit.At([Hours(0, 1)], Nine.AddMinutes(57), Edge));

    [Fact]
    public void At_InTheMiddle_IsInside() =>
        Assert.Equal(new SlotHitResult(0, SlotEdge.Inside), ShareSlotHit.At([Hours(0, 1)], Nine.AddMinutes(30), Edge));

    [Fact]
    public void At_JustOutside_IsTheEdgeStill() =>
        Assert.Equal(new SlotHitResult(0, SlotEdge.Bottom), ShareSlotHit.At([Hours(0, 1)], Nine.AddMinutes(63), Edge));

    [Fact]
    public void At_FarOutside_IsNothing() =>
        Assert.Null(ShareSlotHit.At([Hours(0, 1)], Nine.AddHours(2), Edge));

    [Fact]
    public void At_Overlapping_TheLastInTheListWins() =>
        Assert.Equal(new SlotHitResult(1, SlotEdge.Inside), ShareSlotHit.At([Hours(0, 3), Hours(1, 2)], Nine.AddMinutes(90), Edge));

    [Fact]
    public void At_ShortSlot_SplitsTheEdgesAtTheMiddle() =>
        Assert.Equal(new SlotHitResult(0, SlotEdge.Top), ShareSlotHit.At([Hours(0, 0.25)], Nine.AddMinutes(6), Edge));
}
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test --project tests/LeafCalendar.Tests/LeafCalendar.Tests.csproj -c Debug --filter-class "*ShareSlotHitTests"`
Expected: build FAILS (`ShareSlotHit`, `ResizeRange` not defined).

- [ ] **Step 3: Implement**

`DragMath.cs`, after `ResizeEnd`:

```csharp
    /// <summary>
    /// A picked time with its top (<paramref name="topEdge"/>) or bottom edge dragged to the pointer: that edge snaps,
    /// and the time keeps at least one snap step.
    /// </summary>
    public static (DateTimeOffset Start, DateTimeOffset End) ResizeRange(DateTimeOffset start, DateTimeOffset end, bool topEdge, DateTimeOffset pointerAt, TimeZoneInfo zone)
    {
        var step = TimeSpan.FromMinutes(SnapMinutes);
        var moved = Snap(pointerAt, zone);
        return topEdge
            ? (moved > end - step ? end - step : moved, end)
            : (start, moved < start + step ? start + step : moved);
    }
```

`src/LeafCalendar.Core/Views/ShareSlotHit.cs`:

```csharp
using LeafCalendar.Core.Google;

namespace LeafCalendar.Core.Views;

/// <summary>Where on a picked time the pointer is: its top edge, its bottom edge, or inside.</summary>
public enum SlotEdge
{
    /// <summary>Inside, away from both edges.</summary>
    Inside,

    /// <summary>On the top edge (dragging it moves the start).</summary>
    Top,

    /// <summary>On the bottom edge (dragging it moves the end).</summary>
    Bottom,
}

/// <summary>A picked time under the pointer: its place in the list and which part was hit.</summary>
public readonly record struct SlotHitResult(int Index, SlotEdge Edge);

/// <summary>Finds the picked time under the pointer on the time grid, so its edges can be dragged.</summary>
public static class ShareSlotHit
{
    /// <summary>
    /// The time under <paramref name="at"/>, searched from the end of the list (drawn last, so on top). An instant within
    /// <paramref name="edge"/> of a time's start or end is on that edge, even just outside it; a time shorter than two
    /// edges splits at its middle. Null when nothing is under the pointer.
    /// </summary>
    public static SlotHitResult? At(IReadOnlyList<BusyRange> slots, DateTimeOffset at, TimeSpan edge)
    {
        for (var i = slots.Count - 1; i >= 0; i--)
        {
            var s = slots[i];
            if (at < s.Start - edge || at > s.End + edge)
            {
                continue;
            }

            var middle = s.Start + (s.End - s.Start) / 2;
            if (at <= s.Start + edge && at <= middle)
            {
                return new SlotHitResult(i, SlotEdge.Top);
            }

            if (at >= s.End - edge)
            {
                return new SlotHitResult(i, SlotEdge.Bottom);
            }

            if (at >= s.Start)
            {
                return new SlotHitResult(i, SlotEdge.Inside);
            }
        }

        return null;
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `--filter-class "*ShareSlotHitTests"`, `--filter-class "*DragMathTests"`, then the full logic suite.
Expected: all PASS, 0 warnings.

- [ ] **Step 5: Stage**

```bash
git add src/LeafCalendar.Core/Views tests/LeafCalendar.Tests/DragMathTests.cs tests/LeafCalendar.Tests/ShareSlotHitTests.cs
```

---

### Task 4: View model: saved groups, per-share message and title, Copy saves

**Files:**
- Modify: `src/LeafCalendar.App/ViewModels/CalendarViewModel.People.cs:188-370` (share availability section)
- Modify: `src/LeafCalendar.App/ViewModels/CalendarViewModel.cs` (constructor: first load; `OnMinute`: reload when a shown time ended)
- Modify: `PRIVACY.md`

**Interfaces:**
- Consumes: `ShareGroupStore`, `ShareGroup` (Task 2).
- Produces (all on `CalendarViewModel`):
  - `public const string GenericShareTitle = "Held times";`
  - `public IReadOnlyList<ShareGroup> SavedGroups { get; }`
  - `public long? OpenGroupId { get; }`
  - `public string ShareTitle { get; set; }` (setting it with a group open saves the group)
  - `public string ShareText { get; set; }` (this share's message; with a group open saves the group)
  - `public void OpenGroup(long id)`
  - `public void DeleteOpenGroup()`
  - `public void ReloadGroups()`, `private void WriteGroups(string failure, Action<SqliteConnection> write)`, `private void SaveOpenGroup()` (used by Tasks 6-7)
  - `SetShareMessage` removed
  - `public static string GroupTitle(ShareGroup group)` → title or `GenericShareTitle`
  - existing `AddShareSlot`, `UpdateShareSlot`, `RemoveShareSlot`, `StartSharing`, `StopSharing`, `CopyAvailabilityAsync` keep their signatures

- [ ] **Step 1: Replace the message and add the group state**

In the share availability section, replace the `SetShareMessage` method and add fields/properties:

```csharp
    /// <summary>What a group with no title is called.</summary>
    public const string GenericShareTitle = "Held times";

    private long? _openGroupId;
    private string _shareTitle = "";
    private string _shareText = "";
    private List<ShareGroup> _savedGroups = [];
    private int _groupsGeneration;

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
    public static string GroupTitle(ShareGroup group) => group.Title.Length > 0 ? group.Title : GenericShareTitle;
```

Delete `SetShareMessage` (the panel no longer changes the default; Settings › Calendars saves it through `SettingsContext.Save`, Task 8).

- [ ] **Step 2: Start, open, stop**

`StartSharing` gains (before `_sharing = true;`):

```csharp
        _openGroupId = null;
        _shareTitle = "";
        _shareText = Settings.ShareMessage;
```

Add `OpenGroup` and `DeleteOpenGroup`:

```csharp
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
```

`StopSharing` also clears `_openGroupId = null;`.

- [ ] **Step 3: Edits to an open group save at once**

At the end of `AddShareSlot`, `RemoveShareSlot` and `UpdateShareSlot` (before the `ShareChanged` invoke), call `SaveOpenGroup();`. In `RemoveShareSlot`, when a group is open and `_slots.Count == 0` after removing, the store deletes the group (Update with no times), so follow with `StopSharing(); return;`.

```csharp
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
            ShowMessage("Couldn't save these times.");
        }

        ReloadGroups();
    }

    /// <summary>Reads the saved groups again off the UI thread, dropping ended times first; only the newest read lands.</summary>
    public void ReloadGroups()
    {
        var generation = ++_groupsGeneration;
        var now = Now;
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
            ShareChanged?.Invoke(this, EventArgs.Empty);
        }, "share.groups.load.failed");
    }
```

> `ShowMessage("Couldn't save these times.")` is new wording not in the approved list. Use it only if the owner approves it; otherwise reuse the approved `Copied, but couldn't save these times.` for the Copy path and log-only for the rest. **Ask the owner before shipping.** (Flagged in the final report.)

Check `Fire` marshals its continuation back to the UI thread (read its definition in `CalendarViewModel.cs`); if it doesn't, wrap the assignment in `_dispatcher.TryEnqueue`.

- [ ] **Step 4: Copy saves**

In `CopyAvailabilityAsync`, compose from `_shareText` instead of `Settings.ShareMessage`. `BuildAvailabilityAsync` must also hand back the free ranges: change it to return `(string Text, IReadOnlyList<BusyRange> Free)?` (null keeps meaning "couldn't ask Google"; `("", [])` means nothing free), and update its one other caller if any (grep `BuildAvailabilityAsync`). After the clipboard succeeds and before `StopSharing()`:

```csharp
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
```

- [ ] **Step 5: Load at start and drop ended times**

`CalendarViewModel` constructor, after `RefreshSyncState();`: `ReloadGroups();`.
In `OnMinute()`, add:

```csharp
        // A Saved Time Ended: read the groups again (ended times and empty groups go)
        if (_savedGroups.Any(g => g.Slots.Any(s => s.End <= Now)))
        {
            ReloadGroups();
        }
```

- [ ] **Step 6: Privacy**

In `PRIVACY.md`, in the section listing what Leaf keeps on the PC, add (wording for the owner to approve in the report):

```markdown
- **Saved share times.** When you copy your availability, Leaf keeps the times, the title and the message you shared in its local database, so they stay on your calendar. They're deleted when you approve a time, delete the group, or the times pass. They never leave your PC.
```

- [ ] **Step 7: Build**

Run: `dotnet build LeafCalendar.slnx -c Debug`
Expected: build fails only where `SetShareMessage` was called (`ShareSlotsPanel.cs`), fixed in Task 5. Do Task 5 before running UI tests. (App layer has no unit harness: say so in the report.)

---

### Task 5: Share panel: Title, message per share, saved-group mode

**Files:**
- Modify: `src/LeafCalendar.App/Views/ShareSlotsPanel.cs`

**Interfaces:**
- Consumes: `ShareTitle`, `ShareText`, `OpenGroupId`, `DeleteOpenGroup`, `ApproveSlot` (Task 7 adds it; until then leave the Approve buttons disabled), `IsAddress`.
- Produces AutomationIds: `ShareTitleBox`, `ShareGuestBox`, `SharePanelApprove_{i}`, `ShareDeleteButton`, `ShareCloseButton` (the Cancel button, relabeled `Close` with a group open; keep the id `ShareCancelButton` so existing tests still find it), `SharePanelTitle`.

- [ ] **Step 1: New controls**

Add fields:

```csharp
    private readonly TextBlock _title = new() { Text = "Times to share", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis, Padding = new Thickness(0, 4, 0, 4) };
    private readonly TextBox _titleBox = new() { Header = "Title", PlaceholderText = CalendarViewModel.GenericShareTitle, MaxLength = ShareGroupStore.MaxTitleLength };
    private readonly TextBox _guest = new() { Header = "Guest email", PlaceholderText = "name@example.com", InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.EmailSmtpAddress) } }, Visibility = Visibility.Collapsed };
    private readonly Button _cancel = new() { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _delete = new() { Content = "Delete", HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
```

Replace the local `title` with `_title` (AutomationId `SharePanelTitle`). Wire:

```csharp
        // Title (shown on the saved times; pre-fills the event title on approve)
        AutomationProperties.SetName(_titleBox, "Title");
        AutomationProperties.SetAutomationId(_titleBox, "ShareTitleBox");
        _titleBox.LostFocus += (_, _) => _vm?.ShareTitle = _titleBox.Text;

        // Guest (a saved group only: Approve needs one address)
        AutomationProperties.SetName(_guest, "Guest email");
        AutomationProperties.SetAutomationId(_guest, "ShareGuestBox");
        _guest.TextChanged += (_, _) => _shown.ForEach(r => r.CanApprove = CalendarViewModel.IsAddress(_guest.Text.Trim()));
```

Message box: `LostFocus` sets `_vm.ShareText = _message.Text` (not `SetShareMessage`). The reset link now uses the default from Settings:

```csharp
        resetMessage.Click += (_, _) =>
        {
            if (_vm is { } vm)
            {
                _message.Text = vm.Settings.ShareMessage.Replace("\r\n", "\r", StringComparison.Ordinal);
                vm.ShareText = vm.Settings.ShareMessage;
            }
        };
```

Stack order: `_title`, hint, `_titleBox`, `_zoneBox`, `_message`, messageHint, resetMessage, `_guest`, `_empty`, `_rows`.

Footer: three columns when a group is open (Copy, Close, Delete), two otherwise. Build the grid with three star columns and collapse `_delete`; set `Grid.SetColumnSpan` is not needed since collapsed columns of Star width still take space, so instead toggle the third column's width between `Star` and `0` in `Update`. `_delete.Click += (_, _) => _vm?.DeleteOpenGroup();` `AutomationProperties.SetAutomationId(_delete, "ShareDeleteButton");`. Keep `_cancel` as the existing Cancel (AutomationId `ShareCancelButton`).

`Copy()` sets `vm.ShareTitle = _titleBox.Text; vm.ShareText = _message.Text;` before firing.

- [ ] **Step 2: `Update` shows the mode**

Replace the "when sharing starts" block with one that runs when sharing starts **or the open group changes**:

```csharp
        // Sharing Started Or Another Group Opened: the boxes show its title, message and zone
        if (vm.IsSharing && (!_sharing || _group != vm.OpenGroupId))
        {
            _zoneBox.Show(vm.ShareZoneId, vm.Now);
            _message.Text = vm.ShareText.Replace("\r\n", "\r", StringComparison.Ordinal);
            _titleBox.Text = vm.ShareTitle;
            _guest.Text = "";
        }

        _group = vm.OpenGroupId;
        var saved = _group is not null;
        _title.Text = saved ? "Saved times" : "Times to share";
        _cancel.Content = saved ? "Close" : "Cancel";
        _guest.Visibility = _delete.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
        _deleteColumn.Width = saved ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
```

with fields `private long? _group;` and `private readonly ColumnDefinition _deleteColumn = new();`. Each row's `Show` gets `saved` to show or hide its Approve button.

- [ ] **Step 3: Approve button per row**

In `SlotRow`, add a fifth column with:

```csharp
            Approve = new Button { Content = "Approve…", IsEnabled = false, Visibility = Visibility.Collapsed };
            ToolTipService.SetToolTip(Approve, "Save this time as an event with the guest");
            AutomationProperties.SetAutomationId(Approve, string.Create(CultureInfo.InvariantCulture, $"SharePanelApprove_{index}"));
            Approve.Click += (_, _) => owner.Approve(this);
```

Layout: put Approve on its own third row under the pickers (the pane is 320 wide; a fifth column would squeeze the pickers), `HorizontalAlignment = Left`, spanning all columns.

```csharp
        public Button Approve { get; }

        public bool CanApprove { set => Approve.IsEnabled = value; }
```

In `Show(...)`, set `Approve.Visibility` from `saved`, and `AutomationProperties.SetName(Approve, $"Approve {TimeLabels.Range(slot.Start, slot.End, zone, vm.Settings.Use24HourTime)}");`. After `Update` builds rows, apply `CanApprove` from the guest box.

Owner method (filled in Task 7):

```csharp
    // Approve…: the event editor with this time and the guest
    private void Approve(SlotRow row)
    {
        if (_vm is { } vm && CalendarViewModel.IsAddress(_guest.Text.Trim()))
        {
            vm.ShareTitle = _titleBox.Text;
            vm.ApproveSlot(row.Index, _guest.Text.Trim());
        }
    }
```

(Until Task 7 lands, leave `vm.ApproveSlot` out and keep the body empty, or do Task 7's view-model step first.)

Update the class doc comment to describe the Title box, the saved-group mode, Approve and Delete.

- [ ] **Step 4: Build, install, run the share tests**

Run: `dotnet build LeafCalendar.slnx -c Debug` (0 warnings), `pwsh tools/sign-install.ps1`, `--filter-class "*ShareAvailabilityTests"`.
Expected: PASS (the existing tests still see the default message in the copied text, since the panel starts from it).

- [ ] **Step 5: Stage**

```bash
git add src/LeafCalendar.App PRIVACY.md
```

Proposed message: `feat(share): copied times are saved as a titled group, and a saved group reopens in the share panel`

---

### Task 6: Grid: saved groups drawn and clickable, edges resize

**Files:**
- Modify: `src/LeafCalendar.App/Controls/DayColumn.cs` (`RenderSlots` 389-481, constructor pointer handlers 93-107)
- Modify: `src/LeafCalendar.App/Controls/TimeGridView.cs` (`OnShareChanged` 926-945, drag code 958-1320)
- Modify: `src/LeafCalendar.App/ViewModels/CalendarViewModel.People.cs`

**Interfaces:**
- Consumes: `ShareSlotHit.At`, `SlotEdge`, `DragMath.ResizeRange` (Task 3), `SavedGroups`, `OpenGroupId`, `OpenGroup`, `GroupTitle` (Task 4).
- Produces (view model):
  - `public sealed record GridSlot(long? GroupId, int Index, BusyRange Range, string? Title, bool Resizable);` (in `CalendarViewModel.People.cs`, next to `OverlayBlock`)
  - `public IReadOnlyList<GridSlot> GridSlots()` — saved groups' times first (oldest group first, skipping the open group), then the open picks; open picks are `GroupId = OpenGroupId, Title = null` and resizable; a saved group's times are resizable only when it is `_approvingGroupId` (Task 7; until then `false`)
  - `public void ResizeGridSlot(GridSlot slot, DateTimeOffset start, DateTimeOffset end)` — open picks → `UpdateShareSlot(slot.Index, …)`; a saved group → `UpdateSavedSlot` (Task 7)
- Produces AutomationIds: open picks keep `ShareSlot_{n}` and `ShareSlot_{n}_Remove`; saved times are `SavedSlot_{groupId}_{n}`.

- [ ] **Step 1: The view model's list of drawn times**

```csharp
    /// <summary>
    /// The times the grid draws: every saved group's (oldest first; not the open group's, which are the picks), then the
    /// picks while sharing. The last drawn is on top, so the picks win a click. Only the picks, and a group being
    /// approved, can be resized.
    /// </summary>
    public IReadOnlyList<GridSlot> GridSlots()
    {
        var list = new List<GridSlot>();
        foreach (var g in _savedGroups.Where(g => g.Id != _openGroupId || !_sharing))
        {
            list.AddRange(g.Slots.Select((s, i) => new GridSlot(g.Id, i, s, GroupTitle(g), Resizable: g.Id == _approvingGroupId)));
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
```

Add `private long? _approvingGroupId;` now (Task 7 sets it) and a stub-free `UpdateSavedSlot`:

```csharp
    /// <summary>Changes one time of a saved group (the one being approved) and saves it, merged again.</summary>
    public void UpdateSavedSlot(long groupId, int index, DateTimeOffset start, DateTimeOffset end)
    {
        if (_savedGroups.FirstOrDefault(g => g.Id == groupId) is not { } group || index < 0 || index >= group.Slots.Count || end <= start)
        {
            return;
        }

        var slots = group.Slots.ToList();
        slots[index] = new BusyRange(start, end);
        WriteGroups("share.group.save.failed", conn => ShareGroupStore.Update(conn, group.Id, group.Title, group.Message, group.ZoneId, slots));
    }
```

- [ ] **Step 2: Draw them**

In `DayColumn.RenderSlots`, iterate `vm.GridSlots()` instead of `vm.ShareSlots`. Pooled items gain a title: change the pool tuple to `(Grid Slot, Rectangle Fill, Rectangle Edge, TextBlock Title)`, the title being

```csharp
                var label = new TextBlock { Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 4, 8, 0), IsHitTestVisible = false };
```

added as the grid's third child. For each slot: `label.Text = slot.Title ?? ""`, `label.Visibility = slot.Title is null ? Collapsed : Visible`, `label.Foreground = LeafBrushes.Accent(dark)` (check `LeafBrushes` has a HighContrast-safe text brush for accent text; if `Accent` isn't readable as text in HighContrast, use `LeafBrushes.SecondaryText(dark)` and say so). AutomationId: `slot.Title is null ? $"ShareSlot_{slot.Index}" : $"SavedSlot_{slot.GroupId}_{slot.Index}"`; Name: `slot.Title is null ? $"Time to share {range}" : $"{slot.Title}, {range}"`. Remove buttons only for `slot.Title is null`, and `_slotRemoveIndexes` stores `slot.Index`.

Saved times draw even when not sharing: `TimeGridView.OnShareChanged` already calls `column.RenderSlots()` on every `ShareChanged`, which now also fires after `ReloadGroups`.

- [ ] **Step 3: Hit testing on press and tap**

In `TimeGridView`, add:

```csharp
    // A press near a drawn time: which one and which part (null over none). The edge is the event resize strip's 6 DIP.
    private (GridSlot Slot, SlotEdge Edge)? SlotAt(DateOnly day, double minutes)
    {
        var slots = _vm.GridSlots();
        var at = DragMath.Instant(day, minutes, _vm.Zone);
        var edge = TimeSpan.FromMinutes(6 / HourHeight * 60);
        return ShareSlotHit.At([.. slots.Select(s => s.Range)], at, edge) is { } hit ? (slots[hit.Index], hit.Edge) : null;
    }
```

Add `ResizeSlot` to `DragKind`, and to `DragSession`: `public GridSlot? Slot { get; init; }` and `public bool TopEdge { get; init; }`.

At the top of `BeginCreateDrag` (after `BodyPosition`):

```csharp
        // An Edge Of A Time That Can Be Resized: drag it
        if (SlotAt(day, minutes) is { Slot.Resizable: true, Edge: not SlotEdge.Inside } hit)
        {
            _drag = new DragSession(DragKind.ResizeSlot, e.GetCurrentPoint(this).Position) { Slot = hit.Slot, TopEdge = hit.Edge == SlotEdge.Top };
            return;
        }
```

Add a public method for taps:

```csharp
    /// <summary>A click on empty time: a saved time there opens its group (not while sharing or editing).</summary>
    public bool OpenSlotAt(DateOnly day, double y)
    {
        if (_vm.IsSharing || _vm.Editing is not null || SlotAt(day, y / HourHeight * 60) is not { Slot: { Title: not null, GroupId: { } id } })
        {
            return false;
        }

        _vm.OpenGroup(id);
        return true;
    }
```

In `DayColumn`'s `Tapped` handler, call `if (_owner.OpenSlotAt(Date, e.GetPosition(this).Y)) { e.Handled = true; return; }` before setting `CursorTime`. Check that `BodyPosition`'s minutes and `y / HourHeight * 60` agree for the column (both are minutes into the day).

- [ ] **Step 4: The resize drag**

In `TargetFor`, add:

```csharp
            case DragKind.ResizeSlot:
                var (slotStart, slotEnd) = DragMath.ResizeRange(drag.Slot!.Range.Start, drag.Slot.Range.End, drag.TopEdge, pointerAt, zone);
                return (slotStart, slotEnd, false, false);
```

In `OnDragMoved`, before `ShowGhost`, draw the resized time as the plain ghost (the slot's own box stays until release):

```csharp
        if (drag.Kind == DragKind.ResizeSlot)
        {
            ShowGhost(target, duplicate: false);
            return;
        }
```

In `OnDragReleased`, before the sharing-create branch:

```csharp
        // A Time's Edge Dragged: it takes the new start or end (merged with any it now touches)
        if (drag.Kind == DragKind.ResizeSlot)
        {
            _vm.ResizeGridSlot(drag.Slot!, target.Start, target.End);
            return;
        }
```

Make sure the `Duplicate`/`MoveAsync` tail never runs for `ResizeSlot` (the early return covers it) and that `drag.Occurrence!` is not dereferenced for it (the `_holdResize` line checks `Kind: DragKind.Resize` only; fine).

- [ ] **Step 5: Resize cursor over edges**

In `DayColumn`, add a `PointerMoved` handler that swaps the cursor only when crossing in or out of an edge, mirroring `EventBlock.cs:89-100`:

```csharp
        // The Up-Down Cursor Over A Time's Edge That Can Be Resized
        PointerMoved += (_, e) =>
        {
            var y = e.GetCurrentPoint(this).Position.Y;
            var onEdge = _owner.ResizableEdgeAt(Date, y);
            if (onEdge != _onEdge)
            {
                _onEdge = onEdge;
                ProtectedCursor = onEdge ? s_resizeCursor ??= InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth) : null;
            }
        };
```

with `private bool _onEdge;`, `private static InputSystemCursor? s_resizeCursor;` and in `TimeGridView`:

```csharp
    /// <summary>True when <paramref name="y"/> on <paramref name="day"/> is on the edge of a time that can be resized.</summary>
    public bool ResizableEdgeAt(DateOnly day, double y) => SlotAt(day, y / HourHeight * 60) is { Slot.Resizable: true, Edge: not SlotEdge.Inside };
```

Also reset `_onEdge`/cursor on `PointerExited`.

- [ ] **Step 6: Build and run the share tests**

Run: build (0 warnings), `pwsh tools/sign-install.ps1`, `--filter-class "*ShareAvailabilityTests"` and `--filter-class "*DragTests"`.
Expected: PASS. Then by hand: pick a time, drag its bottom and top edges; copy; see it on the grid with "Held times"; click it; the panel says "Saved times".

- [ ] **Step 7: Stage**

```bash
git add src/LeafCalendar.App
```

Proposed message: `feat(share): saved times stay on the calendar with their title, open with a click, and resize by their edges`

---

### Task 7: Approve a time into an event

**Files:**
- Modify: `src/LeafCalendar.App/ViewModels/CalendarViewModel.People.cs`
- Modify: `src/LeafCalendar.App/ViewModels/CalendarViewModel.cs` (`SaveEditorAsync` 1399-1455, `OnEditingChanged` 1265)
- Modify: `src/LeafCalendar.App/Views/ShareSlotsPanel.cs` (`Approve` from Task 5)

**Interfaces:**
- Consumes: `BeginCreate`, `Editing` (`EventEditorViewModel` with `Title`/`GuestInput`/`AddGuest()`; check the title property's name in `EventEditorViewModel`), `_approvingGroupId`.
- Produces: `public void ApproveSlot(int index, string email)`.

- [ ] **Step 1: ApproveSlot**

```csharp
    /// <summary>
    /// Books one time of the open group: sharing stops (the editor needs the right panel), and the event editor opens on
    /// that time with the group's title and <paramref name="email"/> as a guest. The group is deleted only once the event
    /// saves; until then its times stay on the grid, resizable.
    /// </summary>
    public void ApproveSlot(int index, string email)
    {
        if (_openGroupId is not { } id || index < 0 || index >= _slots.Count || !IsAddress(email))
        {
            return;
        }

        var slot = _slots[index];
        var title = _shareTitle.Length > 0 ? _shareTitle : GenericShareTitle;
        StopSharing();
        BeginCreate(slot.Start, slot.End, isAllDay: false);
        if (Editing is not { IsNew: true } editor)
        {
            return;
        }

        _approvingGroupId = id;
        editor.Title = title;
        editor.GuestInput = email;
        editor.AddGuest();
        ShareChanged?.Invoke(this, EventArgs.Empty);
    }
```

- [ ] **Step 2: Delete the group on save; forget it on close**

In `SaveEditorAsync`, inside the `editor.Occurrence is not { } o` (create) branch, after `id = _services.Editor.Create(draft, sendUpdates);`:

```csharp
                // Approved From A Saved Group: the group's job is done
                if (_approvingGroupId is { } approved)
                {
                    _approvingGroupId = null;
                    WriteGroups("share.group.delete.failed", conn => ShareGroupStore.Delete(conn, approved));
                }
```

In `OnEditingChanged`, when the editor closes (`newValue is null`) or a different editor opens, clear it:

```csharp
        // An Approve Editor Closed Without Saving: the group stays, no longer resizable
        if (_approvingGroupId is not null && !ReferenceEquals(newValue, oldValue))
        {
            _approvingGroupId = null;
            ShareChanged?.Invoke(this, EventArgs.Empty);
        }
```

Place this at the top of `OnEditingChanged` but **after** `ApproveSlot` sets `_approvingGroupId` (it's set after `BeginCreate` returns, so the open itself doesn't clear it). Order matters: `SaveEditorAsync` clears `_approvingGroupId` before `Editing = null`.

- [ ] **Step 3: Panel calls it**

Ensure `ShareSlotsPanel.Approve` calls `vm.ApproveSlot(row.Index, _guest.Text.Trim())` (Task 5 Step 3).

- [ ] **Step 4: Meet with**

`CalendarPage.People.cs:117` adds overlay guests on every new editor in Meet with mode; that's fine (more guests). No change.

- [ ] **Step 5: Build, install, check by hand, run share tests**

Run: build (0 warnings), `pwsh tools/sign-install.ps1`, `--filter-class "*ShareAvailabilityTests"`.
By hand: open a saved group, type an email, Approve… on a time; the editor opens with title, time and guest; the group's times are still drawn and their edges drag; Save; the group is gone. Repeat with Cancel in the editor: the group stays.

- [ ] **Step 6: Stage**

```bash
git add src/LeafCalendar.App
```

Proposed message: `feat(share): approve a saved time into an event with the guest, and the group goes once it's saved`

---

### Task 8: Settings › Calendars: default share message

**Files:**
- Modify: `src/LeafCalendar.App/Views/Settings/CalendarsPage.xaml` (after the account list, inside the page `StackPanel`)
- Modify: `src/LeafCalendar.App/Views/Settings/CalendarsPage.xaml.cs`
- Test: `tests/LeafCalendar.UITests/SettingsTests.cs`

**Interfaces:**
- Consumes: `SettingsContext.Save(Func<LeafSettings, LeafSettings>)`, `LeafSettings.ShareMessage`, `AvailabilityText.DefaultMessage`, `AvailabilityText.MaxMessageLength`.
- Produces AutomationIds: `DefaultShareMessageBox`, `DefaultShareMessageReset`.

- [ ] **Step 1: Write the failing UI test**

In `SettingsTests`:

```csharp
[Fact]
public void ShareMessage_SetInCalendars_IsWhereTheNextShareStarts()
{
    using var leaf = Launch();
    var settings = leaf.OpenSettings("Calendars");
    var box = leaf.WaitInSettings("DefaultShareMessageBox").AsTextBox();
    box.Text = "Pick one: {times}";
    leaf.WaitInSettings("DefaultShareMessageReset").Focus(); // lost focus saves
    leaf.CloseSettings();

    leaf.Press(VirtualKeyShort.KEY_S);
    Assert.Equal("Pick one: {times}", leaf.WaitFor("ShareMessageBox").AsTextBox().Text);
}

[Fact]
public void ShareMessage_Reset_PutsBackLeafsMessage()
{
    using var leaf = Launch();
    leaf.OpenSettings("Calendars");
    var box = leaf.WaitInSettings("DefaultShareMessageBox").AsTextBox();
    box.Text = "Mine {times}";
    leaf.WaitInSettings("DefaultShareMessageReset").AsButton().Invoke();

    Assert.Equal(AvailabilityText.DefaultMessage.Replace("\r\n", "\r", StringComparison.Ordinal), box.Text.Replace("\r\n", "\r", StringComparison.Ordinal));
}
```

Use whatever `SettingsTests` already uses to leave Settings (grep for a close/back helper in `tests/LeafCalendar.UITests/Support/LeafApp.cs`) instead of `CloseSettings` if that name doesn't exist. Add `using LeafCalendar.Core.People;` if missing.

- [ ] **Step 2: Run to see them fail**

Install, run `--filter-class "*SettingsTests"`. Expected: the two new tests FAIL (box not found).

- [ ] **Step 3: The card**

`CalendarsPage.xaml`, after `<!-- /One Expander Per Account -->`, inside the `StackPanel`:

```xml
                <!-- Share Availability Message (where every new share starts; the share panel's changes are for that share only) -->
                <controls:SettingRow
                    Glyph="&#xE8BD;"
                    Header="Share availability message"
                    Description="{}{times} is replaced with your free times."
                    Margin="0,24,0,0" />
                <StackPanel Style="{StaticResource LeafSettingsCardBodyStyle}" Spacing="8">
                    <TextBox
                        x:Name="DefaultMessageBox"
                        AcceptsReturn="True"
                        TextWrapping="Wrap"
                        MinHeight="88"
                        MaxLength="2000"
                        PlaceholderText="Only the times"
                        LostFocus="OnDefaultMessageLostFocus"
                        AutomationProperties.Name="Share availability message"
                        AutomationProperties.AutomationId="DefaultShareMessageBox" />
                    <HyperlinkButton
                        Content="Use Leaf's message"
                        Padding="0"
                        Click="OnDefaultMessageReset"
                        AutomationProperties.AutomationId="DefaultShareMessageReset" />
                </StackPanel>
```

Check `LeafTheme.xaml`/`Styles` for an existing style for a card with content below its header (an expander's body or a `SettingRow` variant). If none fits, put the box and link inside a `SettingRow` whose content is the full-width stack (look at how other pages place a full-width control) rather than inventing a style. `MaxLength` must equal `AvailabilityText.MaxMessageLength` (2000); add a XAML comment saying so. `XamlLintTests` will flag spacing/radius/fixed heights: run them.

`CalendarsPage.xaml.cs`:

```csharp
    // The default message as saved (the box shows \r line breaks, the setting keeps \r\n)
    private void ShowDefaultMessage() => DefaultMessageBox.Text = _context.Calendar.Settings.ShareMessage.Replace("\r\n", "\r", StringComparison.Ordinal);

    private void OnDefaultMessageLostFocus(object sender, RoutedEventArgs e) =>
        _context.Save(s => s with { ShareMessage = DefaultMessageBox.Text.Replace("\r", "\r\n", StringComparison.Ordinal) });

    private void OnDefaultMessageReset(object sender, RoutedEventArgs e)
    {
        _context.Save(s => s with { ShareMessage = AvailabilityText.DefaultMessage });
        ShowDefaultMessage();
    }
```

Call `ShowDefaultMessage()` in `OnNavigatedTo`. Check how the share panel stores line breaks (`ShareSlotsPanel` saves `_message.Text` as typed, with `\r`): match it so `Compose` and the tests agree. Read `AvailabilityText.Compose` to see which line breaks it expects, and save the same form both places.

- [ ] **Step 4: Build, lint, install, run**

Run: build (0 warnings), `--filter-class "*XamlLintTests"` (logic suite), `pwsh tools/sign-install.ps1`, `--filter-class "*SettingsTests"`.
Expected: PASS.

- [ ] **Step 5: Stage**

```bash
git add src/LeafCalendar.App tests/LeafCalendar.UITests/SettingsTests.cs
```

Proposed message: `feat(settings): the default share availability message is set in Settings › Calendars`

---

### Task 9: End-to-end UI tests, spec update, AOT, full suite, install

**Files:**
- Create: `tests/LeafCalendar.UITests/SavedShareGroupTests.cs`
- Modify: `docs/superpowers/specs/2026-09-29-leaf-calendar-design.md` (section 7.6)

**Interfaces:**
- Consumes: `ShareAvailabilityTests.DragHours`, `ShareAvailabilityTests.StartSharing` (both `internal static`), AutomationIds from Tasks 5-8.

- [ ] **Step 1: Write the UI tests**

```csharp
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.Core.Settings;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class SavedShareGroupTests : IDisposable
{
    private readonly FakeGoogleServer _google = new();
    private readonly string _profile = SeededProfile.Create(new LeafSettings { PrimaryTimeZone = "America/New_York" });

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    private LeafApp Launch(string extra = "") => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01 {extra}");

    // S, a 2-3 PM pick titled Coffee, then Copy
    private static void SaveCoffee(LeafApp leaf)
    {
        ShareAvailabilityTests.StartSharing(leaf);
        ShareAvailabilityTests.DragHours(leaf, 14, 15);
        leaf.WaitFor("ShareTitleBox").AsTextBox().Text = "Coffee";
        leaf.WaitFor("ShareCopyButton").AsButton().Invoke();
        Assert.True(Retry.WhileFalse(() => leaf.Exists("SavedSlot_1_0"), TimeSpan.FromSeconds(10)).Success);
    }

    [Fact]
    public void Copy_SavesTheGroup_AndItIsThereAfterARestart()
    {
        using (var leaf = Launch())
        {
            SaveCoffee(leaf);
            Assert.Contains("Coffee", leaf.WaitFor("SavedSlot_1_0").Name, StringComparison.Ordinal);
        }

        using var again = Launch("--restarted");
        Assert.Contains("Coffee", again.WaitFor("SavedSlot_1_0").Name, StringComparison.Ordinal);
    }

    [Fact]
    public void Click_OpensTheGroup()
    {
        using var leaf = Launch();
        SaveCoffee(leaf);

        leaf.WaitFor("SavedSlot_1_0").Click();

        Assert.Equal("Saved times", leaf.WaitFor("SharePanelTitle").Name);
        Assert.Equal("Coffee", leaf.WaitFor("ShareTitleBox").AsTextBox().Text);
    }

    [Fact]
    public void EdgeDrag_ResizesAPickedTime()
    {
        using var leaf = Launch();
        ShareAvailabilityTests.StartSharing(leaf);
        ShareAvailabilityTests.DragHours(leaf, 14, 15);
        var slot = leaf.WaitFor("ShareSlot_0").BoundingRectangle;
        var hour = slot.Height + 2;

        LeafApp.Drag(new System.Drawing.Point(slot.Left + 20, slot.Bottom - 1), new System.Drawing.Point(slot.Left + 20, slot.Bottom - 1 + hour));

        Assert.True(Retry.WhileFalse(() => leaf.WaitFor("ShareSlot_0").BoundingRectangle.Height > hour * 1.5, TimeSpan.FromSeconds(5)).Success);
    }

    [Fact]
    public void Approve_WithAnEmail_OpensTheEditorFilledIn_AndSaveRemovesTheGroup()
    {
        using var leaf = Launch();
        SaveCoffee(leaf);
        leaf.WaitFor("SavedSlot_1_0").Click();

        Assert.False(leaf.WaitFor("SharePanelApprove_0").IsEnabled);
        leaf.WaitFor("ShareGuestBox").AsTextBox().Text = "pat@example.com";
        leaf.WaitFor("SharePanelApprove_0").AsButton().Invoke();

        // The editor's title box and guest chip: check EventEditorView.xaml for their AutomationIds and use them here
        Assert.Equal("Coffee", leaf.WaitFor("EditorTitleBox").AsTextBox().Text);
        Assert.True(leaf.Exists("SavedSlot_1_0"));
        leaf.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.RETURN);

        Assert.True(Retry.WhileTrue(() => leaf.Exists("SavedSlot_1_0"), TimeSpan.FromSeconds(10)).Success);
    }

    [Fact]
    public void Approve_CancelEditor_KeepsGroup()
    {
        using var leaf = Launch();
        SaveCoffee(leaf);
        leaf.WaitFor("SavedSlot_1_0").Click();
        leaf.WaitFor("ShareGuestBox").AsTextBox().Text = "pat@example.com";
        leaf.WaitFor("SharePanelApprove_0").AsButton().Invoke();
        leaf.WaitFor("EditorTitleBox");

        leaf.Press(VirtualKeyShort.ESCAPE);

        Assert.True(Retry.WhileFalse(() => leaf.Exists("SavedSlot_1_0") && !leaf.Exists("EditorTitleBox"), TimeSpan.FromSeconds(5)).Success);
    }

    [Fact]
    public void Delete_RemovesTheGroup()
    {
        using var leaf = Launch();
        SaveCoffee(leaf);
        leaf.WaitFor("SavedSlot_1_0").Click();

        leaf.WaitFor("ShareDeleteButton").AsButton().Invoke();

        Assert.True(Retry.WhileTrue(() => leaf.Exists("SavedSlot_1_0"), TimeSpan.FromSeconds(5)).Success);
    }

    [Fact]
    public void PanelMessageEdit_IsForThatShareOnly()
    {
        using var leaf = Launch();
        ShareAvailabilityTests.StartSharing(leaf);
        ShareAvailabilityTests.DragHours(leaf, 14, 15);
        leaf.WaitFor("ShareMessageBox").AsTextBox().Text = "Just this once {times}";
        leaf.WaitFor("ShareCopyButton").AsButton().Invoke();
        Assert.True(Retry.WhileFalse(() => leaf.Exists("SavedSlot_1_0"), TimeSpan.FromSeconds(10)).Success);

        // The saved group keeps it; a new share starts from the default
        leaf.WaitFor("SavedSlot_1_0").Click();
        Assert.Equal("Just this once {times}", leaf.WaitFor("ShareMessageBox").AsTextBox().Text);
        leaf.WaitFor("ShareCancelButton").AsButton().Invoke();
        ShareAvailabilityTests.StartSharing(leaf);
        Assert.StartsWith("Here are some times", leaf.WaitFor("ShareMessageBox").AsTextBox().Text, StringComparison.Ordinal);
    }
}
```

Fix ids against reality before running: the first group's id is `1` in a fresh profile (AUTOINCREMENT); the editor's title AutomationId comes from `EventEditorView.xaml` (grep `AutomationId` there); Ctrl+Enter saves the editor (commit `5fe55c7`).

- [ ] **Step 2: Run them**

Run: `pwsh tools/sign-install.ps1`, then `--filter-class "*SavedShareGroupTests"`.
Expected: all PASS. Fix the product (not the test) when a test shows a real bug.

- [ ] **Step 3: Update the main spec**

In `2026-09-29-leaf-calendar-design.md` section 7.6, replace the panel bullet and add bullets so it reads, after the existing first three bullets:

```markdown
- The share controls sit in the right panel: a title, the zone, the message the times are wrapped in (it starts from the default set in Settings › Calendars; a change here is for this share only), then the picked times, where each one's start and end can be changed or removed, with Copy and Cancel pinned at the bottom. On the grid, a picked time's top and bottom edges drag to resize it. A hint at the bottom center of the calendar says to mark available times. Copy copies the text, stops sharing, and shows "Availability copied" in the notice. Esc stops sharing.
- Copy also saves the free times as a group (title, message, zone; see `2026-10-06-saved-share-groups-design.md`). Saved groups stay on the calendar as dashed outlines with their title ("Held times" with none) until a time is approved, the group is deleted, or its times pass. Clicking one opens it in the panel ("Saved times"): its changes save at once, Copy copies it again, Delete deletes it. With a guest email, Approve… opens the event editor on that time with the title and the guest; the group is deleted once the event saves.
```

- [ ] **Step 4: AOT check**

Run: `pwsh tools/publish-aot.ps1` and `pwsh tools/check-package.ps1`.
Expected: no trim/AOT warnings; package under budget.

- [ ] **Step 5: Full verification**

Run: full logic suite; `pwsh tools/sign-install.ps1` (Release AOT, installed as an update); then the **full** UI suite once: `dotnet test --project tests/LeafCalendar.UITests/LeafCalendar.UITests.csproj -c Debug`.
Expected: all PASS. If a whole block fails at once, re-run before diagnosing (CLAUDE.md: the owner's running Leaf can cause mass false failures).

- [ ] **Step 6: Screenshots**

Run `ScreenshotTour` with `LEAF_SCREENSHOTS` set; review the share panel (new picks and saved group), saved times on the grid in light and dark, and Settings › Calendars.

- [ ] **Step 7: Stage and report**

```bash
git add tests/LeafCalendar.UITests/SavedShareGroupTests.cs docs/superpowers/specs/2026-09-29-leaf-calendar-design.md
```

Report to the owner: tests run with real results, what was checked only by hand, every new string (including the unapproved `Couldn't save these times.` from Task 4 and the `PRIVACY.md` paragraph), and decisions made. Commit and merge only on the owner's word.
