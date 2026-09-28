# omi-platform

Self-hosted warehouse for an [Omi](https://www.omi.me/) AI necklace. Pulls conversations, memories
and action items into Postgres so they can be queried and, eventually, joined against everything
else in the homelab. Runs under Docker Compose; this repo is only the source, and deployment lives
in a separate repo.

Built before the necklace arrived, against Omi's published API spec (verified 2026-09-28 — see
[docs/omi-api-notes.md](docs/omi-api-notes.md)), with no live account to test against yet. See
[CLAUDE.md](CLAUDE.md) → "First run against a live account" before trusting it with real data.

## The images

`ghcr.io/egoushka/omi-ingest` and `ghcr.io/egoushka/omi-mcp`, public packages, built and pushed by
`scripts/publish.sh` — nothing in the homelab repo or its CI builds them, same convention as
oura-platform.

## The MCP server

`omi-mcp` answers questions about the warehouse over MCP, at `http://<host>:8089/mcp` once
deployed. Read-only, queries the database, never Omi's API, holds no Omi credential.

| Tool | Answers |
|---|---|
| `coverage` | What is actually held: date span, conversation/memory/open-action-item counts |
| `list_conversations` | Summaries over a date range and category, newest first |
| `conversation_detail` | One conversation's full transcript, events and action items |
| `search_conversations` | Full-text search over titles, overviews and transcripts |
| `list_memories` | Memory facts, optionally filtered by category |
| `list_action_items` | Action items across conversations, optionally filtered to open ones |
| `daily_activity` | Conversation and memory counts per day over a range |

## Required secrets

| Key | What |
|---|---|
| `Omi__ApiKey` | Developer API key from the Omi app: Settings → Developer → API Keys, scopes `memories:read conversations:read` |
| `Omi__BackfillFrom` | Earliest day to backfill — set to roughly when you started wearing the necklace |
| `POSTGRES_*` | Warehouse credentials |

## Local dev

```bash
cp .env.example .env    # fill in Omi__ApiKey and Omi__BackfillFrom
docker compose up -d
docker compose logs -f ingest
```

Before committing, enable the hooks: `git config core.hooksPath .githooks`, then copy
`.private-terms.example` to `.private-terms` and list what must never appear here.

## Re-projecting after a change

`omi_raw` is the source of truth; every typed table is a projection of it.

```bash
docker compose run --rm ingest --reproject                    # everything
docker compose run --rm ingest --reproject conversation        # one doc type
```

## License

Apache-2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE).

Not affiliated with or endorsed by Omi or Based Hardware; "Omi" is a trademark of its owner. The
warehouse stores recordings of the people around the wearer: you are responsible for following the
recording and privacy laws where you use the device.
