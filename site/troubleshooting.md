---
title: Troubleshooting
nav_order: 12
---

# Troubleshooting

## Signing in

| What you see | What to do |
| --- | --- |
| **Error 403: access_denied**, "has not completed the Google verification process" | Your Google account isn't a test user on your OAuth client. Go to Google Cloud Console › **Google Auth Platform › Audience › Test users**, click **Add users**, and add your email. See [Google Cloud setup](../getting-started/google-cloud-setup/#4-add-yourself-as-a-test-user). |
| **Google hasn't verified this app** | Normal for your own OAuth client once it's published. Click **Advanced**, then **Go to (your app name)**. |
| You're signed out every 7 days | Your OAuth client is in **Testing**. Sign in again, or [publish the app](../getting-started/google-cloud-setup/#test-users-and-the-7-day-sign-out). |
| Leaf Calendar says to check the client ID and secret | Copy them again from **Google Auth Platform › Clients**, then update them in **Settings › Accounts › Change OAuth client**. |
| An account shows **Needs sign-in** | Click **Add Google account** in **Settings › Accounts** and sign in to that account again. Your changes are kept. |
| Guest suggestions say to turn on the People API | Enable the **People API** in Google Cloud Console › **APIs & Services › Library**. |

## Reminders and notifications

| What you see | What to do |
| --- | --- |
| No reminders | Check that Leaf Calendar is running (look for its tray icon), **Settings › General › Start with Windows** is on, and **Settings › Notifications › Reminder notifications** is on. Windows' **Do not disturb** and **Focus** also hide notifications. |
| The join shortcut doesn't work | Another app may use the same keys. Pick new ones in **Settings › Shortcuts**. |
| Typing AltGr+J or AltGr+K joins a meeting or opens the flyout | Your keyboard's AltGr is the same as `Ctrl+Alt`. Change the shortcuts in **Settings › Shortcuts**. |

## Sync

| What you see | What to do |
| --- | --- |
| The offline icon in the title bar | Leaf Calendar can't reach Google. Your changes are kept and sent once you're back online. See [Offline and sync](../offline-and-sync/). |
| A change needs your review | An event changed on Google and in Leaf Calendar at the same time. Pick which one to keep. See [conflicts](../offline-and-sync/#when-a-change-needs-your-review). |
| A new event in Google Calendar hasn't shown up | Leaf Calendar checks every 15 seconds while it's open. Choose **Sync now** from the tray menu to check right away. |

## Reporting a problem

1. Go to **Settings › About** and turn on **Detailed logging**.
2. Do whatever caused the problem again.
3. Click **Open log folder** and attach `leaf.log` to a [new issue on GitHub](https://github.com/artistro08/leaf-calendar/issues).
4. Turn **Detailed logging** off again.

> Leaf Calendar's log never includes your events, guests, searches, or sign-in secrets. Crash files (only made while Detailed logging is on) can include bits of what was on screen, so look before you share them.
