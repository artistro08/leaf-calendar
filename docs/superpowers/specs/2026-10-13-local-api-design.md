# Local API: Design

Owner request, 2026-10-13, from GitHub issue #29 ("Raycast Extension"). Leaf gets one local JSON API that any app on the PC can use (Raycast first). This spec covers only the API inside Leaf; the owner builds the Raycast extension in its own repo. When built, sections 4 and 9 of `2026-09-29-leaf-calendar-design.md` and `PRIVACY.md` are updated to match.

## 1. Goal

Another app on your PC can read your calendar and act on it through Leaf, the same way you would in the app:

1. See upcoming events, as far ahead as it asks.
2. Join a meeting (the same pick as the join shortcut), from the other app's own hotkey.
3. Create an event and move (reschedule) one.
4. List Cal.com links, their open times, and get a booking link for a time.
5. Bring Leaf forward on a day, or with an event open.

Leaf stays the single source of truth. Other apps read only through the API, never the database, and every change goes through the outbox like an edit made in the app.

## 2. Decisions (owner, 2026-10-13)

| Topic | Decision |
| --- | --- |
| Who it's for | Any local app (universal), not only Raycast. |
| Scope here | The API in Leaf only. The Raycast extension lives in a separate repo, built by the owner. |
| Reading | Only through the API. Apps never open Leaf's database. |
| Transport | A named pipe, locked to the current Windows user. No HTTP server, no network port. |
| Format | JSON-RPC 2.0, one message per line. |
| Access | A switch in Settings, off by default, plus a one-time approval per app, with a list of approved apps you can remove. |
| Scheduling | Through the API without opening Leaf: list links, open times, a booking link for a time. |
| Reschedule | Through the API (`events.move`), the same as a drag: a scope for repeating events, guests are notified. |
| Guests on create | `events.create` takes guests. Invitations go out by default, and the caller can turn them off, like the editor's Save and quiet save. |
| Folder | `Core/Api/`, namespace `LeafCalendar.Core.Api`. |
| Branch | `feat/local-api`. |

Considered and turned down: a local HTTP server (any web page can reach it, so it needs a key, Host and Origin checks, and has a history of DNS rebinding bugs, for example CVE-2026-58169), and a command-line tool (it would either read the database, which the owner ruled out, or call the pipe anyway).

## 3. Architecture

### 3.1 Core (`src/LeafCalendar.Core/Api/`)

Everything testable lives here. None of it knows about pipes or windows.

| Type | Job |
| --- | --- |
| `ApiMessage` | Reads one line of JSON into a JSON-RPC request, and writes results and errors. Source-generated JSON (`ApiJsonContext`). |
| `ApiDispatcher` | Routes a request's `method` to its handler, checks `params`, turns known exceptions into API errors (3.4). Never lets an exception out. |
| `ApiEvents`, `ApiActions`, `ApiScheduling`, `ApiCalendars` | The handlers. Each calls code Leaf already has: the event queries the views use, `JoinPicker`, `EventEditor`, `DefaultCalendar`, `SchedulingHub`. No new query or edit logic. |
| `ApiIds` | Packs account and calendar (and event or instance) IDs into one opaque string and unpacks it, rejecting anything malformed. |
| `ApiApprovals`, `ApiClients` | The one-time approval question (3.5), the pipe name, and which programs are general runtimes. The approved apps themselves live in `LeafSettings.ApprovedApps` (`ApprovedApp`: program path, display name, when approved); paths compare ignoring case. |
| `IApiHost` | What the handlers need from the App: open a link, show a day, show an event. Implemented by the App. |

### 3.2 App

| Piece | Job |
| --- | --- |
| `Api/ApiPipeServer.cs` | Runs the pipe while the switch is on (3.3). Reads lines, passes each to `ApiDispatcher`, writes replies. Stops when the switch goes off or Leaf quits. |
| Client check | `GetNamedPipeClientProcessId` then `QueryFullProcessImageName` (CsWin32) gives the program path. The display name is the file's description, else its product name, else the file name (`ApiClients.DisplayName`; Windows' own programs carry the OS as their product name). |
| Approval | A standalone approval window (`2026-10-13-api-approval-window-design.md`). The connection waits for the answer. |
| `IApiHost` | Marshals to the UI thread with `DispatcherQueue.TryEnqueue`; joins through the existing join code; shows a day or event with the existing navigation. |
| Settings | The switch and the approved-apps list (section 6). |

### 3.3 The Pipe

