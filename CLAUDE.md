# Nytka server

Self-hosted server for the Nytka app: takes Opus chunks from the app, drops silence with Silero VAD,
sends speech to an OpenAI-compatible transcription endpoint and serves conversations over HTTP API
v1. The whole picture: `docs/vision.md`; what v0.1 must do: `docs/specs/v0.1.md`; how it was built:
`docs/plans/`.

## Stack

.NET 10 · ASP.NET Core minimal APIs · Postgres 17 · Dapper · DbUp · Concentus (Opus) · ONNX
Runtime with Silero VAD · Serilog · xUnit, Testcontainers, WebApplicationFactory, FakeTimeProvider.

## Commands

```bash
dotnet build
dotnet test                              # needs a Docker daemon for Nytka.Server.Tests
dotnet test tests/Nytka.Audio.Tests      # no Docker
dotnet run --project src/Nytka.Server    # needs ConnectionStrings__Postgres, Nytka__AdminToken, Nytka__Stt__Url
dotnet run --project src/Nytka.Replay -- --server http://127.0.0.1:8080 --wav speech.wav
docker compose up -d --build
```

## Architecture invariants

1. **Times are capture times.** The pendant stops sending in silence and Bluetooth drops frames, so
   a sample index never converts to a time by dividing by the sample rate. `Timeline` and
   `OffsetMap` do every conversion.
2. **One transaction per processing run.** A `process-session` run writes its batches, speech
   audio, `transcribe` jobs, the session's `processed_through_at` and the nulled chunk bodies in one
   transaction. A chunk keeps its body until the processed point passes its last frame, so a crash
   repeats work and never loses or duplicates it.
3. **Chunk rows outlive their audio.** A retried upload must find its row to answer `200`; retention
   deletes processed rows only after 7 days.
4. **One job at a time.** `JobRunner` is the only consumer of `jobs`; Silero is not thread-safe and
   v0.1 sends one transcription request at a time. Dedupe keys keep one job per session and batch.
5. **Nothing sensitive in logs or errors.** No audio, no transcript text, no token.
   `TranscriptionException` carries the status code only, because error messages land in logs and
   in `transcription_batches.error`.
6. **Real voices never enter this repository.** Tests use synthetic tones; recordings stay private.

## Conventions

- Nullable enabled, warnings as errors, code style enforced in the build.
- Dapper maps constructor parameters to column names: alias every column (`started_at as StartedAt`)
  and read `timestamptz` as `DateTime` (Npgsql returns `Kind = Utc`); writes may pass `DateTimeOffset`.
- One SQL file per migration under `db/migrations/`, numbered, never edited once applied.
- Conventional commits; release-please turns them into releases. Every commit uses a GitHub noreply
  address; the `.githooks` hooks and `.private-terms` stay on.
