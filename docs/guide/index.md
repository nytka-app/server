---
title: "Nytka overview"
description: "A self-hosted server and Android app that keep an Omi pendant's recordings, transcripts and summaries on a machine you run."
order: 1
section: "Get started"
---

Nytka is a companion app and server for the Omi AI necklace. The Android app takes the
pendant's audio over Bluetooth and uploads it to your own server. The server transcribes it with
an endpoint you choose, groups it into conversations and serves them back. Omi's cloud sees none
of it ([README](../../README.md), [vision](../../docs/vision.md)).

It is for someone who owns an Omi pendant and already runs services with Docker Compose. A hosted
version for people who do not self-host is out of scope ([vision](../../docs/vision.md#who-it-is-for)).

## How it works

The pendant streams 20 ms Opus frames to the app. The app queues them and uploads a chunk about
every 30 seconds. The server drops silence with Silero VAD, sends the speech to your
transcription endpoint and stores the result in Postgres. A conversation ends after two minutes
without speech (`Nytka__Conversations__Gap`). With a language model you configure, each closed
conversation gets a title, a summary and tasks, and the server keeps lasting facts about you as
memories. Search, webhooks and a read-only MCP endpoint sit on top. [Architecture](architecture.md)
has the detail.

## What is in the two repositories

| Repository | Holds | Version checked |
|---|---|---|
| [nytka-app/server](https://github.com/nytka-app/server) | ASP.NET Core server, Postgres, MCP | v0.19.0 |
| [nytka-app/android](https://github.com/nytka-app/android) | Kotlin app for Android 12+ | v0.15.0 |

Each repository has its own version. Roadmap milestones have names, and release numbers come from
release-please, so the two do not match ([vision](../../docs/vision.md#roadmap)).

## What it does not do

- No hosted service, user accounts or sharing. One server belongs to one person.
- No iOS, web or desktop client. The app is Android only.
- No live transcripts. Audio is transcribed in batches after upload.
- No OAuth on the MCP endpoint, so a client that can only sign in through OAuth cannot connect
  ([MCP reference](mcp.md)).
- No 1.0 release yet. [Status](status.md) lists what works, what is partial and what is not built.

> [!NOTE]
> Nytka records the people around the wearer. You are responsible for following the recording and
> privacy laws where you use it ([README](../../README.md#license)).

## Where to go next

Start with the [quickstart](quickstart.md) to run the server, then
[install the Android app](android-app.md). To let an AI agent read your conversations, see the
[MCP reference](mcp.md). Every variable is in the [configuration reference](configuration.md).
