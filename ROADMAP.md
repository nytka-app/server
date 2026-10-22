# Roadmap

Each milestone says what changes for you when it is done. Milestones have names, not dates or
release numbers: the server and the app each version themselves, and what each release changed is in
[CHANGELOG.md](CHANGELOG.md) here and in [nytka-app/android](https://github.com/nytka-app/android/blob/main/CHANGELOG.md).
Why Nytka is built this way: [docs/vision.md](docs/vision.md).

Order is deliberate. Nytka grows only after it works: it must replace the official Omi app for its
maintainer before strangers are asked to install it, and strangers must be able to install it
before it chases features.

Legend: ✅ done · ⏳ in progress · ▶ next · · later

## Stage 0 — It works for its maintainer

Shipped and running on the maintainer's server. "Server only" marks a milestone the app does not
show yet.

- ✅ **Capture.** The pendant streams to the app over Bluetooth; the app queues audio and uploads it
  in 30-second chunks; the server drops silence, transcribes speech with your provider and groups it
  into conversations. A double tap on the pendant mutes. The app records link and upload diagnostics.
- ✅ **The AI layer.** Every conversation has a title, a summary and tasks. AI agents read it over a
  MCP endpoint (MCP is the protocol AI tools use): 13 read tools, plus 3 write tools (create a task,
  complete a task, create a memory) behind an `admin` token. Named access tokens with
  `admin` and `read` scopes; server settings editable from the app.
- ✅ **Offline sync.** Audio the pendant stored while the phone was away reaches the server, placed at
  the time it was recorded, and the pendant frees it only after the server has it.
- ✅ **Memory and search.** Lasting facts about you (memories), full-text search in Ukrainian and
  English, and webhooks that tell your other tools when something is new.
- ✅ **Leaving Omi.** Import of Omi's "Export All Data" file, a full export of everything Nytka holds,
  and a daily digest delivered through a webhook.
- ✅ **Moments and questions.** A single tap drops a bookmark; you can play back what was said; the
  Ask tab answers from your history with numbered sources; LED and microphone settings; a notice
  when Omi publishes newer pendant firmware.
- ✅ **Your voice.** You enroll your voice from the pendant, so your lines are marked as yours with
  any transcription provider, and you can mark a line as yours or not yours by hand.
- ✅ **People.** Name suggestions from what voices say about themselves, "Who is this?" cards with
  audio clips, a page per person with facts, tasks owed to a person, a brief before a calendar
  meeting, and one review inbox. Grouping other people's voices is opt-in.
- ✅ **Tags and roles.** Tags on conversations and people that filter lists, search and MCP; roles
  for someone known by what they do before you know their name; tags the model proposes, applied
  only when you accept them.
- ✅ **Tasks that mean something** (server only). Each task candidate is sorted into a commitment,
  an idea, advice or noise, and what others promise you is kept as "waiting on".
- ✅ **Custom vocabulary.** A setting lists your names and terms so transcription spells them right.
- ✅ **Tasks by hand, promises and names that fix themselves.** Create a task yourself with a due date;
  a promise ledger per person; sure name suggestions apply on their own, with undo; a renamed person's
  name is respelled in generated text.
- ✅ **Retries and a budget.** Failed transcription batches retry (`POST /api/v1/transcriptions/retry`);
  every paid transcription is logged (`GET /api/v1/usage`) and a monthly budget can stop spending.
- ✅ **Install and bring in.** One command starts the server with HTTPS, and an optional local profile
  needs no account or API key. The server mints a pairing QR code (the app does not scan it yet).
  It imports transcript files (SRT, VTT, TXT) and audio files as well as Omi's export.
- ✅ **Fits your other tools** (server side). An iCal feed of events and due tasks
  (`/api/v1/calendar.ics`), and a Markdown export for an Obsidian vault.
- ✅ **The app in Ukrainian**, and an optional chime that tells people nearby you are recording.

## Stage 1 — It replaces Omi for its maintainer

- ⏳ **Nothing is lost.** A week of wear with the Omi app uninstalled loses no audio, and the
  coverage report shows it.
  - Shipped: batches close at pauses, a weekly mute schedule, the coverage report.
  - Left: link drops while the phone sleeps (Android's Doze), the cause of upload errors (HTTP 500), a walk test out of Bluetooth range,
    then the week itself.
  - Done when: over seven days the coverage report shows no missing chunk, no audio that never
    arrived and no lost pendant storage, link loss under 0.1% of the audio received, and unobserved
    time under 1% of the week ([pass rule](docs/specs/coverage.md#pass-rule-proposed)).
- ⏳ **Output worth reading.** Most of what Nytka writes is worth keeping.
  - Shipped: prompts in local time, tasks only for what you committed to, memories only for lasting
    facts, one-line summaries for short talk, the task kinds, custom vocabulary, and skipping a task
    that matches an open one from an earlier conversation.
  - Left: a fresh rating; a stronger model tried, with its cost per day.
  - Done when: of 20 rated, at least 15 summaries, 10 tasks and 10 memories are worth keeping. The
    last rating, before the fixes above, found 38% of summaries, 28% of tasks and 17% of memories
    useful.
- ⏳ **TV and calls stay out.** Speech from a TV, a video or a phone call no longer becomes people,
  facts or memories.
  - Shipped: the server guesses the kind of each stretch of speech and labels it, but changes nothing
    yet (shadow mode); the app shows the labels, hides media on request and asks about doubtful
    stretches in the review inbox.
  - Left: the check, then switching it on and backfilling conversations stored before that, and its
    docs.
  - Done when: of the last 100 reviewed stretches guessed media, at least 95 are media, and of the
    last 100 guessed unsure, at least 80 are people; the guess is on and the backfill has run
    ([spec](docs/specs/speech-kind.md)).

## Stage 2 — Anyone can run it (ends with 1.0)

- ▶ **The shop window.** Someone who lands on GitHub understands Nytka in a minute: current
  descriptions, a demo GIF, screenshots, a comparison with Omi and the other open-source options,
  and docs that match the releases.
- · **A stranger installs it in 15 minutes.** One command starts the server with HTTPS (shipped). An optional
  profile runs local speech-to-text and a local model, so the first run needs no account and no API
  key (shipped). The server shows a pairing QR code (shipped); the app should scan it instead of
  asking for a URL and a token. The app is on IzzyOnDroid or F-Droid, and its ID is registered for Android's developer verification.
  - Done when: a person who has never seen Nytka installs it from the docs in 15 minutes, timed.
- · **A second person uses it for a week.** A soft launch in Omi's Discord and on r/selfhosted brings
  the first outside users, and what they report is fixed.
  - Done when: one person other than the maintainer wears it for a week and keeps using it.
- · **Limitless owners can switch.** The app connects to the Limitless pendant, streams from it and
  syncs the audio it stores, using a port of the official Omi app's MIT-licensed Limitless
  connector. The server imports a Limitless export (plain audio and
  transcript files, SRT, VTT and TXT, already import). Limitless sends Opus like Omi, so the server's
  audio path does not change once the app hands it 20 ms frames. Plan: [docs/specs/multi-device.md](docs/specs/multi-device.md).
  - Done when: a Limitless pendant runs a day with no Limitless app, and a Limitless export imports.
  - Why now: Limitless stopped selling its pendant on 5 December 2025 and supports existing pendants
    throughout 2026.
- · **1.0.** The milestones above are done, donations are open, and Nytka is announced on Hacker
  News and r/selfhosted.

## Stage 3 — Better than the official apps for daily use (after 1.0)

The order inside this stage is not fixed yet.

- · **It keeps itself tidy.** A nightly pass proposes merging duplicate memories and people, fixing
  name spellings, and closing tasks that a later conversation shows as done. Proposals go to the
  review inbox; nothing changes without your yes. You can merge two conversations or split one.
- · **It fits your other tools.** A CalDAV list of tasks, so Nextcloud, Apple and Google calendars
  show them (the iCal feed already carries events and due tasks); a Home Assistant integration; MCP
  with OAuth sign-in (the write tools and the Obsidian export are shipped).
- · **It answers better.** Vector search behind Ask; summary templates by kind of conversation
  (meeting, lecture, doctor's visit); an optional local model, or personal data removed before text
  reaches a cloud model.
- · **It is live.** Transcripts while you talk, over WebSocket; pendant firmware updates from the app;
  a home-screen widget; opt-in location tags (neither is in the code yet).
- · **More pendants.** Plaud and Fieldy, ported from the same Omi connectors. Both send Opus, so the
  server's audio path does not change; Omi's connectors read neither one's stored audio, so the README says
  what is lost while the phone is away. Bee is left out while its firmware encrypts audio. Plan:
  [docs/specs/multi-device.md](docs/specs/multi-device.md).
- · **More screens.** A web client first; desktop and iOS when someone builds them.

## Stage 4 — Someone else can run it for you (decided after 1.0)

- · **Ready to host.** Nytka reads its own export back, so you can restore after a lost disk or move
  to another server; it counts usage per day (audio minutes, model tokens, storage); and the memory
  and CPU one server needs are measured. All three help self-hosters too.
- · **Hosted Nytka, if it is decided.** One Nytka server per person from the same image: no shared
  database, no user accounts, and anyone can export and move to their own server.

## Decided later

- The phone's microphone as a recorder, so no pendant is needed. It breaks the rule that the app
  never touches the phone's microphone, and Android limits microphone use from the background.
- How Nytka is paid for beyond donations: a Google Play listing, or a supporter key that unlocks
  nothing.

## Not planned

User accounts, sharing, a plugin store, a Wear OS tile, transcription on the phone, more than one
pendant connected at a time, and database encryption inside the app (encrypt the disk instead).
