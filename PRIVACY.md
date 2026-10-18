# Leaf Calendar Privacy

Leaf Calendar is a Windows app for your Google Calendar. This page says what it keeps on your PC, what it sends and where, and how to remove it.

## The Short Version

- Leaf Calendar talks to Google, through the Google Cloud OAuth client you create yourself, and to Cal.com and Sunsama only if you sign in to them (see Scheduling and Sunsama below). There's no Leaf Calendar server.
- Leaf Calendar has no telemetry, analytics, ads, or tracking. Nothing about you or how you use Leaf Calendar is sent anywhere else.
- Your calendar is kept on your PC so Leaf Calendar works quickly and offline. Your sign-in secrets are kept encrypted in Leaf Calendar's app data folder.
- Leaf Calendar's log never includes your events, guests, searches, or sign-in secrets.

## What Leaf Calendar Asks Google For

When you sign in, Google asks you to allow these. Leaf Calendar uses each one only for what's listed:

- **Your name, email address, and profile** (`openid`, `email`, `profile`): to tell your accounts apart.
- **Your calendars** (`calendar`, read and write): to show your events and to save the events you create, change, or delete, your replies to invitations, and the calendar names and default reminders you change in Leaf Calendar. Calendar colors, order, and which calendars show stay in Leaf Calendar.
- **Your contacts and "other contacts"** (`contacts.readonly`, `contacts.other.readonly`, read-only): to suggest people as you type a guest's name or email address. Leaf Calendar searches every account you've signed in with. People from your own events are suggested too; those come from the copy of your calendars on your PC, not from Google.
- **Your Google Workspace directory** (`directory.readonly`, read-only): to suggest coworkers as guests. Personal Google accounts don't have one, so nothing comes back.

Leaf Calendar also asks Google for people's free and busy times when you look at them (overlaying a teammate or finding a time). Google decides what you can see.

## Scheduling (Cal.com)

This only applies if you sign in to Cal.com in Settings › Accounts. Until you do, Leaf Calendar never contacts Cal.com.

- Leaf Calendar talks to Cal.com with your own Cal.com OAuth client (the client ID and secret you create in your Cal.com settings). Signing in opens Cal.com's sign-in page in your browser. Leaf Calendar asks Cal.com to let it read your booking links (`EVENT_TYPE_READ`), read and create bookings (`BOOKING_READ`, `BOOKING_WRITE`), and read your schedules (`SCHEDULE_READ`).
- Leaf Calendar sends date ranges to Cal.com while you share times, to ask which of your booking link's times are open. To check for bookings, it sends the link and the time you saved your times, to ask whether anyone booked. Leaf Calendar also asks for your list of booking links.
- Leaf Calendar sends your guests' names and email addresses, and the time zone you are sharing in, to Cal.com only when you press Book on a saved time. Cal.com then sends its own confirmation emails.
- Guest email addresses are also inside the links you copy, so anyone who gets a link can see them.
- Your Cal.com client secret and sign-in token are stored encrypted on this PC. Signing out removes the sign-in token. Removing the OAuth client (Settings › Accounts) removes the client ID and secret too.
- Leaf Calendar's log never includes Cal.com link names, booking addresses, attendee names or emails, or any secret.

## Sunsama

This only applies if you sign in to Sunsama in Settings › Accounts. Until you do, Leaf Calendar never contacts Sunsama.