- **Name:** `leaf-calendar-<Windows user name>` for the `default` profile, `leaf-calendar-<Windows user name>-<profile>` for others (so UI test profiles never touch the owner's). Lowercase. Apps get the user name from the OS (Node: `os.userInfo().username`).
- **Security:** `PipeOptions.CurrentUserOnly` (only the current Windows user can connect), `PipeOptions.FirstPipeInstance` (Leaf won't share a name another program already holds), `PipeOptions.Asynchronous`. If the name is taken, Leaf logs it, Settings says so (section 6), and the API stays off. No retry loop.
- **Only while on:** with the switch off the pipe doesn't exist.
- **Limits:** at most 8 connections at once (more are refused at the pipe); a line longer than 1 MB closes the connection; a connection idle for 10 minutes is closed.
- **Framing:** UTF-8, one JSON-RPC message per line (`\n`, `\r\n` accepted). Requests on one connection are answered in order. JSON-RPC batches are not supported (error `-32600`). Notifications (requests without `id`) are run but get no reply.

### 3.4 Errors

Standard JSON-RPC codes for protocol problems: `-32700` parse error, `-32600` invalid request, `-32601` unknown method, `-32602` invalid params, `-32603` internal error. Leaf's own problems use `-32000` with a `data.code`:

| `data.code` | When |
| --- | --- |
| `not_approved` | You chose Don't allow, or the approval timed out. The connection then closes. |
| `not_found` | No event, calendar or link with that ID. |
| `read_only` | You can't change that event or calendar. |
| `not_connected` | Scheduling asked for, but no provider is signed in. |
| `no_meeting` | `meetings.join` found nothing to join. |
| `not_signed_in` | No Google account is signed in. |
| `unavailable` | The scheduling provider couldn't be reached. Try again later. |

`message` is a short English sentence for logs, never exception text, never event content.

### 3.5 Approval

1. On connect, Leaf reads the client's program path (3.2). If it can't, the connection closes.
2. Approved path: requests run.
3. Path refused since Leaf started: `not_approved`, close.
4. New path: Leaf shows the approval window naming the app with its icon and full path, with **Allow** and **Don't allow** (approval window spec). Requests wait up to 60 seconds. Allow saves the path to `ApprovedApps`. Don't allow, Esc, closing the window, or no answer in time: `not_approved`, close; Don't allow is remembered until Leaf restarts, so a retrying app can't spam you.
5. Only one question per path at a time; other connections from the same path wait on the same answer.

The window's name comes from the program file and is cleaned like event text; the app's icon and return link are checked as the approval window spec says.

Known limit (stated in the approval window and in Settings): approval trusts a program file. Approving a general runtime such as `node.exe`, `python.exe` or `powershell.exe` approves every script it runs. A Raycast extension runs inside Raycast's own Node, so its prompt names Raycast's file.

## 4. Methods

Times are ISO 8601 with an offset (`2026-10-14T09:00:00-05:00`); input without an offset is `invalid_params`. Where a method takes a start and end, two dates mean all-day and two times mean timed; mixing them is `invalid_params`. All-day events use dates (`2026-10-14`) with an exclusive end, as Google does. Results use the display time zone's offset, so a client can show them as they are.

IDs are opaque strings (`ApiIds`, 3.1). Calendar IDs carry the account too, since the same calendar can be in two signed-in accounts.

| Method | Params | Result |
| --- | --- | --- |
| `leaf.hello` | `{ client?: string, icon?: string, returnUrl?: string }` | `{ leafVersion, apiVersion: 1, profile }`. `client` is logged as the file name only; it is never trusted. `icon` and `returnUrl` count only on the first line (approval window spec 3). |
| `calendars.list` | none | `[ { id, account, name, color, canEdit, isDefault } ]` for calendars shown in Leaf. `account` is the Google account's email address. |
| `events.list` | `{ start, end, calendarIds? }` | Events overlapping the range, sorted by start: `[ { id, calendarId, title, start, end, allDay, location, response, color, meetingLink } ]`. Range at most 366 days. Only calendars shown in Leaf (the same events the calendar draws, declined ones only when Leaf shows them); `calendarIds` narrows that. |
| `events.get` | `{ id }` | The `events.list` fields plus `description` (plain text through `DescriptionFormatter`) and `guests: [ { name, email, response, organizer } ]`. |
| `events.create` | `{ calendarId?, title, start, end, location?, description?, guests?: [ email ], notifyGuests? }` | `{ id }`. No `calendarId`: the default calendar (`DefaultCalendar.Pick`); nothing writable is `read_only`. Description is plain text. Guests are email addresses (checked with `ContactSearch.ValidEmail`, the same rule the editor uses; duplicates dropped; at most 100; a bad address is `invalid_params`). `notifyGuests` (default true) sends Google's invitations, like the editor's Save; false is like its quiet save. Goes through `EventEditor.Create`. |
| `events.move` | `{ id, start, end, scope? }` | `{ id }`, the moved instance's new ID; null after `following` (the series is split, so list again). `scope`: `this` (default), `following`, `all`; ignored for single events. Through `EventEditor.Move` with `sendUpdates: true`, as a drag. Passing dates makes it all-day; passing times makes it timed. |
| `meetings.join` | `{ id? }` | `{ id, opened }` (`opened` is false when Windows couldn't open the link). No `id`: the meeting the join shortcut would pick (`JoinPicker`). Leaf opens the link itself with account routing, only when `LinkSafety` approves it. |
| `app.showDay` | `{ date }` | `{}`. Leaf comes forward on that day in the current view. |
| `app.showEvent` | `{ id }` | `{}`. Leaf comes forward on the event's day with its details open. |
| `scheduling.links` | none | `[ { id, provider, name, minutes, allowsGuests } ]`. `id` is opaque (it carries the provider); `provider` is the provider's name, such as "Cal.com". |
| `scheduling.openTimes` | `{ linkId, start, end }` | `[ { start, end } ]`. Range at most 31 days. |
| `scheduling.timeLink` | `{ linkId, start, guests?: [ { name, email } ] }` | `{ url }` through the provider's `ITimeLinkBuilder`, the same link Copy makes. |

`meetingLink` is set only when `LinkSafety` approves the link; otherwise null. `response` is `accepted`, `declined`, `tentative`, `needsAction` or null. Writes return as soon as the change is in the outbox, like the app.

`apiVersion` changes only when a method or field is removed or changes meaning. New methods and fields don't change it.

## 5. Security

Checked against the project's security rules and the threat model (main spec 4.8). New rows for 4.8:

| Threat | Defense |
| --- | --- |
| Another Windows user drives your Leaf | `CurrentUserOnly` pipe ACL. |
| A program grabs the pipe name first | `FirstPipeInstance`: Leaf won't share the name; Settings shows it. |
| A web page drives the API | Browsers can't open named pipes. No HTTP server exists. |
| An unknown local app reads your calendar | Off by default, then a one-time approval per program file. |
| A local app pretends to be an approved one | Not preventable under one Windows account (it could also read Leaf's data folder). Stated in the prompt and Settings. |
| Oversized or hostile messages | 1 MB line limit, 8 connections, 10-minute idle close, every field validated, IDs unpacked strictly. |
| Secrets leak through the API | No method returns tokens, OAuth clients, Cal.com credentials or settings. |
| Malicious link launched through `meetings.join` | Leaf opens only links `LinkSafety` approves, through the existing join code. |
| An edit lost or sent out of order | Every write goes through `EventEditor` and the outbox. |

Unchanged: the `leaf-calendar:` protocol still only brings Leaf forward. Logs carry method names, the client's file name, counts and error codes, never event content, paths with the user name, or params.

## 6. Settings

A new **Connected apps** page in Settings (proposed placement: after Notifications). Following the Settings pattern: one setting per card, saves at once.

1. **Allow other apps to connect** switch, off by default.
2. While on: the approved apps, one card each with its program's icon, name and path and a **Remove** button. Empty: a short line saying none yet.
3. If the pipe name is taken: a card saying so.
4. A link to `docs/api.md` on GitHub for developers.

All wording on this page and in the approval window is **pending owner approval**; the plan quotes each string for review before it ships.

## 7. Documentation

- `docs/api.md`: the developer reference. How to connect (pipe name, framing), approval, every method with an example request and reply, errors, and a short Node example.
- `PRIVACY.md`: a section on connected apps (off by default, what they can see and change, how to remove them).
- Main spec: threat model rows (section 5) and the Settings page.

## 8. Testing

- **Logic tests (Core):** message parsing (valid, malformed, oversize, batch, notification), every method's params checks and results against a test database (including guests and `notifyGuests` on create), every error code, `ApiIds` round trips and rejects, `ApprovedApps` add, remove, path normalization, and that no result contains a token or setting.
- **Lint:** the new Settings page passes `XamlLintTests`.
- **UI test:** one class against the fake Google server and a `uitest-` profile: turn the switch on, connect to the pipe, approve through the approval window's Allow button, call `events.list` and `app.showDay`, then remove the app and see `not_approved`.
- Not covered by automated tests: a second Windows user and a squatted pipe name. Checked by hand.

## 9. Out of Scope

- Change notifications pushed to apps (apps ask again). Candidate later if the Raycast extension needs it.
- Delete, guest editing, replying to invites, recoloring.
- The Raycast extension itself, and the "only today" setting (the extension calls `events.list` for today).
