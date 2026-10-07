# Leaf Calendar Privacy

Leaf Calendar is a Windows app for your Google Calendar. This page says what it keeps on your PC, what it sends and where, and how to remove it.

## The Short Version

- Leaf talks only to Google, through the Google Cloud OAuth client you create yourself. There's no Leaf server.
- Leaf has no telemetry, analytics, ads, or tracking. Nothing about you or how you use Leaf is sent anywhere else.
- Your calendar is kept on your PC so Leaf works quickly and offline. Your sign-in secrets are kept encrypted in Leaf's app data folder.
- Leaf's log never includes your events, guests, searches, or sign-in secrets.

## What Leaf Asks Google For

When you sign in, Google asks you to allow these. Leaf uses each one only for what's listed:

- **Your name, email address, and profile** (`openid`, `email`, `profile`): to tell your accounts apart.
- **Your calendars** (`calendar`, read and write): to show your events and to save the events you create, change, or delete, your replies to invitations, and the calendar names and default reminders you change in Leaf. Calendar colors, order, and which calendars show stay in Leaf.
- **Your contacts and "other contacts"** (`contacts.readonly`, `contacts.other.readonly`, read-only): to suggest people as you type a guest's name.
- **Your Google Workspace directory** (`directory.readonly`, read-only): to suggest coworkers as guests. Personal Google accounts don't have one, so nothing comes back.

Leaf also asks Google for people's free and busy times when you look at them (overlaying a teammate or finding a time). Google decides what you can see.

## Where Leaf Sends Data

- **Google.** Sign-in goes to Google's sign-in pages in your browser. Calendar and contact requests go to Google's Calendar and People APIs. All of it is over HTTPS.
- **Your Google Cloud project.** Leaf signs in with the OAuth client ID and secret you created in Google Cloud Console, so Google counts Leaf's requests against your own project. No one else's app or key is involved.
- **Links you open.** When you join a meeting or open a link, a location, or "View on GitHub", Leaf hands the address to your browser or the meeting app. A location opens in Google Maps or Bing Maps (Settings › General › Open locations in), so that site gets the location's text.

Leaf sends nothing else and runs no servers of its own.

## What Leaf Keeps On Your PC

In Leaf's app data folder (the `LocalState` folder of Leaf's package, under `%LOCALAPPDATA%\Packages`), for your Windows user only:

- **A copy of your calendars** in a SQLite database (`leaf.db`): your accounts' email addresses, your calendars, and their events (titles, times, descriptions, locations, guests, and meeting links), so Leaf opens quickly and works offline. Changes you make while offline wait there until Google has them.
- **Your Leaf settings**, in the same database.
- **Saved share times.** When you copy your availability, Leaf keeps the times, the title and the message you shared in its local database, so they stay on your calendar. They're deleted when you approve a time, delete the group, or the times pass. They never leave your PC.
- **A log** (`leaf.log` in the `Logs` folder, at most about 3 MB): what Leaf did and what went wrong, by internal IDs only. It never includes event titles, descriptions, guests, locations, searches, sign-in tokens, codes, or your client secret, and a second pass masks tokens, secrets, and email addresses anyway.
- **Crash files**, only while Settings › About › Detailed logging is on: at most two, in the log folder. A crash file can include bits of what was on screen. Turning Detailed logging off deletes them.

The database isn't encrypted by Leaf. Windows keeps it to your user account, and BitLocker encrypts it on disk if your PC uses BitLocker.

In `secrets.bin`, in the same folder, encrypted with Windows data protection (DPAPI) so only your Windows user can read it:

- Your OAuth client ID and secret.
- One sign-in token per Google account (a refresh token). Short-lived access tokens stay in memory and are never saved.

If `secrets.bin` ever can't be read (damaged, or made for another Windows user), Leaf moves it aside to `secrets.bin.unreadable`, still encrypted, before saving a new one.

Older versions of Leaf kept these in Windows Credential Locker. The first time an updated Leaf starts, it moves them into `secrets.bin` and deletes them from Credential Locker.

Leaf also shows Windows notifications for reminders, meetings, and invitations. Windows keeps those in its notification center until you clear them.

## Removing Your Data

- **Disconnect an account** (Settings › Accounts, open the account, then Disconnect): Leaf asks Google to revoke its access, deletes that account's sign-in token, and deletes its calendars and events from your PC. Your Google Calendar itself isn't changed.
- **Uninstall Leaf**: Windows deletes Leaf's app data folder, with the database, settings, log, crash files, and your sign-in secrets (OAuth client ID and secret, and sign-in tokens) once Leaf has moved them there. Uninstalling doesn't tell Google, so to revoke Leaf's access too, disconnect your accounts first or remove it at Google (below).
- **At Google**: you can remove Leaf's access at any time at [myaccount.google.com/permissions](https://myaccount.google.com/permissions), and delete the OAuth client in your Google Cloud project.

## Questions

Leaf Calendar is open source, so you can check all of this in the code at [github.com/artistro08/leaf-calendar](https://github.com/artistro08/leaf-calendar). Questions or problems go in the project's GitHub issues.
