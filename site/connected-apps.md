---
title: Connected apps
nav_order: 10
---

# Connected apps

Let other apps on your PC work with your calendar through Leaf Calendar. For example, a [Raycast](https://www.raycast.com) extension can show your next meeting, create an event, or join a call without opening Leaf Calendar.

Connected apps can:

- See your events (titles, times, locations, descriptions, and guests)
- Create and move events
- Join meetings
- Use your Cal.com links

They never get your Google or Cal.com sign-in. Every change they make goes through Leaf Calendar, the same as a change you make yourself.

## Turning it on

It's off until you turn it on.

1. Go to **Settings › Connected apps**.
2. Turn on **Allow other apps to connect**.

![Settings › Connected apps with the switch on and the approved apps list](../assets/images/settings-connectedapps.png)

## Allowing an app

The first time an app tries to connect, Leaf Calendar asks you. A small window shows the app's name and icon with **Allow** and **Don't allow**.

![The window asking whether an app can connect to Leaf Calendar, with Allow and Don't allow](../assets/images/approval.png)

> Some apps run through a general tool like `node.exe`, `python.exe`, or PowerShell. If you allow one of those, every script that uses it can connect, so Leaf Calendar warns you first. Only allow it if you trust what's running.

![The approval window warning that a general tool is asking to connect](../assets/images/approval-runtime.png)

## Removing an app

Every app you've allowed is listed under **Approved apps** in **Settings › Connected apps**. Click **Remove** to stop it. Turn **Allow other apps to connect** off to stop them all.

## Only on your PC

Apps connect through a private channel that only your Windows account can open. Web pages can't use it, and other people who sign in to your PC can't either.

## For developers

Building something that talks to Leaf Calendar? See the [API reference](https://github.com/artistro08/leaf-calendar/blob/main/docs/api.md).
