# Nytka

Nytka is a companion app and server for the Omi AI necklace. It keeps every recording, transcript
and summary on a server you run. The pendant streams audio to the Nytka Android app, the app
forwards it to your Nytka server, and the server transcribes and summarizes it with models you
pick, so Omi's cloud sees none of it.

Nytka (нитка) is Ukrainian for thread: the thread of your day, on your own server.

> **Status: design.** This repository still holds omi-platform, a warehouse that pulls data from
> Omi's cloud API. Version 0.1 of the Nytka server replaces that code, and git history keeps it.

## Why Nytka exists

The official Omi app sends your transcripts to Omi's cloud, where Omi's servers run language models
over them to write summaries, memories and action items. The app lets you swap the transcription
endpoint and nothing else. You cannot choose the model that reads your conversations, and you cannot
move that step onto your own hardware. Nytka replaces the app and the cloud with a pair you control:
an Android app that talks to the pendant, and a server that does the rest.

## Who it is for

You own an Omi necklace and you already run services yourself: Docker Compose on a home server or a
VPS, reached through a reverse proxy or a VPN such as Tailscale. You can edit an `.env` file and
read container logs. Nytka assumes that much. A hosted version for people who do not self-host is
out of scope.

## Principles

Nytka follows five rules.

1. **Your server is the only backend.** The app talks to one URL, yours. Nytka has no vendor cloud,
   no telemetry and no analytics.
2. **You bring the models.** Transcription and language models plug in through OpenAI-compatible
   endpoints. That covers hosted providers, gateways such as LiteLLM, and local servers such as
   Ollama or a Whisper server.
3. **Audio is sensitive.** The server drops silence as soon as it processes a chunk, keeps speech
   audio for a number of days you set (14 by default), and sends audio nowhere except the
   transcription endpoint you configured.
4. **Thin app, smart server.** The app captures, queues and displays. The server splits,
   transcribes, summarizes and stores, so you change a prompt or a model without installing a new
   app.
5. **Small, usable releases.** Every version works end to end. Both parts follow semantic
   versioning, and the API carries its version in the URL.

## How it works

```
pendant ──BLE──▶ Nytka app ──HTTPS──▶ Nytka server ──▶ Postgres
(Opus frames)    (queue, upload)       ├──▶ your transcription endpoint
                                       └──▶ your language model (from v0.2)
```

**The pendant** records the whole time it is on. While the phone is connected, it streams 20 ms Opus
frames over Bluetooth Low Energy. While the phone is away, it writes audio to its own storage and
overwrites the oldest audio once that storage fills.

**The app** runs on Android 12 and later. It holds the Bluetooth connection in a foreground service,
strips the Bluetooth packet headers, and keeps the frames in a local queue. Every 30 seconds it
uploads the queued frames as one chunk. When the server is unreachable the queue grows, and it
drains after the connection returns. The app never decodes or re-encodes audio, and it never touches
the phone's microphone.

**The server** runs in Docker next to Postgres. It decodes the Opus frames, drops silence with a
voice-activity detector, and sends the speech to your transcription endpoint. It groups the
transcript into conversations: a conversation ends after two minutes without speech. From v0.2 it
asks your language model for a title, a summary and tasks for each conversation. The app reads
everything over a REST API, and AI tools read it over MCP.

Frames travel in one format: a sequence number, a capture time and the Opus payload. Version 0.1
uploads them over HTTP; a later version streams the same frames over WebSocket for live transcripts.
The server handles a frame the same way whichever transport delivered it.

## What it looks like

The app follows Material 3 with dynamic color, so it takes its palette from your wallpaper. Its icon
is one thread that turns into a sound wave, linen on indigo.

The finished app has five tabs: Conversations, Tasks, Memories, Ask and Device. A status chip in the
top bar shows recording state and pendant battery on every tab. Version 0.1 ships Conversations and
Device, and its navigation leaves room for the other three.

Conversations opens with a status card: recording or muted, pendant battery, server state, queued
uploads and a Mute button. Your conversations follow, grouped by day. A conversation shows its time
range and length, then the transcript as timestamped paragraphs. From v0.2 speaker names appear in
color. You can delete a conversation, which removes its audio and transcript from the server.
Renaming arrives with titles in v0.2, sharing as text comes later, and transcripts stay read-only.

Device gathers everything about the pendant and the server: connection, battery, the upload queue,
pairing and settings.

The pendant button has two gestures. A double tap mutes or unmutes Nytka, and the pendant confirms
with one long buzz for muted and two short buzzes for live. While muted, the app stops listening and
the pendant discards the audio, so nothing gets stored. A single tap will drop a bookmark into the
transcript in a later version. Holding the button for three seconds powers the pendant off, and the
firmware sends no long-press event, so Nytka has no third gesture to use.

A persistent notification shows the recording state, battery and queue, with a Mute action. Beyond
that Nytka stays silent, with three exceptions: the pendant has been disconnected for five minutes,
the server has been unreachable for fifteen minutes, or the pendant battery has dropped to 20%.

First run takes four steps. You enter the server URL and token and test the connection, grant the
nearby-devices and notification permissions, confirm that you know recording people carries legal
duties that differ by country, and pair the pendant.

Seven taps on the version number unlock developer mode: server settings, Bluetooth and upload
diagnostics, a fake pendant that replays recorded audio, the raw transcription response for any
conversation, and a button that copies a debug report.

The interface is in English. Transcription detects the spoken language unless you set one, and a
setting chooses the language for summaries, tasks and memories. By default they follow the
conversation.

