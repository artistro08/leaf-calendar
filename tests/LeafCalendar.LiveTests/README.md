# Live Tests

These tests talk to the real Google Calendar API. They skip on their own until the one-time setup below is done.

## What they do

- Sign in with the Google account stored in the Windows Credential Locker profile `live-tests`. This is whichever account you pick in the browser during setup.
- Only write to temporary calendars they create themselves (named `Leaf live test <guid>`). A write to any other calendar, including the destination of a move, is refused in code.
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
