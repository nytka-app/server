# Run model work again with Claude Code on your own machine

When a prompt or schema changes (salience, cited lines, expiry), conversations summarized earlier keep their old items. You
can redo that work without the server calling its model: the server hands out each request it would have sent, built with its
own prompt, and stores each answer through its own validation. `scripts/backfill-with-claude.sh` answers them with the
`claude` CLI you are signed in to. Nothing runs on a schedule, and the server's own model endpoint is never used for it.

## What it does, per conversation

| Kind | What runs again | Calls |
|---|---|---|
| `recheck` | Which lines support each memory taken before memories cited their lines. A memory no line of yours supports becomes unconfirmed (hidden, not deleted). Skipped for a conversation without such memories | 1 |
| `enrich` | Title, summary, items (with salience and cited lines) and tags, replacing the old run's output. Tasks you edited or finished stay | 1, or one per window plus a merge for a long transcript |
| `memories` | Extraction, with salience, expiry and cited lines | 1 per window |

Only closed conversations that are not [noise](../explanation/features.md#speech-kind) are walked. A conversation still open
or transcribing answers `busy` and is skipped.

## Run it

```bash
export NYTKA_URL=https://your-nytka-host
export NYTKA_ADMIN_TOKEN=...   # an admin token; read it from your password manager, never paste it into a chat
scripts/backfill-with-claude.sh --count
scripts/backfill-with-claude.sh
```

`--count` lists how many conversations and memory re-checks there are and stops. `--kinds recheck` (or any comma list of
`recheck`, `enrich`, `memories`) limits the run; `--model <name>` passes a model to `claude`. The script prints counts only,
never transcript text. Stop it any time: each answer is stored as it comes, and a repeat run does the work again.

## The routes

`GET /api/v1/ai/backfill/conversations?after=&limit=` (admin) lists `{ items: [{ id, startedAt, memories }], nextAfter }`.
`POST /api/v1/ai/backfill/{kind}/{id}` (admin), body `{ answers: { <key>: "<the model's JSON>" } }`, answers
`{ status, key, schemaName, schema, system, user }`: `pending` with the next request to answer under `key`, `done`, `busy`, or
`invalid` when an answer broke the schema (ask again). Post every answer collected so far with each call; a long transcript
asks for its windows one by one, then the merge.

## Terms of your subscription

Run it yourself, on your machine, now and then. Do not schedule it, run it in CI or on a server, and never point
`Nytka__Llm__BaseUrl` at a proxy over a Claude subscription: the subscription is not sold as an API backend. If you want the
server to do this work unattended, use an API key instead.
