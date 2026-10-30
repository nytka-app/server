# Nytka server

Self-hosted server for the Nytka app: takes Opus chunks from the app, drops silence with Silero VAD,
sends speech to an OpenAI-compatible transcription endpoint and serves conversations over HTTP API
v1. It also asks an OpenAI-compatible chat endpoint for titles, summaries, tasks and memories,
indexes text for full-text search, delivers webhooks and serves an MCP endpoint (19 read tools, 3 write tools behind an `admin` token). The whole
picture: `docs/vision.md`; what each version must do: `docs/specs/v0.1.md` to `v0.4.md`; how v0.1
was built: `docs/plans/`.

## Stack

.NET 10 · ASP.NET Core minimal APIs · Postgres 17 · Dapper · DbUp (through Switchknife.Migrations) · Concentus (Opus) · ONNX
Runtime with Silero VAD · ModelContextProtocol SDK · Serilog · xUnit, Testcontainers,
WebApplicationFactory, FakeTimeProvider.

## Commands

```bash
dotnet build
dotnet test                              # needs a Docker daemon for Nytka.Server.Tests
dotnet test tests/Nytka.Audio.Tests      # no Docker
scripts/fetch-uk-dictionary.sh --accept-licence   # fills tsearch_data/; the dictionary tests skip without it
NYTKA_REQUIRE_DICTIONARY=1 dotnet test   # as CI runs it: a missing dictionary fails instead of skipping
scripts/fetch-speaker-model.sh           # TitaNet-small into src/Nytka.Audio/Models/ (before the build); voice tests skip without it
NYTKA_REQUIRE_SPEAKER_MODEL=1 dotnet test   # as CI runs it: a missing speaker model fails instead of skipping
dotnet run --project src/Nytka.Server    # needs ConnectionStrings__Postgres, Nytka__AdminToken, Nytka__Stt__Url
dotnet run --project src/Nytka.Replay -- --server http://127.0.0.1:8080 --wav speech.wav
docker compose up -d --build
```

## Architecture invariants

1. **Times are capture times.** The pendant stops sending in silence and Bluetooth drops frames, so
   a sample index never converts to a time by dividing by the sample rate. `Timeline` and
   `OffsetMap` do every conversion. Audio the app pulls from the pendant's storage carries its
   capture times too, and conversations form by them, not by arrival.
2. **One transaction per processing run.** A `process-session` run writes its batches, speech
   audio, `transcribe` jobs, the session's `processed_through_at` and the nulled chunk bodies in one
   transaction. A chunk keeps its body until the processed point passes its last frame, so a crash
   repeats work and never loses or duplicates it.
3. **Chunk rows outlive their audio.** A retried upload must find its row to answer `200`; retention
   deletes processed rows only after 7 days.
4. **One job at a time per lane.** `JobRunner` is the only consumer of `jobs`, with one runner per
   lane: `Audio` (v0.1's kinds, `rescore-voice`, `rematch-voice`, `group-voices`, `apply-speech`, `classify-speech`, `pick-labeling`,
   `recompute-events`, `build-tracks`, `name-tracks` and `rethink`; Silero is not thread-safe and transcription requests go out one at a
   time), `Ai` (`enrich-conversation`, `extract-memories`, `classify-memories`, `classify-conversations`, `make-digest`, `suggest-names`, `extract-person-facts`, `make-brief`, `tidy-tags`, `measure-quality`, `fold-topic`, `make-rollup`, `relate-conversation`, `link-speakers`) and `Hooks` (`deliver-webhook`, `sync-calendar`). A slow
   model or receiver never delays transcription. Dedupe keys keep one job per session, batch,
   conversation and delivery.
5. **Nothing sensitive in logs or errors.** No audio, no transcript text, no token, no response body
   from the transcription endpoint, the model or a webhook receiver. `TranscriptionException` carries
   the status code or a fixed sentence, because error messages land in logs and in
   `transcription_batches.error`; `LlmException` and delivery errors carry fixed sentences too.
6. **Real voices never enter this repository.** Tests use synthetic tones; recordings stay private.
7. **Events run in the caller's transaction.** `IEventPublisher` calls every subscriber with the
   change's connection, so a subscriber only writes rows and queues jobs, and a throw aborts the
   change. A delivery or an extraction job exists exactly when its change committed.
8. **Settings resolve as environment, table, default; an empty variable is unset.** API keys come
   only from the environment and never reach the database, a response or a log. A route needs an
   `admin` token unless it says `AllowRead()`; only `/healthz` needs none.
9. **Writes never evaluate the `nytka` search configuration.** Only `SearchDictionarySync` (the
   indexer) and search queries do, on `SearchStore`'s own pool of two connections, so a missing
   dictionary can fail indexing but never an insert.

## Conventions

- Nullable enabled, warnings as errors, code style enforced in the build.
- Dapper maps constructor parameters to column names: alias every column (`started_at as StartedAt`)
  and read `timestamptz` as `DateTime` (Npgsql returns `Kind = Utc`); writes may pass `DateTimeOffset`.
- One SQL file per migration under `db/migrations/`, numbered, never edited once applied.
  CI lints each added migration with squawk (`.squawk.toml`); accept a finding on purpose with `-- squawk-ignore <rule>` and the reason.
- A setting is a `SettingDefinition` in a settings group. Its variable is `Nytka__` plus the key with
  each `.` as `__` and each part capitalized (`llm.baseUrl` is `Nytka__Llm__BaseUrl`). A new key or
  option needs its variable in `.env.example` (commented out) and in `docker-compose.yml` (empty
  default), or Compose never passes it on and the app cannot edit it.
- An endpoint, variable or MCP tool that changes gets its section in `docs/reference/` or `docs/explanation/` changed in the same PR (the README only when what it does changes).
- Conventional commits; release-please turns them into releases. Every commit uses a GitHub noreply
  address; the `.githooks` hooks and `.private-terms` stay on.
