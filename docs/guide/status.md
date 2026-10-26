---
title: "Status"
description: "What works, what is partial and what is not built in Nytka server v0.31.0 and Android app v0.23.0, with the test behind each."
order: 7
section: "Project"
---

This page describes server v0.31.0 and Android app v0.23.0, the latest GitHub releases of
[nytka-app/server](https://github.com/nytka-app/server/releases) and
[nytka-app/android](https://github.com/nytka-app/android/releases). There is no 1.0 release.
Roadmap milestones have names and release numbers come from release-please, so the two differ
([vision](../../docs/vision.md#roadmap)).

A test name here means a test exists in the server repository. It does not mean the behavior has
been verified over a week of wear: that is listed below as partial.

## Server

| Area | Status | Evidence |
|---|---|---|
| Chunk upload and capture-time placement | works | [`ChunkUploadTests.cs`](../../tests/Nytka.Server.Tests/Api/ChunkUploadTests.cs) |
| Silence removal | works | [`Vad` tests](../../tests/Nytka.Audio.Tests/Vad) |
| Transcription pipeline | works | [`TranscriptionPipelineTests.cs`](../../tests/Nytka.Server.Tests/Pipeline/TranscriptionPipelineTests.cs) |
| Offline-sync placement of late audio | works | [`OfflineSyncTests.cs`](../../tests/Nytka.Server.Tests/Pipeline/OfflineSyncTests.cs) |
| Mute schedule | works | [`MuteWindowTests.cs`](../../tests/Nytka.Server.Tests/Pipeline/MuteWindowTests.cs) |
| Search | works | [`SearchApiTests.cs`](../../tests/Nytka.Server.Tests/Search/SearchApiTests.cs) |
| Ukrainian search dictionary | partial | [`SearchDictionaryTests.cs`](../../tests/Nytka.Server.Tests/Search/SearchDictionaryTests.cs) |
| Webhooks | works | [`WebhookDeliveryTests.cs`](../../tests/Nytka.Server.Tests/Webhooks/WebhookDeliveryTests.cs) |
| MCP, 19 read tools and 3 write tools (admin token) | works | [`McpToolTests.cs`](../../tests/Nytka.Server.Tests/Mcp/McpToolTests.cs) |
| Token scopes | works | [`ScopeTests.cs`](../../tests/Nytka.Server.Tests/Auth/ScopeTests.cs) |
| Ask over your history | works | [`AskTests.cs`](../../tests/Nytka.Server.Tests/Ask/AskTests.cs) |
| Omi import | works | [`OmiImportTests.cs`](../../tests/Nytka.Server.Tests/Import/OmiImportTests.cs) |
| Full export | works | [`ExportApiTests.cs`](../../tests/Nytka.Server.Tests/Api/ExportApiTests.cs) |
| Daily digest | works | [`DigestTests.cs`](../../tests/Nytka.Server.Tests/Digests/DigestTests.cs) |
| Your voice | works | [`VoiceApiTests.cs`](../../tests/Nytka.Server.Tests/Voice/VoiceApiTests.cs) |
| Tags | works | [`TagApiTests.cs`](../../tests/Nytka.Server.Tests/Tags/TagApiTests.cs) |
| People, facts, name suggestions | works | [`NameSuggestionTests.cs`](../../tests/Nytka.Server.Tests/People/NameSuggestionTests.cs) |
| Voice grouping of other people | partial | [`VoiceGroupTests.cs`](../../tests/Nytka.Server.Tests/People/VoiceGroupTests.cs) |
| Calendar briefs | works | [`CalendarBriefTests.cs`](../../tests/Nytka.Server.Tests/Calendar/CalendarBriefTests.cs) |
| Coverage report for the wear test | works | [`CoverageReportTests.cs`](../../tests/Nytka.Server.Tests/Coverage/CoverageReportTests.cs) |
| Transcription usage ledger, `GET /api/v1/usage`, monthly budget | works | [`UsageReportTests.cs`](../../tests/Nytka.Server.Tests/Usage/UsageReportTests.cs) |
| Batch rules as settings | works | [`SpeechBatcherTests.cs`](../../tests/Nytka.Audio.Tests/Batching/SpeechBatcherTests.cs) |
| Failed-batch retry, `POST /api/v1/transcriptions/retry` | works | [`BatchRetryTests.cs`](../../tests/Nytka.Server.Tests/Pipeline/BatchRetryTests.cs) |
| Auto-applied names with undo; respelling | works | [`AutoNameTests.cs`](../../tests/Nytka.Server.Tests/People/AutoNameTests.cs), [`NameSpellingTests.cs`](../../tests/Nytka.Server.Tests/People/NameSpellingTests.cs) |
| Review clips and context lines | works | [`ReviewClipTests.cs`](../../tests/Nytka.Server.Tests/People/ReviewClipTests.cs) |
| Promise ledger | works | [`PersonPageTests.cs`](../../tests/Nytka.Server.Tests/People/PersonPageTests.cs) |
| Tasks by hand with due dates | works | [`TaskCreateTests.cs`](../../tests/Nytka.Server.Tests/Tasks/TaskCreateTests.cs) |
| Migrations through Switchknife.Migrations | works | [`MigratorAdoptionTests.cs`](../../tests/Nytka.Server.Tests/Storage/MigratorAdoptionTests.cs) |
| Import of transcript and audio files, with range checks | works | [`FileImportTests.cs`](../../tests/Nytka.Server.Tests/Import/FileImportTests.cs) |
| iCal feed (events, due tasks as VTODO) and Obsidian export (unique file names), webhook URLs kept out of logs | works | [`IcsWriterTests.cs`](../../tests/Nytka.Server.Tests/Calendar/IcsWriterTests.cs), [`ObsidianExportApiTests.cs`](../../tests/Nytka.Server.Tests/Api/ObsidianExportApiTests.cs) |

Two rows are marked partial because of how they are gated, not because tests are missing. The
dictionary tests skip until you fetch the Ukrainian dictionary, and CI sets
`NYTKA_REQUIRE_DICTIONARY=1` so a missing file fails the build
([`CLAUDE.md`](../../CLAUDE.md)). Voice grouping is off by default
(`Nytka__People__VoiceMatching=false`) and refused until your own voice is enrolled
([configuration](configuration.md#memories-people-tags-digest)). This page names test files; it does not report a test run.

## Partly built

**Nothing is lost.** The milestone asks for a week of wear with the official app uninstalled and
no lost audio. The vision file says the server parts are shipped (batches close at pauses, the mute
schedule drops audio, the summary language is a setting) and that the Bluetooth and sync fixes and
the week-long wear test are not yet verified
([vision](../../docs/vision.md#roadmap)). `GET /api/v1/coverage` measures it, and its design says a
clean report is not proof ([spec](../../docs/specs/coverage.md)), because it cannot see frames the
pendant drops before numbering them.

## Android app

The app is documented in [its own README](https://github.com/nytka-app/android#readme). Facts
checked against it:

- Installs from an APK on [GitHub Releases](https://github.com/nytka-app/android/releases/latest).
- Ask, tags, people, the review inbox with clips, tasks with due dates and names the server applied (with Undo) are described there as shipped.
- Offline sync and voice enrollment are described there.

## Not built

| Item | Where it is recorded |
|---|---|
| 1.0 release | [vision](../../docs/vision.md#roadmap) |
| F-Droid and IzzyOnDroid builds | [vision](../../docs/vision.md#roadmap) |
| Google Play build | [vision](../../docs/vision.md#distribution-and-money) |
| Live transcripts over WebSocket | [vision](../../docs/vision.md#roadmap) |
| Firmware updates from the app | [vision](../../docs/vision.md#roadmap) |
| Vector search | [vision](../../docs/vision.md#roadmap) |
| iOS, web and desktop clients | [vision](../../docs/vision.md#roadmap) |
| OAuth on the MCP endpoint | [MCP reference](mcp.md) |

Vision lists these as after 1.0, or as the 1.0 target. Not planned at all: a hosted service, user
accounts, sharing, a plugin store, several pendants per person, and database encryption inside
the app.

## Known limits

- The server needs an Omi pendant on consumer firmware 3.0.x; offline sync needs 3.0.20 or later.
- Anonymous speaker labels such as `SPEAKER_00` can mean different people in different batches,
  because each batch is transcribed on its own ([README](../reference/configuration.md#transcription-endpoints)).
- The pendant keeps no mute state, so while the phone is away it records anyway; the app drops
  muted stretches before upload ([vision](../../docs/vision.md#what-it-looks-like)).
- A `read` token cannot be used in the app, which needs `admin`.

## Report a problem

Open an issue on [the server](https://github.com/nytka-app/server/issues) or
[the app](https://github.com/nytka-app/android/issues). Contribution rules are in
[`CONTRIBUTING.md`](../../CONTRIBUTING.md).
