# Saved Share Groups: Design

Owner request, 2026-10-06. Extends spec section 7.6 (Share Availability). When built, section 7.6 of `2026-09-29-leaf-calendar-design.md` is updated to match.

## 1. Goal

You share some times with someone, they write back with the one they want, and you book it. Today the picked times vanish once you copy them, so you have to remember what you offered. After this change:

1. Copied times are **saved as a group** and stay on the calendar until you book one, delete the group, or they pass.
2. Picked and saved times can be **resized by dragging** their top or bottom edge on the grid.
3. You **approve** one time from a group by giving the guest's email; Leaf opens the event editor filled in, and the group goes away once the event is saved.
4. **Esc** leaves scheduling wherever focus is.
5. The **default message** the times are wrapped in is set in Settings › Calendars.

## 2. Decisions (owner, 2026-10-06)

| Topic | Decision |
| --- | --- |
| When a group is saved | On Copy. Cancel still throws the picks away. |
| What a group keeps | The free times that went into the copied text (busy parts already cut), its title, its message, and the zone the text was written in. |
| Default message | Set in Settings › Calendars. Each new share starts from it. Edits in the share panel apply to that share (and its saved group) only. |
| Title | A new Title box in the share panel. Empty means the generic title. Shown on every saved time; pre-fills the event title on approve. |
| Opening a group | Click one of its times on the grid. |
| Approve | Pick a time in the group, give the guest's email; the event editor opens with that time, the guest and the title, editable. |
| After approve | The whole group is deleted, only once the event saves. Canceling the editor keeps it. |
| Past times | A time that has ended is dropped; a group with no times left is deleted. |
| Look | Dashed accent edge, faint accent fill, the title inside; behind events. |
| Resizing | Top and bottom edges, while picking, while a saved group is open, and while the approve editor is open. |
| Esc | Leaves scheduling (new picks or an open saved group) unless a dropdown, picker or suggestion list is open, which Esc closes first. |

## 3. Data

Migration 9 in `Data/Schema.cs` (`PRAGMA user_version` 8 → 9):

```sql
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
```

- `title` holds what you typed, trimmed and capped (100 characters, `DisplayText.Clean`). Empty is stored as empty; the generic title is applied only for display, so a later wording change applies to old groups too.
- `message` is the share message as it was when copied (capped at `AvailabilityText.MaxMessageLength`), so copying the group again uses the same text.
- Times are stored in UTC, like events.
- Groups are local only, not tied to an account, never sent anywhere. `PRIVACY.md` gets a line saying titles, messages and times of saved groups stay on this PC and go when the group is approved, deleted or passes.

`Core/Data/ShareGroupStore.cs` (with the other stores), a static store like the others, taking a connection and an optional transaction:

- `GetAll(conn, now)`: every group with its times, merged and in order; times ending at or before `now` left out.
- `Insert(conn, title, message, zoneId, slots, now)` → new id.
- `Update(conn, id, title, message, zoneId, slots)`: replaces the title, message, zone and times in one transaction. No times left deletes the group.
- `Delete(conn, id)`.
- `Prune(conn, now)`: deletes ended times, then groups with none left. Run at startup, and on the view model's minute tick when a shown time has ended.

In the same file (as `LedgerEntry` sits in `AlertLedger.cs`): `record ShareGroup(long Id, string Title, string Message, string ZoneId, IReadOnlyList<BusyRange> Slots)`.

## 4. View Model (`CalendarViewModel.People.cs`)

Sharing gains a "which group" field:

- `_openGroupId` (`long?`): null while picking new times, else the saved group the panel shows.
- `SavedGroups` (`IReadOnlyList<ShareGroup>`): loaded off the UI thread at start (generation counter, as the tray reads do) and after each change. `ShareChanged` fires when it changes.
- `ShareTitle` (string): the panel's Title box.
- `ShareText` (string): this share's message. `StartSharing` sets it from `Settings.ShareMessage` (the default); the panel's edits change only `ShareText`, never the setting. `SetShareMessage` is replaced by this property, and `CopyAvailabilityAsync` composes from `ShareText`.
- Settings › Calendars' box saves `Settings.ShareMessage` through `SettingsContext.Save` (an empty box saves empty, meaning only the times, as today).
- `OpenGroup(long id)`: starts sharing on that group (its times, title, message and zone, the shareable calendars as they are now). If already sharing new picks, those are dropped first, like Cancel.
- `AddShareSlot`, `UpdateShareSlot`, `RemoveShareSlot` unchanged for new picks. With a group open, each change also writes the group (changes save at once). Removing a group's last time deletes it and stops sharing.
- `CopyAvailabilityAsync`: on success, with no group open it inserts a new group from the free times; with one open it updates that group to the free times. Either way sharing stops as today. A database failure is logged and shown ("Copied, but couldn't save these times."); the copy still counts.
- `DeleteOpenGroup()`: deletes the group and stops sharing.
- `ApproveSlot(int index, string email)`: needs a valid address (`IsAddress`). Remembers `_approvingGroupId`, stops sharing (the editor needs the right pane the share panel was using), then calls `BeginCreate(slot.Start, slot.End)`, sets the title to the group's title (or the generic one) and adds the guest. The group's times stay drawn, and stay resizable while the editor is open (`UpdateSavedSlot`).
- `SaveEditorAsync`: after a successful create while `_approving` is set, deletes that group and clears `_approving`. If the editor closes any other way, `_approving` clears and the group stays.

Saved groups' times are drawn even when not sharing. The faded, lined look of events stays only while sharing.

## 5. Share Panel (`ShareSlotsPanel.cs`)

