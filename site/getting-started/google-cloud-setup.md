---
title: Google Cloud setup
parent: Getting started
nav_order: 2
---

# Set up your Google Cloud OAuth client

Leaf Calendar has no server of its own, so it signs in to Google with an OAuth client that **you** own. You create it once in Google Cloud Console, then paste its Client ID and Client secret into Leaf Calendar. It's free, and it takes about 10 minutes.

## Introduction

An OAuth client is how Google knows which app is asking for your calendar. Normally an app's maker owns it. With Leaf Calendar, you own it, which means:

- Your calendar goes straight between your PC and Google. Nobody else's server sees it.
- Google treats your OAuth client as an app that's still **in testing**. While it's in testing, Google only lets people on its **test users** list sign in. That's why adding yourself as a test user (step 4) is the step you can't skip.

### Prerequisites

- The Google account whose calendar you want in Leaf Calendar
- A web browser

## 1. Create a project

1. Open [Google Cloud Console](https://console.cloud.google.com/) and sign in with your Google account.
2. Open the project picker at the top of the page and click **New project**.
3. Name it anything you like (for example, "Leaf Calendar") and click **Create**.
4. Make sure the new project is picked in the project picker before you go on.

## 2. Turn on the Google APIs

1. Go to **APIs & Services › Library**.
2. Search for **Google Calendar API**, open it, and click **Enable**.
3. Go back to the library, search for **People API**, open it, and click **Enable**.

> The People API is what lets Leaf Calendar suggest your contacts when you add guests. Without it, everything else still works, but guest suggestions don't.

## 3. Set up Google Auth Platform

1. Go to **Google Auth Platform** (search for it in the top search bar if you don't see it in the menu).
2. Click **Get started**.
3. Fill in **App information**: an app name (for example, "Leaf Calendar") and your email as the support email. Click **Next**.
4. Under **Audience**, choose **External**. Click **Next**.
5. Enter your email as the contact address. Click **Next**.
6. Agree to Google's user data policy and click **Create**.

## 4. Add yourself as a test user

{: .important }
> This step is required. Until your Google account is on the test users list, Google blocks the sign-in with **"Error 403: access_denied"** and a message that the app "has not completed the Google verification process".

1. In Google Auth Platform, go to **Audience**.
2. Under **Test users**, click **Add users**.
3. Enter the email address of every Google account you'll sign in to Leaf Calendar with (your Gmail, your work account, and so on).
4. Click **Save**.

You can add up to 100 test users, and you can come back here any time to add another account.

### Test users and the 7-day sign-out

While your OAuth client's publishing status is **Testing**, Google signs test users out of it every 7 days. When that happens, Leaf Calendar shows a "Sign in again" notification and marks the account **Needs sign-in** in **Settings › Accounts**. Every change you've made is kept. To sign back in, click **Add Google account** in **Settings › Accounts** and pick the same Google account.

If you'd rather not sign in again every week, you can click **Publish app** on the **Audience** page. Your app moves to **In production** but stays unverified, which is fine for personal use. When you sign in, Google shows a **"Google hasn't verified this app"** warning: click **Advanced**, then **Go to (your app name)**. You're the developer of this app, so it's safe to continue.

## 5. (Optional) List the scopes

Leaf Calendar asks Google for these permissions when you sign in. You don't need to list them anywhere, but if you choose to fill in **Data access** in Google Auth Platform, add these:

| Scope | What Leaf Calendar uses it for |
| --- | --- |
| `openid`, `email`, `profile` | Knowing which Google account you signed in with |
| `.../auth/calendar` | Reading and editing your calendars and events |
| `.../auth/contacts.readonly` | Suggesting your contacts as guests (read-only) |
| `.../auth/contacts.other.readonly` | Suggesting people you've emailed as guests (read-only) |
| `.../auth/directory.readonly` | Suggesting coworkers from your Google Workspace directory (read-only) |

## 6. Create the OAuth client

1. In Google Auth Platform, go to **Clients** and click **Create client**.
2. For **Application type**, choose **Desktop app**.
3. Name it anything you like and click **Create**.
4. Copy the **Client ID** and the **Client secret** somewhere safe for the next step.

{: .warning }
> Google shows the client secret only once. If you lose it, open the client in **Clients** and add a new secret.

## 7. Paste them into Leaf Calendar

Open Leaf Calendar. The setup window asks for the Client ID and Client secret on its second step. Paste both, then follow [Sign in for the first time](../first-run/).

Already set up and need to change them? Go to **Settings › Accounts › Change OAuth client**.

## Troubleshooting

| What you see | What to do |
| --- | --- |
| **Error 403: access_denied**, "has not completed the Google verification process" | Your Google account isn't a test user. Add it in **Audience › Test users** (step 4), then sign in again. |
| **Google hasn't verified this app** | Expected once you've published the app. Click **Advanced**, then **Go to (your app name)**. |
| Leaf Calendar says to check the client ID and secret | The Client ID or Client secret is wrong, or the client was deleted. Copy them again from **Clients**, or create a new client, and update them in **Settings › Accounts › Change OAuth client**. |
| **Error 400: redirect_uri_mismatch** | The client isn't a **Desktop app** client. Create a new client with the **Desktop app** type. |
| You have to sign in again every week | Your app is in **Testing**. That's expected; see [the 7-day sign-out](#test-users-and-the-7-day-sign-out). |
| Guest suggestions say to turn on the People API | Enable the **People API** (step 2). |
