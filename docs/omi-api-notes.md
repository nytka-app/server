# Omi Developer API — hard-won facts (verified 2026-09-28)

Ground truth for this repo's models is Omi's own OpenAPI spec, not the prose docs at
docs.omi.me, which disagree with it in places:

```
https://raw.githubusercontent.com/BasedHardware/omi/main/docs/api-reference/openapi.json
```

Re-fetch and diff that file, the same way oura-platform diffs Oura's spec snapshots — there is no
changelog.

## What the prose docs got wrong

`docs.omi.me/doc/developer/api/conversations` describes a `discarded` (boolean) and `status`
(string) field on a conversation. Neither exists on `DeveloperConversation` in the actual spec.
Do not add columns for them without re-verifying against the spec first.

## Auth

Bearer token, no OAuth flow: `Authorization: Bearer <omi_dev_...>`. Static and non-rotating —
unlike Oura there is no refresh token, no expiry to track, no `/oauth/start` handshake. Create a
key in the Omi app: Settings → Developer → API Keys, scopes `memories:read conversations:read`.

Rate limits (docs.omi.me, not in the spec itself): 100 requests/minute per key, 10,000/day per
user.

## Endpoints this repo uses

Both are `GET`, base `https://api.omi.me/v1/dev/`, and both return a **bare JSON array** — no
envelope, no next-page token. Confirmed against `openapi.json`, not assumed.

- `user/conversations?start_date=&end_date=&limit=&offset=&include_transcript=true` —
  `start_date`/`end_date` are full ISO-8601 timestamps, not dates. `limit` defaults to 25.
- `user/memories?limit=&offset=&categories=` — no date filter of any kind. This is why
  `omi-ingest` fully re-lists memories every cycle instead of windowing them (see this repo's
  `CLAUDE.md`).

## Conversation shape (`DeveloperConversation`)

`id`, `created_at` and `structured` are always present. `started_at` and `finished_at` are
present as *keys* but can be `null`. `structured.action_items[]` has a confirmed schema —
`description` (required), `completed` (bool, default false), `completed_at`, `due_at`,
`created_at`, `updated_at`, `conversation_id` — which is why `action_items` is a proper typed
table here rather than raw jsonb.

`transcript_segments` is `null` unless the request set `include_transcript=true`. The ingest
always sets it; fetching transcripts per-conversation afterwards would cost one request per
document for nothing.

`structured.category` is a closed enum (`CategoryEnum`) — 33 values as of this writing (personal,
work, health, technology, ...). `structured.emoji` defaults to 🧠 when Omi has not categorised the
conversation yet.

## Memory shape (`DeveloperMemory`)

`id` is the only required field; everything else has a default (`content: ""`,
`category: "interesting"`, `visibility: "private"`, booleans `false`). `category` is a separate
closed enum from conversations' (`MemoryCategory`: interesting, system, manual, workflow, core,
hobbies, lifestyle, interests, habits, work, skills, learnings, other, auto).

## Not implemented here, deliberately

- **Real-time webhooks** (`memory-created`, `transcript`, chat tools, audio streaming). This
  stack polls instead: it stays internal-only, needs no new public exposure, and matches
  oura-ingest's shape.
- **Calendar/tag context correlation**, the way oura-platform has `context`/`context_daily`. Out
  of scope for the first cut; a real follow-up once there is a device generating data to correlate
  against.
- **`POST` write endpoints** (create/update/delete conversations or memories). This is a read-only
  ingest; nothing here ever writes back to Omi.
