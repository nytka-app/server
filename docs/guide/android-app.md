---
title: "Install the Android app"
description: "Install the Nytka APK, connect it to your server and pair the Omi pendant."
order: 3
section: "Get started"
---

The app lives in [nytka-app/android](https://github.com/nytka-app/android). This page is a summary
of its [README](https://github.com/nytka-app/android#readme) at v0.15.0; that README is the
source if the two differ.

## What you need

- A phone on Android 12 or later, with Bluetooth on.
- An Omi pendant on consumer firmware 3.0.x. Nytka checks the audio codec when it connects and
  says if the firmware needs an update. Offline sync needs firmware 3.0.20 or later; live capture
  works without it.
- Your server's address and an `admin` token: `Nytka__AdminToken` from the server's `.env`, or an
  admin token made in the app. A `read` token is refused with "The app needs an admin token."

## Install

1. Uninstall the official Omi app, or force-stop it. Two apps cannot hold the pendant at once.
2. On the phone, download `nytka-<version>.apk` from the
   [latest release](https://github.com/nytka-app/android/releases/latest) and open it. Allow your
   browser to install apps when Android asks, then tap **Install**. From a computer, run
   `adb install nytka-<version>.apk`.

A newer APK installs over the old one and keeps the queue and settings.

> [!NOTE]
> The only install route today is the APK on GitHub Releases. F-Droid and IzzyOnDroid are
> targets for 1.0, not shipped ([vision](../../docs/vision.md#roadmap)).

## First run

Charge the pendant and keep it next to the phone. Open Nytka; the *Set up Nytka* screen has four
steps.

1. **Server.** Enter the address (`https://...`) and the admin token, then tap **Test connection**.
   The next step opens once the server answers and accepts the token.
2. **Permissions.** Tap **Allow**. Android asks for *Nearby devices* and, on Android 13 and later,
   *Notifications*. Nytka asks for nothing else and never uses the microphone or location.
3. **Consent.** Read the note on recording people, tick **I understand** and tap **Continue**.
4. **Pairing.** Turn the pendant on, tap **Pair pendant** and pick it from the list. **Set up
   later** skips this; the **Device** tab has the same button.

Get the token from the server machine without printing it into a shared log:

```bash title="Read the admin token on the server machine"
grep '^Nytka__AdminToken=' .env
```

Copy it to the phone through a password manager or by typing it. Do not paste it into a chat or a
note that syncs elsewhere.

## Check that it works

The chip at the top reads **Waiting** while the phone connects, then **Recording** with the
pendant's battery. Say something. Within a minute the status card shows "Server: Last upload",
and the speech appears under **Conversations** after the silence gap (two minutes by default).
Pull the list down to refresh. If nothing appears, check the server with
`GET /api/v1/status` ([quickstart](quickstart.md#4-read-the-status)).

## Plain HTTP on a private network

The app requires HTTPS. A server reached over a VPN without HTTPS needs the **Private network
(allow plain HTTP)** switch on the server step and an address such as `http://<VPN address>:8080`.
That carries your audio unencrypted, so use it only on a network you trust.

## What the app has

The tabs are Conversations, Tasks, Memories, People, Ask and Device. Offline sync, search, tags,
the review inbox and voice enrollment are in the
[Android README](https://github.com/nytka-app/android#readme). The Ask tab is a placeholder there
("Arrives in a later version"), although the server's `POST /api/v1/ask` and the `ask` MCP tool
work ([status](status.md)).

Seven taps on the version number under Device unlock developer mode, which holds server settings,
access tokens, webhooks and diagnostics.
