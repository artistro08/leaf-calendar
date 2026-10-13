# Scheduling Providers: Design

Owner request, 2026-10-12, from GitHub issue #28 ("Add Cal.com to Scheduling interface"). Extends spec section 7.6 (Share Availability) and the saved share groups design (`2026-10-06-saved-share-groups-design.md`). When built, sections 2, 7.6, 8 and 9 of `2026-09-29-leaf-calendar-design.md` and `PRIVACY.md` are updated to match.

> **Revision 1 (owner, 2026-10-12, mid-build):** Cal.com connects by **OAuth** with your own OAuth client (client ID and client secret), not an API key. Every credential lives in **Settings › Accounts**; **Settings › Scheduling** holds only settings and is hidden until you're signed in to a provider. Self-hosted Cal.com and the server address are dropped. Sections 1, 2, 3.1–3.4, 3.7, 4, 9, 10 and 11 below describe the revised design.

## 1. Goal

You share times with someone through a booking service instead of plain text. Leaf builds a standard way to plug scheduling providers in first, then adds Cal.com as the first one. After this change:

1. You enter your own Cal.com OAuth client (client ID and secret) and **Sign in with Cal.com** in **Settings › Accounts**. Nothing about Cal.com shows anywhere else (not the share panel, not Settings › Scheduling) until you're signed in.
2. While sharing (S), a **Use Cal.com** switch and a **Link** dropdown sit in the share panel. Each new share starts with the switch on and your default link picked.
3. With the switch on, the grid shades the times Cal.com won't book on that link. A drag is fitted to the link's bookable times (a time picked more than half is stretched to its full length, one picked half or less is removed), and a notice says what Leaf did.
4. Copy puts each proposed time on the clipboard **as a link**: the time is the text, and clicking it opens Cal.com on that exact time with your guests filled in.
5. When someone books a time inside a saved group's ranges, the group goes away and a Windows notification says who booked.
6. Adding a second provider later means writing one class and adding one line to the provider list. The share panel, Settings, storage and booking checks don't change.

## 2. Decisions (owner, 2026-10-12)

| Topic | Decision |
| --- | --- |
| What the guest gets | Each proposed time is its own link that opens the provider on that exact time (Cal.com `?date=…&slot=…`). |
| Turning it off | A **Use Cal.com** switch in the share panel, per share. Each new share starts on. Off means today's behavior exactly: any times, plain text, no links. |
| Picking | Drag as today. On release Leaf fits the drag to the link's bookable times (3.5), and a notice says what happened. |
| Partly covered bookable times | A bookable time the drag covers **more than half** of is expanded to the whole time. One covered half or less is removed, and the notice says why. Times the drag covers fully are kept. |
| Splitting | Saved times keep the **range** you dragged (after trimming to open times). Splitting into bookable times of the link's length happens only when building the copied text. |
| Messages | Each message is its own small card above the existing "Mark times that are available on your calendar." hint at the bottom center while scheduling. Messages that appear at the same time stack, newest on top, and each fades on its own. The text is only the message itself, with no label in front. |
| Grid | Times the link has no openings for get a darker shade while sharing with the switch on. |
| Provider layer | One small interface per action (approach A). A provider implements only the actions it supports; Leaf shows only the matching UI. |
| Actions | Provider basics, list links, open times, schedule time, schedule block, bookings. |
| Schedule block | Built and tested in the layer; the share panel always uses schedule time for now. |
| Hosting | cal.com only (revision 1; self-hosted Cal.com and the server address were dropped). |
| Connecting (revision 1) | OAuth authorization code flow with **your own** Cal.com OAuth client, like the Google OAuth client: you enter its client ID and client secret (required) in Settings › Accounts, then Sign in with Cal.com in the browser. Leaf ships no Cal.com client. |
| Where things live (revision 1) | Credentials (client ID, secret, sign in, sign out, reconnect) in **Settings › Accounts**. Settings (default link) in **Settings › Scheduling**, which is hidden from the Settings list until a provider is signed in. |
| Scopes (revision 1) | Only what Leaf uses: view booking links, view bookings, create bookings (Approve…), view availability. |
| Copied text | Rich text (HTML) where each time is a link, plus plain text with the address after each time for apps that can't take links. |
| Guests in the link | The first guest prefills the booker (`name`, `email`). When the link allows extra guests, every other guest is added with a repeated `guests` parameter. When it doesn't, only the first guest goes in the link and the notice says so. |
| Booking detected | The **whole** group goes, and a Windows notification says who booked. |
| In-between times | A booking on the group's link that **starts inside** any of the group's saved ranges matches (any start Cal.com allowed, not only the split times). |
| Webhooks | Not used: Cal.com webhooks need a public server and Leaf has none. Leaf asks Cal.com for new bookings instead. |
| Booking notification setting | A **Booking notifications** switch in Settings › Notifications, on by default, shown only while a provider is connected. |
| RAM | Nothing provider-related is created until a provider is connected. Open times are fetched only for days on screen while sharing and dropped when sharing ends. Bookings are only checked while saved provider groups exist. One shared `HttpClient`. |
| Audit | The plan ends with a full review of the new code (security, threading, AOT, RAM, doc comments) and a memory budget run; findings are fixed before install. |
| Delivery | Branch `feat/scheduling-providers`, opened as a pull request when done. |

