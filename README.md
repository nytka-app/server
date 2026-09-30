# Nytka server

The self-hosted half of [Nytka](docs/vision.md), a companion app and server for the Omi AI
necklace. The Nytka Android app streams the pendant's audio here. The server drops silence, sends
speech to the transcription endpoint you choose, groups the transcript into conversations and serves
them back to the app. With a language model you choose, it also gives each conversation a title, a
summary and tasks, and keeps lasting facts about you as memories. It searches everything in Ukrainian
and English. Webhooks tell your other tools when something new exists, and AI agents read it all
over MCP. Omi's cloud sees none of it.

Status: v0.4 of the [roadmap](docs/vision.md#roadmap) is shipped: capture and transcription
([v0.1](docs/specs/v0.1.md)), the AI layer ([v0.2](docs/specs/v0.2.md)), offline sync
([v0.3](docs/specs/v0.3.md)) and memory and search ([v0.4](docs/specs/v0.4.md)). Each spec says what
its version does and does not do.

## First run (about 5 minutes)

You need Docker Compose and an OpenAI-compatible transcription endpoint; an API key from OpenAI or
Groq is the quickest. The five minutes assume you already have an HTTPS proxy or a VPN to put the
server behind (step 4); setting one up is not counted.

1. Download the Compose file and the settings template:

   ```bash
   mkdir nytka && cd nytka
   curl -fsSLO https://raw.githubusercontent.com/nytka-app/server/main/docker-compose.yml
   curl -fsSL -o .env https://raw.githubusercontent.com/nytka-app/server/main/.env.example
   ```

2. Fill in `.env`. Compose stops and names the first required value that is still empty.
   - `POSTGRES_PASSWORD` and `Nytka__AdminToken`: a random string each, from `openssl rand -hex 24`.
     The token is what the app asks for; it needs 32 characters or more.
   - `Nytka__Stt__Url`: the transcription endpoint. OpenAI and Groq also need `Nytka__Stt__ApiKey`
     and `Nytka__Stt__Model`: remove the `#` in front of those two lines.
     [Transcription endpoints](#transcription-endpoints) lists the values.

   Everything else is optional. The server's own settings are commented out on purpose; see
   [Configuration](#configuration).

3. Start the server and check that it answers:

   ```bash
   docker compose up -d --wait
   curl http://127.0.0.1:8080/healthz
   ```

   It answers `{"status":"healthy"}`. If it does not come up, `docker compose logs server` says why,
   for example a token shorter than 32 characters.

4. Make it reachable from the phone. The server listens on `127.0.0.1:8080` only: put a reverse
   proxy with HTTPS in front, or set `NYTKA_BIND` in `.env` to an address on your VPN (Tailscale,
   WireGuard) and run `docker compose up -d` again.

5. Give the app the server's address and the token; `grep '^Nytka__AdminToken=' .env` shows it again.
   Behind a proxy the address is `https://…`. On a VPN it is `http://<that address>:8080`, and the
   app's private-network switch, which allows plain HTTP, has to be on. The phone side continues in
   [Nytka for Android](https://github.com/nytka-app/android#first-run-about-5-minutes).

Conversations show up in the app a few minutes after speech; pull the list down to refresh if a new
one hasn't appeared. If they stay empty, `GET /api/v1/status` with the token (see [API](#api))
reports `lastError`, for example `The transcription endpoint answered 401.`: check the endpoint's
URL, key and model.

Titles, summaries, tasks and memories need a language model, which is optional and comes next:
[The language model](#the-language-model).

## Configuration

You configure the server with environment variables in `.env`; Compose passes them on. Most of them
are also **settings**, which the app can change (Device, developer mode, Server settings) and the
server keeps in Postgres. A setting is the first of these that exists:

1. its environment variable, when it is set and not empty;
2. the value the app saved;
3. its default, in the table below.

So a variable that is set **locks** its setting: the app shows it read-only, "Set by the server's
environment", and `PATCH /api/v1/settings` answers `409`. Leave a variable out to change the setting
in the app. An empty variable counts as unset, so the Compose file passes every optional variable
through empty, and [`.env.example`](.env.example) comments every optional server setting out: a line
you copy without thinking would lock its setting.

| Variable | Required | Default | In the app | Meaning |
|---|---|---|---|---|
| `POSTGRES_PASSWORD` | yes | | | Password of the bundled Postgres |
| `Nytka__AdminToken` | yes | | env only | Token for the app and your scripts, 32 characters or more, always scope `admin`. The server refuses to start without it. |
| `Nytka__Stt__Url` | yes | | env only | Full URL of the transcription endpoint |
| `Nytka__Stt__ApiKey` | no | | env only | Sent as a bearer token; OpenAI and Groq need it |
| `Nytka__Stt__Model` | no | | editable | Sent as `model`; OpenAI and Groq need it |
| `Nytka__Stt__Language` | no | `auto` | editable | `auto` lets the endpoint detect the language; or a language tag such as `uk` or `en`, sent as `language` (providers usually want a two-letter ISO 639-1 code) |
| `Nytka__Conversations__Gap` | no | `00:02:00` | editable | Silence that ends a conversation, `hh:mm:ss`, from 30 seconds to 1 hour |
| `Nytka__Audio__RetentionDays` | no | `14` | editable | Days to keep speech audio, 0 to 3650; `0` deletes it once transcribed |
| `Nytka__Mute__Windows` | no | `[]` | editable | JSON list of weekly windows whose audio is dropped by capture time before transcription, at most 50, such as `[{"days":[1,2,3,4,5],"start":"09:30","end":"10:00"}]`. `days` are ISO weekdays of the start day (1 Monday to 7 Sunday), `start` and `end` are local `HH:mm`; an end at or before the start crosses midnight. Uses `Nytka__User__TimeZone` |
| `Nytka__User__TimeZone` | no | `UTC` | editable | IANA time zone such as `Europe/Kyiv`: prompts and mute windows use it; an unknown id falls back to UTC |
| `Nytka__Llm__BaseUrl` | no | | editable | Base URL of an OpenAI-compatible chat endpoint, without a query or fragment; the server calls `<base URL>/chat/completions` |
| `Nytka__Llm__ApiKey` | no | | env only | Sent as a bearer token; leave it out for a local model |
| `Nytka__Llm__Model` | no | | editable | Sent as `model` |
| `Nytka__Llm__OutputLanguage` | no | `auto` | editable | Language of titles, summaries, tasks and memories: `auto` follows the conversation, or a tag such as `uk` |
| `Nytka__Llm__JsonMode` | no | `schema` | env only | `schema` sends a strict `json_schema`; `object` asks for a JSON object instead, for endpoints that reject `json_schema` |
| `Nytka__Llm__TimeoutSeconds` | no | `120` | env only | Seconds to wait for one model request |
| `Nytka__Llm__MaxInputChars` | no | `100000` | env only | Transcript characters per model request; a longer conversation goes in several |
| `Nytka__Llm__BackfillDays` | no | `7` | env only | How far back conversations without a summary are summarized |
| `Nytka__Memories__Enabled` | no | `true` | editable | `false` stops memory extraction |
| `Nytka__Memories__UserName` | no | empty | editable | Who "you" is for the model, up to 64 characters; empty means the person wearing the pendant |
| `Nytka__Search__Dictionary` | no | `simple` | editable | `simple` or `uk_hunspell`: [Ukrainian search](#ukrainian-search-optional) |
| `NYTKA_BIND`, `NYTKA_PORT` | no | `127.0.0.1`, `8080` | | Where Compose publishes the server |
| `NYTKA_VERSION` | no | `latest` | | Image tag, such as `0.4.1` |

Model names take up to 128 characters. Values travel as strings, as an environment variable carries
them (`"14"`, `"true"`). `GET /api/v1/settings` lists every key with its `value`, its `source` (`env`,
`db` or `default`) and `locked`.

- **Environment only.** The admin token, the transcription URL, the API keys and the four
  `Nytka__Llm__` tuning values cannot be changed from the app or the API. `GET /api/v1/settings`
  shows the URL without any `user:password@`, and shows an API key as `isSet: true` or `false`,
  never its value; a `PATCH` naming `stt.url` or an API key gets `409`, and one naming the admin token or a `Nytka__Llm__` tuning value gets `400` ("Unknown setting."). No key reaches the database.
- **Bad values.** The app's `400` names the key. In `.env`, a bad transcription, conversation, audio
  or model value stops the server at start with a message that names the variable.
  `Nytka__Memories__*` and `Nytka__Search__Dictionary` are checked when first used, so type them as
  the table shows: a bad `Nytka__Memories__Enabled` makes every summary fail, and a bad
  `Nytka__Search__Dictionary` stops indexing.
- **When a change applies.** A change in the app reaches the next job or request without a restart,
  a new `llm.model` included. A change to `.env` needs `docker compose up -d`.

## Tokens and scopes

Every request under `/api` and `/mcp` carries `Authorization: Bearer <token>`. Only `/healthz`
needs none.

- **The admin token**, `Nytka__AdminToken`, always has scope `admin`. The server never stores,
  lists or revokes it, and compares it in constant time, so a broken database cannot lock you out.
- **Named tokens** have a name (1 to 64 characters, unique among active tokens, case-insensitive)
  and a scope. Make one per client, so you can revoke one without touching the rest. The server keeps
  a SHA-256 of each token and its last four characters, shows the token once, when you create it, and
  checks every request against the database: a revoked token gets `401` on its next request.

| Scope | May call |
|---|---|
| `admin` | Everything. The app needs it, and refuses a `read` token. |
| `read` | `/mcp`, and the `GET` endpoints marked `read` in the [API](#api) table: info, conversations, tasks, memories and search. |

A valid token with too little scope gets `403`. A missing, unknown or revoked one gets `401`. A new
endpoint is closed to `read` until it is marked.

Create a token in the app (Device, developer mode, Access tokens) or over the API:

```bash
curl -X POST https://nytka.example.com/api/v1/tokens \
  -H "Authorization: Bearer $NYTKA_ADMIN_TOKEN" -H "Content-Type: application/json" \
  -d '{"name": "laptop", "scope": "read"}'
```

The answer holds the token, `nyt_` and 43 characters, in `token`. Copy it now: nothing shows it
again. `DELETE /api/v1/tokens/{id}` revokes it, and `GET /api/v1/tokens` lists all of them with their
hint, last use and revocation.

## Transcription endpoints

The server sends each batch of speech as a WAV file (16 kHz, mono, 16-bit) in `multipart/form-data`
with `response_format=verbose_json`, and reads `text` and `segments`. The endpoint must support
`verbose_json`:

| Provider | `Nytka__Stt__Url` | `Nytka__Stt__Model` |
|---|---|---|
| OpenAI | `https://api.openai.com/v1/audio/transcriptions` | `whisper-1` (the `gpt-4o-*-transcribe` models answer `json` only) |
| Groq | `https://api.groq.com/openai/v1/audio/transcriptions` | `whisper-large-v3-turbo` |
| whisper.cpp server | `http://<host>:<port>/inference` | none |
| LiteLLM or another gateway | its `/v1/audio/transcriptions` | whatever it routes to a Whisper model |

**Speaker labels.** When a segment in the answer carries `speaker` (a string, or an integer read as
its decimal string, up to 64 characters), the app shows it in color above the segment, and the
transcript the language model reads carries it. Without `speaker` the label is null. Nytka sends each
batch on its own, so an anonymous label such as `SPEAKER_00` can mean different people in different
batches. Labels hold across batches only when the provider names speakers, for instance through voice
enrollment; Nytka does not match them itself.

## The language model

A language model gives each closed conversation a title, a summary and tasks, and it feeds
[memories](#memories). It is optional. Without one, conversations keep their transcripts and show
`aiStatus: none`.

**Set it up.** Set the base URL and the model, in `.env` (`Nytka__Llm__BaseUrl`,
`Nytka__Llm__Model`) or in the app (Server settings). A hosted endpoint also needs its key in `.env`
(`Nytka__Llm__ApiKey`): keys are environment only. Any OpenAI-compatible chat endpoint works. The
server calls `<base URL>/chat/completions` with the key as a bearer token, and sends no temperature
and no token limit, because some models refuse them.

| Provider | `Nytka__Llm__BaseUrl` |
|---|---|
| OpenAI | `https://api.openai.com/v1` |
| Groq | `https://api.groq.com/openai/v1` |
| Ollama | `http://<host>:11434/v1`, no key |
| LiteLLM or another gateway | its `/v1` URL |

The server asks for a JSON answer with a strict `json_schema` and checks the answer itself: a
missing or extra property or a wrong type fails the attempt. An endpoint that answers `400` to
`json_schema`, or a model that ignores it, needs `Nytka__Llm__JsonMode=object`, which asks for a
JSON object and puts the schema in the prompt.

**What runs, and when.** Every minute the server queues a run for each closed conversation that:

- has never been summarized and ended within `Nytka__Llm__BackfillDays` (7 days), or
- has new speech since its last run, or
- failed an hour ago, with fewer than three failed rounds.

Nothing is queued while the base URL or the model is empty, and an open conversation is never
summarized. At most 100 conversations are queued a minute. A conversation under 20 words is `skipped` ("Too short to summarize."). A run writes a
title (up to 80 characters), a summary (up to 1,200) and up to ten tasks (200 characters each), in
`llm.outputLanguage`, and asks for tasks only the wearer has to do. A transcript longer than
`Nytka__Llm__MaxInputChars` is cut at line boundaries into windows; each is answered on its own and
a last request merges them. More than six windows fail the run.

**When it fails.** A run makes up to three attempts, 30 seconds and 2 minutes apart. After the third,
the conversation shows `aiStatus: failed` and a one-sentence `aiMessage`, such as "The language model
endpoint answered 429." An hour later the server tries again, three rounds at most. A 4xx answer other
than 429 (a wrong key, an unknown model) fails the round at once, because trying again cannot help.
When the last three runs all failed, the server sends one conversation a minute instead of every due
one until a run works, so a wrong key does not start a burst of requests. `GET /api/v1/status`
reports the current failure under `ai.lastError`; it clears once a later run works.

**Run it again.** `POST /api/v1/conversations/{id}/enrich` (in the app, ⋮, Regenerate summary) queues
a run at once and resets the failure count. It answers `409` while the conversation is open or no
model is set. A conversation that grows after its summary, for instance when the phone syncs stored
audio, is summarized again once it closes. A title you set stays, and tasks you completed, edited or
deleted are neither duplicated nor brought back.

**Privacy.** The text of each conversation goes to the model endpoint you set, and nowhere else. Point
`Nytka__Llm__BaseUrl` at a local server, such as Ollama, to keep it on your network. Logs and errors
never hold transcript text or a response body.

## Tasks

A task is something the wearer has to do, taken from a summary. Its text carries its deadline: there
are no due dates and no hand-made tasks. Tick, reopen, edit or delete a task in the Tasks tab, or with
`PATCH` and `DELETE` on `/api/v1/tasks/{id}`. A later summary never adds a deleted task again with the same wording, and
never removes or rewrites one you touched. `GET /api/v1/tasks` lists open or done tasks, newest
first, with the title and day of their conversation.

## Memories

A memory is a lasting fact about you: who you are, family, friends, home, work, health, habits,
preferences, goals, commitments. It is not a one-off event, a task or another person's affairs. After
each stored summary the server asks the model for up to five new memories of up to 300 characters, in
`llm.outputLanguage`. It sends the title, the transcript, your 200 newest memories and
`Nytka__Memories__UserName`, so facts about other speakers stay out. Without a name, "you" is the
person wearing the pendant.

- A fact the server already holds is not added again with the same wording, even one you deleted. Extraction never rewrites
  a memory you edited or added yourself.
- Add, edit and delete memories in the Memories tab, or with `POST`, `PATCH` and `DELETE` on
  `/api/v1/memories`. Adding text you deleted earlier brings the memory back; text a live memory
  already holds is a `409`. Deleting a conversation deletes the memories taken from it.
- `Nytka__Memories__Enabled=false`, or no model, means no extraction. The memories you have stay.
- A failed extraction is tried three times, then again an hour later, three rounds at most.
  Conversations summarized before you upgraded to v0.4 have no memories; regenerate a summary to feed
  one in.

## Search

`GET /api/v1/search?q=` and the `search` MCP tool search transcripts, titles, summaries and memories
in Ukrainian and English together.

- Every word of `q` (the first eight, made of letters and digits) must match, exactly or as a prefix,
  in the same place: one transcript segment, the title and summary together, or one memory. `зустріч`
  finds "зустрічами". English words are stemmed, so `running` finds "run", and common ones such as
  "the" are ignored.
- A hit is one conversation or one memory, best first: a title counts more than a summary or a memory,
  and those count more than the transcript. Deleted items never show. The `snippet` is HTML-escaped,
  and the matches sit in `<mark>` tags, the only tag it holds.
- `kinds` picks `conversation`, `memory` or both. `limit` defaults to 20 and caps at 50; `offset` runs
  from 0 to 500 (a larger one counts as 500), and `nextOffset` is null on the last page and when the
  next page would start past 500. No word, or an unknown kind, is a `400`.
- New text is searchable a few seconds after it is written. After the upgrade that adds search, or when
  you change the dictionary, the server indexes the archive in the background, so older rows appear
  over the next minutes; a row not yet indexed is simply not found.

### Ukrainian search (optional)

By default (`Nytka__Search__Dictionary=simple`) a Cyrillic word matches exactly or by prefix, so
`зустріч` finds "зустрічами" but `рік` does not find "року". The opt-in Ukrainian Hunspell dictionary
maps word forms to their base forms, so `рік` finds "року" and "роки". Words it does not know, such
as names and slang, still match exactly or by prefix, and it does not link every form: `друг` does
not find "друзі".

1. Fetch the dictionary before the first `docker compose up`, in the folder that holds
   `docker-compose.yml`. (On Linux, Docker creates a missing `tsearch_data` as root, and the script
   then needs `sudo`.)

   ```bash
   curl -fsSLO https://raw.githubusercontent.com/nytka-app/server/main/scripts/fetch-uk-dictionary.sh
   bash fetch-uk-dictionary.sh --accept-licence ./tsearch_data
   ```

   The script prints the licence caveat below and refuses to run without `--accept-licence`. It
   downloads a pinned release of [dict_uk](https://github.com/brown-uk/dict_uk), checks its SHA-256
   and that the files are UTF-8, and writes `uk_ua.dict` and `uk_ua.affix` (upstream's `uk_UA.dic` and
   `uk_UA.aff`, renamed as Postgres wants them) and upstream's licence notice into the folder. From a
   checkout of this repository, `scripts/fetch-uk-dictionary.sh --accept-licence` fills `./tsearch_data`
   without an argument. An existing pair is left alone unless you add `--force`. It needs `curl`,
   `unzip`, `iconv` and `sha256sum` or `shasum`.

2. Compose mounts `./tsearch_data` read-only into the Postgres container, and a small entrypoint
   links the two files into Postgres's `tsearch_data` when they exist. Missing files never stop
   Postgres. Files fetched later need `docker compose restart postgres`.

3. Set `Nytka__Search__Dictionary=uk_hunspell` in `.env` and run `docker compose up -d`, or set
   `search.dictionary` in the app. The server loads the dictionary in the background and re-indexes:
   rows are found again a few seconds later, or minutes on a large archive.

Check that it loads:

```bash
docker compose exec postgres psql -U nytka -d nytka -c "select ts_lexize('nytka_uk', 'зустрічами');"
```

The answer includes `зустріч`. If Postgres says the dictionary does not exist, the server has not
switched to it: the setting is still `simple`, or the files did not load. `docker compose logs server`
says which.

**Fallback.** Migration 0007 applies with or without the files. Asking for `uk_hunspell` without them
logs a warning, in the server's log and in Postgres's, and search stays on `simple`. Set `simple`
again, or remove the files, and it falls back the same way. Writes never break: only the indexer and
the search queries use the dictionary. The server runs the SQL function `nytka_search_setup(<setting>)`
at every start, whenever the setting changes and when indexing fails; a search that finds the files
gone switches to `simple`. You can call it by hand to debug: `select nytka_search_setup('uk_hunspell');`
answers `uk` when the dictionary loaded and `simple`, with a warning, when it did not. It switches the
configuration as the server does, and when that changes the mapping it clears the search vectors for
the indexer to rebuild. With no argument it means `simple`. The server sets it back to the setting's
value at its next start.

**Your own Postgres.** Copy `uk_ua.dict` and `uk_ua.affix` into the `tsearch_data` folder under
`pg_config --sharedir`, readable by the Postgres user, then set the setting; the server checks again
when it changes. Cyrillic case folding depends on the database's `LC_CTYPE`: the bundled
`postgres:17-alpine` is the tested setup.

**Cost.** Postgres reads the dictionary once per session, on first use: about 2.6 s and 65 MB each on
`postgres:17-alpine`. Search uses two sessions of its own (the indexer and the queries) and no other
connection touches the dictionary, so the price is up to about 130 MB. With the dictionary on, a
search whose words match 5,000 of 100,000 segments took 132 ms.

**Licence caveat.** The dictionary's Hunspell package says GPL 3+, LGPL 2.1+ and MPL 1.1, while the
project README puts the dictionary data under CC BY-NC-SA 4.0, which is non-commercial and
share-alike, and does not say how the two fit. Nytka never ships the files: you fetch them onto your
machine and accept that with the flag. Personal self-hosting is fine either way; ask the upstream
authors before commercial use or redistribution. This is a reading, not legal advice. See
[NOTICE](NOTICE).

## Webhooks

A webhook tells another tool, such as Home Assistant, n8n or a script, that something new exists.
Add one in the app (Device, developer mode, Webhooks) or over the API:

```bash
curl -X POST https://nytka.example.com/api/v1/webhooks \
  -H "Authorization: Bearer $NYTKA_ADMIN_TOKEN" -H "Content-Type: application/json" \
  -d '{"url": "https://hooks.example.com/nytka", "events": ["conversation.ready", "task.created"]}'
```

`events` names event types, or `*` for all of them. The answer holds the signing `secret`, `whsec_`
and 43 characters, once: nothing shows it again, and there is no rotation, so delete the webhook and
make a new one. The URL is `http` or `https`, at most 2,048 characters. Private addresses are allowed,
because your receivers usually sit on your own network. At most 20 webhooks. `PATCH` changes `url`,
`events`, `description` or `active`. `POST /api/v1/webhooks/{id}/test` sends a `ping`, even to an
inactive webhook, and `GET /api/v1/webhooks/{id}/deliveries` lists the log.

| Event | Sent when | `data` |
|---|---|---|
| `conversation.ready` | A summary was stored, the first one and every re-run | `{ id, startedAt, endedAt, title, summary, tasks: [{ id, text }] }` |
| `task.created` | A summary produced a new task | the task, as `GET /api/v1/tasks` shows it |
| `task.completed` | A task was completed | the task |
| `memory.created` | A memory was added, by extraction or by hand | `{ id, text, conversationId }` |
| `ping` | You called `test` | `{}` |

A webhook gets nothing for events from before it existed, or while it is inactive. The change and its
deliveries commit together, so a delivery exists exactly when the change did. A payload never carries
a transcript: fetch what you need with a `read` token, for example `GET /api/v1/conversations/{id}`.

```json
{
  "id": "3f0d2c6e-5b1a-5c8e-9a47-0d1b7e2c9f10",
  "type": "task.created",
  "createdAt": "2026-09-29T18:14:36.4829131Z",
  "data": {
    "id": "018f0000-0000-7000-8000-0000000000a1",
    "conversationId": "018f0000-0000-7000-8000-000000000001",
    "conversationTitle": "Planning the move",
    "conversationStartedAt": "2026-09-29T17:02:11Z",
    "text": "Call the landlord about the key by Friday",
    "done": false,
    "doneAt": null,
    "createdAt": "2026-09-29T18:14:36.4829131Z"
  }
}
```

**Check the signature.** Each request carries `Content-Type: application/json` and these headers:

| Header | Value |
|---|---|
| `Nytka-Event` | The event type |
| `Nytka-Delivery` | The delivery's id |
| `Nytka-Timestamp` | When this attempt was sent, in Unix seconds |
| `Nytka-Signature` | `v1=` and the lowercase hex HMAC-SHA256 of `{timestamp}.{body}`, keyed with the secret |

The signed text is the timestamp's digits, a dot and the exact bytes of the body; the key is the whole
secret, `whsec_` included, as UTF-8. Compute it over the raw body, before you parse the JSON: key
order and spacing in the body are not fixed, so a re-serialized copy will not match. Compare in
constant time. In Python:

```python
import hashlib
import hmac
import time


def verify(secret: str, headers: dict, body: bytes) -> bool:
    timestamp = headers["Nytka-Timestamp"]
    if abs(time.time() - int(timestamp)) > 300:
        return False  # older than five minutes: a replay, or a stuck clock
    signed = timestamp.encode() + b"." + body
    expected = "v1=" + hmac.new(secret.encode(), signed, hashlib.sha256).hexdigest()
    return hmac.compare_digest(expected, headers["Nytka-Signature"])
```

By hand, `printf '%s.%s' "$TIMESTAMP" "$BODY" | openssl dgst -sha256 -hmac "$SECRET"` prints the digest
to compare. Refuse timestamps more than five minutes old. Delivery is at least once and not ordered,
so drop repeats by the payload's `id`.

**Delivery and retries.** A delivery is one `POST` with a 10-second timeout, and redirects are not
followed. Any `2xx` succeeds. Anything else, another status, a timeout or a refused connection, is
tried again after 1 minute, 5 minutes, 30 minutes, 2 hours and 12 hours, six attempts in all, and the
delivery ends `failed`. Every attempt is signed with a fresh timestamp. The log keeps the status, the
attempts, the last HTTP status and a short error such as "HTTP 500" or "timeout", never a response
body. It drops a payload once its delivery ends, and keeps the newest 200 deliveries per webhook.

## MCP

Agents read Nytka over the [Model Context Protocol](https://modelcontextprotocol.io) at
`https://nytka.example.com/mcp`. It speaks Streamable HTTP and keeps no session: `POST` only, and
`GET` and `DELETE` answer `405`. Every request needs a bearer token with scope `read` or `admin`; a
missing, wrong or revoked one gets `401`. A request with an `Origin` header, which a browser always
sends, gets `403`: MCP clients are not browsers. There is no OAuth, so a client that can only sign in
through OAuth cannot connect.

| Tool | Input | Output |
|---|---|---|
| `list_conversations` | `since?`, `before?` (ISO 8601 with an offset, or a date), `limit?` (1 to 50, default 20) | `{ items: [{ id, startedAt, endedAt, title, summary, preview }], nextBefore }` |
| `get_conversation` | `id` (UUID), `transcript?` (default true) | `{ id, startedAt, endedAt, title, summary, tasks: [{ id, text, done }], transcript, truncated }` |
| `list_tasks` | `status?` (`open` or `done`), `conversationId?`, `before?` (a task id), `limit?` (1 to 200, default 50) | `{ items: [Task], nextBefore }` |
| `list_memories` | `before?` (a memory id), `limit?` (1 to 200, default 50) | `{ items: [Memory], nextBefore }` |
| `search` | `query`, `kinds?` (a list of `conversation` and `memory`), `limit?` (1 to 30, default 10) | `{ items: [Hit] }` |

Every tool is read-only (`readOnlyHint`), declares an output schema and returns its result as
structured content and as JSON text. A tool returns the fields its REST endpoint returns, with the
limits and defaults in the table (`search` has no `offset`). Pass `nextBefore` as `before` to read the
next page. An unknown id is a tool error ("No such conversation."); a malformed id, time, status or
kind is JSON-RPC error `-32602`. The transcript has one line per segment, in UTC and without a
speaker label when there is none: `[HH:mm:ss] Speaker: text`. It is cut at a line boundary after
60,000 characters (`truncated: true`) and is null when `transcript` is false.

**Connect a client.** Give it the URL and a `read` token (see [Tokens and scopes](#tokens-and-scopes)).
In Claude Code:

```bash
claude mcp add --transport http nytka https://nytka.example.com/mcp \
  --header "Authorization: Bearer nyt_..."
```

Or in a project's `.mcp.json`, with the token in an environment variable:

```json
{
  "mcpServers": {
    "nytka": {
      "type": "http",
      "url": "https://nytka.example.com/mcp",
      "headers": { "Authorization": "Bearer ${NYTKA_TOKEN}" }
    }
  }
}
```

Any client that speaks Streamable HTTP and sends a fixed `Authorization` header connects the same
way. Behind a reverse proxy, pass `/mcp` and that header through unchanged.

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
[Nytka for Android](https://github.com/nytka-app/android#offline-sync).

## API

Every request under `/api` carries `Authorization: Bearer <token>`; the Scope column says what it
needs. Errors are RFC 9457 problem details. A `400` about a field of a body adds `errors`, an object
from field to a list of messages; a search without a word, a bad chunk and a bad diagnostics body
carry a title only. Objects always carry every field, null ones too. The API is additive,
so `apiVersion` stays `1` and an app from an older version keeps working.

| Method | Path | Scope | Result |
|---|---|---|---|
| GET | `/healthz` | none | 200 when the database answers |
| GET | `/api/v1/info` | read | `{ serverVersion, apiVersion, scope, features }`; `scope` is the caller's |
| GET | `/api/v1/status` | admin | `{ pendingChunks, oldestPendingAt, lastError, lastErrorAt, lastSuccessAt, ai: { configured, pending, lastError, lastErrorAt } }`; a `lastError` is set only while it is current |
| POST | `/api/v1/chunks` | admin | Stores one chunk of Opus frames (`application/vnd.nytka.frames.v1`) |
| POST | `/api/v1/diagnostics` | admin | Stores 1 to 500 diagnostics samples (JSON array, at most 256 KiB); answers `{ accepted }` |
| GET | `/api/v1/diagnostics?since=&limit=` | admin | Samples oldest first: `{ items, nextSince }`; `limit` defaults to 500, caps at 5000 |
| GET | `/api/v1/conversations?before=&since=&limit=` | read | `{ items, nextBefore }`, newest first; an item is `{ id, startedAt, endedAt, status, preview, title, summary, aiStatus }`; `since` keeps conversations that started at or after it; `limit` defaults to 30, caps at 100 |
| GET | `/api/v1/conversations/{id}` | read | The item without `preview`, plus `titleEdited`, `aiMessage`, `aiUpdatedAt`, `tasks` and `segments` (`{ id, startedAt, endedAt, text, speaker }`) |
| PATCH | `/api/v1/conversations/{id}` | admin | Body `{ title }`, 1 to 120 characters, or `null` for the generated title |
| POST | `/api/v1/conversations/{id}/enrich` | admin | Queues a summary run: `202 { aiStatus: "pending" }`; `409` while the conversation is open or no model is set |
| DELETE | `/api/v1/conversations/{id}` | admin | Deletes it with its transcript, audio, tasks and memories |
| GET | `/api/v1/conversations/{id}/transcriptions` | admin | Raw transcription responses |
| GET | `/api/v1/conversations/{id}/audio` | read | The conversation's speech as `audio/ogg` (Opus, packed without re-encoding); pauses are not stored, so they are not played; range requests work; `404` when no speech audio is stored |
| GET | `/api/v1/conversations/{id}/audio/index` | read | `{ durationMs, runs: [{ offsetMs, startedAt, endedAt }] }`: each stretch of continuous capture and where it starts in the stream; `404` as above |
| GET | `/api/v1/tasks?status=&conversationId=&before=&limit=` | read | `{ items, nextBefore }`, newest first; a task is `{ id, conversationId, conversationTitle, conversationStartedAt, text, done, doneAt, createdAt }`; `status` is `open` (default) or `done`; `limit` defaults to 50, caps at 200 |
| PATCH, DELETE | `/api/v1/tasks/{id}` | admin | PATCH body `{ text?, done? }`, `text` 1 to 200 characters; DELETE answers `204` |
| GET, PATCH | `/api/v1/settings` | admin | GET: `{ items: [{ key, type, value, isSet, source, locked, default }] }`. PATCH body `{ values: { "<key>": value or null } }`, all or nothing, `null` restores the default; `400` for an unknown key or a bad value, `409` for a locked key or any API key |
| POST | `/api/v1/tokens` | admin | Body `{ name, scope }`; `201` with the token's fields and `token`, shown once; `409` for a name in use |
| GET | `/api/v1/tokens` | admin | `{ items }`, newest first, revoked ones included: `{ id, name, scope, hint, createdAt, lastUsedAt, revokedAt }` |
| DELETE | `/api/v1/tokens/{id}` | admin | Revokes it: `204` |
| GET | `/api/v1/memories?before=&limit=` | read | `{ items, nextBefore }`, newest first; a memory is `{ id, text, source, conversationId, conversationTitle, conversationStartedAt, createdAt, updatedAt }`, `source` is `ai` or `user`; `limit` defaults to 50, caps at 200 |
| POST | `/api/v1/memories` | admin | Body `{ text }`, 1 to 300 characters; `201` with the memory; `409` when a live memory holds the fact |
| PATCH, DELETE | `/api/v1/memories/{id}` | admin | PATCH body `{ text }`; DELETE answers `204` |
| GET | `/api/v1/search?q=&kinds=&limit=&offset=` | read | `{ items, nextOffset }`; a hit is `{ kind, id, score, title, snippet, at, conversationId }` |
| POST | `/api/v1/webhooks` | admin | Body `{ url, events, description? }`, `description` up to 200 characters; `201` with the webhook and `secret`, shown once; `409` at 20 webhooks |
| GET | `/api/v1/webhooks` | admin | `{ items }`: `{ id, url, events, description, active, createdAt, lastDelivery }`, `lastDelivery` is `{ status, at }` or null |
| PATCH, DELETE | `/api/v1/webhooks/{id}` | admin | PATCH body with any of `url`, `events`, `description`, `active`; DELETE answers `204` and drops its deliveries |
| POST | `/api/v1/webhooks/{id}/test` | admin | Queues a `ping`: `202 { deliveryId }` |
| GET | `/api/v1/webhooks/{id}/deliveries?limit=` | admin | `{ items }`, newest first: `{ id, eventType, status, attempts, lastStatusCode, lastError, createdAt, deliveredAt }`; `limit` defaults to 30, caps at 100 |
| POST | `/mcp` | read | [MCP](#mcp) |

An unknown id is a `404`, a missing or wrong token a `401`, a token with too little scope a `403`.
`aiStatus` is `none`, `pending`, `done`, `skipped` or `failed`. The chunk format and the upload answers
are specified in [docs/specs/v0.1.md](docs/specs/v0.1.md), the other objects in
[v0.2](docs/specs/v0.2.md) and [v0.4](docs/specs/v0.4.md).

Diagnostics are health samples from the app (link quality, queue and upload state) that the app
sends only while its developer switch is on: no audio, no transcripts, no token. Samples older than
30 days are deleted daily; there is nothing to configure.

## What it stores

Everything lives in Postgres, so `pg_dump` backs it up. Keep a dump private: it holds your
transcripts and your webhook secrets.

- Silence is dropped as soon as a chunk is processed. Speech audio (Opus, about 18 MB per hour of
  speech) stays for `RetentionDays`. With `RetentionDays=0` it goes as soon as its transcript
  exists; audio whose transcription failed stays, so you can see what failed, until you delete its
  conversation.
- Transcripts, titles, summaries, tasks and memories stay until you delete them. Deleting a
  conversation deletes its audio, transcript, tasks and memories.
- Tokens are kept as a hash, and the settings the app saved as plain rows. An API key is never in the
  database. A webhook secret is, as plain text, because signing needs it.
- A webhook's delivery log keeps statuses only: no response body, and no payload once a delivery ends.

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
[gitleaks](https://github.com/gitleaks/gitleaks). See [CONTRIBUTING.md](CONTRIBUTING.md).

```bash
dotnet build
dotnet test
```

The search tests that need the Ukrainian dictionary skip themselves until you run
`scripts/fetch-uk-dictionary.sh --accept-licence`; CI runs it first and sets
`NYTKA_REQUIRE_DICTIONARY=1`, so a missing file fails the build.

## License

Apache-2.0, see [LICENSE](LICENSE) and [NOTICE](NOTICE).

Nytka is an independent project, not affiliated with or endorsed by Based Hardware; "Omi" is a
trademark of its owner. Nytka records the people around the wearer: you are responsible for
following the recording and privacy laws where you use it.
