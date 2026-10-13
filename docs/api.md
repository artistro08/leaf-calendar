# Leaf Calendar Local API

A JSON-RPC 2.0 API that lets apps on your PC, such as a Raycast extension, read your calendar and act on it through Leaf.

## Introduction

Leaf keeps your calendar and every change you make. Other apps never open Leaf's database: they ask Leaf, and Leaf answers with what it would show you. Edits go through the same outbox the app uses, so nothing an app does can skip Leaf's safety for your changes.

### Prerequisites

- Leaf Calendar running, signed in to Google.
- **Settings › Connected apps › Allow other apps to connect** turned on. It's off until you turn it on.

## Turning It On

1. Open Leaf, then **Settings › Connected apps**.
2. Turn on **Allow other apps to connect**.
3. Send your first request from your app. Leaf shows a notification asking whether to allow it, with **Allow** and **Don't allow**. Your request waits up to 60 seconds for the answer.

Allowed apps are listed in **Settings › Connected apps**. Remove one there and its next request is refused.

> Leaf remembers an app by the program file that connected. If you allow a general runtime such as `node.exe` or `python.exe`, every script that uses it can connect. A Raycast extension runs inside Raycast's own copy of Node, so the notification names that file.

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

- **Times** are ISO 8601 with an offset, such as `2026-10-14T09:00:00-04:00` or `2026-10-14T13:00:00Z`. A time without an offset is refused. Leaf answers in the time zone it shows (your primary zone in Leaf, else Windows').
- **All-day events** use dates, such as `2026-10-14`. The end date isn't included, as in Google Calendar: a one-day event on Oct 14 ends `2026-10-15`.
- Where a method takes a `start` and `end`, two dates mean all-day and two times mean timed. Mixing them is refused.
- **IDs** are opaque strings. Keep them as they are. An event's ID can change when it moves, so use the one `events.move` returns.

## Methods

### leaf.hello

Says which Leaf you're talking to. `client` is your app's name; Leaf only logs it.

```json
{"jsonrpc":"2.0","id":1,"method":"leaf.hello","params":{"client":"Raycast"}}
{"jsonrpc":"2.0","id":1,"result":{"leafVersion":"0.1.308","apiVersion":1,"profile":"default"}}
```

`apiVersion` changes only when a method or field is removed or changes meaning. New methods and fields don't change it.

### calendars.list

The calendars shown in Leaf. `account` is the Google account's email address. `isDefault` marks where new events go.

```json
{"jsonrpc":"2.0","id":2,"method":"calendars.list"}
{"jsonrpc":"2.0","id":2,"result":[{"id":"Ywpx…","account":"you@gmail.com","name":"you@gmail.com","color":"#9fe1e7","canEdit":true,"isDefault":true}]}
```

### events.list

Events overlapping `start` to `end` (at most 366 days), sorted by start, from the calendars shown in Leaf. Declined events come back only when Leaf shows them. `calendarIds` (optional) narrows the list.

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

```json
{"jsonrpc":"2.0","id":5,"method":"events.create","params":{"title":"Coffee","start":"2026-10-15T10:00:00-04:00","end":"2026-10-15T10:30:00-04:00","guests":["sam@example.com"]}}
{"jsonrpc":"2.0","id":5,"result":{"id":"ZQpx…"}}
```

### events.move

Moves an event, like dragging it in Leaf: guests get an update. `scope` is `this` (the default), `following`, or `all`, and matters only for repeating events. The result is the moved event's new `id`; after `following` it's null, because Leaf splits the series, so list again.

```json
{"jsonrpc":"2.0","id":6,"method":"events.move","params":{"id":"ZQpx…","start":"2026-10-01T16:00:00-04:00","end":"2026-10-01T17:00:00-04:00"}}
{"jsonrpc":"2.0","id":6,"result":{"id":"ZQpx…"}}
```

### meetings.join

Leaf opens the meeting link itself. Without `id`, it joins the meeting Leaf's join shortcut would pick. `opened` is false when Windows couldn't open the link.

```json
{"jsonrpc":"2.0","id":7,"method":"meetings.join"}
{"jsonrpc":"2.0","id":7,"result":{"id":"ZQpx…","opened":true}}
```

### app.showDay

Brings Leaf forward on a day.

```json
{"jsonrpc":"2.0","id":8,"method":"app.showDay","params":{"date":"2026-10-08"}}
{"jsonrpc":"2.0","id":8,"result":{}}
```

### app.showEvent

Brings Leaf forward with an event open.

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

## Errors

Protocol problems use JSON-RPC's codes: `-32700` (not JSON), `-32600` (not a JSON-RPC request), `-32601` (unknown method), `-32602` (invalid params), `-32603` (Leaf failed).

Leaf's own errors use `-32000` with a short code in `data.code`:

```json
{"jsonrpc":"2.0","id":4,"error":{"code":-32000,"message":"No event with that ID.","data":{"code":"not_found"}}}
```

| `data.code` | When |
| --- | --- |
| `not_approved` | You chose Don't allow, the approval timed out, or the app was removed. The connection closes. |
| `not_found` | No event, calendar or link with that ID. |
| `read_only` | You can't change that event or calendar. |
| `not_connected` | Scheduling asked for, but no provider is signed in. |
| `no_meeting` | Nothing to join. |
| `not_signed_in` | No Google account is signed in. |
| `unavailable` | The scheduling provider couldn't be reached. Try again later. |

## Security

- Only your Windows account can open the pipe, and web pages can't reach it.
- Leaf never sends tokens, OAuth clients, Cal.com credentials or settings.
- Titles, locations, descriptions and guest names come from whoever sent the invite. Show them as plain text, never as HTML.

### Files Used

- [`src/LeafCalendar.Core/Api/`](https://github.com/artistro08/leaf-calendar/tree/main/src/LeafCalendar.Core/Api)
- [`src/LeafCalendar.App/Api/ApiPipeServer.cs`](https://github.com/artistro08/leaf-calendar/blob/main/src/LeafCalendar.App/Api/ApiPipeServer.cs)
- [`src/LeafCalendar.App/App.Api.cs`](https://github.com/artistro08/leaf-calendar/blob/main/src/LeafCalendar.App/App.Api.cs)
- [`src/LeafCalendar.App/Views/Settings/ConnectedAppsPage.xaml`](https://github.com/artistro08/leaf-calendar/blob/main/src/LeafCalendar.App/Views/Settings/ConnectedAppsPage.xaml)
