# Configuration

Every setting the server reads, the tokens it accepts, and recipes for the transcription and language-model endpoints.

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
through empty, and [`.env.example`](../../.env.example) comments every optional server setting out: a line
you copy without thinking would lock its setting.

| Variable | Required | Default | In the app | Meaning |
|---|---|---|---|---|
| `POSTGRES_PASSWORD` | yes | | | Password of the bundled Postgres |
| `Nytka__AdminToken` | yes | | env only | Token for the app and your scripts, 32 characters or more, always scope `admin`. The server refuses to start without it. |
| `Nytka__Stt__Url` | yes | | env only | Full URL of the transcription endpoint |
| `Nytka__Stt__ApiKey` | no | | env only | Sent as a bearer token; OpenAI and Groq need it |
| `Nytka__Stt__Model` | no | | editable | Sent as `model`; OpenAI and Groq need it |
| `Nytka__Stt__Language` | no | `auto` | editable | `auto` lets the endpoint detect the language; or a language tag such as `uk` or `en`, sent as `language` (providers usually want a two-letter ISO 639-1 code) |
| `Nytka__Stt__Vocabulary` | no | empty | editable | Comma- or newline-separated words sent as `prompt`, a spelling hint for names and jargon ([Transcription endpoints](#transcription-endpoints)); named people's names are added automatically. Capped to 1000 characters; never logged |
| `Nytka__Stt__SonioxApiKey` | no | | env only | A Soniox API key: batches go to Soniox directly instead of `Nytka__Stt__Url` ([Soniox, direct](#soniox-direct)). Never stored, returned or logged |
| `Nytka__Stt__SonioxBaseUrl` | no | `https://api.soniox.com` | env only | Soniox's API base URL |
| `Nytka__Stt__Prices` | no | Soniox async | editable | JSON price table by engine (the `model` sent, any case; `auto` when none is): `{"soniox":{"audioTokensPerHour":30000,"audioUsdPerMillionTokens":1.5,"requestTokens":390,"textInUsdPerMillionTokens":3.5,"textCharsPerToken":3,"textOutUsdPerMillionTokens":3.5}}`. A field left out is 0; an engine left out, such as `parakeet`, costs nothing. See [Usage and budget](../specs/usage.md) |
| `Nytka__Stt__MonthlyBudgetUsd` | no | empty | editable | Transcription budget per calendar month in US dollars, `0` to `100000` with a dot; empty is no limit. Past it, batches go to `Nytka__Stt__BudgetModel` and are marked to transcribe again; none is skipped |
| `Nytka__Stt__BudgetWarningPercent` | no | `80` | editable | Percent of the budget, 1 to 100, at which `usage.budget.warning` is sent |
| `Nytka__Stt__BudgetModel` | no | `parakeet` | editable | Sent as `model` past the budget: the homelab `stt` service reads it as a pinned engine; pick a free one |
| `Nytka__Stt__Batch__MinSeconds` | no | `5` | editable | Speech a batch must hold before a pause closes it, 1 to 60 seconds; at most `MaxSeconds`. A shorter batch waits for more speech, up to `LongSilenceSeconds` |
| `Nytka__Stt__Batch__MaxSeconds` | no | `30` | editable | Speech after which a batch closes at the next pause (a soft limit), 1 to 60 seconds; between `MinSeconds` and `HardMaxSeconds` |
| `Nytka__Stt__Batch__HardMaxSeconds` | no | `45` | editable | The most speech one batch holds, 1 to 60 seconds; longer unbroken speech is cut there. 60 is the most the `stt` service takes within its memory limit |
| `Nytka__Stt__Batch__LongSilenceSeconds` | no | `30` | editable | Silence that closes a batch even when it is short, 1 to 600 seconds, and never more than `Nytka__Conversations__Gap`. At `120` a batch under `MinSeconds` waits for the next speech of its conversation instead of going out alone. A capture session also counts as ended after this much silence in its uploads, but not before 60 seconds |
| `Nytka__Conversations__Gap` | no | `00:02:00` | editable | Silence that ends a conversation, `hh:mm:ss`, from 30 seconds to 1 hour |
| `Nytka__Audio__RetentionDays` | no | `14` | editable | Days to keep speech audio and [context ranges](../explanation/features.md#context-from-the-phone), 0 to 3650; `0` deletes audio once transcribed and ranges a day after they end |
| `Nytka__Labeling__KeepClips` | no | `false` | editable | Keep the clip of each line labelled in the [labelling app](../explanation/features.md#labelling) past `Nytka__Audio__RetentionDays`, until you delete the label. Off, a labelled clip goes with its audio and only the label stays |
| `Nytka__Mute__Windows` | no | `[]` | editable | JSON list of weekly windows whose audio is dropped by capture time before transcription, at most 50, such as `[{"days":[1,2,3,4,5],"start":"09:30","end":"10:00"}]`. `days` are ISO weekdays of the start day (1 Monday to 7 Sunday), `start` and `end` are local `HH:mm`; an end at or before the start crosses midnight. Uses `Nytka__User__TimeZone` |
| `Nytka__User__TimeZone` | no | `UTC` | editable | IANA time zone such as `Europe/Kyiv`: prompts and mute windows use it; an unknown id falls back to UTC |
| `Nytka__Llm__BaseUrl` | no | | editable | Base URL of an OpenAI-compatible chat endpoint, without a query or fragment; the server calls `<base URL>/chat/completions` |
| `Nytka__Llm__ApiKey` | no | | env only | Sent as a bearer token; leave it out for a local model |
| `Nytka__Llm__Model` | no | | editable | Sent as `model`. A gateway in front of the endpoint may serve a different model under that name; see "A gateway in front of the model" below |
| `Nytka__Llm__OutputLanguage` | no | `auto` | editable | Language of titles, summaries, tasks and memories: `auto` follows the conversation, or a tag such as `uk` |
| `Nytka__Llm__JsonMode` | no | `schema` | env only | `schema` sends a strict `json_schema`; `object` asks for a JSON object instead, for endpoints that reject `json_schema` |
| `Nytka__Llm__TimeoutSeconds` | no | `120` | env only | Seconds to wait for one model request |
| `Nytka__Llm__MaxInputChars` | no | `100000` | env only | Transcript characters per model request; a longer conversation goes in several |
| `Nytka__Llm__BackfillDays` | no | `7` | env only | How far back conversations without a summary are summarized |
| `Nytka__Memories__Enabled` | no | `true` | editable | `false` stops memory extraction |
| `Nytka__Memories__UserName` | no | empty | editable | Who "you" is for the model, up to 64 characters; empty means the person wearing the pendant |
| `Nytka__People__SuggestNames` | no | `true` | editable | `false` stops [name suggestions](../explanation/features.md#people) and [roles](../explanation/features.md#roles) for unnamed voices; it needs the language model |
| `Nytka__People__AutoApplyNames` | no | `true` | editable | `true` lets a sure [name suggestion](../explanation/features.md#auto-applied-names) name its voice at once, undoable; `false` leaves every suggestion for you |
| `Nytka__People__AutoApplyConfidence` | no | `0.85` | editable | Confidence, 0.5 to 1, at or above which a suggestion applies itself |
| `Nytka__People__CardMinConversations` | no | `3` | editable | Conversations, 1 to 50, a voice group must be heard in before a card asks who it is |
| `Nytka__People__Facts` | no | `true` | editable | `false` stops [facts about people](../explanation/features.md#facts-about-people) from being taken from conversations; it needs the language model |
| `Nytka__Tags__Suggest` | no | `true` | editable | `false` stops the model from [proposing tags](../explanation/features.md#tags) for conversations and people and stops sending it tag names; it needs the language model |
| `Nytka__Tags__Auto` | no | `true` | editable | `true` applies a conversation's proposed tags at once and tidies tags nightly (synonyms merged by the model, one-off tags removed after 60 days); `false` leaves proposals for you |
| `Nytka__Privacy__Hide` | no | `health,therapy,intimate,money` | editable | The [sensitive classes](../explanation/features.md#sensitive-content) readers you did not trust leave out (MCP, webhooks, digests, exports, read tokens), comma-separated, or `none` |
| `Nytka__Topics__Enabled` | no | `false` | editable | `true` keeps a [topic page](../explanation/features.md#topic-pages) for each tag and named person on 3 or more conversations, one model call per new conversation on a topic (the first run reads each topic's earlier summaries); it needs the language model |
| `Nytka__Rollups__Enabled` | no | `false` | editable | `true` makes a [summary over time](../explanation/features.md#summaries-over-time) of each week, month and year as it closes (and all time after each month), plus each topic's week, one model call each from digests and summaries; older periods need `POST /api/v1/rollups/backfill`; it needs the language model |
| `Nytka__Events__SilenceMinutes` | no | `20` | editable | Minutes with no speech before a gap between two conversations that both started on the same local day becomes a `silence` [day event](../explanation/features.md#day-events); from 1 to 180 |
| `Nytka__Timeline__Scenes` | no | `false` | editable | `true`: `GET /api/v1/timeline` returns [scenes](../explanation/features.md#scenes) (a conversation cut at scene-starting events and topic shifts, each with `startsWith`) instead of one scene per conversation, and memory extraction runs the person part of the continuity check |
| `Nytka__Summary__Templates__Enabled` | no | `false` | editable | `true` lets a tag named `meeting`, `lecture`, `doctor` or `call`, else the kind the model names, pick the [summary template](../explanation/features.md#summary-templates); `false` uses the default template for every summary; it needs the language model |
| `Nytka__Summary__Templates__Default`, `__Meeting`, `__Lecture`, `__Doctor`, `__Call` | no | built-in | editable | What the summary of that kind holds, at most 600 characters; empty is the built-in text. Also read and edited through `/api/v1/summary-templates` |
| `Nytka__Digest__Enabled` | no | `false` | editable | `true` makes the [daily digest](../explanation/features.md#daily-digest); it needs the language model |
| `Nytka__Digest__Hour` | no | `21` | editable | Local hour, 0 to 23, after which the day's digest is made; the day and the hour use `Nytka__User__TimeZone` |
| `Nytka__Nudges__Enabled` | no | `true` | editable | Whether `GET /api/v1/nudges` returns [nudges](../explanation/features.md#nudges) |
| `Nytka__Nudges__QuietFrom`, `Nytka__Nudges__QuietTo` | no | `22:00`, `08:00` | editable | Local `HH:mm` hours in which no nudge is shown; one made then is shown when they end. Equal values mean no quiet hours |
| `Nytka__Search__Dictionary` | no | `simple` | editable | `simple` or `uk_hunspell`: [Ukrainian search](../explanation/features.md#ukrainian-search-optional) |
| `Nytka__Embeddings__BaseUrl` | no | | editable | Base URL of an OpenAI-compatible embeddings endpoint, without a query or fragment; the server calls `<base URL>/embeddings`. Unset, search and Ask stay full-text only: [vector search](#vector-search) |
| `Nytka__Embeddings__ApiKey` | no | | env only | Sent as a bearer token; leave it out for a local server |
| `Nytka__Embeddings__Model` | no | | editable | Sent as `model`; changing it embeds the archive again |
| `Nytka__Embeddings__Dimensions` | no | | editable | 1 to 2000; sent as `dimensions` when set, for a model that can shorten its vectors. Unset, the model's own size, learned from one request |
| `Nytka__Embeddings__QueryPrefix` | no | empty | env only | Text put before a question, never before stored text; Qwen3-Embedding wants an instruction there. `\n` in it is a line break |
| `Nytka__Embeddings__MaxDistance` | no | `0.5` | env only | Largest cosine distance, above 0 and at most 2, at which a passage still counts as close to the question |
| `Nytka__Embeddings__TimeoutSeconds` | no | `30` | env only | Seconds to wait for one embeddings request |
| `Nytka__Embeddings__BatchSize` | no | `16` | env only | Texts per indexing request, 1 to 512 |
| `Nytka__Voice__Enabled` | no | `true` | editable | Once a voice is enrolled, fingerprint new segments and label the ones that match it as the wearer's; `false` checks nothing new and keeps stored verdicts |
| `Nytka__Voice__UserThreshold` | no | `0.38` | editable | Similarity, 0.1 to 0.95, at or above which a segment is the wearer's; a change re-labels the stored segments |
| `Nytka__Voice__LearnThreshold` | no | `0.5` | editable | Similarity, 0.1 to 0.95, at or above which a segment of 2 s or longer updates the voiceprint; never below `Nytka__Voice__UserThreshold`: the app's change is refused with `400`, and a lower value from the environment counts as that threshold |
| `Nytka__Voice__Learn` | no | `true` | editable | `false` stops matched segments from updating the voiceprint |
| `Nytka__Voice__MinSegmentSeconds` | no | `1.0` | editable | Shortest segment, 1.0 to 5.0 seconds, that is fingerprinted; a shorter one gets no label from Nytka |
| `Nytka__Voice__NewPrintBelow` | no | `0.6` | editable | Similarity, 0.1 to 0.95, at or above which a line that updates the voiceprint updates its nearest centroid instead of opening a new one (several voiceprints instead of one running mean, NYTKA-159) |
| `Nytka__Voice__MaxPrints` | no | `5` | editable | How many voiceprint centroids to keep, 2 to 20, before the two closest merge, weighted by line count |
| `Nytka__People__VoiceMatching` | no | `false` | editable | Group the voices of other people across conversations and match them to the voiceprints of people you named ([voice grouping](../explanation/features.md#voice-grouping)); `true` is refused (`400`) until your own voice is enrolled |
| `Nytka__People__VoiceThreshold` | no | `0.7` | editable | Similarity, 0.5 to 0.95, at or above which a voice joins a group or matches a person's voiceprint |
| `Nytka__Calendar__IcsUrl` | no | | env only | `http` or `https` address of a read-only ICS feed; with it set, meetings of the next 48 hours with someone you named get a [calendar brief](../explanation/features.md#calendar-briefs). The address usually carries a token, so `GET /api/v1/settings` shows only `isSet`, and no log or error repeats it |
| `Nytka__Calendar__BriefMinutes` | no | `30` | editable | How long before a meeting its brief is made, 5 to 240 minutes |
| `Nytka__Speech__Mode` | no | `shadow` | editable | `off`, `shadow` or `on`: whether [speech kinds](../explanation/features.md#speech-kind) are guessed (`off`: no), only shown (`shadow`) or applied (`on`); your own marks apply in every mode. A change queues `apply-speech` |
| `Nytka__Speech__MediaThreshold` | no | `0.94` | editable | Score, 0.5 to 0.99, at or above which a guess is `media`; a score less than 0.15 below it is `unsure`. A change works out the stored guesses again |
| `Nytka__Voice__ModelPath` | no | `Models/nemo_en_titanet_small.onnx` | env only | The speaker model; a relative path is read from beside the server's binaries. The image carries the model |
| `NYTKA_BIND`, `NYTKA_PORT` | no | `127.0.0.1`, `8080` | | Where Compose publishes the server |
| `NYTKA_VERSION` | no | `latest` | | Image tag, such as `0.4.1` |
| `COMPOSE_PROFILES` | no | empty | | `local` starts the no-key speech-to-text and language model containers too |
| `NYTKA_STT_LOCAL_VERSION` | no | `0.5.0-cpu` | | Image tag of the local profile's speech-to-text server |
| `NYTKA_STT_LOCAL_MODEL` | no | `Systran/faster-whisper-small` | | Hugging Face faster-whisper model the local profile downloads on first use |
| `NYTKA_LLM_LOCAL_VERSION` | no | `0.3.14` | | Image tag of the local profile's language model server (Ollama) |

Model names take up to 128 characters. Values travel as strings, as an environment variable carries
them (`"14"`, `"true"`). `GET /api/v1/settings` lists every key with its `value`, its `source` (`env`,
`db` or `default`) and `locked`.

- **Environment only.** The admin token, the transcription URL, the API keys, the four
  `Nytka__Llm__` tuning values and the four `Nytka__Embeddings__` ones (`QueryPrefix`, `MaxDistance`,
  `TimeoutSeconds`, `BatchSize`) cannot be changed from the app or the API; the calendar feed's address is one too. `GET /api/v1/settings`
  shows the URL without any `user:password@`, and shows an API key as `isSet: true` or `false`,
  never its value; a `PATCH` naming `stt.url` or an API key gets `409`, and one naming the admin token or a `Nytka__Llm__` or `Nytka__Embeddings__` tuning value gets `400` ("Unknown setting."). No key reaches the database.
- **Bad values.** The app's `400` names the key. In `.env`, a bad transcription, conversation, audio
  or model value stops the server at start with a message that names the variable.
  `Nytka__Memories__Enabled` and `Nytka__Search__Dictionary` are checked at start too.
  `Nytka__Memories__*` (the rest), `Nytka__People__*`, `Nytka__Tags__*`, `Nytka__Topics__*`, `Nytka__Rollups__*`, `Nytka__Digest__*`, `Nytka__Calendar__*`, `Nytka__Speech__*` and `Nytka__Voice__*` are checked when first used, so type them as
  the table shows.
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
| `read` | `/mcp`, `POST /api/v1/ask`, and the `GET` endpoints marked `read` in the [API](../reference/api.md#api) table: info, conversations, tasks, memories, people, search and the [calendar feed](../explanation/features.md#calendar-feed). |
| `labeling` | Only the [labelling routes](../reference/api.md#labelling) under `/api/v1/labeling`, for a labeller app that cannot sit behind your SSO; `403` everywhere else, `/api/v1/info` included. |

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

### Pairing by QR code

`POST /api/v1/pair` mints a fresh `admin` token (the app refuses `read`, see the table above) and
hands it back as a QR code, so a phone can scan it instead of someone typing the server's address
and the token by hand. It needs an `admin` token to call, same as `POST /api/v1/tokens`.

```bash
curl -X POST https://nytka.example.com/api/v1/pair \
  -H "Authorization: Bearer $NYTKA_ADMIN_TOKEN" -H "Content-Type: application/json" \
  -d '{"name": "phone", "url": "https://nytka.example.com"}'
```

The answer is `{ id, name, createdAt, token, payload, qrCodePng }`; `url` is at most 500 characters, so the code stays readable. `token` is shown once, like any
other new token; `payload` is the same thing as text:

```
nytka://pair?url=<server url, percent-encoded>&token=<the new token>
```

`qrCodePng` is that payload as a QR code, PNG bytes encoded as base64. The Android app scans the
code, decodes the `nytka://pair` URI, and uses `url` and `token` exactly as if they had been typed
into the **Server** step of first run (see the [Android app guide](../guide/android-app.md)).

## Transcription endpoints

The server sends each batch of speech as a WAV file (16 kHz, mono, 16-bit) in `multipart/form-data`
with `response_format=verbose_json`, and reads `text` and `segments`. The endpoint must support
`verbose_json`. With `Nytka__Stt__Vocabulary` set or named people on record, it also sends `prompt`:
the endpoint's own spelling hint, `Nytka__Stt__Vocabulary`'s words and named people's names, joined
with `, ` and capped to 1000 characters; left out when there is nothing to hint.

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
batches. Labels of other people hold across batches only when the provider names speakers, for
instance through its own voice enrollment. Which lines are yours Nytka decides itself once you enroll
[your voice](../explanation/features.md#your-voice); until then, and for segments it does not check, it keeps the provider's
`is_user`.
The name a line shows comes from three steps, first match wins: the person set on that segment
(`PATCH /api/v1/segments/{id}` with `personId`), then the person who owns the segment's `speaker_id`,
then the provider's `speaker`; your own lines show as `Wearer` before any of them. Deleting a person
returns their lines to the next step. `PATCH /api/v1/people/{id}` also keeps your own note on a
person (up to 500 characters), which no model reads or writes.

**Failed batches.** A batch the endpoint refuses three times in a row (30 seconds, then 2 minutes
apart) is marked failed and keeps its audio. The server tries it again on its own 1 hour later, then
after 2, 4, 8 and 16 hours, five rounds in all, so an endpoint that was down for a day costs a delay
and not a transcript, while one that rejects a batch for good stops being asked. `POST
/api/v1/transcriptions/retry` (admin) tries every failed batch that still has its audio right away and
starts its five rounds over; it answers `{ queued }`. New lines in a summarized conversation get it
summarized again, as any new line does.

**Batch rules.** A request carries one batch, and a paid engine charges per request, so fewer, longer batches cost
less. `Nytka__Stt__Batch__MinSeconds`, `MaxSeconds`, `HardMaxSeconds` and `LongSilenceSeconds` default to 5, 30, 45 and
30 seconds; 15, 45, 60 and 120 suit a paid engine per batch. A change reaches the next processing run, not batches
already cut. A PATCH that breaks `MinSeconds <= MaxSeconds <= HardMaxSeconds` is refused with a message on the key;
an environment value that does is lifted to the next one up. See the [Auto transcription spec](../specs/auto-transcription.md).

### Soniox, direct

With `Nytka__Stt__SonioxApiKey` set, the server sends each batch to Soniox's async API itself: it uploads the WAV
(`POST /v1/files`), creates a transcription (`POST /v1/transcriptions`, model `stt-async-v5`, speaker diarization on,
language hints `Nytka__Stt__Language` or `uk`, `ru`, `en` when it is `auto`), polls it every half second for up to 100
seconds, reads the transcript, and then deletes the transcription and the file, on success, failure and timeout alike.
One `context` goes with each request: a `general` entry for the domain and, as `terms`, `Nytka__Stt__Vocabulary`'s words
and named people's names (up to 1000 characters). Soniox's tokens become segments where the speaker changes or a pause of
1 second falls; speakers are labelled `SPEAKER_1`, `SPEAKER_2` within the batch. The ledger records each batch as engine
`soniox`. Past the monthly budget, batches go to `Nytka__Stt__BudgetModel` on `Nytka__Stt__Url` as before, so that URL is
still required. An error names the status code only; Soniox's own error message is never kept.

## The language model

A language model gives each closed conversation a title, a summary and tasks, and it feeds
[memories](../explanation/features.md#memories). It is optional. Without one, conversations keep their transcripts and show
`aiStatus: none`.

**Set it up.** Set the base URL and the model, in `.env` (`Nytka__Llm__BaseUrl`,
`Nytka__Llm__Model`) or in the app (Server settings). A hosted endpoint also needs its key in `.env`
(`Nytka__Llm__ApiKey`): keys are environment only. Any OpenAI-compatible chat endpoint works. The
server calls `<base URL>/chat/completions` with the key as a bearer token, and sends no temperature
and no token limit, because some models refuse them.

**A gateway in front of the model.** The `model` you send is only a name. A gateway or router (one that holds your keys, say) can
map it to another model, usually a cheaper one, and can cap tokens and requests per hour for the key. Read the real model and
price from the gateway's own metrics before you estimate a rerun of many conversations. When a cap is hit the endpoint answers
`429`; a job gives up after three tries, the conversation shows `aiStatus: failed` with a fixed message saying so, and it keeps its
previous summary, so nothing is lost. Queue it again with `POST /api/v1/conversations/{id}/enrich` once the window has reset,
and pace a bulk rerun to the cap (a conversation costs about 10,000 input tokens).

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
`llm.outputLanguage`, and for up to three [proposed tags](../explanation/features.md#tags). In the same call the model labels every candidate task as a
commitment, an idea, advice or noise, and only the wearer's commitments become tasks (see [Tasks](../explanation/features.md#tasks)). Each task may name the person it
is owed to, taken from the people you named in that conversation. A transcript longer than
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

**Privacy.** The text of each conversation goes to the model endpoint you set, and nowhere else. With
`Nytka__Tags__Suggest` on, so do up to 100 of your [tag names](../explanation/features.md#tags), with each summary; turn it off to keep them home. Point
`Nytka__Llm__BaseUrl` at a local server, such as Ollama, to keep it on your network. Logs and errors
never hold transcript text or a response body.

## Vector search

With an embeddings endpoint, search and Ask find what you said by meaning and not only by its words:
"when do I clean the fish tank" finds the conversation where you said "треба помити акваріум". It is
optional and off by default; without it nothing changes.

**What it needs.** Two things: [pgvector](https://github.com/pgvector/pgvector) 0.8 or later in
Postgres, and an OpenAI-compatible `/embeddings` endpoint.

- The stock `postgres:17-alpine` image in `docker-compose.yml` has no pgvector, and Alpine's
  `postgresql-pgvector` package is built for Alpine's own Postgres, not the official image's. Use
  `pgvector/pgvector:0.8.7-pg17` (Debian), or `-pg18` for Postgres 18, in place of the Postgres image.
  The extension is not trusted, so the database user must be a superuser, as `POSTGRES_USER` is in
  the official images, or someone must run `create extension vector;` once by hand. Moving an existing
  database from an Alpine image to a Debian one changes the C library that sorts text, so run
  `REINDEX DATABASE nytka;` once after the switch.
- Any OpenAI-compatible embeddings endpoint works: OpenAI, Ollama (`http://<host>:11434/v1`), LiteLLM,
  or [Text Embeddings Inference](https://github.com/huggingface/text-embeddings-inference) (TEI) on
  your own machine. The speech is mixed Ukrainian, Russian and English, so pick a multilingual
  model. Two that run on a CPU:

| Model | Vectors | TEI container memory | Settings |
|---|---|---|---|
| `BAAI/bge-m3` | 1024 | 2.5 GB steady, 3.6 GB peak while loading | `Nytka__Embeddings__Model=BAAI/bge-m3` |
| `Qwen/Qwen3-Embedding-0.6B` | 1024 (32 to 1024 with `Dimensions`) | 2.5 GB steady, 4.5 GB peak while loading | `Nytka__Embeddings__Model=Qwen/Qwen3-Embedding-0.6B`, `Nytka__Embeddings__QueryPrefix=Instruct: Given a question, retrieve conversation passages that answer it\nQuery: ` |

  Memory as measured with TEI 1.9.4 on a CPU, `--max-batch-tokens 2048`, after embedding batches of 32
  lines; the peak is the container's high-water mark, file cache included. With TEI's default warm-up
  (`--max-batch-tokens 16384`) bge-m3 ran out of memory in a 7.8 GB Docker VM, so pass a smaller value
  for short lines like ours. Neither model card lists Ukrainian among tested languages; both are trained
  on over 100 languages.
  Measure them on your own questions with the [evaluation tool](#measure-it) before you pick.

  ```yaml
  embeddings:
    image: ghcr.io/huggingface/text-embeddings-inference:cpu-1.9.4
    command: ["--model-id", "BAAI/bge-m3", "--max-batch-tokens", "2048"]
    volumes: [embeddings-data:/data]
  ```

  Then `Nytka__Embeddings__BaseUrl=http://embeddings/v1` and the model's name.

**What runs.** A background indexer, never a request, embeds every transcript line of 12 characters or
more, each conversation's title with its summary, and each memory, a batch per request, oldest first.
Each row carries a mark that says it is done, so a restart or a failed request goes on where it stopped,
and an edited title, summary or memory is embedded again. The first time it asks the model for one
vector to learn its size, then runs the SQL function `nytka_embeddings_setup(<model>, <size>)`, which
creates the extension and three tables with HNSW indexes (pgvector's approximate nearest-neighbour
index). Another model or size drops the vectors and embeds the archive again. On a CPU expect a few
lines a second (about 4 for bge-m3 and 2 for Qwen3-Embedding-0.6B on a laptop's Docker VM), so 10,000
lines take an hour or more the first time; search uses what is ready meanwhile.
`GET /api/v1/info` lists `semantic-search` once the tables are ready.

**How a search ranks.** Each search also embeds its words (Ask embeds the question) and asks for the
nearest lines, conversations and memories, under the same kinds, dates and tag as the words. A
passage further than `Nytka__Embeddings__MaxDistance` from the question is left out. The right value
depends on the model: with bge-m3, passages on the question's topic were 0.22 to 0.47 away and
unrelated ones 0.51 to 0.61 in a small synthetic test, hence the default 0.5. Check it with the
[evaluation tool](#measure-it) on your own questions. The two lists
merge by reciprocal-rank fusion: a hit scores 1/(60 + its rank in the full-text list) plus 1/(60 +
its rank in the vector list), so a hit in both comes first and a hit by meaning alone can appear.
`score` then holds that sum. People have no vectors and rank by words alone. When the endpoint fails
or times out, the search answers by words alone and logs one fixed sentence.

**Fallback.** Migration 0040 applies with or without pgvector. Without it, the server logs that
pgvector is not available and search stays full-text. Unset the base URL or the model and the server
stops using the vectors at once; the tables stay until a model is set again.

**Privacy.** Every transcript line, title, summary and memory goes to the embeddings endpoint, and so
does every search and Ask question. Point it at a server on your network to keep them home. Logs and
errors never hold the text or a key.

### Measure it

`tools/Nytka.SearchEval` reports recall at 5 and 10 (the share of the expected conversations among the
first 5 or 10) for full-text search and for hybrid search, over questions you write. Keep the file out
of the repository: it holds your conversations' ids. One JSON object per line:

```json
{"question": "коли я мию акваріум?", "expected": ["0199..."], "terms": "акваріум"}
```

`terms` is optional; without it the question's own words are the full-text query, any of them
matching, as Ask runs a query. Run it against the database the server has embedded:

```bash
ConnectionStrings__Postgres="Host=...;Database=nytka;Username=nytka;Password=..." \
Nytka__Embeddings__BaseUrl=http://127.0.0.1:8081/v1 Nytka__Embeddings__Model=BAAI/bge-m3 \
dotnet run --project tools/Nytka.SearchEval -- --file ~/nytka-questions.jsonl --max-distance 0.5
```

It prints one row per question, by number, and the averages; never text from the archive.
