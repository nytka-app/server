# Run and maintain the server

What the server keeps and for how long, upgrading, offline sync, the no-loss wear test, testing without a pendant and building from source.

## What it stores

Everything lives in Postgres, so `pg_dump` backs it up. Keep a dump private: it holds your
transcripts and your webhook secrets.

- Silence is dropped as soon as a chunk is processed. Speech audio (Opus, about 18 MB per hour of
  speech) stays for `RetentionDays`. With `RetentionDays=0` it goes as soon as its transcript
  exists; audio whose transcription failed stays, to be [tried again](../reference/configuration.md#transcription-endpoints) and so
  you can see what failed, until you delete its conversation.
- Transcripts, titles, summaries, tasks and memories stay until you delete them. Deleting a
  conversation deletes its audio, transcript, tasks and memories.
- Daily digests stay until you delete their rows; they hold model-written text about your day, so a dump holds them too.
- Tokens are kept as a hash, and the settings the app saved as plain rows. An API key is never in the
  database. A webhook secret is, as plain text, because signing needs it.
- A person's name and your note on them, and the person you set on a segment, stay until you delete the person (the links go with them). A person known only by a [role](../explanation/features.md#roles) is marked `named: false` until a name is given.
- [Tags](../explanation/features.md#tags) (a name, and which conversations and people hold it) stay until you remove the last link, delete the tag, or delete what held it. They are words you chose and can be sensitive, so a dump holds them.
- Proposed tags (the name, the conversation it came from, the person for a person's tag, and your answer) stay until you delete that conversation or person; accepted and rejected ones too, which is how a rejected tag stays rejected. They hold model-chosen words about your day, so a dump holds them.
- Name suggestions (the name or role, the voice, the line that shows it and the model's confidence) stay until you delete the conversation they came from or the person they name; accepted and rejected ones too, which is how a rejected name stays rejected.
- Each line of a transcript carries its [speech kind](../explanation/features.md#speech-kind) as columns of `segments`: your mark (`speech_manual`), Nytka's guess with its score, signals and version (`speech_guess`, `speech_score`, `speech_signals`, `speech_version`) and the kind that applies (`speech_kind`). They stay with the line. A single row, `speech_state`, records the mode and threshold the stored guesses and kinds follow. No audio, no voiceprint and no text goes into them.
- Facts about a person stay until you delete them, their person or the conversation they were taken from. A deleted fact leaves a hidden row with its wording, so extraction does not add it again; it goes with the person.
- With a [calendar feed](../explanation/features.md#calendar-briefs) set, the events of the next 48 hours (uid, start, end, title and the attendees' display names, no address) stay until a day after they end, and so do the briefs the model wrote for them, which hold facts about the people; a brief goes with a person you delete. The feed's address is only in `.env`: never in the database, an API answer or a log.
- A webhook's delivery log keeps statuses only: no response body, and no payload once a delivery ends.
- [Context ranges](../explanation/features.md#context-from-the-phone) (a kind, a route, two times, the id the app made and when it arrived; no app, title or number) stay as long as speech audio: retention deletes those that ended before the audio cutoff, or a day after they end with `RetentionDays=0`. They say when your phone played sound or was in a call, so a dump holds them.
- Once you enroll [your voice](../explanation/features.md#your-voice): your voiceprint (192 numbers, plus the enrolled mean it
  resets to) until you delete it, and a fingerprint of each checked segment for as long as its speech
  audio stays (`RetentionDays`; with `0` not at all). The similarity and the verdict stay on the
  segment; they cannot be turned back into a voice. No API answer, webhook, MCP answer, export or log
  carries a voiceprint or a fingerprint; `pg_dump` does. Without an enrolled voice nothing is
  fingerprinted, and the enrollment's audio is never stored.
- With [voice grouping](../explanation/features.md#voice-grouping) on, the fingerprints of other people's segments are kept as long as
  their audio and gathered into groups (a centroid, the mean of the fingerprints in it), which go with their
  last fingerprint. A person you confirmed keeps one voiceprint (a centroid and a sample count) until you
  delete the person or call `DELETE /api/v1/people/voiceprints`; that voiceprint is the only vector of
  someone else that outlives audio. Pending voice matches (a person, the segments, the best similarity) stay
  until decided or forgotten. Nothing of this is in an API answer, export, webhook, MCP answer or log.

The server logs no audio, no transcript text, no token, and no response body from the transcription
endpoint, the language model or a webhook receiver.

## Upgrading

Download the Compose file again under a new name, compare it with yours, then pull the new image and restart:

```bash
curl -fsSL https://raw.githubusercontent.com/nytka-app/server/main/docker-compose.yml -o docker-compose.new.yml
docker compose pull && docker compose up -d
```

Compose passes each variable to the server by name, so a file from an older release does not pass the
newer ones: `Nytka__Llm__*` in `.env` would do nothing. Older files also gave `Nytka__Conversations__Gap`
and `Nytka__Audio__RetentionDays` a value, which locked both in the app: delete those two lines from an `.env` copied from the old template. The new file needs server image 0.4.0 or later; if you pin an older tag, take the Compose file from that tag's URL instead. Merge your own changes into
the new file.

The server applies new database migrations at start, and a database from v0.1 migrates without losing
data. Its conversations show `aiStatus: none` until a model is set: then the server summarizes those
that ended within the last `Nytka__Llm__BackfillDays` days on its own, and older ones on request
(Regenerate summary in the app). An older app keeps working against a newer server; a newer app says
"This server needs an update" for a screen its server lacks.

## Test without a pendant

`src/Nytka.Replay` sends a WAV file to a server as one capture session:

```bash
dotnet run --project src/Nytka.Replay -- --server http://127.0.0.1:8080 --wav speech.wav
```

The token comes from `--token` or `NYTKA_TOKEN`. The file must be 16 kHz mono 16-bit:
`ffmpeg -i in -ar 16000 -ac 1 -sample_fmt s16 out.wav`.

## Develop

.NET 10 SDK, a running Docker daemon (the tests start Postgres through Testcontainers) and
[gitleaks](https://github.com/gitleaks/gitleaks). See [CONTRIBUTING.md](../../CONTRIBUTING.md).

```bash
dotnet build
dotnet test
```

The search tests that need the Ukrainian dictionary skip themselves until you run
`scripts/fetch-uk-dictionary.sh --accept-licence`; CI runs it first and sets
`NYTKA_REQUIRE_DICTIONARY=1`, so a missing file fails the build. The same goes for the voice tests and
`scripts/fetch-speaker-model.sh`, which puts TitaNet-small in `src/Nytka.Audio/Models/` (run it before
`dotnet build`, which copies the model beside the binaries), and `NYTKA_REQUIRE_SPEAKER_MODEL=1`. The audio
tagger test takes `scripts/fetch-audio-tagger.sh` (YAMNet and its class map, same place) and
`NYTKA_REQUIRE_AUDIO_TAGGER=1`.

## Offline sync

While the phone is out of range, the pendant stores what it hears on its own card. When the phone
comes back, the app reads that storage and uploads it as ordinary chunks, with the times the audio
was recorded. The server needs no setting for this. Migration 0005 ships in the image, and
`GET /api/v1/info` lists `offline-sync` under `features`, which tells the app it may sync.

What the server does with late audio:

- **It places it by time.** Stored audio joins the conversation its capture time falls in, an older
  one included, which opens again. A stored batch that falls within the conversation gap of two
  conversations merges them, so one stretch of talk stays one conversation. The one that starts first
  survives with its title, and its summary runs again.
- **Live speech goes first.** Audio whose last frame is more than 5 minutes old when it arrives is
  late. Its jobs rank as if they were queued 10 minutes later, so they wait behind live jobs due in
  that time and still cannot starve. A backlog does not hold back new transcripts.
- **Retention counts from capture time.** Old audio goes at the next daily run; its transcript stays.

The pendant cannot mute itself while the phone is away, so the app drops stored audio from muted
stretches before it uploads anything: that audio never reaches this server. The firmware it needs
(3.0.20 or later) and the Pendant storage card are described in
[Nytka for Android](https://github.com/nytka-app/android/blob/main/docs/explanation/offline-sync.md).

## Running the no-loss wear test

The milestone "Nothing is lost" asks for a week of wear with the official app uninstalled and no
lost audio. `GET /api/v1/coverage` answers how much of the audio the phone counted reached this
server, and lists the stretches that did not. The design and its limits are in
[docs/specs/coverage.md](../specs/coverage.md).

**Before the week.** Install the Nytka app on the phone you wear it with, update the server to a
release with this endpoint. In the app, tap the version under Device → About seven times to turn
on developer mode, then switch on Device → Developer mode → **Send diagnostics to my server**; the
app's token must be an `admin` token. Without the samples the report has no time axis and says so.
Keep the phone's battery optimisation off for the app, so Android does not stop capture.

**During the week.** Wear the pendant as usual; mute with the button or the schedule as usual
(muted time is not a loss). Chunk rows are kept 7 days, so save the report every evening:

```bash
curl -fsS -H "Authorization: Bearer $NYTKA_ADMIN_TOKEN" \
  "$NYTKA_URL/api/v1/coverage?from=$(date -u +%Y-%m-%dT00:00:00Z -d '7 days ago')" \
  > coverage-$(date +%F).json
```

On macOS use `date -u -v-7d +%Y-%m-%dT00:00:00Z`. Leave `from` out for the last seven local days
(the time zone is the `user.timeZone` setting).

**Reading it.** Look at `totals` first: `coverage` is audio that arrived divided by arrived plus
lost, and `lostS` is the lost seconds. Then read `gaps`:

- `link-loss`, `ring-lost`, `missing-chunk` and `not-arrived` are losses. Match their times to what
  you did: a loss in a crowded place says Bluetooth, one right after a reconnect says sync.
- `away` and `unobserved` are not losses. For `away`, `audioS` is the audio that arrived for that
  stretch, so a stretch with talk and `audioS` near 0 is worth a look. `unobserved` means the app
  sent no samples: hours of it make the week unmeasured, not clean. Check `warnings` and
  `now.lastSampleAt` after a day with the phone off or out of signal, since samples wait on the
  phone.
- `pending` is audio still on its way; `now.pendingOnPendantS` is what the pendant still holds.
- `muted` is time you chose; `droppedByMuteS` is stored audio the mute filter dropped.

Read the final report only after the phone has uploaded everything: `now.queuedChunksOnPhone` is
0 and `pendingOnPendantS` is 0 or close to it.

**Passing.** The proposed rule is in the spec: no `missing-chunk`, `not-arrived` or `ring-lost`
gap, `link-loss` under 0.1% of `receivedS`, and `unobservedS` under 1% of the week. The report
cannot see frames the pendant drops before numbering them when its link stalls, so also check a few
conversations you remember against the transcripts.
