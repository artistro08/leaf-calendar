---
title: Install
parent: Getting started
nav_order: 1
---

# Install Leaf Calendar

Leaf Calendar ships as a signed `.msix` package on GitHub. Windows only installs it once you trust the certificate it's signed with, so there's one extra step the first time.

1. Download `LeafCalendar.cer` and the `.msix` file from the [latest release](https://github.com/artistro08/leaf-calendar/releases/latest).
2. Trust the certificate. Open PowerShell **as administrator** (right-click Start › Terminal (Admin)), go to your Downloads folder, and run this once:

    ```powershell
    Import-Certificate -FilePath .\LeafCalendar.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
    ```

3. Double-click the `.msix` file and click **Install**. Or run this in PowerShell, using the file name you downloaded:

    ```powershell
    Add-AppxPackage .\LeafCalendar_0.1.308.0_x64.msix
    ```

4. Open Leaf Calendar from the Start menu.

That's it!

> The certificate is the publisher's own, not one from a certificate company, which is why Windows needs you to trust it first. You only do this once. Later updates install over the top and keep your settings and sign-ins.

## Updating

Download the newer `.msix` from the [latest release](https://github.com/artistro08/leaf-calendar/releases/latest) and open it. Windows installs it as an update, so your settings, your OAuth client, and your sign-ins stay put.

## Uninstalling

Open **Settings › Apps › Installed apps** in Windows, find Leaf Calendar, and choose **Uninstall**. Windows removes the app along with everything it kept on your PC, including your saved sign-ins.

Your Google Calendar isn't changed. To also remove Leaf Calendar's access from your Google account, visit [your Google account's third-party connections](https://myaccount.google.com/connections) and remove your OAuth app there.
