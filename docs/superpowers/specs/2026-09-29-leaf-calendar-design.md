# Leaf Calendar Design

A native Windows 11 calendar app for Google Calendar, modeled on Notion Calendar, built with WinUI 3 and .NET 10.

- **Date:** 2026-09-29
- **Status:** Draft for review
- **Owner:** Devin Green (Artistro08)

---

## 1. Purpose and Scope

### 1.1 Intended Outcome

Leaf Calendar is a fast, low-memory, native Windows 11 desktop calendar with feature parity with Notion Calendar, minus AI and Notion workspace features. It lives in the system tray, reminds you of meetings with real Windows notifications, and joins the next meeting with one global keyboard shortcut using the right Google account.

### 1.2 Audience

- Personal use first (the owner's daily driver).
- Microsoft Store submission later, after extensive testing.
- Source lives in a private GitHub repository.

### 1.3 Success Criteria

1. Every in-scope feature in this spec works against real Google accounts, and has logic tests, a UI test, and (where Google is involved) a live Google test.
2. Offline editing works: edits made offline sync correctly when back online, and conflicts are caught and resolved by the user.
3. The join shortcut opens the correct meeting with the correct Google account.
4. The Release build passes the memory budget test (budget set in Milestone 1).
5. The app looks like a first-party Windows 11 app throughout.

### 1.4 Non-Goals

- AI features of any kind.
- Note taking, Notion pages, Notion databases, Notion workspace integration.
- Calendar providers other than Google (no Outlook, no iCloud).
- Leaf-hosted booking pages, invitee reschedule/cancel links, or any feature requiring a Leaf server.
- Mobile apps and widgets.
- Cross-account busy blocking (deferred by decision).
- Zoom auto-created meeting links (requires Zoom sign-in; users paste Zoom links instead).
- "Propose a new time" (not supported by the Google Calendar API).
- A "pause notifications" feature (Windows Do Not Disturb handles it).

### 1.5 Deferred (Not in This Spec, Candidates Later)

- ICS file import/export.
- `leafcal://` deep links to open a specific event.
- Localization beyond English (all strings go in resource files so this stays possible).
- Instant updates through a self-hosted relay (sync code keeps a hook for it).
- Splitting the tray/sync work into a separate lightweight helper process.

---

## 2. Decision Log

| Topic | Decision |
|---|---|
| Visual direction | Windows 11 native. Mica on the main window, acrylic on the tray flyout. |
| Title bar | XAML `TitleBar` control, tall height, caption buttons match the height. |
| Offline | Full offline editing with an outbox. |
| Conflicts | Side-by-side dialog, user picks "Keep mine" or "Keep Google's" each time. |
| Reminders | Regular toast at Google's default reminder times, then a persistent "Join now" toast at start time. |
| Notifications | All notifications are real Windows notifications. |
| Join shortcut target | Soonest-starting qualifying meeting within 10 minutes, else the one in progress. |
| Join account routing | Append `authuser=<email>` to Google Meet links, open in default browser. |
| Tests | Logic tests, FlaUI UI tests, and live Google tests, written alongside each feature. |
| Process model | One process, Core logic kept UI-free so a helper split stays possible. |
| Compilation | Native AOT on Release builds only. |
| Google access | Plain `HttpClient` against the REST API. No Google client libraries. |
| Sync | Smart polling. No push (Google push needs a public HTTPS server). |
| Local DB encryption | None beyond Windows user profile protection. Tokens are encrypted separately. |
| Share availability | Google free/busy based, copied as text. Plus a button that opens Google's appointment schedule page. |
| Search | No search box. Search goes through the command menu. |
| Tray menu | XAML menu, placement follows taskbar position. |

---

## 3. Architecture and Tech Stack

### 3.1 Solution Layout

| Project | Purpose |
|---|---|
| `LeafCalendar.Core` | All non-UI logic: OAuth, Google REST client, SQLite store, sync engine, outbox, conflict detection, recurrence expansion, reminder scheduler, join picker, time zones, availability text. No WinUI references. |
| `LeafCalendar.App` | WinUI 3 app: main window, views, tray icon, flyout, XAML tray menu, toasts, global hotkeys, settings. Screens and view models only; logic comes from Core. |
| `LeafCalendar.Tests` | Logic tests for Core with a fake Google and a fake clock. |
| `LeafCalendar.UITests` | FlaUI tests that launch the packaged app and drive it. |
| `LeafCalendar.LiveTests` | Tests against a real throwaway Google account. Run on demand only. |

### 3.2 Stack

- .NET 10, C# 14.
- Windows App SDK 2.5.x (latest stable at spec time), WinUI 3.
- Packaged MSIX. Required: unpackaged builds currently drop `.xbf`/`.pri` files under AOT (WindowsAppSDK#6394) and lose toast activation (WindowsAppSDK#6774).
- Native AOT on Release only:
  ```xml
  <PublishAot Condition="'$(Configuration)' == 'Release'">true</PublishAot>
  <IsAotCompatible>true</IsAotCompatible>
  ```
  Debug builds use JIT because AOT in Debug deadlocks the GC on .NET 10 (dotnet/runtime#121538, fixed only in .NET 11).
- JSON: `System.Text.Json` source generation only. Never call overloads without `JsonTypeInfo`.
- Local storage: `Microsoft.Data.Sqlite` with plain parameterized SQL. No Entity Framework.
- MVVM: `CommunityToolkit.Mvvm` 8.4.x, partial-property style only (field style triggers MVVMTK0045 under AOT). Bound types that use `{Binding}` carry `[GeneratedBindableCustomProperty]`; prefer `x:Bind`.
- Win32 interop (tray icon, hotkeys, taskbar position, working-set trim): `Microsoft.Windows.CsWin32` with `"allowMarshaling": false`.
- Notifications: `Microsoft.Windows.AppNotifications.AppNotificationManager`.
- Recurrence: `Meziantou.Framework.Scheduling` (MIT), pinned to an exact version (4.1.3 at spec time), for RRULE expansion. Leaf adds a small helper for Google's `EXDATE`/`RDATE` lines and date-window queries (built in Milestone 1).
  - Verified 2026-09-29: 0 AOT warnings; correct across DST, `BYSETPOS`, ordinal `BYDAY`, `UNTIL` in UTC, `BYMONTHDAY=31`, and the RFC `WKST` examples.
  - `Ical.Net` 5.2.3 was rejected: plain AOT publish crashes at runtime (`MissingMethodException` on `RecurrencePattern`), and rooting the assembly still leaves 11 IL trim warnings.
  - `EWSoftware.PDI` was rejected: it mishandles `UNTIL` in UTC, using the machine's time zone, and its parser adds 6 AOT warnings.
- Tests: xUnit v3, FlaUI (UIA3).

### 3.3 AOT Evidence (Spike, 2026-09-29)

- WinUI 3 on Windows App SDK 2.5.1 + net10.0 published AOT with 0 IL warnings and ran.
- `Microsoft.Data.Sqlite` 10.0.x: 0 warnings, ran correctly.
- `System.Text.Json` source generation: 0 warnings.
- CsWin32 tray icon calls: 0 warnings, `NIM_ADD`/`NIM_DELETE` succeeded.
- Google's official .NET client: 54 warnings and a runtime crash. Rejected.

### 3.4 Memory Plan

Measured on a minimal WinUI 3 window: AOT ~53 MB private working set (Task Manager "Memory"), ~72 MB private bytes. Closing the window frees almost nothing once WinUI is loaded.

1. Native AOT on Release.
2. When the app goes to tray-only, trim the working set (`SetProcessWorkingSetSize(-1, -1)`). Measured drop from ~55 MB to ~8 MB in Task Manager. Pages return from the page file when the window reopens.
3. Windows efficiency mode (EcoQoS) while idle in the tray, for CPU and battery.
4. Event data kept in a bounded sliding window (Section 6.4). Event visuals recycled.
5. No WebView2. Event descriptions render with native text controls.
6. Avatars and images decoded at display size (`DecodePixelWidth`).
7. An automated memory budget test on the Release package. Budget set 2026-09-29: private bytes ≤ 95 MB, working set ≤ 25 MB (volatile after trim), tray-only, AOT, x64. Measured with no OAuth client configured, so no sync loop was running. Re-measure with the fake-Google test mode in Milestone 2.

---

## 4. Accounts, Sign-In, and Security

### 4.1 First-Run Setup

1. Leaf asks for a Google OAuth **Client ID** and **Client Secret** of type "Desktop app". An in-app guide explains how to create them in Google Cloud Console and which APIs to enable (Google Calendar API, People API).
2. "Add Google account" opens the system browser to Google's consent page.
3. Leaf receives the authorization code on a loopback listener and exchanges it for tokens.
4. More accounts are added the same way. No account limit.

### 4.2 OAuth Flow

- Authorization Code flow with **PKCE** (`S256`), a random `state` value, and a loopback redirect `http://127.0.0.1:<random port>/`.
- The listener binds `127.0.0.1` only, accepts exactly one request, validates `state`, then closes. It times out after 5 minutes.
- The browser gets a simple "You can close this tab" page. No tokens are ever shown there.
- Scopes:
  - `openid`, `email`, `profile`
  - `https://www.googleapis.com/auth/calendar`
  - `https://www.googleapis.com/auth/contacts.readonly`
  - `https://www.googleapis.com/auth/contacts.other.readonly`
  - `https://www.googleapis.com/auth/directory.readonly`
- Access tokens are refreshed silently. A failed refresh (revoked or expired grant) marks the account "needs sign-in" and shows a banner plus a notification. The outbox is kept.

### 4.3 Secret Storage

- Client ID, client secret, and each account's refresh token are stored in the **Windows Credential Locker** (`PasswordVault`), scoped to the current Windows user. Leaf is a full-trust app, so the vault is user-wide, not per-package.
- Access tokens live in memory only.
- Disconnecting an account calls Google's revoke endpoint, removes its Credential Locker entry, and deletes its local data. If it has pending outbox changes, Leaf warns first.

### 4.4 Local Data

- SQLite database in the package's `LocalFolder`. Only the current Windows user can read it.
- No database-level encryption (decision). BitLocker is recommended in the setup guide.

### 4.5 Untrusted Content

Anyone can send an invite, so event content is treated as hostile.

- **Launching links:** only `https` URLs and an allowlist of meeting app schemes (`zoommtg`, `zoomus`, `msteams`, `webex`) can be launched. Anything else (`file`, `ms-msdt`, `javascript`, custom schemes) is blocked.
- **Descriptions:** Google's HTML subset is parsed into native text runs (bold, italic, underline, lists, line breaks, links). No scripts, no remote images, no embedded browser. Only `https` and `mailto` links are clickable.
- **Meeting link detection:** URL parsing through `Uri`, host matched against a known list (Meet, Zoom, Teams, Webex, Around, Whereby, BlueJeans, Doxy.me), never by substring.

### 4.6 Logging

- Rolling local log in `LocalFolder\Logs`, capped in size.
- Never logs tokens, secrets, authorization codes, event titles, descriptions, guest emails, or locations. Event references use internal IDs only.

### 4.7 Transport and Packaging

- All network traffic is HTTPS with default certificate validation. No custom validation callbacks.
- The MSIX package is signed.

### 4.8 Threat Model

| Threat | Defense |
|---|---|
| Stolen refresh token from disk | Stored in Credential Locker, encrypted to the Windows user. Never in SQLite, logs, or settings files. |
| Intercepted OAuth redirect | PKCE, `state` check, loopback bound to `127.0.0.1`, one request, 5-minute timeout. |
| Malicious invite link launches a local program | Scheme allowlist for launching. |
| Malicious invite description (script, tracking image) | Native text rendering, no scripts, no remote images. |
| Look-alike meeting link host | Host matching through parsed `Uri`, exact or suffix match on registered domains. |
| Sensitive data in logs | Redaction rules plus a test that scans logs produced by a full test run. |
| Vulnerable dependency | Vulnerable package check on every build. |
| Other local users reading data | Package `LocalFolder` and Credential Locker are per user. |

---

## 5. Data, Sync, and Offline Editing

### 5.1 Local Schema (Tables)

| Table | Contents |
|---|---|
| `accounts` | Account ID, email, display name, avatar URL, status (ok / needs sign-in), settings timezone. |
| `calendars` | Calendar ID, account ID, summary, summary override, color, access role, hidden, order, default reminders, sync token. |
| `events` | Event ID, calendar ID, iCalUID, etag, status, start/end (UTC plus original zone), all-day flag, recurrence rules, recurring event ID, original start, updated time, raw JSON. Indexed by calendar and time range. |
| `outbox` | Sequence, account ID, operation (create / patch / delete / move / RSVP), target event, payload JSON, base etag, send-updates choice, attempt count, last error. |
| `conflicts` | Outbox entry, local version JSON, Google version JSON, detected time. |
| `settings` | Key/value app settings. |

### 5.2 Initial Sync

- Per calendar: `events.list` with `singleEvents=false`, `showDeleted=true`, paging until the final page returns a `nextSyncToken`.
- Recurring events are stored as masters plus exceptions and expanded locally.

### 5.3 Incremental Sync (Smart Polling)

- Per calendar: `events.list` with the stored `syncToken`. Only changes come back.
- Cadence:
  - Every 15 seconds while the main window or flyout is visible.
  - Every 60 seconds while in the tray.
  - Immediately on window/flyout open, resume from sleep, network reconnect, and one minute before each reminder fires.
- `410 Gone` (token expired): full resync of that calendar. The outbox is untouched.
- The calendar list, colors, and settings sync on startup and every 15 minutes.
- The sync engine exposes a "changes available" trigger so a push relay can be added later without rework.

### 5.4 Offline Editing (Outbox)

1. Edits apply to the local database and the UI immediately.
2. Each edit adds an outbox entry that records the base etag and the "email guests" choice.
3. When online, entries are sent in order per account.
4. Patches and deletes send `If-Match: <base etag>`. A `412 Precondition Failed` becomes a conflict.
5. New events use a client-generated ID (base32hex, per Google's rules), so retrying a create after a dropped connection can't produce duplicates.
6. Google Meet links requested offline are sent as `conferenceData.createRequest` when online. The link appears after Google creates it.
7. Rate limits (`429`, `403 rateLimitExceeded`) and `5xx` retry with exponential backoff and jitter.

### 5.5 Conflicts

- Triggered by a `412` on patch or delete, or by a local edit to an event that Google deleted (and the reverse).
- Leaf shows a Windows notification ("1 change needs your review") and a badge in the app.
- The dialog shows both versions side by side, with differing fields highlighted. The choices are **Keep mine** (re-send with Google's current etag) and **Keep Google's** (drop the local change).
- Only the conflicted event waits. The rest of the outbox keeps sending.

### 5.6 In-Memory Event Window

- **Data layer:** events are kept in memory for 3 months before and 3 months after the visible range. Leaf prefetches in the scroll direction from SQLite on a background thread and releases data beyond that window.
- **Visual layer:** event blocks are built for the visible range plus one full screen on each side, and recycled as they scroll off.

---

## 6. Main Window and Views

### 6.1 Window

- Mica backdrop. Theme follows the system, or is forced Light or Dark.
- XAML `TitleBar` control with `AppWindow.TitleBar.PreferredHeightOption = Tall`, so the caption buttons match the 48 px title bar height.

### 6.2 Title Bar (Left to Right)

1. Navigation icon (toggles the sidebar), top-left corner.
2. Back button, visible only when there is somewhere to go back to (Settings pages, an opened command menu result).
3. Leaf icon and "Leaf Calendar".
4. Right side: Today button, previous/next pagers, view picker (Day / Week / Month / X days), search icon (opens the command menu), then the caption buttons.

### 6.3 Sidebar (Collapsible)

- Mini month calendar with today highlighted. Clicking a date jumps to it.
- Accounts, each listing its calendars:
  - Show/hide (eye icon)
  - Color picker
  - Drag to reorder
  - Right-click: rename (Google summary override), change color, show upcoming events for this calendar
- Buttons: "Share availability" and "Manage Google booking pages" (opens Google Calendar's appointment schedule page in the browser).
- Subscribed calendars appear automatically from the Google calendar list.

### 6.4 Calendar Area

- **Views:** Day, Week, Month, and custom 1 to 31 days.
- **Time zones:** multiple zone columns on the left edge. Add, rename, and drag to reorder. Search zones by city or abbreviation (NYC, SF, LON).
- **All-day row:** collapsible. Multi-day events keep their titles visible.
- **Toggles:** weekends, declined events, week numbers. The week can start on any day.
- **Current-time line.** Working hours shaded from Google's working hours where available.
- **Zoom:** grid density (hour height) and whole-app interface scale.
- **Event styles:** focus time, out of office, and birthday events each have a distinct look.
- **Navigation:** smooth horizontal scrolling (trackpad, `Shift`+wheel, drag). Pagers jump a full period with a slide animation, with the next period preloaded so nothing flashes blank.

### 6.5 Right Panel (Collapsible)

- Nothing selected: upcoming meetings for the next X hours (setting), each with a Join button.
- Event selected: event details and the editor (Section 7).

### 6.6 Command Menu

- Opened with `Ctrl+K`, `Ctrl+F`, `/`, or the title bar search icon.
- Searches events (title, description, guests, location) across past and future local data, plus every app action:
  - Create event
  - Jump to date
  - Switch view
  - Join call
  - Overlay a teammate, Meet with
  - Time travel to a zone
  - Share availability
  - Toggle settings, open settings
  - Show shortcuts
- Selecting an event result opens its details. `Alt`+Enter jumps the calendar to it.

### 6.7 Settings

A full page inside the main window, with the Back button. See Section 9 for every option.

---

## 7. Events, People, and Availability

### 7.1 Creating Events

- Drag on the grid, press `C`, double-click, or use the command menu. The editor opens in the right panel.
- Dragging across several days creates a multi-day timed event. Zero-minute events are allowed.
- `Ctrl+Enter` saves and emails guests. `Ctrl+Shift+Enter` saves without emailing.

### 7.2 Editor Fields

- Title. A title containing "birthday" becomes a yearly all-day event.
- Start, end, all-day toggle, and the event's own time zone.
- Repeat: daily, weekly, monthly, yearly, or custom, ending on a date, after a count, or never.
- Calendar picker, which also moves the event between calendars or accounts. A move within one account uses `events.move`; a move across accounts is a create plus a delete.
- Guests:
  - Autocomplete from contacts, other contacts, the Workspace directory, and people you meet often or recently.
  - Optional-guest toggle.
  - Meeting rooms and resources (Workspace accounts).
- Location, opened in Google Maps or Bing Maps (setting).
- Description with bold, italic, underline, links, and lists.
- Conferencing: auto Google Meet (default per account, or none). Pasted Zoom, Teams, Webex, and other links are detected.
- Reminders: calendar defaults or custom.
- Event type: Event, Focus time, Out of office. The last two need Workspace accounts and are hidden otherwise.
- Busy/Free, Public/Private visibility, and per-event color.

### 7.3 Editing

- Drag to move, drag edges to resize, `Alt`+drag to duplicate. Drag an all-day event into a timed slot to convert it.
- Multi-select with `Ctrl`+click or `Shift`+drag box, then bulk move, delete, or recolor.
- `Ctrl+C`, `Ctrl+X`, `Ctrl+V` copy, cut, and paste events. `Delete` removes them.
- Repeating events ask: this event, this and following, or all events.

### 7.4 Guests and RSVP

- Yes / No / Maybe from right-click, the details panel, `E` then `Y`/`N`/`M`, or invite notifications.
- Optional RSVP note, sent with or without an email.
- Guest list shows every response and note.
- "Email guests" opens a `mailto:` link with all guests.
- The same event present in several of your accounts (same iCalUID) is shown once.

### 7.5 People

- `P`: overlay a teammate's calendar using `freebusy.query`. Busy blocks only, unless their calendar is shared with details.
- `F` "Meet with": pick one or more people, see combined busy times, then drag to create an event with them as guests.
- `E` then `F`: participant overlay for the selected event's guests.

### 7.6 Share Availability

- Press `S` or use the sidebar button. Drag on the calendar to pick candidate slots.
- Leaf checks free/busy across the calendars you choose and removes busy time.
- It copies text such as "Tue Sep 30: 10–11 AM, 2–4 PM ET" to the clipboard, in a selectable time zone.
- "Manage Google booking pages" opens Google's appointment schedule page, since Google has no API for booking pages.

---

## 8. Tray, Notifications, and Shortcuts

### 8.1 Tray Icon

- Tooltip: next event and countdown ("Standup in 12 min").
- Created with `Shell_NotifyIcon` through CsWin32, owned by a hidden message window.
- Leaf starts with Windows (MSIX `StartupTask`, setting, on by default). Closing the main window keeps Leaf in the tray. Only Quit exits.

### 8.2 Tray Flyout (Left-Click)

- WinUI window with a desktop acrylic backdrop.
- **Placement:** Leaf gets the icon rectangle (`Shell_NotifyIconGetRect`) and the taskbar edge (`SHAppBarMessage(ABM_GETTASKBARPOS)`) on the icon's monitor. The flyout opens next to the icon, slides in from the taskbar edge (bottom, top, left, or right), and stays inside that monitor's work area.
- **Content:**
  - Next event, countdown, and a large Join button.
  - Agenda grouped by day, each meeting with a Join button. The number of days is a setting.
  - "New event" button.
  - Clicking an event opens the main window on it.
- Closes on `Esc` or when it loses focus.
- The flyout window is created once and kept hidden, so it opens instantly.

### 8.3 Tray Menu (Right-Click)

- XAML `MenuFlyout` with Fluent styling and icons, shown from a small host window at the icon, using the same taskbar-aware placement.
- Items: Open Leaf Calendar, New event, Join next meeting, Sync now, Settings, Quit.

### 8.4 Notifications

All notifications are Windows App SDK app notifications.

1. **Reminder:** fires at each reminder time (the event's own reminders, else the calendar's Google defaults). Shows title, time, and location, with Join (if the event has a link), Snooze, and Dismiss.
2. **Persistent "Join now":** fires at start time for events with a meeting link that you haven't declined. Uses `scenario="reminder"`. It always carries a Join button that activates in the background, which Windows requires for the reminder scenario to stay on screen. It stays until you click Join or Dismiss.
3. **New or updated invite:** Yes / No / Maybe buttons in the notification.
4. **Conflict needs review** and **Sign in again**.

Rules:
- Windows Do Not Disturb and Focus settings apply. Leaf has no pause feature of its own.
- The scheduler uses `TimeProvider`, so tests can drive it.
- On resume from sleep, Leaf shows reminders for meetings that are still upcoming or in progress, and skips meetings that are already over.
- Reminders fire only while Leaf runs, which is why it starts with Windows by default.

### 8.5 Join Logic

- A meeting **qualifies** when it has a meeting link, you haven't declined it, and it starts within 10 minutes or is in progress.
- The join shortcut picks the soonest-starting qualifying meeting that hasn't started yet. If none, it picks the in-progress one.
- Google Meet links get `authuser=<account email>` and open in the default browser. Other providers open their links as-is; their desktop apps handle them when installed.
- If nothing qualifies, a short notification says "No meeting to join".

### 8.6 Global Shortcuts (Changeable in Settings)

Registered with `RegisterHotKey`. If a combination is already taken by another app, Settings shows a warning and asks for another.

| Shortcut | Action |
|---|---|
| `Ctrl+Alt+J` | Join meeting (Section 8.5) |
| `Ctrl+Alt+K` | Show or hide the tray flyout |

### 8.7 In-App Shortcuts

`?` opens a searchable cheat sheet. Key sequences like `E` then `Y` time out after 1.5 seconds.

**Navigation**

| Keys | Action |
|---|---|
| `T` | Today |
| `←` / `→` | Previous / next period |
| `J` / `K` | Next / previous period |
| `N` | Next event |
| `B` or `Shift+N` | Previous event |
| `.` | Go to date |
| `D` or `1` | Day view |
| `W` or `0` | Week view |
| `M` | Month view |
| `2`–`9` | Show that many days |
| `Z` | Time travel to another time zone |

**App**

| Keys | Action |
|---|---|
| `Ctrl+K` | Command menu |
| `Ctrl+F` or `/` | Search (command menu) |
| `?` | Shortcut cheat sheet |
| `Ctrl+,` | Settings |
| `Ctrl+Shift+L` | Toggle light / dark |
| `Ctrl+=` / `Ctrl+-` / `Ctrl+0` | Zoom in / out / reset |
| `Ctrl+Shift+E` | Show / hide weekends |
| `Ctrl+Shift+D` | Show / hide declined events |

**Events**

| Keys | Action |
|---|---|
| `C` | Create event |
| `E` | Edit selected event |
| `E` then `Y` / `N` / `M` | RSVP yes / no / maybe |
| `E` then `E` | Email guests |
| `E` then `U` | Edit duration |
| `E` then `Z` | Edit time zone |
| `E` then `F` | Participant overlay |
| `Ctrl+Enter` | Save and email guests |
| `Ctrl+Shift+Enter` | Save without emailing |
| `Ctrl+Shift+Delete` | Cancel event without emailing |
| `Delete` | Delete selected |
| `Ctrl+J` | Join selected or next meeting |
| `V` | Open meeting link in browser |

**Selection**

| Keys | Action |
|---|---|
| `X` | Select / deselect |
| `Ctrl+A` | Select all visible |
| `Shift`+drag | Box select |
| `Ctrl`+click | Add to / remove from selection |
| `Ctrl+C` / `Ctrl+X` / `Ctrl+V` | Copy / cut / paste events |
| `Alt`+drag | Duplicate |
| `Esc` | Clear selection or close panel |

**Scheduling and People**

| Keys | Action |
|---|---|
| `S` | Share availability |
| `P` | Overlay a teammate's calendar |
| `F` | Meet with |

---

## 9. Settings

**General**
- Start week on
- Show weekends, declined events, week numbers
- 12-hour or 24-hour time
- Default view and default day count
- Upcoming meetings lookahead in the right panel
- Prompt to switch time zone when the Windows time zone changes
- Map provider (Google Maps or Bing Maps)
- Launch at startup

**Accounts**
- Google OAuth client ID and secret, with the setup guide
- Add or disconnect accounts
- Primary account
- Default calendar for new events
- Default conferencing per account (Google Meet or none)

**Calendars**
- Color, visibility, order, and display name (Google summary override)
- Default reminders per calendar (written back to Google)

**Notifications**
- Reminder notifications on or off
- Persistent "Join now" notification on or off
- Invite notifications on or off
- Sound on or off

**Tray**
- Days shown in the flyout agenda
- Include all-day events
- Which calendars appear
- Next-event lookahead for the flyout header and tooltip (15, 30, 60 minutes, or 2, 4, 8 hours)

**Shortcuts**
- Global shortcut remapping with conflict warnings
- Link to the in-app cheat sheet

**Time Zones**
- Primary zone
- Additional zone columns with labels

**Appearance**
- Theme (System, Light, Dark)
- Interface scale
- Grid density
- All-day section expanded or collapsed by default

**About**
- Version
- Open logs folder
- Third-party licenses

---

## 10. Testing

### 10.1 Approach

Tests are written alongside each feature, test first: write the failing test, then the code that makes it pass. No milestone is complete until its features have all three test layers where applicable.

### 10.2 Layers

- **Logic tests (`LeafCalendar.Tests`):**
  - Fake Google through a fake `HttpMessageHandler` replaying recorded responses: success, paging, `401`, `403`, `404`, `410`, `412`, `429`, and `5xx`.
  - Fake clock through `TimeProvider` for reminders, persistent toasts, the join picker, polling cadence, and daylight-saving edges.
  - A fresh SQLite database per test.
- **UI tests (`LeafCalendar.UITests`):**
  - FlaUI UIA3 launching the packaged app by its AUMID.
  - Every tested control gets an `AutomationProperties.AutomationId`.
  - UI tests run on an STA thread.
  - The app gets a test mode that points it at the fake Google server, so UI tests never need a network.
- **Live tests (`LeafCalendar.LiveTests`):**
  - A throwaway Google account. Credentials come from environment variables, never from the repo.
  - Each run creates a "Leaf Test <timestamp>" calendar, works inside it, and deletes it afterward.

### 10.3 Special Tests

- **Memory budget:** launches the Release AOT package, sends it to tray-only, waits, reads private working set and private bytes, and fails over budget.
- **Log redaction:** scans all logs produced during the test run for tokens, secrets, and event content.
- **Link safety:** blocked schemes and look-alike hosts.

---

## 11. Build, Quality, and CI

- `.editorconfig` with the project's C# conventions. Nullable reference types enabled. Warnings treated as errors.
- Built-in .NET analyzers at the recommended level, security rules included.
- Vulnerable package check (`dotnet list package --vulnerable`) on every build.
- Security review pass at the end of each milestone.
- GitHub Actions on the private repository:
  - Every push: build plus logic tests.
  - UI tests and live tests: run locally or by manual workflow trigger, since Windows runners cost double minutes on private repositories.

---

## 12. Milestones

Each milestone gets its own implementation plan. Tests are built within each milestone, not after.

1. **Foundation:**
   - Solution and MSIX packaging, Release-only AOT
   - Memory measurement and budget number
   - OAuth sign-in, Google REST client, SQLite store
   - Initial and incremental sync
   - Recurrence expansion (Meziantou plus the `EXDATE`/`RDATE` helper)
2. **Main window:**
   - Title bar and sidebar
   - All views, scrolling and pagers
   - Time zone columns
   - In-memory event window
   - Fake-Google app test mode, plus UI tests for add, sync, and disconnect
   - Calendar-list sync every 15 minutes instead of on every poll
3. **Events:**
   - Editor, drag, resize, multi-select, copy/paste
   - Repeating events, RSVP
   - Offline outbox and conflicts
4. **Tray and alerts:**
   - Tray icon, flyout, and XAML menu
   - Notifications, persistent join toast
   - Global shortcuts and join picker
5. **Power features:**
   - Command menu, people overlay, Meet with
   - Share availability
   - Full settings page and in-app shortcut set
6. **Polish and Store prep:**
   - Visual pass and accessibility pass
   - Store listing requirements

---

## 13. Risks and Items Verified in Milestone 1

- **Recurrence library churn:** `Meziantou.Framework.Scheduling` shipped 4 versions in 3 weeks. Pin the exact version, and re-run the recurrence tests plus the AOT publish before any upgrade.
- **Hostile recurrence rules:** invites come from anyone, so a rule like `FREQ=SECONDLY` or one that never matches must not freeze Leaf. The expander caps its work, and tests cover both cases.
- **Package size:** the self-contained MSIX is about 50 MB, and about 40 MB of that is Windows App SDK AI libraries (`onnxruntime.dll`, `DirectML.dll`). Trim them in Milestone 6.
- **AOT publish PATH:** the AOT link step needs `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer` on `PATH` (it calls `vswhere.exe` by bare name).
- **Tray-only memory of the real app:** sets the memory budget.
- **FlaUI with the XAML `TitleBar`:** caption buttons may lack AutomationIds (microsoft-ui-xaml#9178). Tests target Leaf's own controls.
- **Cross-architecture AOT publish** (x64 host to ARM64) fails on .NET 10 (dotnet/sdk#53387). Build ARM64 on an ARM64 machine or wait for the fix. x64 is the first target.
