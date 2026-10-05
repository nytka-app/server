---
title: "Configuration"
description: "Every environment variable the Nytka server reads, its default, and whether the app can change it."
order: 5
section: "Reference"
---

You configure the server with environment variables in `.env`; Compose passes them on
([`docker-compose.yml`](../../docker-compose.yml), [`.env.example`](../../.env.example)). The
source of the definitions is the settings code under
[`src/Nytka.Server/Settings/`](../../src/Nytka.Server/Settings/SettingDefinition.cs), with tests in
[`tests/Nytka.Server.Tests/Settings/`](../../tests/Nytka.Server.Tests/Settings/SettingsServiceTests.cs).

## How settings resolve

A setting is the first of these that exists:

1. its environment variable, when set and not empty;
2. the value the app saved, kept in Postgres;
3. its default.

A set variable **locks** its setting. The app shows it read-only, and `PATCH /api/v1/settings`
answers `409`. Leave a variable out to change the setting in the app. A change in the app applies
to the next job or request; a change to `.env` needs `docker compose up -d`. The variable name is
`Nytka__` plus the key with each `.` as `__` and each part capitalized, so `llm.baseUrl` is
`Nytka__Llm__BaseUrl`.

`GET /api/v1/settings` lists every key with its `value`, `source` (`env`, `db` or `default`) and
`locked`. API keys come only from the environment and never reach the database, a response or a
log: the API shows `isSet: true` or `false`.

> [!WARNING]
> Do not copy a block of defaults into `.env`. Every line you set locks its setting.

In the tables, "App" says whether the app can change the value: `edit` or `env only`.

## Required

| Variable | App | Meaning |
|---|---|---|
| `POSTGRES_PASSWORD` | | Password of the bundled Postgres |
| `Nytka__AdminToken` | env only | Admin token, 32+ characters |
| `Nytka__Stt__Url` | env only | Transcription endpoint URL |

The server refuses to start without a valid admin token. A bad transcription, conversation, audio
or model value stops it at start with a message naming the variable.

## Transcription, conversations, audio

| Variable | Default | App | Meaning |
|---|---|---|---|
| `Nytka__Stt__ApiKey` | | env only | Bearer token for the endpoint |
| `Nytka__Stt__Model` | | edit | Sent as `model` |
| `Nytka__Stt__Language` | `auto` | edit | `auto` or a tag such as `uk` |
| `Nytka__Conversations__Gap` | `00:02:00` | edit | Silence that ends a conversation |
| `Nytka__Audio__RetentionDays` | `14` | edit | Days to keep speech audio; 0 to 3650 |
| `Nytka__Mute__Windows` | `[]` | edit | Weekly windows whose audio is dropped |
| `Nytka__User__TimeZone` | `UTC` | edit | IANA zone for prompts and mute windows |