## 3. Provider Layer (`Core/Scheduling/`)

### 3.1 Actions

Each action is its own interface. A provider class implements `ISchedulingProvider` and any of the others. Leaf checks with a type test (`provider is IOpenTimesSource`), which is AOT-safe.

| Interface | Members | Cal.com |
| --- | --- | --- |
| `ISchedulingProvider` | `Id` (`"calcom"`), `Name` ("Cal.com"). How a provider signs in, and its creation, live in `SchedulingProviderInfo` (3.3); signing in ends by listing links to prove the tokens work (`SchedulingAuthException` when refused) | Yes |
| `ILinkSource` | `GetLinksAsync(ct)` → `IReadOnlyList<SchedulingLink>` | Yes |
| `IOpenTimesSource` | `GetOpenTimesAsync(linkId, startUtc, endUtc, ct)` → `IReadOnlyList<BusyRange>` (each a bookable start and end) | Yes |
| `ITimeLinkBuilder` | `TimeLink(link, startUtc, zone, guests)` → `Uri` that opens that exact time | Yes |
| `IBlockLinkBuilder` | `BlockLink(link, day, guests)` → `Uri` that opens that day | Yes (not used by the panel yet) |
| `IBookingSource` | `GetBookingsAsync(linkId, createdAfterUtc, ct)` → `IReadOnlyList<SchedulingBooking>` | Yes |
| `IBookingCreator` | `BookAsync(link, startUtc, guests, zone, ct)` → `SchedulingBooking`; throws `SchedulingSlotTakenException` when the time is no longer open | Yes |

Records, in `Core/Scheduling/SchedulingModels.cs`:

