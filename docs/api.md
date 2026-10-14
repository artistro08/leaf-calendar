# Leaf Calendar Local API

A JSON-RPC 2.0 API that lets apps on your PC, such as a Raycast extension, read your calendar and act on it through Leaf Calendar.

## Introduction

Leaf Calendar keeps your calendar and every change you make. Other apps never open Leaf Calendar's database: they ask Leaf Calendar, and Leaf Calendar answers with what it would show you. Edits go through the same outbox the app uses, so nothing an app does can skip Leaf Calendar's safety for your changes.

### Prerequisites

- Leaf Calendar running, signed in to Google.
- **Settings › Connected apps › Allow other apps to connect** turned on. It's off until you turn it on.

## Turning It On

1. Open Leaf Calendar, then **Settings › Connected apps**.
2. Turn on **Allow other apps to connect**.
3. Send your first request from your app. Leaf Calendar opens a small "{app} wants to connect to Leaf Calendar" window on top of the screen, with **Allow** and **Don't allow**. Your request waits up to 60 seconds for the answer. Make that first request a `leaf.hello` with your `icon` and `returnUrl` (below) so the window shows your icon and Leaf Calendar can send the person back to you.

Allowed apps are listed in **Settings › Connected apps**. Remove one there and its next request is refused.

> Leaf Calendar remembers an app by the program file that connected. If you allow a general runtime such as `node.exe` or `python.exe`, every script that uses it can connect, and the window warns you. A program inside an installed app's own package folder, such as Raycast's copy of Node, is named after that app and gets no warning, since only that app runs it. Leaf Calendar remembers it by the app, so it stays allowed when the app updates. If that same file connects without running as part of its app, Leaf Calendar asks again, with the warning.

## Connecting

- **Pipe:** `\\.\pipe\leaf-calendar-<your Windows user name, lowercase>`. Profiles other than `default` add `-<profile>`.
- **Format:** UTF-8, one JSON-RPC 2.0 message per line (`\n`).
- **Limits:** 1 MB per line, 8 apps at once. A connection closes after 10 minutes without a request.
- **Order:** requests on one connection are answered in the order they arrive. Batches aren't supported. A request without an `id` runs but gets no reply.
- **Who:** only your Windows account can open the pipe.

## Example (Node)

```js
import net from "node:net";
import os from "node:os";
import readline from "node:readline";

const pipe = net.connect(`\\\\.\\pipe\\leaf-calendar-${os.userInfo().username.toLowerCase()}`);
const lines = readline.createInterface({ input: pipe });
lines.on("line", (line) => console.log(JSON.parse(line)));

pipe.write(JSON.stringify({
  jsonrpc: "2.0",
  id: 1,
  method: "events.list",
  params: { start: "2026-10-14T00:00:00-04:00", end: "2026-10-15T00:00:00-04:00" },
}) + "\n");
```

## Times and IDs