- When you sign in for the first time, or after signing out, Leaf Calendar registers its own OAuth client with Sunsama (it has no secret). Signing in opens Sunsama's sign-in page in your browser. Leaf Calendar asks Sunsama only to read (`read`) and to stay signed in (`offline_access`).
- **What's read:** your Sunsama email and time zone, your calendars' IDs, and, for the days on screen, your tasks (title, channel, planned and tracked time, whether they're done, and their planned times) and your calendar events' IDs, titles, and times. Task notes may arrive with a task but aren't kept or shown.
- **What's sent:** nothing about your calendar. Leaf Calendar never changes anything in Sunsama or Google.
- **What's kept:** each task's title, channel, planned and tracked time, whether it's done, its times on your calendar, and the Google event it matches, in `leaf.db`, so they show offline. The client ID, sign-in token, and your Sunsama email are stored encrypted in `secrets.bin`.
- **Sign out** (Settings › Accounts) removes the sign-in token, client ID, and email, and every saved Sunsama task time. To revoke Leaf Calendar's access at Sunsama too, remove it in your Sunsama settings.
- Leaf Calendar's log never includes task titles, channels, your email, or any token.

## Location Suggestions (Google Maps)

This only applies if you turn on Location suggestions in Settings › General and save a Maps API key. Until you do, Leaf Calendar never contacts Google Maps.

- **What's sent:** the text you type into an event's location, with your Maps API key, to Google's Places API, so Google can suggest places. Apps you allowed in Settings › Connected apps can ask for the same suggestions. Google's own terms and privacy policy cover what Google does with it.
- **What's kept:** nothing from the suggestions, except the place you pick, which is saved in the event like any location you type.
- **Your key** is stored encrypted on your PC with your other Leaf Calendar secrets. Clear the key box in Settings › General to remove it, or turn the switch off to stop lookups.
- Leaf Calendar's log never includes what you type, the places Google suggests, or your key.

## Connected Apps

Connected apps is off until you turn it on in Settings › Connected apps. When it's on, apps on this PC that you allow can see your events (titles, times, locations, descriptions and guests), create and move events, join meetings, and use your Cal.com links. They connect through a channel only your Windows account can open, and Leaf Calendar never gives them your Google or Cal.com sign-in. Remove an app in Settings › Connected apps to stop it; turn the switch off to stop them all. An app may send its icon and a link back to itself when it connects; Leaf Calendar shows the icon in the approval window and opens the link after you allow it, and keeps neither.

## Where Leaf Calendar Sends Data

- **Google.** Sign-in goes to Google's sign-in pages in your browser. Calendar and contact requests go to Google's Calendar and People APIs. Location suggestions go to Google's Places API, with your own Maps API key, only while you have them turned on. All of it is over HTTPS.
- **Your Google Cloud project.** Leaf Calendar signs in with the OAuth client ID and secret you created in Google Cloud Console, so Google counts Leaf Calendar's requests against your own project. No one else's app or key is involved.
- **Connected apps.** To apps on this PC you allowed in Settings › Connected apps (off by default).
- **Links you open.** When you join a meeting or open a link, a location, or "View on GitHub", Leaf Calendar hands the address to your browser or the meeting app. A location opens in Google Maps or Bing Maps (Settings › General › Open locations in), so that site gets the location's text.
- **Sunsama.** Only if you sign in to it: the sign-in, and read-only requests to Sunsama's MCP server (`api.sunsama.com`) for the days on screen, over HTTPS.

Leaf Calendar sends nothing else (Cal.com only if you sign in to it, see Scheduling above; Sunsama only if you sign in to it; Google Maps only if you turn on location suggestions) and runs no servers of its own.

## What Leaf Calendar Keeps On Your PC

In Leaf Calendar's app data folder (the `LocalState` folder of Leaf Calendar's package, under `%LOCALAPPDATA%\Packages`), for your Windows user only:

- **A copy of your calendars** in a SQLite database (`leaf.db`): your accounts' email addresses, your calendars, and their events (titles, times, descriptions, locations, guests, and meeting links), so Leaf Calendar opens quickly and works offline. Changes you make while offline wait there until Google has them.
- **Your Leaf Calendar settings**, in the same database.
- **Sunsama task times**, only if you sign in to Sunsama: each task's title, channel, planned and tracked time, whether it's done, the times it's on your calendar, and the Google event it matches, for the days you've looked at, in the same database. Sign out deletes them.
- **Saved share times.** When you save or copy your availability, Leaf Calendar keeps the times, the title and the message you shared, and any guests you save with them, in its local database, so they stay on your calendar. Saved Cal.com times also keep the booking link's address. They're deleted when you approve a time, delete the group, the times pass, or Leaf Calendar finds a Cal.com booking for them. Approving a time on a Cal.com group sends its guests to Cal.com to book it; otherwise they never leave your PC.
- **A log** (`leaf.log` in the `Logs` folder, at most about 3 MB): what Leaf Calendar did and what went wrong, by internal IDs only. It never includes event titles, descriptions, guests, locations, searches, sign-in tokens, codes, or your client secret, and a second pass masks tokens, secrets, and email addresses anyway.
- **Crash files**, only while Settings › About › Detailed logging is on: at most two, in the log folder. A crash file can include bits of what was on screen. Turning Detailed logging off deletes them.

The database isn't encrypted by Leaf Calendar. Windows keeps it to your user account, and BitLocker encrypts it on disk if your PC uses BitLocker.

In `secrets.bin`, in the same folder, encrypted with Windows data protection (DPAPI) so only your Windows user can read it:

- Your OAuth client ID and secret, and the same for Cal.com if you set it up (the Cal.com sign-in token is stored there too).
- If you sign in to Sunsama: the client ID Leaf Calendar registered, your Sunsama sign-in token, and your Sunsama email.
- Your Maps API key, if you saved one for location suggestions.
- One sign-in token per Google account (a refresh token). Short-lived access tokens stay in memory and are never saved.

If `secrets.bin` ever can't be read (damaged, or made for another Windows user), Leaf Calendar moves it aside to `secrets.bin.unreadable`, still encrypted, before saving a new one.

Older versions of Leaf Calendar kept these in Windows Credential Locker. The first time an updated Leaf Calendar starts, it moves them into `secrets.bin` and deletes them from Credential Locker.

Leaf Calendar also shows Windows notifications for reminders, meetings, and invitations. Windows keeps those in its notification center until you clear them.

## Removing Your Data

- **Disconnect an account** (Settings › Accounts, open the account, then Disconnect): Leaf Calendar asks Google to revoke its access, deletes that account's sign-in token, and deletes its calendars and events from your PC. Your Google Calendar itself isn't changed.
- **Sign out of Cal.com** (Settings › Accounts): removes its sign-in token from your PC and turns your saved Cal.com share times into plain saved times; your Cal.com OAuth client stays. **Remove OAuth client** also removes the client ID and secret, and forgets your default Cal.com link. To revoke Leaf Calendar's access at Cal.com too, remove it in your Cal.com settings.
- **Sign out of Sunsama** (Settings › Accounts): removes the Sunsama sign-in token, client ID, and email from your PC, and deletes every saved Sunsama task time. To revoke Leaf Calendar's access at Sunsama too, remove it in your Sunsama settings.
- **Delete saved share times**: open the saved times on your calendar and use Delete saved times at the top of the panel. They aren't tied to an account, so disconnecting an account doesn't remove them; they also go once a time is approved or they pass.
- **Uninstall Leaf Calendar**: Windows deletes Leaf Calendar's app data folder, with the database, settings, log, crash files, and your sign-in secrets (OAuth client ID and secret, and sign-in tokens) once Leaf Calendar has moved them there. Uninstalling doesn't tell Google, so to revoke Leaf Calendar's access too, disconnect your accounts first or remove it at Google (below).
- **At Google**: you can remove Leaf Calendar's access at any time at [myaccount.google.com/permissions](https://myaccount.google.com/permissions), and delete the OAuth client in your Google Cloud project.

## Questions

Leaf Calendar is open source, so you can check all of this in the code at [github.com/artistro08/leaf-calendar](https://github.com/artistro08/leaf-calendar). Questions or problems go in the project's GitHub issues.