- `SchedulingLink(string ProviderId, string Id, string Name, int Minutes, Uri BookingUrl, bool AllowsGuests)`. `AllowsGuests` says whether the link takes people besides the booker (Cal.com: `disableGuests` is false).
- `SchedulingBooking(string Id, DateTimeOffset Start, DateTimeOffset Created, string AttendeeName)`.
`BusyRange` is reused for open times (it is already Leaf's start-and-end record).

### 3.2 Signing In (OAuth, revision 1)

A provider describes its OAuth endpoints and scopes as data (`SchedulingOAuth`: authorize URL, token URL, scopes, redirect URI). Settings › Accounts draws the same section for every provider from that data: client ID box, client secret box, **Sign in with {provider}**. A new OAuth provider needs no new Accounts code.

Cal.com:

| Item | Value |
| --- | --- |
| Authorize | `https://app.cal.com/auth/oauth2/authorize` with `client_id`, `redirect_uri`, `scope` (space separated), `state`, `code_challenge` (S256), `response_type=code` |
| Token | `POST https://api.cal.com/v2/auth/oauth2/token` (form-encoded): `grant_type=authorization_code`, `code`, `redirect_uri`, `client_id`, `client_secret`, `code_verifier`; refresh with `grant_type=refresh_token`, `refresh_token`, `client_id`, `client_secret` |
| Redirect | `http://127.0.0.1:53817/calcom/callback` (fixed port, since Cal.com matches redirect URIs exactly; `http://localhost:53817/calcom/callback` is also registered) |
| Scopes | `EVENT_TYPE_READ` (view booking links; name confirmed in the live test), `BOOKING_READ`, `BOOKING_WRITE`, `SCHEDULE_READ` |
| Tokens | Access token 30 minutes (`expires_in`), kept in memory and refreshed quietly a minute before it ends. Refresh token stored encrypted (4.1). A refused refresh (`invalid_grant`, 400/401) marks the provider **needs reconnecting**. |

The sign-in follows the Google sign-in's safety rules (main spec 4.2): PKCE S256 and a random `state`; the listener binds `127.0.0.1` only, on the fixed port, answers only the expected path, checks `state`, accepts one request, never echoes query values into its page, and times out after 5 minutes. A port already in use says so ("Port 53817 is busy…" — wording in 11). The browser page reuses the Google sign-in's pages ("You can close this tab" / "Sign-in didn't finish").

### 3.3 Provider List

`SchedulingProviders.All` is the one place that knows every provider. Each entry is a `SchedulingProviderInfo`: id, name, a one-sentence description, its OAuth data (`SchedulingOAuth`, 3.2), a `Create` function (shared `HttpClient`, a function that hands out a fresh access token, and the API address) that builds the provider, its hosted API address, and the name of the menu path where its OAuth client page lives (the link text of the Accounts client guide).

`SchedulingHub` (Core) owns everything that changes while Leaf runs: each provider's OAuth client, sign-in, sign-out and Remove OAuth client, the quiet token refresh, its cache of links, and the back-off after a rate limit. A provider is in one of four states: **no OAuth client**, **client saved** (not signed in), **signed in**, or **needs reconnecting** (the provider refused the sign-in or a refresh). Only signed-in providers are handed out; one that needs reconnecting is left out until you sign in again. At startup (and whenever the secrets become readable again) `Load()` builds a provider for each saved refresh token without calling the network; a secrets file that can't be read right now leaves each provider as it was and retries later. Sign-out and Remove OAuth client drop the provider's instance.

### 3.4 Cal.com (`CalComProvider`)

All calls go to `https://api.cal.com/` with `Authorization: Bearer <access token>` over HTTPS, through the app's shared `HttpClient`. The access token comes from a per-provider token source that refreshes it when needed (revision 1).

| Action | Request | `cal-api-version` |
| --- | --- | --- |
| Links | `GET /v2/event-types` (your own; hidden links left out) | `2024-06-14` |
| Open times | `GET /v2/slots?eventTypeId=&start=&end=&format=range` (UTC) | `2024-09-04` |
| Bookings | `GET /v2/bookings?eventTypeId=&afterCreatedAt=&status=upcoming` | `2024-08-13` |
| Book | `POST /v2/bookings` with `start` (UTC), `eventTypeId`, `attendee` (`name`, `email`, `timeZone`), and `guests` (extra emails, only when the link allows them) | `2024-08-13` |

- The booker (`attendee`) is the first guest. Its `name` is the guest's name, else the email address (Cal.com requires a name). `timeZone` is the share's zone (IANA).

- JSON through a new source-generated `CalComJsonContext`.
- Schedule time: `bookingUrl?date=yyyy-MM-dd&slot=<UTC ISO start>&name=&email=&guests=…` (Cal.com's booking page reads `date` and `slot`; see its `Booker/store.ts`). `date` is the day in the zone of the share. `guests` is repeated once per extra guest, and left out when the link doesn't allow guests.
- Schedule block: `bookingUrl?date=yyyy-MM-dd` plus the same guest parameters.
- Version headers and parameter names are checked against a live account in the plan's first Cal.com task (live test, 10.3).

### 3.5 Splitting Math (`SlotFitting`, pure, Core)

- `Fit(drag, openTimes)` → a `FitResult`: the kept range(s) and a `FitSummary`. Used on drag release and on resize. For each open time that overlaps the drag:
  - Covered fully: kept.
  - Covered **more than half** (strictly; exactly half is not more): expanded, so the whole open time is kept.
  - Covered half or less: removed, and counted in the summary.
  - Open times are taken earliest first; one that overlaps an already kept time is skipped (a 30-minute link every 15 minutes doesn't double up).
  - The kept times are merged into ranges; that's what's saved and drawn. Stretches of the drag with no open time are counted as "not open".
  - Example, 30-minute link with openings at 9:00, 9:30, 10:00 and a drag of 9:10–10:10: 9:00 (20 of 30 minutes covered) is expanded, 9:30 is kept, 10:00 (10 of 30) is removed. Saved: 9:00–10:00.
- `Split(range, openTimes, minutes)` → the bookable starts inside a saved range: open times that fit wholly inside it, taken earliest first without overlap. Offline (no open times known) it steps by the link's length from the range start.
- `FitSummary` holds: times kept, minutes each, times expanded, times removed for too little coverage, and minutes not open. The view model turns it into the notice text (11).

### 3.6 Booking Matching (`BookingMatch`, pure, Core)

- A booking matches a saved group when it's on the group's provider and link, it was created at or after the group was saved, and its start is at or after a saved range's start and before its end.
- Every matching group is removed (two groups with the same link and overlapping ranges both go; Leaf can't tell which offer it came from).

### 3.7 Safety

- Everything a provider returns is untrusted. Link names and attendee names go through `DisplayText.Clean` and are capped (100 characters). A booking address must be absolute `https://` and pass `LinkSafety`, or that link is dropped.
- Never logged: the client secret, codes, access or refresh tokens, link names, booking addresses, attendee names or emails. Logged: provider id, counts, HTTP status codes and exception type names.
- Toast text from a provider is escaped like every other toast.
- Tokens and the client secret go only to `api.cal.com` (token endpoint and API) over HTTPS, never in a URL.
- `PRIVACY.md` gets a Scheduling section: Leaf talks to Cal.com with your own OAuth client; it sends date ranges to Cal.com while you share, and the link and the time you saved your times when it checks bookings; it sends your guests' names and emails, and the share's time zone, to Cal.com only when you press Book; guest emails are also in the links you copy; the client ID, client secret and refresh token are stored encrypted on this PC; signing out removes the refresh token, and Remove OAuth client removes all three.

## 4. Storage

### 4.1 Secrets

`ITokenStore` gains `GetProviderSecret(providerId, key)`, `SetProviderSecret(providerId, key, value)` and `RemoveProviderSecrets(providerId)`. `ProtectedFileTokenStore` keeps them in a new `Providers` map in `secrets.bin`, with the same lock, DPAPI entropy, atomic write and unreadable-file rules. `CredentialLockerTokenStore` (the old store, read only by the one-time move) doesn't support them.

Revision 1: the keys per provider are `client_id`, `client_secret` and `refresh_token`. Signing out removes `refresh_token` only (the OAuth client stays, like the Google OAuth client staying when an account is disconnected); **Remove OAuth client** removes all three.

### 4.2 Settings (`LeafSettings`)

- `Scheduling`: a list of `ProviderSettings(string ProviderId, string Server, string DefaultLinkId)`. Revision 1: `Server` is no longer used (always empty; kept so saved settings still read).
- `BookingNotifications` (bool, default true).
- `Normalize` clips both and drops unknown provider ids.

### 4.3 Saved Groups (migration 13, `user_version` 12 → 13)

```sql
ALTER TABLE share_groups ADD COLUMN provider_id  TEXT    NOT NULL DEFAULT '';
ALTER TABLE share_groups ADD COLUMN link_id      TEXT    NOT NULL DEFAULT '';
ALTER TABLE share_groups ADD COLUMN link_url     TEXT    NOT NULL DEFAULT '';
ALTER TABLE share_groups ADD COLUMN link_minutes INTEGER NOT NULL DEFAULT 0;
```

- An empty `provider_id` means a plain group (every group saved before this change, and any saved with the switch off).
- `link_url` and `link_minutes` let Copy & Save rebuild links offline.
- `ShareGroup` gains `ProviderId`, `LinkId`, `LinkUrl`, `LinkMinutes` and `CreatedUtc`; `Insert` and `Update` take them.

## 5. Share Panel (`ShareSlotsPanel.cs`)

Shown only while at least one provider is connected and not signed out:

- Under the hint, above Title: a **Use Cal.com** `ToggleSwitch` (no On/Off text; AutomationId `ShareProviderSwitch`; the label is "Use {provider name}" and, with two providers connected, "Use a booking link"), then a **Link** `ComboBox` (AutomationId `ShareLinkBox`) listing links from every connected provider as "{name} · {minutes} min", grouped by provider when there are two or more.
- The Link box shows only while the switch is on. Each new share starts with the switch on and the default link picked (else the first link).
- Turning the switch off keeps the picks as they are. Changing the link, or turning the switch on, fits every picked time to the link's bookable times at once, with the same messages.
- A saved group opens with its own switch and link. Its link gone from the provider: the switch shows off, and the notice says "That link isn't on Cal.com anymore. These times will copy as plain text."
- The provider signed out or its client removed while a share is open (final review fix): the share turns plain at once (switch off, so Save and Copy & Save write no link and Copy matches what's saved). Needing reconnecting while a share is open keeps the share's switch and link, so Save keeps the group's link and booking check (8); a message shows the needs-reconnecting text (11), and Approve… shows that text instead of opening the event editor.
- **Editing a saved Cal.com group** works like any saved group (click it on the grid to open it), with the provider rules kept:
  - The grid shades the group's link's openings, and its times stay resizable by their edges.
  - Adding a time (drag), resizing one, or editing its start and end in the panel fits it to the link's bookable times (3.5), with the same messages.
  - Changing the link, or turning the switch on for a plain group, fits every saved time to the new link at once, with the same messages.
  - Turning the switch off makes the group plain on Save (times kept as they are).
  - Removing a time, adding or removing guests, and title and message edits work as today.
  - Changes wait for **Save** or **Copy & Save**, as today. Copy & Save copies fresh links for the edited times and guests. Save alone sends nothing.
  - Booking checks use the group as last saved (its link and ranges). Links sent before an edit still book on Cal.com if that time is still open there; a booking from one counts only if it starts inside the group's saved ranges.

### 5.1 Approve… on a Cal.com Group (owner, 2026-10-12)

With the group's switch on and its provider able to book (`IBookingCreator`), **Approve…** books the time on Cal.com instead of opening the event editor:

1. Approve… on a saved range with one bookable time picks it. With several (a range of 9–11 on a 30-minute link), a small flyout under the button lists the bookable times in the range (from fresh open times; booking needs Cal.com, so offline goes straight to step 6), and you pick one.
2. A confirmation dialog: "Book {time} on Cal.com?" with the guests listed, and **Book** (accent) and **Cancel**. Booking sends Cal.com's confirmation emails, so it is never done without this step.
3. On Book, Leaf calls `BookAsync`. While it runs, the buttons are off and a progress ring shows.
4. Success: the whole group is deleted, a message says "Booked on Cal.com.", and the event reaches your Google calendar the way every Cal.com booking does (through Cal.com's own calendar connection), so Leaf doesn't create a Google event itself (no duplicate).
5. Time taken (`SchedulingSlotTakenException`): the group stays, a message says "That time was just taken on Cal.com. Pick another.", and the open times refresh.
6. Offline, server error, or sign-in refused: the group stays and nothing is booked; a message says what's wrong (offline: "Couldn't reach Cal.com. Nothing was booked."; sign-in refused: the needs-reconnecting state, 8).
7. The booking check (8) then sees this booking with no group left, so it shows no second notification.

With the switch off (a plain group), Approve… opens the event editor as today. Approve… still needs at least one guest.

## 6. Time Grid

- While sharing with the switch on, the view model asks for the link's open times for the days on screen (again on paging and on a link change; a generation counter keeps only the newest answer).
- `DayColumn` draws a darker shade (a new `LeafBrushes` entry, with a HighContrast entry) over each stretch that has no open time.
- On drag release (and on resize), the pick becomes `SlotFitting.Fit` of the drag. Nothing left: the pick is dropped. The notice describes the result (11).
- Open times unknown (offline, server error): no shade, picks stay as dragged, and a message says Cal.com couldn't be reached.
- **Moving a time** (owner, 2026-10-12; for every share, plain or Cal.com): while picking new times or with a saved group open, dragging inside a picked time (away from its 6 DIP edge strips) moves the whole time, keeping its length. It snaps to `SnapMinutes` (15), can move to another day column, and shows the plain drag ghost while moving. On release:
  - Plain share: the time lands where dropped and merges with any picked time it now overlaps.
  - Cal.com on: the moved time is fitted to the link's bookable times (3.5), with the same messages.
  - A press inside a picked time that doesn't move (a click) changes nothing.
  - This replaces "a drag inside an open slot picks a new time" from the saved share groups design. Saved times of a group that isn't open still can't be moved (click to open first), so a stray drag can't change what you offered.
  - `DragMath` gets `MoveRange(start, end, grabbedAt, pointerAt, zone)`; `DragKind.MoveSlot` holds which group (or the open picks) and which slot. The pointer shows the move cursor over a picked time.
- **Message stack:** a vertical stack above the "Mark times…" hint, same style as the hint (an `InfoBar`-like card with the info icon). One fit can add several messages (split, stretched, removed, not open); each is its own card. Newest on top, each fades out on its own after the notice's usual time, at most 4 shown (the oldest goes first). The stack is a polite live region, so a screen reader reads each new card. AutomationIds `ShareMessage_{i}`, top first.

## 7. Copy

With the switch on, `CopyAndSaveShare` builds:

- Each saved range split with `SlotFitting.Split` (fresh open times when online, else by length).
- One line per bookable time, in the share's zone: "Tue Oct 13, 10–10:30 AM CT", formatted by `AvailabilityText` (a new `FormatTimes` beside `Format`).
- **HTML**: each line is `<a href="{time link}">{line}</a>`, joined with `<br>`, placed where `{times}` goes in the message (the message HTML-encoded). Set with `DataPackage.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(...))`.
- **Plain text**: "{line}: {time link}" per line, through `AvailabilityText.Compose` as today. Set with `SetText` on the same package.
- Time links carry the guests (3.4) as they are in the panel at Copy time. With two or more guests on a link that doesn't allow extra guests, only the first goes in, and the notice after Copy says so (11).

With the switch off, Copy is unchanged.

## 8. Booking Checks

- `BookingWatcher` (Core) runs inside each sync pass (`SyncLoop`'s pace: 15 s on screen, 60 s in the tray, 5 minutes on Energy Saver or a metered connection), only while saved groups with a provider exist.
- For each provider and link in use, it asks for bookings created after the oldest such group was saved, runs `BookingMatch`, deletes the matched groups through `ShareGroupStore.Delete`, then raises `BookingFound`.
- `App` shows a Windows notification when `Settings.BookingNotifications` is on: "{attendee} booked {time}" ("Someone" when no name). A click opens Leaf on that day (toast argument parsed with `ToastArgs`, a date only).
- The grid reloads saved groups (`ShareChanged`).
- Failures: offline or 5xx, skip and try next pass. 401: the provider is marked **needs reconnecting** (Settings card and panel switch reflect it), groups stay, no more calls until reconnected. 429: wait for `Retry-After` (or the next pass). Nothing here ever fails a Google sync.

## 9. Settings

### 9.1 Settings › Scheduling (new page, between Calendars and Time zones; revision 1)

- Shown in the Settings list only while at least one provider is signed in (or needs reconnecting); otherwise the page and its nav item are hidden.
- One card per signed-in provider: **Default link** (`ComboBox` of your links, "{name} · {minutes} min"). Nothing about credentials here.
- AutomationIds: `SchedulingCard_{id}`, `SchedulingDefaultLink_{id}`.

### 9.3 Settings › Accounts: Scheduling Providers (revision 1)

A section under the Google accounts, one card per provider in `SchedulingProviders.All`, following the page's existing pattern (the Google OAuth client and account rows):

- **No OAuth client saved:** the provider name and description, with **Add OAuth client…** opening a form: **Client ID** (`TextBox`) and **Client secret** (`PasswordBox`), both required, with a guide line and link to the provider's OAuth client page (`https://app.cal.com/settings/developer/oauth`) and the redirect URI to register, shown as selectable text. **Save** stores both encrypted (4.1); the secret box is cleared after.
- **OAuth client saved, not signed in:** **Sign in with Cal.com** (accent), and **Remove OAuth client**.
- **Signing in:** a progress ring and **Cancel** while the browser is open. Success lists your links once to prove the tokens work, then shows the signed-in state. Refused or failed: says why (11) and saves nothing.
- **Signed in:** "Signed in" with **Sign out** (removes the refresh token; saved groups on that provider become plain groups; the OAuth client stays).
- **Needs reconnecting:** says so, with **Sign in again** (the same sign-in, replacing the refresh token).
- AutomationIds: `ProviderCard_{id}`, `ProviderAddClient_{id}`, `ProviderClientId_{id}`, `ProviderClientSecret_{id}`, `ProviderSaveClient_{id}`, `ProviderSignIn_{id}`, `ProviderSignOut_{id}`, `ProviderRemoveClient_{id}`, `ProviderError_{id}`.

### 9.2 Settings › Notifications

- A **Booking notifications** card with a `ToggleSwitch`, saving `BookingNotifications`, shown only while a provider is connected. AutomationId `BookingNotificationsSwitch`.

## 10. Testing

### 10.1 Logic Tests (written first)

- `DragMathTests`: `MoveRange` keeps the length, snaps to 15 minutes, crosses midnight and DST days, and moves between days.
- `SlotFittingTests`: fully covered kept; more than half expanded; exactly half and less removed; the 9:10–10:10 example; drag with no open time; slot interval shorter than length (no doubling up); split by open times and offline by length; DST days; cross-midnight; summary values.
- `BookingMatchTests`: exact start, in-between start, start at a range's end (no match), other link, created before the group, two groups both matched.
- `CalComProviderTests` (fake `HttpMessageHandler` with recorded JSON): links parse and hidden ones are dropped, open times parse with offsets, bookings parse, 401 → auth exception, unsafe or non-https booking addresses dropped, headers and query strings exact, key never in a URL.
- `CalComProviderTests` also: `BookAsync` sends the exact body (booker name falls back to the email, guests only when allowed), parses the created booking, and maps a taken time to `SchedulingSlotTakenException`.
- `CalComLinkTests`: time and block links, `date` in the share's zone, guests prefill and escape; several guests on a link that allows them; only the first on one that doesn't; `disableGuests` parsed into `AllowsGuests`.
- `ProtectedFileTokenStoreTests`: provider secrets round trip, remove, survive client and token writes.
- `LeafSettingsTests`: scheduling settings normalize; `BookingNotifications` defaults on.
- `ShareGroupStoreTests` and migration tests: `user_version` 13; a version-12 database upgrades with groups intact and empty provider columns.
- `AvailabilityTextTests`: `FormatTimes` lines and the HTML builder (encoding, links, `{times}`).

### 10.2 UI Tests

The fake Google server gains fake Cal.com routes (`/v2/event-types`, `/v2/slots`, `/v2/bookings`, plus an authorize page that redirects straight back with a code, and the token endpoint). Under the existing `--fake-google <root>` flag, every provider's API, authorize and token addresses move under `<root><provider id>/` (Cal.com: `<root>calcom/`), the way `GoogleEndpoints.ForFake` points Google at the fake, so no test reaches the real Cal.com. The sign-in "browser" in tests follows the redirect the way the Google sign-in tests do. No new launch flag is added, and nothing changes in a normal run.

- New `SchedulingProviderTests`: add an OAuth client, sign in, a refused sign-in saves nothing; Settings › Scheduling appears only once signed in; default link saves; sign out hides the panel switch and the Scheduling page; the panel shows nothing before connecting; a drag over a closed stretch is trimmed and the notice shows; Copy puts HTML links and plain text on the clipboard with the guest prefilled; switch off copies plain text; a fake booking removes the group and shows the notification.
- `SchedulingProviderTests` also: Approve… on a one-time range asks to confirm, Book sends the booking to the fake Cal.com with the booker and guests, deletes the group and shows no second notification; on a longer range the flyout lists the times; Cancel books nothing; a taken time keeps the group and shows the message.
- `SavedShareGroupTests` (existing class): drag a new pick to a later time and to another day; open a saved group, move a time, Save, reopen and see it moved; a click inside a picked time changes nothing; a saved time of a closed group doesn't move.
- `SchedulingProviderTests` also: move a time onto a partly closed stretch and see it fitted; open a saved Cal.com group, resize a time over a closed stretch and see it fitted, change the link and see every time refitted, Save, reopen after a restart with the switch and link kept, Copy & Save copies links for the edited times.
- `ShareAvailabilityTests` and `SavedShareGroupTests` (existing): still pass unchanged with no provider connected.
- `ScreenshotTour`: the Scheduling page and the share panel with the switch.

### 10.3 Live Test

`LeafCalendar.LiveTests` gets a Cal.com test that lists links, reads open times and builds a link. Revision 1: a one-time setup test (run only with `LEAF_LIVE_CALCOM_SIGNIN=1`, client ID and secret read from environment variables) signs in through the browser and stores the refresh token in the `live-tests` profile, like the Google live setup; normal runs reuse it and skip until it's done (README updated). It never books.

### 10.4 Finish

AOT publish (`publish-aot.ps1`), package size check, memory budget run (results recorded in main spec 3.4), full review of the new code with findings fixed, `sign-install.ps1`, then the pull request.

## 11. Wording (new; needs owner approval before shipping)

| Where | Text |
| --- | --- |
| Settings nav item | Scheduling |
| Cal.com card description | Share times as booking links from your Cal.com account. |
| Default link card | Default link |
| Accounts section header (revision 1) | Scheduling |
| Add client button (revision 1) | Add OAuth client… |
| Client form (revision 1) | Client ID / Client secret / Save, Cancel |
| Client guide (revision 1) | Create an OAuth client in Cal.com › Settings › Developer › OAuth, and add this redirect URI: |
| Sign in button (revision 1) | Sign in with Cal.com |
| Signing in (revision 1) | Finish signing in in your browser. / Cancel |
| Signed in (revision 1) | Signed in |
| Sign out button (revision 1) | Sign out |
| Remove client button (revision 1) | Remove OAuth client |
| Sign-in refused (revision 1) | Cal.com didn't accept this OAuth client. Check the client ID and secret, and that the redirect URI is registered. |
| Port busy (revision 1) | Port 53817 is busy, so Leaf can't finish signing in. Close the app using it and try again. |
| Can't reach | Couldn't reach Cal.com. Check your connection and try again. |
| Needs reconnecting (revision 1) | Cal.com signed Leaf out. Sign in again to keep sharing booking links. |
| Sign in again button (revision 1) | Sign in again |
| Notifications card | Booking notifications / Show a notification when someone books a time you shared. |
| Panel switch | Use Cal.com (two providers: Use a booking link) |
| Panel dropdown header | Link |
| Link item | {name} · {minutes} min |
Messages over the calendar while scheduling (each its own stacked card; the left column only names the case and is never shown):

| Case | Message |
| --- | --- |
| Drag split | Split into {n} times of {minutes} minutes. |
| Drag split, one | Split into 1 time of {minutes} minutes. |
| Times stretched | Stretched {n} times to their full {minutes} minutes, since you picked more than half of each. |
| Times stretched, one | Stretched 1 time to its full {minutes} minutes, since you picked more than half of it. |
| Times removed | Removed {n} times, since you picked half or less of each. |
| Times removed, one | Removed 1 time, since you picked half or less of it. |
| Part not open | {duration} wasn't open on Cal.com. |
| Guests not allowed | This Cal.com link doesn't allow extra guests, so only {first guest} is filled in. |
| Nothing open | None of that time is open on Cal.com. |
| Offline | Couldn't reach Cal.com, so times aren't limited to your openings. |
| Link gone | That link isn't on Cal.com anymore. These times will copy as plain text. |
| Booking notification | {attendee} booked {time} (Someone booked {time} with no name) |
| Approve confirmation title / buttons | Book {time} on Cal.com? / Book, Cancel |
| Approve success | Booked on Cal.com. |
| Approve, time taken | That time was just taken on Cal.com. Pick another. |
| Approve, offline | Couldn't reach Cal.com. Nothing was booked. |

The "one" rows are the singular forms for a count of one (owner, 2026-10-12: "1 time" at one).

## 12. Out of Scope

- Webhooks, or any Leaf server.
- Rescheduling or canceling Cal.com bookings from Leaf (Approve… creates one; nothing else does).
- Editing Cal.com links or availability from Leaf.
- A panel choice between exact times and blocks (the layer supports blocks; the panel doesn't offer them yet).
- Cal.com team links and organizations (only your own links).
- More than one account per provider.
- Self-hosted Cal.com and API keys (revision 1).
- A Cal.com OAuth client shipped with Leaf (revision 1: each user brings their own).
