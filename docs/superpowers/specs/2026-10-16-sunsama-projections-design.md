# Sunsama Projections: Design

Owner request, 2026-10-16: show Sunsama's projected task times on the Leaf Calendar calendar, with each task's tracked time. When built, sections 2, 4 and 5 of `2026-09-29-leaf-calendar-design.md` and `PRIVACY.md` are updated to match.

## 1. Goal

You see when Sunsama plans for you to work on each task, next to your real events, without opening Sunsama. After this change:

1. You sign in to Sunsama once in **Settings › Accounts**. The browser opens, you approve, the browser says you can close the tab, and Leaf Calendar comes to the front.
2. Each task Sunsama has placed on a day (projected or timeboxed) shows as a block on the calendar at that time.
3. Clicking a block opens the details panel with the task's title, channel, estimate and **tracked total**.
4. A Sunsama task that is already a Google event in Leaf Calendar gets no block of its own; the Google event's details show the task's tracked total instead.
5. A **Sunsama** group in the sidebar calendar list has two items you check or uncheck like calendars: **Projected tasks** shows or hides every Sunsama block, and **Tracked time** shows or hides every tracked total.

## 2. Decisions (owner, 2026-10-16)

| Topic | Decision |
| --- | --- |
| Direction | **Read-only, Leaf Calendar only.** Nothing is written to Google or to Sunsama. No syncing between them, and Sunsama blocks are never copied into Google Calendar. |
| What's shown | Projected times (Sunsama's automatic placement) and timeboxes on a non-Google calendar (Sunsama's own "Sunsama Calendar", where timeboxes go by default). |
| Look | Projected tasks look like an unanswered invitation (outlined, light fill). Timeboxed tasks look like a regular event (solid). |
| Tracked time | Shown as a total in details ("1 h 24 m tracked") and as a striped texture laid **on top of** the task's blocks (and a matched Google event), filling them from the top in proportion to the time tracked (6.3.1). Sunsama gives totals only, not when the time was worked, so tracked time is never drawn as its own block. |
| Duplicates | When a Sunsama task matches a Google event Leaf Calendar already shows, the Sunsama block is hidden and the tracked total goes on the Google event (5.2). |
| Turning it off | Two sidebar items, like calendars. **Projected tasks** off hides every Sunsama block. **Tracked time** off hides every tracked total, on Sunsama blocks and on Google events. Each works on its own. |
| Open in Sunsama | Opens the Sunsama app with `sunsama://action/details/<task id>` (Sunsama help: "Linking to Sunsama"). When no app handles `sunsama://`, Windows opens `https://app.sunsama.com/` instead (`LauncherOptions.FallbackUri`). Sunsama's web task link needs a workspace ID the MCP server doesn't give, so the web fallback opens Sunsama's home page. |
| Connection | Sunsama's MCP server, `https://api.sunsama.com/mcp`, through a small built-in client (approach 1). No new package. |
| Sign-in | OAuth with PKCE, through the browser, caught on the local loopback listener like the Google and Cal.com sign-ins. The `leaf-calendar:` protocol is **not** used, so its "bring forward only" rule is unchanged. |
| OAuth client | Leaf Calendar registers its own public client with Sunsama the first time you sign in (dynamic client registration). You enter no client ID. |
| Scopes | `read` and `offline_access` only. `execute` is never asked for. |
| UI tests | Only the UI test classes for the areas this change touches (8.2). No full-suite run for this work. |
| Delivery | Branch `feat/sunsama-projections`. |

## 3. Sunsama's Server (checked 2026-10-16)

From `https://api.sunsama.com/.well-known/oauth-authorization-server` and the help article "MCP (Model Context Protocol)":

- Registration endpoint `https://api.sunsama.com/oauth/register`; token endpoint auth method `none` is supported, so Leaf Calendar is a public client and keeps no client secret.
- PKCE `S256` only. Grants `authorization_code` and `refresh_token`. Scopes `read`, `execute`, `offline_access`.
- Resources used (`resources/read`):
  - `sunsama://me`: the user's `timezone` (IANA, for example `America/Chicago`) and `calendars` (each with `id` and `provider`, `google-calendar` or `sunsama-calendar`).
  - `sunsama://tasks/{YYYY-MM-DD}`: the day's tasks. Each task has `_id`, `title`, `channel`, `timeEstimate`, `completed`, `timeboxEventIds`, `importedFrom`, `actualTimeSpent.total`, and for projected tasks `projectedTimeEntries[]` with `startDate`/`endDate` as local text (`"2026-10-16 10:50 AM"`, no zone).
  - `sunsama://calendar/events/{YYYY-MM-DD}`: the day's events with `eventId`, `calendarId`, `start`/`end` (`dateTime` with offset, or `date`).
- Durations come back as text (`"1 hours and 24 minutes"`, `"30 minutes"`, `"2 hours"`).

**To check first in the build (before any UI work):** register a client with the loopback redirect and complete one sign-in; confirm a timeboxed task's `timeboxEventIds` matches the `eventId` in the day's events.

## 4. Core (`Core/Sunsama/`)

### 4.1 MCP Client (`SunsamaMcpClient`)

- Streamable HTTP: POST JSON-RPC 2.0 to the server URL with `Accept: application/json, text/event-stream`. Sends `initialize`, then `notifications/initialized`, keeps the `Mcp-Session-Id` header the server returns, then calls `resources/read`.
- Reads a reply as plain JSON or as one SSE `data:` message, whichever the server sends.
- A 404 for the session starts a new session once and retries the call.
- JSON only through a source-generated context (`SunsamaJsonContext`).
- One shared `HttpClient`. Nothing is created until Sunsama is connected.

### 4.2 Sign-In and Tokens

- Reuses `ProviderSignIn` and `ProviderTokens` from `Core/Scheduling/`. `ProviderSignIn` gains PKCE (on for Sunsama, off for Cal.com, which refuses `code_verifier`).
- Registration (`SunsamaRegistration`) posts `client_name` "Leaf Calendar", the loopback `redirect_uris`, `token_endpoint_auth_method: none`, `grant_types: [authorization_code, refresh_token]`. The returned client ID is saved; a refused or lost client ID registers again at the next sign-in.
- The listener uses a fixed loopback port and path, so the registered redirect stays valid.

### 4.3 Parsing (`SunsamaParser`, pure)

- Projected entries' local text → UTC instants, using the zone from `sunsama://me` and invariant culture. DST: a time that falls in a skipped hour moves forward; a time that falls in a repeated hour uses the first one.
- Duration text → `TimeSpan` (hours and minutes, singular or plural). Unreadable text → no total shown, never an error.
- Timeboxes: a task's `timeboxEventIds` matched to the day's events. Only events whose calendar's `provider` is not `google-calendar` become blocks.

### 4.4 Matching (`SunsamaMatch`, pure)

A Sunsama task matches a Google event Leaf Calendar has when:

1. its timebox event's `calendarId` is a Google calendar in `sunsama://me` and the event ID equals a cached Google event ID, or
2. it was imported from Google Calendar (`importedFrom.service == "googleCalendar"`) and a cached event on a visible calendar has the same title and the same start instant.

A matched task draws no block. The matched Google event shows the task's tracked total.

### 4.5 Fetching (`SunsamaSync`)

- Runs on `SyncLoop`'s pace (15 s on screen, 60 s in the tray, 5 minutes on Energy Saver or a metered connection), only while Sunsama is connected and at least one of the two sidebar items is on.
- Fetches the days on screen. `sunsama://me` is read once per session and again when the zone is needed and missing.
- Each day's result replaces that day's saved rows in one transaction.

## 5. Storage

### 5.1 Secrets

The client ID and refresh token go through `ITokenStore` into `secrets.bin`, like Cal.com's. The access token stays in memory.

### 5.2 Saved Blocks (migration 14, `user_version` 13 → 14)

Table `sunsama_block`: `day` (local date text), `task_id`, `title`, `channel`, `start_utc`, `end_utc`, `estimate_minutes` (nullable), `tracked_minutes` (nullable), `completed`, `kind` (`projected` or `timebox`), `matched_event_id` (nullable). Every statement parameterized. Disconnecting deletes every row.

### 5.3 Settings

`LeafSettings` gains the two sidebar items' on/off choices (both on by default after sign-in).

## 6. App

### 6.1 Settings › Accounts

A Sunsama **expander** card, built like the Google account expanders right above it in Settings › Accounts (`AccountsPage.xaml`, "One Expander Per Account") and the Working hours expander in Settings › General (`Expander` with `LeafSettingExpanderStyle`, `LeafExpanderHeaderRowStyle` header, `LeafExpanderItemRowStyle` rows). It is its own control, not `ProviderAccountCard`: Sunsama has no OAuth client form, since Leaf Calendar registers its own client.

- **Header row (owner, 2026-10-16: the button lives here):** the Sunsama name, a status line ("Not signed in" or the signed-in account email), and on the right, before the expand chevron (the header `SettingRow`'s content slot, the empty space on the Google account headers), the state's button: **Sign in with Sunsama** (accent) when signed out; a progress ring and **Cancel** while the browser is open, like the Cal.com sign-in; **Sign out** when signed in. Clicking the button doesn't expand or collapse the card. Collapsed by default.
- **Stacked row inside:**
  - **Permissions:** a plain line saying what Leaf Calendar can see: read-only access to your Sunsama tasks and calendar.
- A success InfoBar ("Successfully signed in to Sunsama", the existing provider wording) shows above the expander after a sign-in. Errors show in an InfoBar inside the expander.
- A refused refresh token shows the signed-out state with the reason line (9).
- Signing out deletes the tokens and every saved block, and the sidebar group goes away.
- Every control has an `AutomationId` (`SunsamaExpander`, `SunsamaSignIn`, `SunsamaSignOut`, `SunsamaCancelSignIn`, `SunsamaStatus`).

### 6.2 Sidebar

A **Sunsama** group under the Google accounts in the calendar list, shown only while signed in, with two items using the calendar row's checkbox: **Projected tasks** and **Tracked time**. Projected tasks has a color choice if calendar rows have one; Tracked time has none (it draws no blocks).

### 6.3 Calendar Grid

- Sunsama blocks draw in the same day columns as events, with the existing `EventBlock`, in the **Projected tasks** item's color (owner, 2026-10-16):
  - **Projected** tasks look like an **unanswered invitation**: the same outlined card with a light fill that `EventBlock` draws for `NeedsAction` (`EventBlock.cs`, the `outlined` case).
  - **Timeboxed** tasks look like a **regular event**: a solid card.
  - Selected blocks are solid, like any selected event.
  - **Channel on the block (owner, 2026-10-16):** the task's channel follows the time on the block's time line, separated by " · " ("10:50 – 11:30 · Illuminate Digital"). When the block is too short for the time line (compact), the channel is hidden with it; the tooltip and spoken name still include it. Emoji in channel names (for example "Jazmin 💖") show as sent.
- Completed tasks draw faded, like past events (`EventBlock`'s `past` look).

### 6.3.1 Tracked Time Overlay (owner, 2026-10-16)

Tracked time is drawn **on top of** the block it belongs to, never as its own block beside it: no change to how side-by-side events are laid out.

- **Where:** Sunsama blocks (projected and timeboxed) and Google events matched to a task (4.4).
- **How much:** proportional, filling from the top down. A task's tracked minutes fill its blocks in time order: each block takes up to its own length, the rest carries to the next. 1 h 24 m tracked on blocks of 40 m and 1 h 20 m: the first is fully textured, the second 44 of 80 minutes (55%). Tracked time beyond the blocks' total leaves every block fully textured. A matched Google event is filled against its own length.
- **Look:** a texture laid over the event's own color: diagonal stripes in the event's accent color at low opacity over its current fill, so the event's color still reads through. The edge where the texture ends is a straight line across the card. The title and time stay readable above it.
- **Drawing:** one overlay element inside `EventBlock`, sized by a fraction (0 to 1) computed in Core (`SunsamaTracked.Fill`, pure, logic-tested). The stripes come from a `LinearGradientBrush` with hard stops repeated across the card. If WinUI won't repeat a gradient, the fallback is the same stripes from a small Composition brush. Checked in the build.
- **High contrast:** no stripes; the tracked part gets a solid `SystemColorHighlightColor` edge line and the fraction is spoken, so the information isn't color-only.
- **Accessibility:** the block's spoken name and tooltip add "{duration} tracked".
- **Tracked time off:** no overlay anywhere.
- Blocks can't be dragged, resized or edited.
- Not shown in Month view's all-day row; Month view lists them like timed events.

### 6.4 Details Panel

For a Sunsama block: title, time, channel, estimate, tracked total, and **Open in Sunsama**. For a matched Google event: the existing details plus a tracked total line and **Open in Sunsama**. The tracked total lines show only while **Tracked time** is on.

### 6.5 Safety

- Titles, channels and notes are untrusted: shown as plain text. Notes are not shown.
- **Open in Sunsama** builds its link from fixed text plus the task ID, and only when the ID is 24 hex characters (Sunsama's ID format); any other ID shows no link. The fallback is the fixed `https://app.sunsama.com/`. `sunsama://` is added to `LinkSafety`'s allow-list for this one link shape only.
- No task content, email or token is logged; only counts, IDs and exception type names.

## 7. Errors

| Case | What happens |
| --- | --- |
| Offline, 429, 5xx | Saved blocks stay; next pass retries. A quiet line in the sync status, nothing else. |
| Refresh token refused | Signed-out state in Settings › Accounts; saved blocks stay until you sign out or sign in again. Google sync is unaffected. |
| Unreadable resource or field | That day keeps its saved rows; the error type is logged. |
| Sign-in port busy, timeout, canceled | The same messages the Cal.com sign-in shows. |

## 8. Testing

### 8.1 Logic Tests (`tests/LeafCalendar.Tests`)

Test-first, for: the tracked fill (`SunsamaTracked.Fill`: one block, split blocks carrying over, more tracked than planned, zero, no estimate); the Open in Sunsama link (valid ID, bad ID, fallback address); the MCP client (session start, JSON and SSE replies, session retry) against a fake handler; registration and PKCE sign-in; `SunsamaParser` (local text across DST days, duration text, timebox events); `SunsamaMatch` (event ID match, imported title-and-start match, no match); migration 14 and the `user_version` checks; the store; disconnect clearing rows.

### 8.2 UI Tests (affected areas only)

The fake Sunsama server lives inside the fake Google server under a `sunsama/` path, like the fake Cal.com under `calcom/`, so `--fake-google` covers it and no new launch flag is added. Only these classes run, on the installed build:

- The sidebar calendar list class: the Sunsama group shows when signed in; Projected tasks hides and shows blocks; Tracked time hides and shows tracked totals.
- The calendar grid class: projected blocks draw at their times with the channel after the time; a matched task draws no block; the tracked overlay shows on a block with tracked time and its spoken name says "tracked".
- The details panel class: a Sunsama block's details and a matched Google event's tracked total.
- The Settings › Accounts class: sign in, cancel and sign out from the expander's header without it expanding; the expander opens to show the Permissions row.

`ScreenshotTour` gains the Sunsama shots (8.3), and the tour is run once to make them and for a visual check in light and dark.

### 8.3 User Guide (`site/`)

The user guide shows how to use it, in the same voice and layout as `site/people-and-scheduling/cal-com.md`:

- **New page `site/calendar/sunsama.md`** ("Sunsama tasks", parent "Your calendar", after Time zones). Sections:
  1. What it does, in one paragraph: your Sunsama plan on the calendar, read-only, nothing synced.
  2. Prerequisites: a Sunsama account.
  3. Sign in: Settings › Accounts › **Sign in with Sunsama** on the Sunsama card › allow access in the browser › back in Leaf Calendar.
  4. Turning it on and off: the two sidebar items.
  5. Reading a block: projected (outlined) versus timeboxed (solid), the striped tracked overlay and what its height means, the tracked total, **Open in Sunsama** (opens the Sunsama app, or sunsama.com without it).
  6. Tasks that are already Google events: no second block; the tracked total shows on the event.
  7. Signing out.
  8. `### Files Used` is not added (this is a user guide, not a feature doc for developers).
- **Updated pages:** `site/calendar/sidebar.md` (the Sunsama group), `site/settings.md` (the Sunsama expander in Accounts), `site/index.md` if it lists features.
- **Screenshots** from `ScreenshotTour` on fake data, saved as light and dark pairs in `site/assets/images/` like the existing ones:
  - `sunsama-expander` (Settings › Accounts, Sunsama expander open, signed in)
  - `sunsama-sidebar` (the Sunsama group with both items)
  - `sunsama-week` (week view with projected and timeboxed blocks beside events, one with the tracked overlay)
  - `sunsama-details` (details panel for a Sunsama block, with tracked total and Open in Sunsama)
  - `sunsama-matched` (details panel for a Google event with its tracked total)
- `PRIVACY.md` gets a Sunsama section: what's read, that it's saved in `leaf.db` on this PC, that the token is in `secrets.bin`, and that Sign out removes both.
- All new guide text is listed for the owner's approval with the UI wording (9).

## 9. Wording (needs the owner's approval)

| Where | Text |
| --- | --- |
| Settings › Accounts card title | "Sunsama" |
| Expander status, signed out | "Not signed in" |
| Expander status, signed in | the account email |
| Sign-in button | "Sign in with Sunsama" (matches the existing "Sign in with {provider}") |
| Sign-out button | "Sign out" |
| Cancel while signing in | "Cancel" |
| Permissions row header | "Permissions" |
| Permissions row text | "Leaf Calendar can only read your Sunsama tasks and calendar. It never changes anything in Sunsama." |
| After sign-in | "Successfully signed in to Sunsama" (existing provider wording) |
| Signed out after a refusal | "Sunsama signed you out. Sign in again to see your task plan." |
| Sidebar group | "Sunsama" |
| Sidebar items | "Projected tasks", "Tracked time" |
| Details: tracked | "{duration} tracked" |
| Details: estimate | "{duration} planned" |
| Details: link | "Open in Sunsama" |
| Sync status | "Couldn't reach Sunsama. Leaf Calendar will try again." |
| Browser page after sign-in | "You're signed in to Sunsama. You can close this tab." |

## 10. Non-Goals

- Writing anything to Sunsama (timers, timeboxing, completing tasks) or to Google.
- Drawing tracked time as blocks.
- Tasks with no time on the calendar (backlog, unscheduled).
- Showing task notes.