- **Times** are ISO 8601 with an offset, such as `2026-10-14T09:00:00-04:00` or `2026-10-14T13:00:00Z`. A time without an offset is refused. Leaf Calendar answers in the time zone it shows (your primary zone in Leaf Calendar, else Windows').
- **All-day events** use dates, such as `2026-10-14`. The end date isn't included, as in Google Calendar: a one-day event on Oct 14 ends `2026-10-15`.
- Where a method takes a `start` and `end`, two dates mean all-day and two times mean timed. Mixing them is refused.
- **IDs** are opaque strings. Keep them as they are. An event's ID can change when it moves, so use the one `events.move` returns.

## Methods

### leaf.hello

Says which Leaf Calendar you're talking to. `client` is your app's name; Leaf Calendar only logs it.

```json
{"jsonrpc":"2.0","id":1,"method":"leaf.hello","params":{"client":"Raycast"}}
{"jsonrpc":"2.0","id":1,"result":{"leafVersion":"0.1.308","apiVersion":1,"profile":"default"}}
```

Three optional params count only when `leaf.hello` is the **first** message on the connection, since that's when Leaf Calendar asks the person. Later, or once your app is approved, they're accepted and ignored. Leaf Calendar keeps none of them.

| Param | Rules | When it breaks a rule |
| --- | --- | --- |
| `icon` | Base64 PNG, at most 64 KB decoded, at most 256 × 256 pixels. The approval window shows it in place of your program's icon. | Ignored: the window shows your program's icon. So does an icon that passes these rules but Windows can't decode. |
| `returnUrl` | An absolute link to your app's own scheme, at most 2,048 characters, no user info. Not `http`, `https`, `microsoft-edge`, `file`, `ftp`, `javascript`, `data`, `shell`, `search-ms`, `search`, or any `ms-` scheme. | Ignored. |
| `askOnLink` | A random code you make, 32 to 128 letters, digits, `-` or `_` (16 random bytes as hex is enough). Leaf Calendar waits to ask until the person opens `leaf-calendar://connect?code=<the code>`. | Ignored: Leaf Calendar asks right away. |

```json
{"jsonrpc":"2.0","id":1,"method":"leaf.hello","params":{"client":"My extension","icon":"iVBORw0KGgo…","returnUrl":"raycast://extensions/me/leaf"}}
```

After **Allow**, Leaf Calendar opens `returnUrl`; without one it brings your program's main window to the front.

#### Asking From Your Own Button

Send `askOnLink` when your app has its own "Sign in" or "Connect" button, so the Allow window shows when the person presses it instead of as soon as you connect:

1. Send the first `leaf.hello` with `askOnLink`. An app that's already allowed gets the usual result. One that isn't gets the `approval_needed` error at once, and the connection stays open. Every request you send before the person opens the link gets the same error.
2. When the person presses your button, open `leaf-calendar://connect?code=<the code>`. Leaf Calendar shows the Allow window. The code works once, for 5 minutes; after that, your next request asks right away.
3. After **Allow**, Leaf Calendar opens `returnUrl`. Send your next request on the same connection; it's answered as an allowed app's. After **Don't allow**, it gets `not_approved`.

```json
{"jsonrpc":"2.0","id":1,"method":"leaf.hello","params":{"client":"My extension","returnUrl":"raycast://oauth?package_name=Extension&code=allowed&state=…","askOnLink":"9f86d081884c7d659a2feaa0c55ad015"}}
{"jsonrpc":"2.0","id":1,"error":{"code":-32000,"message":"Open leaf-calendar://connect?code=<your askOnLink code> so Leaf Calendar asks to allow this app.","data":{"code":"approval_needed"}}}
```

Leaf Calendar reads nothing else from a `leaf-calendar:` link. Any other link, or a code it isn't waiting for, only brings Leaf Calendar to the front.

`apiVersion` changes only when a method or field is removed or changes meaning. New methods and fields don't change it.

### calendars.list

The calendars shown in Leaf Calendar. `account` is the Google account's email address. `isDefault` marks where new events go.

```json
{"jsonrpc":"2.0","id":2,"method":"calendars.list"}
{"jsonrpc":"2.0","id":2,"result":[{"id":"Ywpx…","account":"you@gmail.com","name":"you@gmail.com","color":"#9fe1e7","canEdit":true,"isDefault":true}]}
```

### events.list

Events overlapping `start` to `end` (at most 366 days), sorted by start, from the calendars shown in Leaf Calendar. Declined events come back only when Leaf Calendar shows them. `calendarIds` (optional) narrows the list.

```json
{"jsonrpc":"2.0","id":3,"method":"events.list","params":{"start":"2026-10-01T00:00:00-04:00","end":"2026-10-02T00:00:00-04:00"}}
{"jsonrpc":"2.0","id":3,"result":[{"id":"ZQpx…","calendarId":"Ywpx…","title":"Design review","start":"2026-10-01T14:00:00-04:00","end":"2026-10-01T15:00:00-04:00","allDay":false,"location":"Room 4","response":"accepted","color":"#9fe1e7","meetingLink":"https://meet.google.com/abc-defg-hij"}]}
```

`response` is `accepted`, `tentative`, `declined`, or `needsAction`. `meetingLink` is set only for known meeting hosts (Google Meet, Zoom, Teams and the like) and is null otherwise.

### events.get

One event, plus its `description` (plain text) and `guests` (`name`, `email`, `response`, `organizer`).

```json
{"jsonrpc":"2.0","id":4,"method":"events.get","params":{"id":"ZQpx…"}}
```

### events.create

Creates an event and returns its `id`. Without `calendarId`, it goes to your default calendar.

| Param | |
| --- | --- |
| `title` | Required, at most 1,024 characters. |
| `start`, `end` | Two times, or two dates for all-day. |
| `calendarId` | Optional, from `calendars.list`. |
| `location` | Optional, at most 1,024 characters. |
| `description` | Optional plain text, at most 8,192 characters. |
| `guests` | Optional email addresses, at most 100. |
| `notifyGuests` | Optional, `true` by default: Google emails the invitations. `false` adds the guests without emailing them. |
| `meet` | Optional, `false` by default. `true` asks Google for a Google Meet link, as Leaf Calendar's **Add Google Meet** does; it shows as `meetingLink` once Google answers. |

```json
{"jsonrpc":"2.0","id":5,"method":"events.create","params":{"title":"Coffee","start":"2026-10-15T10:00:00-04:00","end":"2026-10-15T10:30:00-04:00","guests":["sam@example.com"]}}
{"jsonrpc":"2.0","id":5,"result":{"id":"ZQpx…"}}
```

### events.move

Moves an event, like dragging it in Leaf Calendar: guests get an update. `scope` is `this` (the default), `following`, or `all`, and matters only for repeating events. The result is the moved event's new `id`; after `following` it's null, because Leaf Calendar splits the series, so list again.

```json
{"jsonrpc":"2.0","id":6,"method":"events.move","params":{"id":"ZQpx…","start":"2026-10-01T16:00:00-04:00","end":"2026-10-01T17:00:00-04:00"}}
{"jsonrpc":"2.0","id":6,"result":{"id":"ZQpx…"}}
```

### events.update

Changes an event, like saving it in Leaf Calendar's editor. Only the params you send change. The result is the event's new `id` (null after `following`, as with `events.move`, and after a move to a calendar on another account, which Google sees as a new event).

| Param | |
| --- | --- |
| `id` | Required. |
| `title`, `location` | Optional, at most 1,024 characters each. |
| `description` | Optional plain text, at most 8,192 characters. |
| `start`, `end` | Optional, together: two times, or two dates for all-day. |
| `calendarId` | Optional: moves the event to that calendar. A repeating event moves only with `scope` `all`. |
| `guests` | Optional, the whole guest list as email addresses. Guests you keep keep their answers; new ones are invited. Rooms and you stay. |
| `meet` | Optional: `true` adds a Google Meet link, `false` removes it. |
| `scope` | `this` (the default), `following`, or `all`, for repeating events. |
| `notifyGuests` | Optional, `true` by default: guests get Google's update. |

```json
{"jsonrpc":"2.0","id":7,"method":"events.update","params":{"id":"ZQpx…","title":"Design review (moved)","location":"Room 5"}}
{"jsonrpc":"2.0","id":7,"result":{"id":"ZQpx…"}}
```

### events.delete

Deletes an event, like Delete in Leaf Calendar: guests get a cancellation unless `notifyGuests` is `false`. `scope` is `this` (the default), `following`, or `all`, and matters only for repeating events. Leaf Calendar holds the delete for a few seconds before sending it, as it does for its own Undo. Events you can't change are `read_only`.

```json
{"jsonrpc":"2.0","id":7,"method":"events.delete","params":{"id":"ZQpx…"}}
{"jsonrpc":"2.0","id":7,"result":{}}
```

### meetings.join

Leaf Calendar opens the meeting link itself. Without `id`, it joins the meeting Leaf Calendar's join shortcut would pick. `opened` is false when Windows couldn't open the link.

```json
{"jsonrpc":"2.0","id":7,"method":"meetings.join"}
{"jsonrpc":"2.0","id":7,"result":{"id":"ZQpx…","opened":true}}
```

### app.showDay

Brings Leaf Calendar forward on a day.

```json
{"jsonrpc":"2.0","id":8,"method":"app.showDay","params":{"date":"2026-10-08"}}
{"jsonrpc":"2.0","id":8,"result":{}}
```

### app.showEvent

Brings Leaf Calendar forward with an event open.

```json
{"jsonrpc":"2.0","id":9,"method":"app.showEvent","params":{"id":"ZQpx…"}}
```

### scheduling.links

Your booking links from a signed-in scheduling provider (Cal.com). `provider` is the provider's name.

```json
{"jsonrpc":"2.0","id":10,"method":"scheduling.links"}
{"jsonrpc":"2.0","id":10,"result":[{"id":"bApj…","provider":"Cal.com","name":"30 min meeting","minutes":30,"allowsGuests":true}]}
```

### scheduling.openTimes

A link's bookable times between `start` and `end` (at most 31 days).

```json
{"jsonrpc":"2.0","id":11,"method":"scheduling.openTimes","params":{"linkId":"bApj…","start":"2026-10-13T00:00:00-04:00","end":"2026-10-15T00:00:00-04:00"}}
{"jsonrpc":"2.0","id":11,"result":[{"start":"2026-10-13T10:00:00-04:00","end":"2026-10-13T10:30:00-04:00"}]}
```

### scheduling.timeLink

A booking link that opens one exact time. The first guest fills in the booker; others are added when the link allows guests (at most 20).

```json
{"jsonrpc":"2.0","id":12,"method":"scheduling.timeLink","params":{"linkId":"bApj…","start":"2026-10-13T10:00:00-04:00","guests":[{"name":"Sam","email":"sam@example.com"}]}}
{"jsonrpc":"2.0","id":12,"result":{"url":"https://cal.com/you/30min?date=2026-10-13&slot=…"}}
```

### people.search

People to suggest as guests, as Leaf Calendar's guest box does: those in your events whose name has a word starting with `query`, or whose address or its domain does, then your Google contacts. Without `query` (or with an empty one), the people you meet with most. Each address once, at most 8. `query` is at most 100 characters. `name` is null when Leaf Calendar doesn't know it.

```json
{"jsonrpc":"2.0","id":12,"method":"people.search","params":{"query":"sam"}}
{"jsonrpc":"2.0","id":12,"result":[{"name":"Sam Rivera","email":"sam@example.com"}]}
```

### availability.free

Your free times between `start` and `end` (at most 31 days): your working hours (**Settings › General**) on your work days, minus events that show you as busy (not declined, not marked free), from now on. Stretches shorter than `minutes` (optional, 5 to 1,440, 30 by default) are left out. With `linkId`, only times that link has open are kept.

```json
{"jsonrpc":"2.0","id":13,"method":"availability.free","params":{"start":"2026-10-13T00:00:00-04:00","end":"2026-10-20T00:00:00-04:00"}}
{"jsonrpc":"2.0","id":13,"result":[{"start":"2026-10-13T09:00:00-04:00","end":"2026-10-13T11:30:00-04:00"}]}
```

### shares.create

Saves times to offer someone, like **Copy & Save** in Leaf Calendar's share panel: they stay on your calendar until you book one, delete them, or they pass. Returns the saved times' `id` and the text to paste, in your share message (**Settings › Scheduling**) and Leaf Calendar's time format. With `linkId`, each time is split by the link's open times (by its length alone when the provider can't say) and carries its own booking link, and `html` is the same text with each time as a link; otherwise `html` is null.

| Param | |
| --- | --- |
| `times` | Required, 1 to 50 `{start, end}` times, each ending in the future. Overlapping and touching times are joined. |
| `title` | Optional, at most 100 characters. |
| `message` | Optional, at most 2,000 characters; `{times}` marks where the times go. Your share message by default. |
| `guests` | Optional, at most 50 `{name, email}`; `name` is optional. A link fills in the first as the booker. |
| `linkId` | Optional, from `scheduling.links`. |

```json
{"jsonrpc":"2.0","id":14,"method":"shares.create","params":{"title":"Coffee with Sam","times":[{"start":"2026-10-13T10:00:00-04:00","end":"2026-10-13T11:00:00-04:00"}],"guests":[{"name":"Sam","email":"sam@example.com"}]}}
{"jsonrpc":"2.0","id":14,"result":{"id":"cwox…","text":"Here are some times that work for me:\r\nTue Oct 13: 10–11 AM ET","html":null}}
```

### shares.list

Your saved times that haven't all passed, oldest first, with passed times left out. `link` is null for plain times, else `{id, minutes}`.

```json
{"jsonrpc":"2.0","id":15,"method":"shares.list"}
{"jsonrpc":"2.0","id":15,"result":[{"id":"cwox…","title":"Coffee with Sam","times":[{"start":"2026-10-13T10:00:00-04:00","end":"2026-10-13T11:00:00-04:00"}],"guests":[{"name":"Sam","email":"sam@example.com"}],"link":null}]}
```

### shares.book

Books one of the saved times, like **Approve…**, and deletes the saved times. `start` must fall inside one of them.

- **Plain times:** creates an event from `start` to `end` (required, inside the same time) on your default calendar, titled with the saved title (or "Held times"), with the guests invited. The result is the new event's `id`.
- **Times on a link:** books `start` on the provider for the guests (the link's whole length must fit inside the same time) (the first is the booker, so at least one is needed); the provider sends its confirmations and puts the event on your calendar, so the result's `id` is null. `end` is ignored. A time no longer open fails with `taken`.

```json
{"jsonrpc":"2.0","id":16,"method":"shares.book","params":{"id":"cwox…","start":"2026-10-13T10:00:00-04:00","end":"2026-10-13T10:30:00-04:00"}}
{"jsonrpc":"2.0","id":16,"result":{"id":"ZQpx…"}}
```

### shares.delete

Deletes saved times.

```json
{"jsonrpc":"2.0","id":17,"method":"shares.delete","params":{"id":"cwox…"}}
{"jsonrpc":"2.0","id":17,"result":{}}
```

## Errors

Protocol problems use JSON-RPC's codes: `-32700` (not JSON), `-32600` (not a JSON-RPC request), `-32601` (unknown method), `-32602` (invalid params), `-32603` (Leaf Calendar failed).

Leaf Calendar's own errors use `-32000` with a short code in `data.code`:

```json
{"jsonrpc":"2.0","id":4,"error":{"code":-32000,"message":"No event with that ID.","data":{"code":"not_found"}}}
```

| `data.code` | When |
| --- | --- |
| `not_approved` | You chose Don't allow, the approval timed out, or the app was removed. The connection closes. |
| `approval_needed` | Your first `leaf.hello` sent `askOnLink` and your app isn't allowed yet. Open the connect link; the connection stays open. |
| `not_found` | No event, calendar, link or saved times with that ID. |
| `taken` | That time isn't open on the scheduling provider anymore. |
| `read_only` | You can't change that event or calendar. |
| `not_connected` | Scheduling asked for, but no provider is signed in. |
| `no_meeting` | Nothing to join. |
| `not_signed_in` | No Google account is signed in. |
| `unavailable` | The scheduling provider couldn't be reached. Try again later. |

## Security

- Only your Windows account can open the pipe, and web pages can't reach it.
- Leaf Calendar never sends tokens, OAuth clients, Cal.com credentials or settings.
- Titles, locations, descriptions and guest names come from whoever sent the invite. Show them as plain text, never as HTML.

### Files Used

- [`src/LeafCalendar.Core/Api/`](https://github.com/artistro08/leaf-calendar/tree/main/src/LeafCalendar.Core/Api)
- [`src/LeafCalendar.App/Api/ApiPipeServer.cs`](https://github.com/artistro08/leaf-calendar/blob/main/src/LeafCalendar.App/Api/ApiPipeServer.cs)
- [`src/LeafCalendar.App/App.Api.cs`](https://github.com/artistro08/leaf-calendar/blob/main/src/LeafCalendar.App/App.Api.cs)
- [`src/LeafCalendar.App/Views/Settings/ConnectedAppsPage.xaml`](https://github.com/artistro08/leaf-calendar/blob/main/src/LeafCalendar.App/Views/Settings/ConnectedAppsPage.xaml)