## Roadmap

- **v0.1, capture.** Pairing, reconnection and battery. Background capture with mute. Upload with an
  offline queue. Transcription and conversation splitting on the server. The conversation list and
  transcripts. Server URL and token settings, the consent step and developer-mode diagnostics.
  Version 0.1 is done when someone wears the pendant for a full day with the official app uninstalled
  and the phone nearby, and every conversation shows up. Details: [specs/v0.1.md](specs/v0.1.md).
- **v0.2, the AI layer.** Titles, summaries and tasks for each conversation, and a Tasks tab. Speaker
  labels when the transcription provider returns them. A read-only MCP endpoint. Server settings
  editable from the app. Named tokens with `admin` and `read` scopes.
- **v0.3, offline sync.** The app pulls the audio the pendant stored while the phone was away, so
  leaving your phone behind stops costing you conversations.
- **v0.4, memory and search.** Memories (lasting facts about you) with their own tab, full-text
  search and outgoing webhooks.
- **Later.** Questions about your history, live transcripts, bookmarks from the pendant button, audio
  playback, a daily digest, import from Omi's "Export All Data" file, firmware updates, pendant
  settings (LED and mic gain), voice enrollment on the server so it recognizes your voice with any
  transcription provider, stripping personal data before text reaches a cloud model, people and
  calendar extraction, opt-in location tags, a home-screen widget, full data export, and iOS, desktop
  or web clients.
- **Not planned.** A hosted service, user accounts, sharing, a plugin store, a Wear OS tile,
  transcription on the phone, several pendants per person, and database encryption inside the app
  (encrypt the disk instead).

## Configuration and security

You configure the server with environment variables in its Compose file: the transcription
endpoint, key and model; the language model endpoint, key and model (from v0.2); the admin token;
audio retention; and the silence gap that ends a conversation. From v0.2 you can also edit settings
in the app's developer mode. A value set by an environment variable shows as locked there, and the
app can write API keys but never read them back.

A Nytka server belongs to one person. The admin token in the environment always works, so you cannot
lock yourself out. From v0.2 you can create named tokens with one of two scopes: `admin` for the app
and `read` for MCP clients and agents. The server stores a hash of each token and shows the token
once, when you create it.

The app requires HTTPS. A private-network switch allows plain HTTP for a server you reach over a VPN
or tailnet, and the app warns you when you turn it on.

## Distribution and money

Nytka is free on GitHub Releases and F-Droid, and every build has every feature. Nytka locks nothing
behind a payment. Donations go through Ko-fi, with a monobank jar for donors in Ukraine.

Later Nytka will appear on Google Play as a paid app, where the purchase counts as your support. The
Play build leaves out links to outside payments, which Play's rules forbid, and matches the GitHub
build in everything else.

At 1.0, people who installed a free build can buy a supporter key. The key is a signed thank-you that
adds a Supporter badge. The app checks the signature offline, so Nytka runs no activation server and
nothing phones home.

## The project

- **Repositories.** `nytka-app/android` holds the Kotlin app. `nytka-app/server` holds this code.
- **License.** Apache-2.0. Code adapted from the official Omi app (MIT) keeps its notice in
  `NOTICE`. The license grants no trademark rights, so a fork can reuse the code but not the Nytka
  name or icon.
- **Versions.** Each repository has its own semantic version. Release-please turns conventional
  commits into releases. CI builds a signed APK for the app, and an amd64 and arm64 image,
  `ghcr.io/nytka-app/server`, for the server.
- **Docs.** This file holds the whole picture. `docs/specs/` holds one spec per version. Setup guides
  and provider recipes will sit next to them.
- **Independence.** Based Hardware makes Omi and owns its name. Nytka is an independent project with
  no affiliation to them.

## Key decisions

| Decision | Reason |
|---|---|
| Thin app, smart server | You change prompts and models without shipping an app, and the phone does little work. |
| Kotlin, Android only | The official Omi app handles Bluetooth and its foreground service in native Kotlin under MIT, and Nytka reuses that code. iOS waits for a contributor with an iPhone. |
| Android 12 and later | One Bluetooth permission model, with no location permission needed to scan. |
| Raw Opus to the server | Decoding on the phone adds no quality and multiplies the upload size. |
| HTTP chunks first | Chunks survive flaky mobile networks. WebSocket arrives with live transcripts. |
| .NET, Postgres, Dapper, DbUp | The maintainer's stack. Plain SQL suits jsonb, full-text search and pgvector. |
| Hilt | It injects dependencies into the foreground service and view models, and swaps in the fake pendant for tests. |
| One person per server | A household runs two servers. Accounts would crowd out the first versions. |
| Apache-2.0 | It grants a patent license and keeps the name out of the license. |
| `io.github.nytka_app` | A permanent app ID with no domain to buy. The website lives on GitHub Pages. |
| Two-minute conversation gap | A starting value; each server can tune it. |

## Glossary

| Term | Meaning |
|---|---|
| Pendant | The Omi necklace. |
| Frame | One 20 ms Opus audio frame, with a sequence number and a capture time. |
| Chunk | A batch of frames the app uploads in one request. |
| Conversation | Speech with no gap of two minutes or more inside it. |
| Segment | One transcribed stretch of a conversation, with an optional speaker. |
| Task | Something to do, taken from a conversation. Omi's "action items" import as tasks. |
| Memory | A lasting fact about you, taken from your conversations. |
| Token | A named credential for the server, with scope `admin` or `read`. |
| Developer mode | Hidden settings, unlocked by tapping the version number seven times. |
