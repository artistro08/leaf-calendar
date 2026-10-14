# Live Tests

These tests talk to the real Google Calendar API. They skip on their own until the one-time setup below is done.

## What they do

- Sign in with the Google account stored in the Windows Credential Locker profile `live-tests`. This is whichever account you pick in the browser during setup.
- Only write to temporary calendars they create themselves (named `Leaf Calendar live test <guid>`). A write to any other calendar, including the destination of a move, is refused in code.
- Delete those calendars when each test ends, even if it fails. Each is deleted on its own; if a delete fails, the calendar ID is printed to the error output so you can remove it by hand.
- Never log event content or tokens.
- `LivePeopleTests` reads your contacts to check the search works, and never prints or stores them.

## One-time setup

> Run these from the repo root (`cd` to the folder with `global.json` first). From anywhere else `dotnet test --project` fails with "MSB1001: Unknown switch", because only the root's `global.json` turns on the test runner these commands use.

You'll need a Google OAuth client ID and secret. Then, in PowerShell:

```powershell
$env:LEAF_LIVE_SIGNIN = '1'; $env:LEAF_LIVE_CLIENT_ID = '...'; $env:LEAF_LIVE_CLIENT_SECRET = '...'
dotnet test --project tests/LeafCalendar.LiveTests/LeafCalendar.LiveTests.csproj --filter-class "*LiveSignInTests"
```

Your default browser opens. Sign in with the account you want to use. It only runs when `LEAF_LIVE_SIGNIN=1` is set; normal runs skip it.

## Running

> Run these from the repo root (`cd` to the folder with `global.json` first). From anywhere else `dotnet test --project` fails with "MSB1001: Unknown switch", because only the root's `global.json` turns on the test runner these commands use.

```powershell
dotnet test --project tests/LeafCalendar.LiveTests/LeafCalendar.LiveTests.csproj
```

## Cal.com

`CalComLiveTests` checks the Cal.com scheduling provider against a real account. They skip until the one-time setup below is done.

It checks that:

- Your links list (at least one).
- The first link's open times for the next 7 days read back.
- A time link built for the first open time starts with that link's booking URL.
- The first link's bookings read back (a read only).

It **never books**. A booking sends real emails.

### One-time setup

1. In Cal.com, go to Settings › Developer › OAuth and create a test OAuth client.
2. Register this redirect URI: `http://127.0.0.1:53817/calcom/callback`
3. Give the client these four scopes: `EVENT_TYPE_READ`, `BOOKING_READ`, `BOOKING_WRITE`, `SCHEDULE_READ`.
4. Save the client ID and secret as user environment variables. Use a masked prompt so the secret never lands in your shell history:

```powershell
[Environment]::SetEnvironmentVariable('LEAF_LIVE_CALCOM_CLIENT_ID', (Read-Host -AsSecureString | ConvertFrom-SecureString -AsPlainText), 'User')
[Environment]::SetEnvironmentVariable('LEAF_LIVE_CALCOM_CLIENT_SECRET', (Read-Host -AsSecureString | ConvertFrom-SecureString -AsPlainText), 'User')
```

5. From the repo root, run the sign-in once and approve it in the browser that opens:

```powershell
$env:LEAF_LIVE_CALCOM_SIGNIN = '1'
dotnet test --project tests/LeafCalendar.LiveTests/LeafCalendar.LiveTests.csproj --filter-class "*CalComLiveTests"
```

The variables are read from the process first, then from your user-level variables, so an older shell still works. The client and refresh token are saved, encrypted for your Windows user, in `%LOCALAPPDATA%\LeafCalendar.LiveTests\profiles\live-tests\secrets.bin` (not the Credential Locker, and never your `default` profile). Delete that file to sign out. Normal runs (without `LEAF_LIVE_CALCOM_SIGNIN`) use them.
