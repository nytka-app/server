# omi-platform

Self-hosted warehouse for an Omi AI necklace. Pulls conversations, memories and action items into
Postgres so they can eventually be joined against everything else in the homelab (Oura sleep,
calendar). Deployed under Docker Compose alongside oura-platform, from a separate repo.

Built before the necklace itself arrived, against Omi's published API spec, with no live account to
test against. Treat anything here that touches real Omi data as unverified until it has run against
a real key — see "First run against a live account" below.

## Stack

.NET 10 · Worker Service (ingest) + MCP server (`ModelContextProtocol.AspNetCore`, HTTP transport,
over the database) · plain Postgres 17 (no TimescaleDB — this data is discrete records, not a fine
time series) · Dapper · DbUp migrations · Polly · Serilog · Docker Compose.

No EF Core, no OAuth flow (Omi's Developer API key is a static bearer token, not a rotating
refresh-token pair like Oura's).

## Commands

```bash
dotnet build
dotnet test                                    # OmiPlatform.Omi.Tests needs no credentials or Docker
dotnet test tests/OmiPlatform.Storage.Tests     # needs a Docker daemon (Testcontainers)
dotnet run --project src/OmiPlatform.Ingest
docker compose up -d
docker compose logs -f ingest
```

## Architecture invariants

1. **`omi_raw` is the source of truth.** Every typed table (`conversations`, `action_items`,
   `memories`) is a projection and must be rebuildable from raw jsonb without re-calling the API —
   `dotnet run --project src/OmiPlatform.Ingest -- --reproject`.
2. **Single ingest instance.** No distributed locking. Not because of a single-use token the way
   Oura needs it (Omi's key is static) — just no reason to run two, and it keeps the invariant the
   same shape as oura-ingest.
3. **Conversations are windowed by day and bookkept in `ingest_window`; memories are not.** The
   memories list endpoint has no date filter at all, so every ingest cycle fully re-lists them and
   upserts. See `docs/omi-api-notes.md`.
4. **Ingest is idempotent and resumable.** Every write is an upsert keyed on Omi's own document id
   (conversations, memories) or `(conversation_id, idx)` (action items, which carry no id of their
   own — re-projecting a conversation deletes and re-inserts its action items rather than diffing).
5. **The MCP server reads the warehouse, never Omi's API, and holds no credential.** Validation
   failures must be `McpException`, or the SDK replaces the message with "An error occurred
   invoking 'x'" and the caller has nothing to correct against.

## Omi API — see `docs/omi-api-notes.md`

Ground truth is Omi's OpenAPI spec (`github.com/BasedHardware/omi`, `docs/api-reference/openapi.json`),
fetched and diffed directly — not the prose docs at docs.omi.me, which describe fields
(`discarded`, `status`) that do not exist on the actual schema. Short version: bearer key, no
OAuth, two `GET` endpoints returning bare JSON arrays, 100 req/min rate limit.

## First run against a live account

Nothing here has been exercised against a real Omi account — there was no necklace yet when this
was written, only the published spec. Before trusting it:

1. Create a Developer API key in the Omi app (Settings → Developer → API Keys), scopes
   `memories:read conversations:read`.
2. Set `Omi__ApiKey` and `Omi__BackfillFrom` (roughly when you started wearing it — there is no
   sensible historical default the way Oura's 2020-01-01 is).
3. `docker compose up -d`, then `docker compose logs -f ingest` and watch the first backfill.
4. **If a request 401s, 404s, or a payload fails to deserialize**, the API has moved since
   2026-09-28 or the spec fetch was stale. Re-fetch `openapi.json`, diff it against
   `docs/omi-api-notes.md`, and fix the model before assuming the code is otherwise wrong.
5. `dotnet test tests/OmiPlatform.Omi.Tests` uses synthetic fixtures matching the spec, not real
   captured responses — a real response that differs in some untested way will not be caught by
   them.

## Secrets

`.env` only, never committed. `Omi__ApiKey`, `Omi__BackfillFrom`, `POSTGRES_*`. Never logged — the
key must never appear in Serilog properties or exception messages.

## Conventions

- Nullable enabled, warnings as errors.
- Records for API DTOs (`[property: JsonPropertyName("...")]` per field, matching the spec exactly),
  classes for stateful things.
- **Dapper matches record constructors positionally against the reader's columns.** Npgsql maps
  `timestamptz` to `DateTime`, not `DateTimeOffset` — a read-side record declaring
  `DateTimeOffset?` throws "no constructor" at runtime, not at compile time. Every Dapper-read
  record in `WarehouseRepository` uses `DateTime?`. Write-side parameters (via `DynamicParameters`
  or anonymous objects) can pass `DateTimeOffset` fine; it is only the positional read path that
  cares.
- One SQL file per migration, numbered, never edited after being applied.
- Conventional commits.

## Roadmap

Stage 1: poll ingest (conversations windowed + backfilled, memories fully re-listed) + MCP server
over the warehouse — this repo, as built. Stage 2 (not started): calendar/tag context correlation,
mirroring oura-platform's `context`/`context_daily`. Stage 3 (not started, deliberately deferred):
real-time webhook receiver — needs a new public endpoint, a real exposure
tradeoff the poll-only design avoids for now. Stage 4 (not started): Grafana dashboards.