- **Title** box at the top (above the zone). It saves on lost focus and on Copy.
- The Message box starts from the default (or the open group's message). Its "Use the default message" link now puts back your Settings default, not the built-in text. With a group open, a message change saves to the group.
- Heading: "Times to share" for new picks, "Saved times" for an open group.
- **Guest email** box, shown only for an open group. Each time row gets an **Approve…** button, enabled while the box holds one valid address.
- Footer: Copy and Cancel as today for new picks. For an open group: Copy, Close, and a **Delete** button.

## 6. Time Grid (`TimeGridView.cs`, `DayColumn.cs`)

**Drawing.** `DayColumn.RenderSlots` draws the open picks (as today) plus every saved group's times except the open group's (which are the open picks). Saved times show their group's title in caption text at the top left, trimmed with an ellipsis. Open picks keep their remove button.

**Hit testing.** The slots layer stays click-through. On a press, the grid checks the pointer against the slots on that day (the same place the event cards are checked):

- Within 6 DIP of a slot's top or bottom edge (the event resize grip's size): a new `DragKind.ResizeSlot` drag, holding which edge, which group (or the open picks) and which slot.
- A click within a saved time (`OpenSlotAt`), while not sharing or editing, opens that group; a click in the edge strip just outside the time is on empty time. A drag inside a saved time, away from its edges, starts a normal new-event drag, as on empty time.
- Inside an open slot (not an edge) while sharing: a drag picks a new time, as today.

The pointer shows the up-down resize cursor over slot edges.

**Resizing.** `DragMath` gets `ResizeRange(start, end, topEdge, pointer, zone)`: the dragged edge snaps to `SnapMinutes` (15) and the slot keeps at least 15 minutes (the existing `ResizeEnd` takes an event, so it stays as is). During the drag the plain drag ghost shows the new size, and the time's box takes it on release. On release the view model gets `UpdateShareSlot` (open picks or an open group) or `UpdateSavedSlot(groupId, index, start, end)` (a saved group shown under the approve editor), which merges and saves.

Resizing a saved group's time while no group is open is not offered (click it to open it first). This keeps a stray drag from changing what you offered someone.

## 7. Esc

`CalendarPage.OnEscapeInvoked` already stops sharing, but in the owner's use Esc doesn't leave scheduling. The plan's first task reproduces this with a UI test (Esc with focus on the grid, the Title, Message and time pickers) and finds what eats the key, then fixes it at that layer. Expected: Esc closes an open dropdown, picker or suggestion list first; otherwise it ends scheduling, the same as Cancel or Close. With the approve editor open, Esc closes the editor (as today) and the group stays saved.

## 8. Settings › Calendars: Default Message

A new card on the Calendars page, following the Windows 11 Settings pattern (one setting per card, saves on change):

- Card header "Share availability message", with the description "{times} is replaced with your free times."
- A multi-line `TextBox` under the header (full width, since a message doesn't fit on the right), `MaxLength` `AvailabilityText.MaxMessageLength`, placeholder "Only the times" (as in the panel). Saves on lost focus.
- A "Use Leaf's message" link that puts back `AvailabilityText.DefaultMessage` and saves.
- The setting is the existing `LeafSettings.ShareMessage` (no new setting, no migration; owners who already changed it in the panel keep their text as the default).
- AutomationIds: `DefaultShareMessageBox`, `DefaultShareMessageReset`.

## 9. Wording (owner-approved 2026-10-06)

| Where | Text |
| --- | --- |
| Title box header | Title |
| Generic group title | Held times |
| Guest box header | Guest email |
| Guest box placeholder | name@example.com |
| Row button | Approve… |
| Row button tooltip | Save this time as an event with the guest |
| Delete button | Delete |
| Open-group heading | Saved times |
| Open-group close button | Close |

New wording not yet approved; it must be approved before shipping:

- Notice when the copy worked but saving failed: "Copied, but couldn't save these times."
- Grid slot automation name: "{title}, {time range}". Approve automation name: "Approve {time range}".
- Settings card header "Share availability message"; its reset link "Use Leaf's message" (the panel's link keeps "Use the default message", which now means your Settings default).

## 10. Testing

Logic tests (`tests/LeafCalendar.Tests`), written first:

- `ShareGroupStoreTests`: insert/read round trip, update replaces times, last time removed deletes the group, delete cascades, `GetAll` hides ended times, `Prune` drops ended times and empty groups, title clean and cap.
- Migration tests: `user_version` is 9; a version-8 database upgrades with its data intact.
- `DragMathTests`: `ResizeRange` snaps either edge, keeps at least 15 minutes, crosses DST days and midnight correctly.

UI tests (`tests/LeafCalendar.UITests`), affected classes only:

- `ShareAvailabilityTests` (existing) still pass.
- New `SavedShareGroupTests`: Copy saves a group and it shows after a restart (`--restarted`); click opens it; edge drag resizes a picked time and a saved one; Approve with an email opens the editor filled in, and Save removes the group; Cancel in the editor keeps it; Delete removes it; Esc leaves scheduling with focus on the grid and on each panel box; a panel message edit is used for that copy and saved with the group, but the next new share starts from the default again.
- `SettingsTests` (existing class): the default message box saves and the next share starts from it; the reset link puts back Leaf's message.

AOT check (`publish-aot.ps1`) after the store and view model land, since new records and SQLite reads are involved.

## 11. Out of Scope

- Sending invitations or email from the group (the editor's Save already emails the guest when you choose to).
- Moving a slot by dragging its middle (only edges resize).
- Groups syncing between PCs.
- A sidebar list of groups.