The gap runs from 30 seconds to 1 hour. With `RetentionDays=0`, audio goes once it is
transcribed ([README](../../README.md#what-it-stores)). Mute windows are a JSON list of at most
50, such as `[{"days":[1,2,3,4,5],"start":"09:30","end":"10:00"}]`; `days` are ISO weekdays
(1 Monday to 7 Sunday) and times are local `HH:mm`. Tests:
[`MuteWindowTests.cs`](../../tests/Nytka.Server.Tests/Pipeline/MuteWindowTests.cs).

### Transcription endpoints

The server sends WAV (16 kHz, mono, 16-bit) as `multipart/form-data` with
`response_format=verbose_json`. The endpoint must support that format.

| Provider | `Nytka__Stt__Url` |
|---|---|
| OpenAI | `https://api.openai.com/v1/audio/transcriptions` |
| Groq | `https://api.groq.com/openai/v1/audio/transcriptions` |
| whisper.cpp server | `http://<host>:<port>/inference` |

OpenAI uses model `whisper-1`; its `gpt-4o-*-transcribe` models answer `json` only and do not
work. Groq's README example is `whisper-large-v3-turbo` ([README](../../README.md#transcription-endpoints)).

## Language model

The model is optional. Without one, conversations keep their transcripts and show
`aiStatus: none`. The server calls `<base URL>/chat/completions`.

| Variable | Default | App | Meaning |
|---|---|---|---|
| `Nytka__Llm__BaseUrl` | | edit | OpenAI-compatible base URL |
| `Nytka__Llm__ApiKey` | | env only | Bearer token; omit for a local model |
| `Nytka__Llm__Model` | | edit | Sent as `model` |
| `Nytka__Llm__OutputLanguage` | `auto` | edit | Language of titles and summaries |
| `Nytka__Llm__JsonMode` | `schema` | env only | `schema` or `object` |
| `Nytka__Llm__TimeoutSeconds` | `120` | env only | Wait per model request |
| `Nytka__Llm__MaxInputChars` | `100000` | env only | Transcript characters per request |
| `Nytka__Llm__BackfillDays` | `7` | env only | How far back to summarize |

Use `JsonMode=object` for an endpoint that rejects `json_schema`. A run makes up to three
attempts, then the conversation shows `aiStatus: failed` with a one-sentence `aiMessage`. The
text of each conversation goes to the endpoint you set and nowhere else
([README](../../README.md#the-language-model)).

## Memories, people, tags, digest

| Variable | Default | App | Meaning |
|---|---|---|---|
| `Nytka__Memories__Enabled` | `true` | edit | `false` stops memory extraction |
| `Nytka__Memories__UserName` | empty | edit | Who "you" is for the model |
| `Nytka__People__SuggestNames` | `true` | edit | Name suggestions for voices |
| `Nytka__People__Facts` | `true` | edit | Facts about people |
| `Nytka__People__VoiceMatching` | `false` | edit | Group and match other voices |
| `Nytka__People__VoiceThreshold` | `0.7` | edit | Similarity to join a group |
| `Nytka__Tags__Suggest` | `true` | edit | Model proposes tags |
| `Nytka__Digest__Enabled` | `false` | edit | Daily digest |
| `Nytka__Digest__Hour` | `21` | edit | Local hour after which it is made |

These need the language model. Several are checked when first used, so a bad value in `.env`
fails at that point, not at start ([README](../../README.md#configuration)).
`Nytka__People__VoiceMatching=true` is refused with `400` until your own voice is enrolled.

## Your voice

| Variable | Default | App | Meaning |
|---|---|---|---|
| `Nytka__Voice__Enabled` | `true` | edit | Label new segments once enrolled |
| `Nytka__Voice__UserThreshold` | `0.38` | edit | Similarity at which a line is yours |
| `Nytka__Voice__LearnThreshold` | `0.5` | edit | Similarity at which the print updates |
| `Nytka__Voice__Learn` | `true` | edit | `false` stops the print updating |
| `Nytka__Voice__MinSegmentSeconds` | `1.0` | edit | Shortest segment fingerprinted |
| `Nytka__Voice__ModelPath` | `Models/nemo_en_titanet_small.onnx` | env only | Speaker model path |

The image carries the TitaNet-small speaker model. The learn threshold may not be below the user
threshold. The thresholds are starting values; the README gives a procedure for choosing them
from your own recordings ([spec](../../docs/specs/your-voice.md#how-we-measure-it)).

## Search and calendar

| Variable | Default | App | Meaning |
|---|---|---|---|
| `Nytka__Search__Dictionary` | `simple` | edit | `simple` or `uk_hunspell` |
| `Nytka__Calendar__IcsUrl` | | env only | Read-only ICS feed for briefs |
| `Nytka__Calendar__BriefMinutes` | `30` | edit | Minutes before a meeting; 5 to 240 |

`uk_hunspell` needs a dictionary you fetch yourself with
`scripts/fetch-uk-dictionary.sh --accept-licence`, because its licence is unclear and Nytka does
not ship it ([vision](../../docs/vision.md#key-decisions),
[script](../../scripts/fetch-uk-dictionary.sh)). The calendar address usually carries a token, so
the API shows only `isSet` and no log repeats it.

## Compose

| Variable | Default | Meaning |
|---|---|---|
| `NYTKA_BIND` | `127.0.0.1` | Address Compose publishes on |
| `NYTKA_PORT` | `8080` | Port Compose publishes on |
| `NYTKA_VERSION` | `latest` | Image tag, such as `0.19.0` |

## Tokens and scopes

Every request under `/api` and `/mcp` carries `Authorization: Bearer <token>`. Only `/healthz`
needs none.

| Scope | May call |
|---|---|
| `admin` | everything; the app needs it |
| `read` | `/mcp`, `POST /api/v1/ask` and the `GET` routes marked `read` |

A valid token with too little scope gets `403`; a missing, unknown or revoked one gets `401`
([`ScopeTests.cs`](../../tests/Nytka.Server.Tests/Auth/ScopeTests.cs),
[`TokenApiTests.cs`](../../tests/Nytka.Server.Tests/Auth/TokenApiTests.cs)). The environment admin
token always works and is never stored, so a broken database cannot lock you out. Named tokens
have a unique name of 1 to 64 characters.
