# Install the server

How I set up a Nytka server: the 15-minute install, then bringing in recordings from Omi and other sources. Settings are in [Configuration](../reference/configuration.md).

## Install in 15 minutes

For a person with an Omi pendant and a computer or small server that stays on. The times add up to
about 15 minutes when the prerequisites are in place; the [last list](#what-does-not-fit-in-15-minutes)
says what is not counted.

### Before you start

- A Linux, macOS or Windows machine with [Docker Compose](https://docs.docker.com/compose/install/)
  that runs while you wear the pendant. A VPS or a home server is best; a laptop that sleeps loses
  nothing, because the app keeps the audio and uploads it later, but conversations then appear late.
- A transcription account, unless you use the no-key local profile below. The cheapest working
  hosted choice is [Groq](https://console.groq.com): a free tier covers 28,800 seconds of audio a
  day for `whisper-large-v3-turbo` (check the current limits there). The server drops silence
  before it sends anything, so a day of wearing stays far below that. OpenAI's `whisper-1` also
  works and is paid by the minute. Create an API key and keep it.
- A way for the phone to reach the server over HTTPS. The easiest is [Tailscale](https://tailscale.com):
  the server machine and the phone both join your tailnet, and the next section gives the server an
  `https://` address with a real certificate. A domain with a reverse proxy works too.
- The pendant charged, on consumer firmware 3.0.x, and an Android 12+ phone.

### Fastest: one command, no account, no key

On a Linux or macOS host with Docker:

```bash
curl -fsSL https://raw.githubusercontent.com/nytka-app/server/main/scripts/install.sh | bash
```

This creates `./nytka`, writes `docker-compose.yml` and a `.env` with a generated admin token and
Postgres password (never printed, except the admin token once at the end), and starts the stack
with the `local` Compose profile: a speech-to-text server
([faster-whisper-server](https://github.com/fedirz/faster-whisper-server)) and a language model
server ([Ollama](https://ollama.com)) running alongside Postgres and the server, wired through
`Nytka__Stt__Url` and `Nytka__Llm__BaseUrl` like any other endpoint. The first run needs no
transcription account and no API key, at the cost that **local models are slower and less accurate
than a hosted provider**: expect a longer wait for a transcript and for a summary, and more
mistakes in both, especially outside English. Switch to a hosted provider any time by editing
`.env` ([Configuration](../reference/configuration.md#configuration)) and running `docker compose up -d` again. Either way,
continue at [step 4](#4-give-the-phone-an-https-address-3-minutes-with-tailscale) below.

### With a hosted transcription account, step by step

#### 1. Download the Compose file and the settings template (1 minute)

```bash
mkdir nytka && cd nytka
curl -fsSLO https://raw.githubusercontent.com/nytka-app/server/main/docker-compose.yml
curl -fsSL -o .env https://raw.githubusercontent.com/nytka-app/server/main/.env.example
```

#### 2. Fill in `.env` (3 minutes)

Generate the two secrets and write them in, with the Groq endpoint, key and model:

```bash
sed -i.bak \
  -e "s|^POSTGRES_PASSWORD=.*|POSTGRES_PASSWORD=$(openssl rand -hex 24)|" \
  -e "s|^Nytka__AdminToken=.*|Nytka__AdminToken=$(openssl rand -hex 24)|" \
  -e "s|^Nytka__Stt__Url=.*|Nytka__Stt__Url=https://api.groq.com/openai/v1/audio/transcriptions|" \
  -e "s|^# Nytka__Stt__ApiKey=.*|Nytka__Stt__ApiKey=PASTE_YOUR_GROQ_KEY|" \
  -e "s|^# Nytka__Stt__Model=.*|Nytka__Stt__Model=whisper-large-v3-turbo|" .env
```

Replace `PASTE_YOUR_GROQ_KEY` in `.env` with your key (open the file in an editor; do not paste the key
into a shared terminal log). With OpenAI use `https://api.openai.com/v1/audio/transcriptions` and
`whisper-1`. Other endpoints, including a self-hosted whisper.cpp, are in
[Transcription endpoints](../reference/configuration.md#transcription-endpoints). Leave everything else commented out: a
variable that is set locks its setting in the app ([Configuration](../reference/configuration.md#configuration)).

#### 3. Start it and check (2 minutes)

```bash
docker compose up -d --wait
curl http://127.0.0.1:8080/healthz
```

The first start pulls two images. `healthz` answers `{"status":"healthy"}`. If the server does not
come up, `docker compose logs server` says why, for example a token shorter than 32 characters.

#### 4. Give the phone an HTTPS address (3 minutes with Tailscale)

The server listens on `127.0.0.1:8080` only. With Tailscale installed and signed in on the server
machine:

```bash
tailscale serve --bg --https=443 localhost:8080
tailscale serve status
```

`status` prints the address, `https://<machine>.<tailnet>.ts.net`. Install Tailscale on the phone,
sign in to the same tailnet and keep it on. (If the command says HTTPS certificates are off, enable
HTTPS in the Tailscale admin console under DNS, then run it again.) With a domain instead, point a
reverse proxy that terminates HTTPS, such as Caddy, at `127.0.0.1:8080`. On a VPN without HTTPS,
set `NYTKA_BIND` in `.env` to the VPN address, run `docker compose up -d` and use
`http://<that address>:8080` with the app's private-network switch, which sends audio unencrypted.

#### 5. Install the app and pair the pendant (4 minutes)

Stop the official Omi app (two apps cannot hold the pendant). Install `nytka-<version>.apk` from the
[app's releases](https://github.com/nytka-app/android/releases/latest), then follow
[Nytka for Android](https://github.com/nytka-app/android/blob/main/docs/tutorial.md#first-run-about-5-minutes): enter the
address from step 4 and the token, tap **Test connection**, allow Nearby devices and notifications,
accept the consent note and tap **Pair pendant**. The token is on your server machine:

```bash
grep '^Nytka__AdminToken=' .env
```

Copy it to the phone without pasting it into a chat or a note that syncs elsewhere (a password
manager's share, or typing it, both work).

#### 6. Check that it works (2 minutes)

1. In the app the chip at the top reads **Recording** and the status card shows "Server: Last upload".
   If the chip stays on **Waiting**, check that the pendant is on and next to the phone.
2. Say a few sentences, wait about two minutes, then pull the **Conversations** list down. The
   speech appears as a conversation after the silence gap (two minutes by default).
3. If it does not, ask the server what it thinks (replace the address and token):

   ```bash
   curl -H "Authorization: Bearer $NYTKA_ADMIN_TOKEN" https://<address>/api/v1/status
   ```

   `lastError` names the problem, for example `The transcription endpoint answered 401.`: the key, URL
   or model in `.env` is wrong. Fix it and run `docker compose up -d`.

**Then, when you have a minute:** titles, summaries, tasks and memories need a language model, which
is optional: [The language model](../reference/configuration.md#the-language-model) (Groq's `https://api.groq.com/openai/v1` with
the same key works; pick a chat model from its list). Back up the `postgres-data` volume, and read
[Upgrading](../how-to/operations.md#upgrading).

### What does not fit in 15 minutes

- Installing Docker, joining Tailscale on two devices for the first time (about 5 minutes each) and
  pointing a domain at a server with a proxy and a certificate (10 minutes or more).
- Creating the Groq or OpenAI account and key, and any wait for approval.
- Updating the pendant's firmware: it needs the official Omi app, and Nytka tells you when the
  codec is wrong. Offline sync needs firmware 3.0.20 or later.
- The Ukrainian search dictionary: an extra download with a licence prompt
  ([Ukrainian search](../explanation/features.md#ukrainian-search-optional)).
- Tuning a local transcription or language model for speed or accuracy: the `local` profile above
  gets you running without an account, not a model picked and sized for your machine.
- Evaluating the result: summaries only show once a conversation has closed and a model is set,
  and a week of wear is what shows whether it fits you.

## Import from Omi

Omi's "Export All Data" file (`omi-export.json`, from `GET /v1/users/export`) goes into Nytka once, so
your conversations, memories and tasks from before Nytka sit next to the new ones. The file has no
audio.

```bash
curl -sS -X POST "$NYTKA_URL/api/v1/import/omi" \
  -H "Authorization: Bearer $NYTKA_ADMIN_TOKEN" \
  -H "Content-Type: application/json" \
  --data-binary @omi-export.json
```

The answer counts what happened:
`{ conversations: { imported, alreadyImported, overlapping, discarded, empty }, segments, tasks: { imported, skipped }, memories: { imported, skipped } }`.

- Each kept Omi conversation becomes a closed conversation with `source: "omi"`, at its Omi times,
  with Omi's title and summary and its transcript. Discarded conversations and ones with no text
  (`empty`) are left out. Imported conversations are searchable, never merge with the ones the pendant
  captures, and trigger no webhook, memory extraction or summary; `POST
  /api/v1/conversations/{id}/enrich` still writes a new summary when you ask.
- Posting the same file again creates nothing: conversations already imported count as
  `alreadyImported`, and a memory or task whose text is already there is `skipped`.
- An Omi conversation whose time overlaps one Nytka captured (the pendant may have sent to both) is
  not imported and counts as `overlapping`. Add `?overlapping=import` to import those as well.
- Memories go in with `source: "omi"`, linked to their conversation when it came in, cut at 300
  characters; dismissed ones are left out. Tasks join the conversation they came from.
- The file is read whole and applied in one transaction, so a bad file changes nothing: `400` when it
  is not an Omi export or a time has no offset, `413` above 100 MB. Needs an admin token. Nothing from
  the file reaches a log or an error.

## Restore a Nytka export

The file `GET /api/v1/export` writes ([Export](../explanation/features.md#export)) goes back into a Nytka
server: the same one after a lost database, or a new one you are moving to.

```bash
curl -sS -X POST "$NYTKA_URL/api/v1/import/nytka" \
  -H "Authorization: Bearer $NYTKA_ADMIN_TOKEN" \
  -H "Content-Type: application/x-ndjson" \
  --data-binary @nytka-export.ndjson
```

The answer counts what happened: `{ people, conversations, tasks, memories, personFacts, bookmarks, digests,
settings }`, each `{ imported, skipped }`, and `segments`.

- Every record keeps its id. What the server already has (the same id, a person of the same name, an Omi
  conversation imported there too, a task or memory with the same text) is `skipped` and left as it is, so
  posting the file twice changes nothing.
- Restored conversations trigger no webhook, summary or memory extraction, and are searchable once the
  indexer has run.
- Voiceprints and audio are not in the export, so they do not come back: the server learns voices again.
- Settings in the file apply where your environment does not set them.
- One transaction: `400` for a file that is no Nytka export, one from a newer format version, one cut short
  (no `end` line) or one with a value the server refuses, and nothing is written; `413` above 100 MB.

## Importing other sources

Besides an Omi export, Nytka takes two kinds of file from anywhere else, both admin token, both at
most 100 MB: a plain transcript, and an audio file that is run through the same VAD and
transcription pipeline as a live capture. A Limitless export is not supported: Limitless (now owned
by Meta) has not published its export format, and nothing in this repository or its dependencies
documents it either.

### Transcript files

```bash
curl -sS -X POST "$NYTKA_URL/api/v1/import/transcript?format=srt&startedAt=2026-03-01T09:00:00Z" \
  -H "Authorization: Bearer $NYTKA_ADMIN_TOKEN" \
  --data-binary @transcript.srt
```

- `format` is `srt`, `vtt` or `txt`. `startedAt` (a time with a UTC offset) is where the file's own
  times, if any, start counting from.
- SRT and VTT each become one conversation, source `"import"`, with one segment per cue, timed at
  `startedAt` plus the cue's own offset. A VTT file needs its `WEBVTT` header; `NOTE` blocks are
  skipped in both formats.
- TXT has no times of its own: the whole file becomes one conversation with a single segment, at
  `startedAt`. Any segment longer than 20,000 characters is cut at a space into several, each with the
  same times, because the search index cannot hold a longer one. NUL characters are dropped.
- The answer is `{ conversationId, segments }`. Posting the same file again makes another
  conversation: there is no id to dedupe transcript imports by.
- `400` for a file that does not parse as the given format, or a `startedAt` with no UTC offset or
  outside the range a chunk's base time can hold (1970-01-01 up to just shy of year 10000); `413`
  above 100 MB.

### Audio files

```bash
curl -sS -X POST "$NYTKA_URL/api/v1/import/audio?startedAt=2026-03-01T09:00:00Z" \
  -H "Authorization: Bearer $NYTKA_ADMIN_TOKEN" \
  -H "Content-Type: audio/wav" \
  --data-binary @recording.wav
```

- Body: 16 kHz mono 16-bit WAV (`audio/wav`) or mono Ogg Opus (`audio/ogg`); `startedAt` (a time with
  a UTC offset) is the capture time of the file's first sample, since the file carries no clock of
  its own: every later sample is timed as if it played back without a gap.
- The file is decoded, re-encoded to the pendant's own chunk format and queued through the ordinary
  `process-session` job, so VAD, transcription and conversation assignment run exactly as they would
  for a live capture; the resulting conversation is indistinguishable from one the pendant sent.
- The answer is `202 { sessionId, durationMs }`.
- `415` for any other content type, `400` for a WAV that is not 16 kHz mono 16-bit, holds no audio,
  is longer than 1 hour once decoded, or a `startedAt` with no UTC offset or outside the range a
  chunk's base time can hold, `413` above 100 MB.
