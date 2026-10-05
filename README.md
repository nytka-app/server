# Nytka server

The self-hosted half of [Nytka](docs/vision.md), a companion app and server for the Omi AI
necklace. The Nytka Android app streams the pendant's audio here. The server drops silence, sends
speech to the transcription endpoint you choose, groups the transcript into conversations and serves
them back to the app. With a language model you choose, it also gives each conversation a title, a
summary and tasks, and keeps lasting facts about you as memories. It searches everything in Ukrainian
and English. Webhooks tell your other tools when something new exists, and AI agents read it all
over MCP. Omi's cloud sees none of it.

Status: the first four milestones of the [roadmap](docs/vision.md#roadmap) are shipped: capture and
transcription ([spec v0.1](docs/specs/v0.1.md)), the AI layer ([v0.2](docs/specs/v0.2.md)), offline
sync ([v0.3](docs/specs/v0.3.md)) and memory and search ([v0.4](docs/specs/v0.4.md)). Parts of the
later milestones are shipped too. Each spec says what it does and does not do. Milestones have
names and release numbers come from release-please, so the two differ.

## Install in 15 minutes

For a person with an Omi pendant and a computer or small server that stays on. The times add up to
about 15 minutes when the prerequisites are in place; the [last list](#what-does-not-fit-in-15-minutes)
says what is not counted.

**Before you start**

- A Linux, macOS or Windows machine with [Docker Compose](https://docs.docker.com/compose/install/)
  that runs while you wear the pendant. A VPS or a home server is best; a laptop that sleeps loses
  nothing, because the app keeps the audio and uploads it later, but conversations then appear late.
- A transcription account. The cheapest working choice is [Groq](https://console.groq.com): a free
  tier covers 28,800 seconds of audio a day for `whisper-large-v3-turbo` (check the current limits
  there). The server drops silence before it sends anything, so a day of wearing stays far below
  that. OpenAI's `whisper-1` also works and is paid by the minute. Create an API key and keep it.
- A way for the phone to reach the server over HTTPS. The easiest is [Tailscale](https://tailscale.com):
  the server machine and the phone both join your tailnet, and the next section gives the server an
  `https://` address with a real certificate. A domain with a reverse proxy works too.
- The pendant charged, on consumer firmware 3.0.x, and an Android 12+ phone.

**1. Download the Compose file and the settings template (1 minute)**

```bash
mkdir nytka && cd nytka
curl -fsSLO https://raw.githubusercontent.com/nytka-app/server/main/docker-compose.yml
curl -fsSL -o .env https://raw.githubusercontent.com/nytka-app/server/main/.env.example
```

**2. Fill in `.env` (3 minutes)**

Generate the two secrets and write them in, with the Groq endpoint, key and model:

```bash
sed -i.bak \
  -e "s|^POSTGRES_PASSWORD=.*|POSTGRES_PASSWORD=$(openssl rand -hex 24)|" \
  -e "s|^Nytka__AdminToken=.*|Nytka__AdminToken=$(openssl rand -hex 24)|" \
  -e "s|^Nytka__Stt__Url=.*|Nytka__Stt__Url=https://api.groq.com/openai/v1/audio/transcriptions|" \
  -e "s|^# Nytka__Stt__ApiKey=.*|Nytka__Stt__ApiKey=PASTE_YOUR_GROQ_KEY|" \
  -e "s|^# Nytka__Stt__Model=.*|Nytka__Stt__Model=whisper-large-v3-turbo|" .env
```

Replace `PASTE_YOUR_GROQ_KEY` in `.env` with your key (open the file in an editor; do not paste the key
into a shared terminal log). With OpenAI use `https://api.openai.com/v1/audio/transcriptions` and
`whisper-1`. Other endpoints, including a self-hosted whisper.cpp, are in
[Transcription endpoints](#transcription-endpoints). Leave everything else commented out: a
variable that is set locks its setting in the app ([Configuration](#configuration)).

**3. Start it and check (2 minutes)**

```bash
docker compose up -d --wait
curl http://127.0.0.1:8080/healthz
```

The first start pulls two images. `healthz` answers `{"status":"healthy"}`. If the server does not
come up, `docker compose logs server` says why, for example a token shorter than 32 characters.

**4. Give the phone an HTTPS address (3 minutes with Tailscale)**

The server listens on `127.0.0.1:8080` only. With Tailscale installed and signed in on the server
machine:

```bash
tailscale serve --bg --https=443 localhost:8080
tailscale serve status
```

`status` prints the address, `https://<machine>.<tailnet>.ts.net`. Install Tailscale on the phone,
sign in to the same tailnet and keep it on. (If the command says HTTPS certificates are off, enable
HTTPS in the Tailscale admin console under DNS, then run it again.) With a domain instead, point a
reverse proxy that terminates HTTPS, such as Caddy, at `127.0.0.1:8080`. On a VPN without HTTPS,
set `NYTKA_BIND` in `.env` to the VPN address, run `docker compose up -d` and use
`http://<that address>:8080` with the app's private-network switch, which sends audio unencrypted.

**5. Install the app and pair the pendant (4 minutes)**

Stop the official Omi app (two apps cannot hold the pendant). Install `nytka-<version>.apk` from the
[app's releases](https://github.com/nytka-app/android/releases/latest), then follow
[Nytka for Android](https://github.com/nytka-app/android#first-run-about-5-minutes): enter the
address from step 4 and the token, tap **Test connection**, allow Nearby devices and notifications,
accept the consent note and tap **Pair pendant**. The token is on your server machine:

```bash
grep '^Nytka__AdminToken=' .env
```

Copy it to the phone without pasting it into a chat or a note that syncs elsewhere (a password
manager's share, or typing it, both work).

**6. Check that it works (2 minutes)**

1. In the app the chip at the top reads **Recording** and the status card shows "Server: Last upload".
   If the chip stays on **Waiting**, check that the pendant is on and next to the phone.
2. Say a few sentences, wait about two minutes, then pull the **Conversations** list down. The
   speech appears as a conversation after the silence gap (two minutes by default).
3. If it does not, ask the server what it thinks (replace the address and token):

   ```bash
   curl -H "Authorization: Bearer $NYTKA_ADMIN_TOKEN" https://<address>/api/v1/status
   ```

   `lastError` names the problem, for example `The transcription endpoint answered 401.`: the key, URL
   or model in `.env` is wrong. Fix it and run `docker compose up -d`.

**Then, when you have a minute:** titles, summaries, tasks and memories need a language model, which
is optional: [The language model](#the-language-model) (Groq's `https://api.groq.com/openai/v1` with
the same key works; pick a chat model from its list). Back up the `postgres-data` volume, and read
[Upgrading](#upgrading).

### What does not fit in 15 minutes

- Installing Docker, joining Tailscale on two devices for the first time (about 5 minutes each) and
  pointing a domain at a server with a proxy and a certificate (10 minutes or more).
- Creating the Groq or OpenAI account and key, and any wait for approval.
- Updating the pendant's firmware: it needs the official Omi app, and Nytka tells you when the
  codec is wrong. Offline sync needs firmware 3.0.20 or later.
- The Ukrainian search dictionary: an extra download with a licence prompt
  ([Ukrainian search](#ukrainian-search-optional)).
- A local transcription model: it needs a machine with enough CPU or GPU and setup time that
  depends on the model.
- Evaluating the result: summaries only show once a conversation has closed and a model is set,
  and a week of wear is what shows whether it fits you.

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
| `Nytka__People__SuggestNames` | no | `true` | editable | `false` stops [name suggestions](#people) and [roles](#roles) for unnamed voices; it needs the language model |
| `Nytka__People__Facts` | no | `true` | editable | `false` stops [facts about people](#facts-about-people) from being taken from conversations; it needs the language model |
| `Nytka__Tags__Suggest` | no | `true` | editable | `false` stops the model from [proposing tags](#tags) for conversations and people and stops sending it tag names; it needs the language model |
| `Nytka__Digest__Enabled` | no | `false` | editable | `true` makes the [daily digest](#daily-digest); it needs the language model |
| `Nytka__Digest__Hour` | no | `21` | editable | Local hour, 0 to 23, after which the day's digest is made; the day and the hour use `Nytka__User__TimeZone` |
| `Nytka__Search__Dictionary` | no | `simple` | editable | `simple` or `uk_hunspell`: [Ukrainian search](#ukrainian-search-optional) |
| `Nytka__Voice__Enabled` | no | `true` | editable | Once a voice is enrolled, fingerprint new segments and label the ones that match it as the wearer's; `false` checks nothing new and keeps stored verdicts |
| `Nytka__Voice__UserThreshold` | no | `0.38` | editable | Similarity, 0.1 to 0.95, at or above which a segment is the wearer's; a change re-labels the stored segments |
| `Nytka__Voice__LearnThreshold` | no | `0.5` | editable | Similarity, 0.1 to 0.95, at or above which a segment of 2 s or longer updates the voiceprint; never below `Nytka__Voice__UserThreshold`: the app's change is refused with `400`, and a lower value from the environment counts as that threshold |
| `Nytka__Voice__Learn` | no | `true` | editable | `false` stops matched segments from updating the voiceprint |
| `Nytka__Voice__MinSegmentSeconds` | no | `1.0` | editable | Shortest segment, 1.0 to 5.0 seconds, that is fingerprinted; a shorter one gets no label from Nytka |
| `Nytka__People__VoiceMatching` | no | `false` | editable | Group the voices of other people across conversations and match them to the voiceprints of people you named ([voice grouping](#voice-grouping)); `true` is refused (`400`) until your own voice is enrolled |
| `Nytka__People__VoiceThreshold` | no | `0.7` | editable | Similarity, 0.5 to 0.95, at or above which a voice joins a group or matches a person's voiceprint |
| `Nytka__Calendar__IcsUrl` | no | | env only | `http` or `https` address of a read-only ICS feed; with it set, meetings of the next 48 hours with someone you named get a [calendar brief](#calendar-briefs). The address usually carries a token, so `GET /api/v1/settings` shows only `isSet`, and no log or error repeats it |
| `Nytka__Calendar__BriefMinutes` | no | `30` | editable | How long before a meeting its brief is made, 5 to 240 minutes |
| `Nytka__Voice__ModelPath` | no | `Models/nemo_en_titanet_small.onnx` | env only | The speaker model; a relative path is read from beside the server's binaries. The image carries the model |
| `NYTKA_BIND`, `NYTKA_PORT` | no | `127.0.0.1`, `8080` | | Where Compose publishes the server |
| `NYTKA_VERSION` | no | `latest` | | Image tag, such as `0.4.1` |

Model names take up to 128 characters. Values travel as strings, as an environment variable carries
them (`"14"`, `"true"`). `GET /api/v1/settings` lists every key with its `value`, its `source` (`env`,
`db` or `default`) and `locked`.

- **Environment only.** The admin token, the transcription URL, the API keys and the four
  `Nytka__Llm__` tuning values cannot be changed from the app or the API; the calendar feed's address is one too. `GET /api/v1/settings`
  shows the URL without any `user:password@`, and shows an API key as `isSet: true` or `false`,
  never its value; a `PATCH` naming `stt.url` or an API key gets `409`, and one naming the admin token or a `Nytka__Llm__` tuning value gets `400` ("Unknown setting."). No key reaches the database.
- **Bad values.** The app's `400` names the key. In `.env`, a bad transcription, conversation, audio
  or model value stops the server at start with a message that names the variable.
  `Nytka__Memories__*`, `Nytka__People__*`, `Nytka__Tags__*`, `Nytka__Digest__*`, `Nytka__Calendar__*`, `Nytka__Search__Dictionary` and `Nytka__Voice__*` are checked when first used, so type them as
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
| `read` | `/mcp`, `POST /api/v1/ask`, and the `GET` endpoints marked `read` in the [API](#api) table: info, conversations, tasks, memories, people and search. |

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
batches. Labels of other people hold across batches only when the provider names speakers, for
instance through its own voice enrollment. Which lines are yours Nytka decides itself once you enroll
[your voice](#your-voice); until then, and for segments it does not check, it keeps the provider's
`is_user`.
The name a line shows comes from three steps, first match wins: the person set on that segment
(`PATCH /api/v1/segments/{id}` with `personId`), then the person who owns the segment's `speaker_id`,
then the provider's `speaker`; your own lines show as `Wearer` before any of them. Deleting a person
returns their lines to the next step. `PATCH /api/v1/people/{id}` also keeps your own note on a
person (up to 500 characters), which no model reads or writes.

## Your voice

Nytka learns your voice once and then marks the segments you said as yours, with any transcription
provider. A line marked yours shows as `Wearer` in the app, in the transcript the language model
reads, and in tasks, memories, Ask and MCP; other lines keep the provider's speaker or the name you
gave that voice. It needs the speaker model, TitaNet-small, which the Docker image carries
(`GET /api/v1/info` lists `voice` under `features` when it is there).

**Enroll.** In the app, read the prompts into the pendant for 30 to 60 seconds, in every language you
speak. The app sends the pendant's Opus frames to `POST /api/v1/voice/enrollment` instead of the
normal upload, so the reading never becomes a conversation. The server finds the speech, cuts it into
windows of 3 to 10 seconds, fingerprints each and keeps their mean as your voiceprint. It refuses
(`422`) a reading with less than 20 seconds of speech, or one whose windows disagree (a second voice or
heavy noise), and says which. `mode=add` blends a new reading into the voiceprint, for example for a
new language; the default `mode=replace` starts over. The audio is never stored.

**Matching.** Each new segment from 1 second (`Nytka__Voice__MinSegmentSeconds`) to 30 seconds gets a
similarity to your voiceprint, and is yours at `Nytka__Voice__UserThreshold` (0.38) or above. A shorter
segment gets no label rather than a guess, and a longer one keeps the provider's. If fingerprinting
fails, the transcript is stored anyway. Segments of 2 seconds or longer that match well
(`Nytka__Voice__LearnThreshold`) move the voiceprint towards your voice as the pendant hears it;
`POST /api/v1/voice/reset` goes back to what you enrolled. A threshold change or a new voiceprint
re-labels the stored segments.

**Which label wins.** Your own mark on a segment ("This is me" or "This is not me",
`PATCH /api/v1/segments/{id}`), then Nytka's verdict when Nytka checked the segment, then the
provider's `is_user`. A conversation's segments say which one decided in `isUserSource`. Marking a
segment of 2 seconds or longer as yours also teaches the voiceprint.

**Forget.** `DELETE /api/v1/voice` deletes the voiceprint, every segment fingerprint, every
[voice group](#voice-grouping) and every similarity and verdict in one go; the provider's labels come back. Your marks stay: they are
statements, not biometrics.

**Choosing the threshold.** `GET /api/v1/voice/segments` lists every segment's similarity and the three
labels, without text. Label a few hundred segments of your own conversations blind, as yours or not,
join them on `segmentId`, and pick the threshold that keeps false "yours" lines rare in each language
you speak. Someone in your household with a voice close to yours may need a higher threshold. The
full procedure is in [docs/specs/your-voice.md](docs/specs/your-voice.md#how-we-measure-it); your
recordings and labels stay on your side.

## People

Names for the other voices in your conversations, from what was said. After each stored summary, the
server asks the model whether any unnamed voice gave its name: it introduced itself ("I'm Anna",
"мене звати Олена"), or someone addressed it by name in the next line or two. A name that is only
mentioned does not count, and neither does the wearer's own (`Nytka__Memories__UserName`). Each answer
becomes a **suggestion** with the line that shows the name and a confidence of 0.5 to 1; a model
answer below 0.5 is dropped. A suggestion changes no label: you accept or reject it.

- A voice is what the label rule can tell apart: the provider's `speaker_id`, or, for a segment with
  only a `speaker` label, that label within one batch. A segment with neither cannot be named.
- A conversation under 60 words, a repeated summary with no new speech, and a voice that already has
  a name (a person known only by a [role](#roles) is asked for one) or is yours are not asked about. At most one suggestion per voice per run, the most
  confident.
- **Accept** names the voice as `POST /api/v1/people` does, so every segment of that `speaker_id`
  shows the name, in every conversation; a suggestion for a batch's label names that batch's segments
  only, as `PATCH /api/v1/segments/{id}` with `personId` does. A name equal to a person's (any case)
  uses that person. Other pending names for the same voice go. A suggestion for a [voice group](#voice-grouping)
  names the group as a card does, and goes with the group.
- **Reject** keeps the name on record, so it is never suggested again for that voice.
- **Checks.** The server does not trust the model: a name is one to three capitalized words of letters
  (40 characters at most), none a pronoun, answer, interjection, evaluation or generic address
  ("Ти", "Нет", "Прикольно", "Девочка", "girl") unless the line writes it with a capital in mid-sentence,
  and it must occur in the line shown as evidence (any case ending: Діма, Діму, Дімі). The wearer's own
  name (`Nytka__Memories__UserName`, or a name the wearer gave for themselves, "I'm X", "я X") is never
  suggested, nor is a voice that is also the wearer.
- **Limits.** English audio playing nearby is a voice like any other: a name spoken by media can be
  suggested, and you reject it.

### Roles

Someone is often known by what they do before you know their name: the repairman. The same call may
return a **role** for a voice when the transcript ties one to it: the voice says it ("I'm the plumber"), or
another speaker refers to or addresses it so ("the repairman is here", "майстер приїхав", "дякую,
майстре"). The model gives the base form of the word ("майстер"); the server stores what it sent after
normalizing it as a [tag](#tags), with no stemming. The model is told to take nothing from how a voice sounds
and to give no age, gender, health, religion, ethnicity or politics.

- **Checks.** A role is one to three words of letters, none a pronoun, answer or generic address (the name
  stoplist: "ти", "he", "friend", "друже"), is not one the wearer gave for themselves ("I'm the plumber"),
  and occurs in the line shown as evidence or one of the three either side (any case ending). A suggestion
  with neither a valid name nor a valid role is dropped; a name that fails with a valid role leaves the role.
- **A role without a name** is a suggestion shown as "Repairman" (`name`, with `role: "repairman"`
  and `named: false`). **Accept** creates a **new** person, never one found by name (two repairmen are two
  people until you merge them): "Repairman", or "Repairman 2" when that name is taken, with `named: false`
  and the role as a plain [tag](#tags) (`repairman`), and names the voice, label or batch as for any name.
  With a name and a role, accept names the voice as above and the person also gets the role as a tag.
- **A name said later.** The voice of a person known only by role is sent to the model as "Voice A (known
  as: repairman)", and a name it hears for them becomes a suggestion with `target: "person"` and that
  person's `personId`. Accept renames the person (their id, voices, segments and tags stay) and sets
  `named: true`, or, when a person of that name exists, merges the role-only person into them. Renaming by hand
  (`PATCH /api/v1/people/{id}` with `name`) also sets `named: true`. A role-only person is not listed among the
  people already named in the prompt, and gets no role suggestion.
- Nothing matches roles across conversations by text: the same repairman in another conversation is the
  same person once the voice is linked (voice grouping, or by hand).
- Roles are part of the name suggestion: they follow `Nytka__People__SuggestNames`, are rejected the same way
  (a rejected role is never offered again for that voice), and `GET /api/v1/info` lists `roles` under `features`.
  Existing conversations are asked again for roles through `POST /api/v1/people/backfill?force=true` (the name
  checks are now version 2).

A failed run is retried like [memories](#memories): three attempts, then an hour later, three rounds
at most. The text of the conversation goes to your language model endpoint, as for a summary, together
with the names of the people you already have; logs and errors hold neither. Turn it off with
`Nytka__People__SuggestNames=false`; the suggestions already made stay.

### Backfill

Conversations summarized before People existed have no suggestions or facts. `POST /api/v1/people/backfill?limit=`
(admin) queues `suggest-names` and, while `Nytka__People__Facts` is on, `extract-person-facts` for conversations whose summary
is done and that have no finished run of that job, newest first, at most `limit` (default 200, up to 1000). It does not run
the summary again, so titles, summaries, tasks and memories stay as they are. Calling it twice queues nothing twice: a
conversation whose job already waits counts in `skipped`, and `remaining` counts the eligible conversations beyond `limit`,
so call it again until it is `0`. The jobs run in the usual AI lane, behind live summaries, one at a time. With
`Nytka__People__SuggestNames=false` no name job is queued, and without a language model the answer is `409`. Facts the backfill
finds publish `person.fact.created` like any other, so a webhook subscribed to it gets one per backfilled fact.

To read old conversations again after the name checks changed, call `POST /api/v1/people/suggestions/revalidate` (admin; deletes
the pending suggestions that fail the checks and answers `{ checked, removed, kept }`), then `POST /api/v1/people/backfill?force=true`
until `remaining` is `0`: `force` also queues conversations whose name run was made under older checks. A plain backfill queues
only conversations with no finished run. Accepted and rejected suggestions stay.

## Tags

Short words on conversations and on people (`work`, `family`, `repairman`), only yours: nothing adds a
tag but you. Lists show them, and `?tag=` on conversations, people and [search](#search) keeps the ones that have it.
The MCP tools carry them too: `list_tags`, a `tag` filter on `list_conversations`, `list_people` and `search`, and
`tags` on their items, `get_conversation` and `get_person` ([MCP](#mcp)).

- A name is normalized: trimmed, one leading `#` dropped, lower case, each run of spaces one `-`. What is
  left is 1 to 32 letters (any script), digits, `-` and `_`, starting with a letter or digit, else `400`.
  `Робота` and `#робота` are one tag; `dog walker` is `dog-walker`; `a/b` is refused.
- At most 20 tags on one conversation or person (`409`). There is no limit on tags in all.
- A tag exists while something holds it: removing its last link, deleting the conversation or person
  that held it, or merging does not leave an empty tag behind. `GET /api/v1/tags` counts from the
  links, so the counts cannot drift.
- Merging two conversations (the same speech arriving late) or two people gives the survivor the tags of
  both. Rename, merge into another tag and delete work on the whole server and need an `admin` token.
- The `conversation.ready` webhook carries `tags`, the conversation's tags when it was summarized. Adding
  a tag sends no event; read `GET /api/v1/conversations/{id}` for the current ones.
- **Proposed tags.** With `Nytka__Tags__Suggest` on (the default) and a [language model](#the-language-model) set, each
  summary may bring up to 3 proposed tags for the conversation, and each run of [facts about people](#facts-about-people)
  up to 3 for each person it lists ("neighbour", "doctor"). A proposal is a name waiting in the
  [review inbox](#review) (kind `tag`) and `GET /api/v1/tags/suggestions`; it tags nothing until you accept it. A
  rejected tag is never proposed again for that conversation or person, and neither is one you accepted and later removed.
  The model does not propose a name of a person in the conversation, an invalid name, or a tag the item has. A conversation
  under 60 words gets none. Accepting is the same add as `PUT`, so it is refused with `409` when the conversation or person
  has 20 tags, and the proposal stays pending. Merging two conversations or two people gives the survivor the proposals of both.
  A person's proposal also names the conversation it came from.
- **What the model sees.** With proposals on, the 100 tags in use most (names only) go to the model with each summary and
  each facts request so it reuses them, and the prompts ask for nothing about health, religion, ethnicity, politics or how
  someone sounds (nor age or gender, for a person). The model reads text, never audio. With proposals off no tag name is sent
  and an answer's tags are ignored.
- Tags can be private ("therapy"). No tag name is in a log line or an error message; the server logs no
  request lines (Serilog's `Request starting` and `Request finished`), because they hold the path.

## Voice grouping

Off by default. Your voice only tells you apart from everyone else; this opt-in layer also tells the
others apart, so Nytka can ask "Who is this?" and later suggest the name of someone you already named.
It needs [your voice](#your-voice) enrolled, the speaker model and `Nytka__Audio__RetentionDays` of 1 or
more (with `0` fingerprints die in the transcription transaction, before any grouping runs); then
`GET /api/v1/info` lists `voice-groups` under `features`. The app shows the switch with a sentence on the
legal duty of recording people.

**Grouping.** Every minute, while `Nytka__People__VoiceMatching` is on and a fingerprint waits, the
`group-voices` job takes the fingerprints of segments that are not yours and have no person, in segment
order. One that reaches `Nytka__People__VoiceThreshold` against the voiceprint of a person you confirmed
before joins that person's pending voice match for its conversation. Otherwise it joins the group whose
centroid it reaches the threshold with, which moves as a running mean, or starts a group. No model is
called and no label changes: only you naming a group, or accepting a match, links segments to a person
(the cards below). A group's fingerprints are the ones still held.

**Cards.** `GET /api/v1/people/cards` offers what is waiting for your answer: a group with no person ("Who
is this?") and a pending match ("Is this Olena?"). A card has one clean stretch of that voice: consecutive
segments in one conversation with no other segment between them, together at least 5 s, with their speech
audio still stored. Its clip is the stretch's first 10 s at most, cut from the stored frames by capture
time (`GET /api/v1/people/cards/{kind}/{id}/clip`, `audio/ogg`), and its `lines` are the segments that
start inside it. A set holds at most 4 cards and at most 2 from one conversation, the newest conversation
first, one card per group or match (its newest stretch); it is empty while voice matching is off. Answer
with `POST /api/v1/people/cards/{kind}/{id}`: `{ personId }` or `{ name }` names a group (a person of that
name, any case, else a new one) as [Confirming](#voice-grouping) says, and confirms a match (the person
must be the one it asks about); `{ skip: true }` hides the card for 7 days; `{ reject: true }` says "not a
person" (the group is deleted and its fingerprints are never grouped again) or "not them" (the match is
kept as rejected and never offered again). A group suggestion from [People](#people) accepts the same way.
A card whose audio has been deleted is not offered and its clip is `404`; a pending match then waits for
an answer without a clip, and confirming it links its segments but adds no sample. The routes need an
admin token and carry no vector.

**Confirming.** Naming a group or accepting a match links its segments to the person, blends the
fingerprints still held into that person's voiceprint (weighted by count, as for yours) and deletes the
group, in one transaction. A person's voiceprint is the one vector of someone else that outlives audio.

**A group never outlives its audio.** Retention deletes the groups its fingerprints leave empty in the
same statement, deleting a conversation does the same, and `DELETE /api/v1/voice` deletes every group
together with your voiceprint. A group that only lost some of its fingerprints keeps its centroid.
`DELETE /api/v1/people/{id}` deletes the person's voiceprint and pending matches with them.

**Forget.** `DELETE /api/v1/people/voiceprints` deletes every group, every person voiceprint and every
pending match. Segment links stay: they are statements, as your marks are. Turning the setting off stops
new work and keeps what is stored until that call.

**Measuring it.** `GET /api/v1/people/voice-eval` lists the fingerprinted segments of other people with
their group, the person they were linked to, a pending match's person and the similarity, without text or
vectors. Label a few hundred of them blind and join on `segmentId`; the procedure and the pass marks are
in [docs/specs/people.md](docs/specs/people.md#how-we-measure-it). How well the model tells two other
people apart in Ukrainian and Russian is unmeasured until you run it, so the switch starts off.

No voiceprint, group centroid or fingerprint appears in an API answer, export, webhook, MCP answer or
log; `pg_dump` holds them.

## Review

One list for everything waiting on your answer, so the app needs one screen. `GET /api/v1/review`
merges four queues, newest first:

- **`name`:** a pending [name suggestion](#people), shown with the line that carries the name; it may carry a [role](#roles).
- **`voice`:** a pending [voice match](#voice-grouping) ("Is this Olena?"), shown with its first three
  lines and its similarity, with no clip. None while voice matching is off.
- **`label`:** a segment of the last 14 days that Nytka scored within 0.05 of `Nytka__Voice__UserThreshold`
  and you have not marked, at most 20; the proposal is Nytka's verdict (`isUser`) and the similarity.
- **`tag`:** a pending [proposed tag](#tags) for a conversation or a person, shown with the summary of the conversation it came from; for a person, `proposal.personId` is set (`GET /api/v1/tags/suggestions` adds `personName`).

An item is `{ kind, id, conversationId, conversationTitle, at, text, proposal: { name, personId,
confidence, similarity, isUser, tag, role, named } }`; fields that do not belong to the kind are `null`. `id` is a
segment id for a label and a guid for the others. `limit` is 1 to 200, default 50.

`POST /api/v1/review/{kind}/{id}/accept` and `/reject` answer an item. A `name` or `voice` item is
answered as its own routes do (`200` with the person on accept, `204` on reject; `409` when a
suggestion is no longer pending), and a `tag` as `POST /api/v1/tags/suggestions/{id}/accept` does (`200` with the
item's tags; `409` when it is no longer pending or the item has 20 tags), so nothing changes a label or a tag until you accept. For a `label`, accept
stores Nytka's verdict as your mark (`PATCH /api/v1/segments/{id}` with `isUser`) and reject stores the
opposite, both `204`; either way the segment leaves the list, and a mark of yours may teach your
voiceprint as that route does. `404` for an unknown kind or item. The list needs a read token, the
answers an admin one; no answer carries a vector.

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
`llm.outputLanguage`, and asks for tasks only the wearer has to do, and for up to three [proposed tags](#tags). Each task may name the person it
is owed to, taken from the people you named in that conversation (see [Tasks](#tasks)). A transcript longer than
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
`Nytka__Tags__Suggest` on, so do up to 100 of your [tag names](#tags), with each summary; turn it off to keep them home. Point
`Nytka__Llm__BaseUrl` at a local server, such as Ollama, to keep it on your network. Logs and errors
never hold transcript text or a response body.

## Tasks

A task is something the wearer has to do, taken from a summary. Its text carries its deadline: there
are no due dates and no hand-made tasks. Tick, reopen, edit or delete a task in the Tasks tab, or with
`PATCH` and `DELETE` on `/api/v1/tasks/{id}`. A later summary never adds a deleted task again with the same wording, and
never removes or rewrites one you touched. `GET /api/v1/tasks` lists open or done tasks, newest
first, with the title and day of their conversation.

A task can name the person it is owed to or who asked for it (`personId`, `personName`). A summary
sets it when it is created, only to a person whose voice you named in that conversation (the wearer
never counts), matching the name ignoring case; any other name leaves it empty. A later summary never
changes it. Set, change or clear it with `PATCH` and `personId` (`null` clears it; an unknown person
is `404`); that counts as touching the task. Deleting the person clears it. What other people owe
the wearer is not a task.

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
  Conversations summarized before memory extraction shipped (release 0.4.0) have no memories;
  regenerate a summary to feed one in.

## Person page

`GET /api/v1/people/{id}` is one place for what Nytka holds about a person: `name`, your `note`, when
they were last heard (`lastSeenAt`, the newest segment the [label rule](#transcription-endpoints) gives
them; your own lines never count, and it is null when they were never heard), their `voices`, the newest
10 conversations they spoke in (`{ id, title, startedAt }`), their newest 50 [facts](#facts-about-people)
and the open tasks owed to them (newest 100). `hasVoiceprint` and `voiceprintSamples` say whether a
[voiceprint](#voice-grouping) exists and how many segments it was made from; the voiceprint itself
never leaves the database. `404` for an unknown person. The `list_people` and `get_person` [MCP tools](#mcp)
read the same page, without the two voiceprint fields. `named` is `false` for a person known so far only by a [role](#roles).

## Facts about people

A fact is a short, lasting thing about one person you named: where they live, their work, family,
habits, preferences, commitments. After each stored summary the server asks the model for up to ten
facts of up to 300 characters, in `llm.outputLanguage`, when the conversation is not brief and has
someone to write about: a speaker you confirmed as a person (by naming their voice or marking a line,
see [Speaker labels](#transcription-endpoints)), or a line that names a person you know, as a whole word
in any case. It sends the title, the transcript with a segment id on each line, those people with their
30 newest facts, and `Nytka__Memories__UserName`. Your own lines (`Wearer`) are never a person's; facts
about you stay memories.

- **Each fact names its evidence line and a basis, both set by the server.** `said`: the person's own line.
  `about`: your line or another confirmed person's. `mentioned`: a line of a speaker nobody confirmed that
  names the person. A fact whose evidence line is none of these is dropped, whatever the model said.
- A fact the person already holds is not added again with the same wording, even one you deleted.
  Extraction never rewrites a fact you edited or added. Deleting a conversation deletes the facts taken
  from it; deleting a person deletes theirs; merging two people moves them, and the target's row wins a
  duplicate.
- List, add, edit and delete facts with `/api/v1/people/{id}/facts`. A fact you add has `source: user`
  and no basis; adding text you deleted earlier brings it back, and text a live fact holds is a `409`.
- The same answer may propose up to 3 [tags](#tags) for each listed person; they wait in the [review inbox](#review) and tag
  nobody until you accept them. `Nytka__Tags__Suggest=false` stops them.
- `Nytka__People__Facts=false`, or no model, means no extraction. The facts you have stay.
- A failed extraction is tried three times, then again an hour later, three rounds at most.
- Facts are in [search](#search) but not in Ask or the daily digest, and no webhook payload carries a transcript.

## Import from Omi

Omi's "Export All Data" file (`omi-export.json`, from `GET /v1/users/export`) goes into Nytka once, so
your conversations, memories and tasks from before Nytka sit next to the new ones. The file has no
audio.

```bash
curl -sS -X POST "$NYTKA_URL/api/v1/import/omi" \
  -H "Authorization: Bearer $NYTKA_ADMIN_TOKEN" \
  -H "Content-Type: application/json" \
  --data-binary @omi-export.json
```

The answer counts what happened:
`{ conversations: { imported, alreadyImported, overlapping, discarded, empty }, segments, tasks: { imported, skipped }, memories: { imported, skipped } }`.

- Each kept Omi conversation becomes a closed conversation with `source: "omi"`, at its Omi times,
  with Omi's title and summary and its transcript. Discarded conversations and ones with no text
  (`empty`) are left out. Imported conversations are searchable, never merge with the ones the pendant
  captures, and trigger no webhook, memory extraction or summary; `POST
  /api/v1/conversations/{id}/enrich` still writes a new summary when you ask.
- Posting the same file again creates nothing: conversations already imported count as
  `alreadyImported`, and a memory or task whose text is already there is `skipped`.
- An Omi conversation whose time overlaps one Nytka captured (the pendant may have sent to both) is
  not imported and counts as `overlapping`. Add `?overlapping=import` to import those as well.
- Memories go in with `source: "omi"`, linked to their conversation when it came in, cut at 300
  characters; dismissed ones are left out. Tasks join the conversation they came from.
- The file is read whole and applied in one transaction, so a bad file changes nothing: `400` when it
  is not an Omi export or a time has no offset, `413` above 100 MB. Needs an admin token. Nothing from
  the file reaches a log or an error.

## Export

`GET /api/v1/export` (admin token) streams everything Nytka holds for you as NDJSON, one JSON object
per line, so a long history never sits in memory on either side:

```bash
curl -sS -H "Authorization: Bearer $NYTKA_ADMIN_TOKEN" "$NYTKA_URL/api/v1/export" -o nytka-export.ndjson
```

Every line has a `type` first. The order is `header` (`format: "nytka-export"`, `version`, `generatedAt`,
`serverVersion`), `setting` (`key`, `value`), `person` (`id`, `name`, `note`, `voiceprint` as true or false, `voices`, `tags`, `named`, `createdAt`),
`conversation` (`id`, `source`, `externalId`, `startedAt`, `endedAt`, `status`, `title`, `titleEdited`,
`summary`, `tags`, and `segments`: `{ startedAt, endedAt, text, speaker, speakerId, isUser, person }`), `task`
(`id`, `conversationId`, `personId`, `text`, `done`, `doneAt`, `createdAt`, `updatedAt`), `memory` (`id`, `text`,
`source`, `conversationId`, `createdAt`, `updatedAt`), `person_fact` (`id`, `personId`, `text`, `source`,
`basis`, `conversationId`, `edited`, `createdAt`, `updatedAt`), `bookmark` (`id`, `at`, `note`, `source`,
`createdAt`), `digest` (`id`, `localDate`, `headline`, `overview`, `highlights`, `decisions`,
`openQuestions`, `createdAt`) and `end` (`counts` per type). A file without its `end` line was cut
short. Times are UTC, ids are stable between exports.

- Deleted tasks, memories and person facts are not in it. Voice groups, voiceprints (only `voiceprint:
  true|false` is), name suggestions, voice matches, calendar events, briefs and proposed tags are not either; `tags` holds only the tags you set or accepted. API keys, tokens, webhooks and their secrets, and every
  URL setting are left out; the `setting` lines are the non-secret settings in effect.
- No audio: fetch `GET /api/v1/conversations/{id}/audio` for the conversations you want.
- Nytka cannot import its own export yet. Every field is specified in
  [docs/specs/export.md](docs/specs/export.md).

## Bookmarks

A bookmark marks a moment: a single tap on the pendant, or a tap in the app, sends `POST
/api/v1/bookmarks` with the time of the tap. The client makes the `id`, so an upload it retries changes
nothing. A bookmark holds no conversation: a conversation lists the bookmarks made from 30 seconds
before it starts to 30 seconds after it ends, so bookmarks follow merges, and one outside every
conversation stays in `GET /api/v1/bookmarks` alone (`conversationId` is then null). Deleting a
conversation keeps its bookmarks. The `bookmark.created` webhook and the `list_bookmarks` MCP tool
carry the same fields.

## Daily digest

Once a day the server writes a short account of your day: a headline, an overview of two to four
sentences, highlights (each tied to the conversation it comes from), decisions and open questions.
It is off by default. Set `Nytka__Digest__Enabled=true` and it makes the digest of each local day
after `Nytka__Digest__Hour` (21 by default), in `Nytka__User__TimeZone` and in `llm.outputLanguage`. It
needs the language model.

- **What goes to the model.** One request with that day's summarized conversations (title, local time,
  summary), the tasks and the memories created that day, and no transcript. A day with no summarized
  conversation makes no digest and no request. A busy day sends its newest 80 conversations and says
  so in the prompt. A conversation summarized after the digest was made is not added; the digest of
  a date is made once. The server also makes yesterday's digest if it is missing (after an outage or
  failed runs), and never one for a day without a summarized conversation.
- **Read it.** `GET /api/v1/digests` lists them, newest date first, and `GET /api/v1/digests/{id}`
  returns one. The `digest.ready` webhook carries `{ id, localDate, headline, overview }`, enough for an
  ntfy or n8n message, and the `list_digests` MCP tool returns the list. A highlight whose conversation
  you deleted later keeps its id; treat it as plain text.
- **Make or redo one.** `POST /api/v1/digests/run?date=2026-09-29` (admin) queues a run for that local
  date, today or earlier, that replaces the date's digest with a new one (and sends `digest.ready`
  again). It answers `202 { localDate }`, `400` for a missing, malformed or future date and `409`
  without a model. It works while `Nytka__Digest__Enabled` is `false`.
- **When it fails.** A run makes three attempts; after the third it is tried again an hour later while
  the date is today or yesterday. Logs and errors say only what failed, never the digest or its input.

## Calendar briefs

Before a meeting with someone you named, the server writes a short brief: who they are to you, what to
remember and what is still open. It needs the language model and a calendar feed: set
`Nytka__Calendar__IcsUrl` to the secret ICS address your calendar offers (read-only; Google, Outlook,
Nextcloud and most others have one). Nothing is fetched while it is empty.

- **The feed.** Every 15 minutes the server fetches it (`http` or `https`, no redirect, a 3xx fails the
  fetch, 10 seconds for the whole read, at most 5 MB) and stores the occurrences that run after now and start
  within 48 hours, recurring events expanded in their own time zone (an event with no zone is read in
  `Nytka__User__TimeZone`). All-day and cancelled events are left out. The address may be a private one, as for
  webhooks: only you set it. An event keeps its title and its attendees' display names (`CN`), not their
  addresses or anything else. A fetch that fails keeps what is stored, and the log says only that it failed and why in
  a fixed sentence (such as "HTTP 302"), never the address or the feed. An event goes a day after it ended.
- **Who counts.** An attendee is a person you named when the display name equals the person's name, ignoring
  case. Only events with such a person get a brief, once the event starts within `Nytka__Calendar__BriefMinutes`
  (30 by default).
- **What goes to the model.** One request per brief with the event's title and start, and for each matched
  person their name, your note, up to 20 facts, up to 10 open tasks owed to them and the title and summary of
  their last 5 conversations, never a transcript. Other attendees' names are not sent. The calendar address is
  not sent. A failed run is tried three times and again ten minutes later until the meeting starts.
- **Read it.** `GET /api/v1/briefs/upcoming?minutes=` lists the events not over yet that start within
  `minutes` (1 to 1440, 60 by default, clamped), each with its attendees and the matched person's id and its
  `brief` (`{ id, text, createdAt }`), or null before it is made or when nobody matches. The `brief.ready` webhook
  carries `{ id, title, startsAt, people: [{ id, name }], text }`. A brief goes with its event, and with a
  person you delete.

## Search

`GET /api/v1/search?q=` and the `search` MCP tool search transcripts, titles, summaries, memories, and
people by name or by [fact](#facts-about-people), in Ukrainian and English together.

- Every word of `q` (the first eight, made of letters and digits) must match, exactly or as a prefix,
  in the same place: one transcript segment, the title and summary together, or one memory. `зустріч`
  finds "зустрічами". English words are stemmed, so `running` finds "run", and common ones such as
  "the" are ignored.
- A hit is one conversation, one memory or one person, best first: a title or a person's name counts
  more than a summary, a memory or a fact, and those count more than the transcript. A person's hit
  has `kind: person`, their id, the name as `title`, and a `snippet` of the name or of the best
  matching fact. Deleted items and deleted facts never show. The `snippet` is HTML-escaped,
  and the matches sit in `<mark>` tags, the only tag it holds.
- `tag` (a [tag](#tags) name, normalized as there) keeps only conversations and people that have it and leaves memories out, so
  `kinds=memory` with a `tag` finds nothing. The words of `q` are never matched against tag names: searching by tag is
  the filter, and `q` stays required. An invalid tag is a `400`.
- `kinds` picks any of `conversation`, `memory` and `person` (all three by default). `limit` defaults to 20 and caps at 50; `offset` runs
  from 0 to 500 (a larger one counts as 500), and `nextOffset` is null on the last page and when the
  next page would start past 500. No word, or an unknown kind, is a `400`.
- New text, a new or renamed person and a new or edited fact are searchable a few seconds after they are
  written. After the upgrade that adds search or people in search, or when
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

## Ask

`POST /api/v1/ask { "question": "..." }` and the `ask` MCP tool answer a question from your
conversations and memories, with numbered sources. A `read` token may call it: it changes nothing. It
needs the [language model](#the-language-model) (`503` without one; `504` when the model does not
answer within `llm.timeoutSeconds`; `502` when it fails otherwise).

```json
{ "answer": "You planned to leave on Friday [1].",
  "sources": [{ "n": 1, "kind": "conversation", "id": "...", "conversationId": "...",
                "title": "Weekend trip", "at": "2026-09-29T08:00:00Z", "snippet": "..." }] }
```

The server makes two model calls. The first turns the question into up to three short keyword
queries and, when the question names a period, a range of local days (in `user.timeZone`). The queries
run through the search (any word of a query may match), limited to that range; a question about a
period only takes the conversations of those days, newest first. At most 8 conversations and 10
memories go to the second call, best first. A conversation brings its title, summary, tasks and the
lines of its transcript around the best-matching one, cut so that everything fits
`llm.maxInputChars`. The model answers with `[n]` markers; the server drops a marker that names no
source and returns only the sources it cites, renumbered from 1 in order of first mention. A source
is a `conversation` (`conversationId` is its own id) or a `memory` (`conversationId` is the
conversation it came from, or null); `at` is the conversation's start or the memory's last change.

The question, the sources sent to the model and the answer are not stored and not logged; failures
carry fixed sentences. Both calls go to the model you configured, so what your history holds leaves
the server the way summaries do.

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
| `conversation.ready` | A summary was stored, the first one and every re-run | `{ id, startedAt, endedAt, title, summary, tasks: [{ id, text }], tags }` |
| `task.created` | A summary produced a new task | the task, as `GET /api/v1/tasks` shows it, without `personId` and `personName` |
| `task.completed` | A task was completed | the task |
| `memory.created` | A memory was added, by extraction or by hand | `{ id, text, conversationId }` |
| `bookmark.created` | A bookmark was added | `{ id, at, note, source }` |
| `person.fact.created` | A fact about a person was added, by extraction or by hand | `{ id, personId, personName, text, basis, conversationId }`; `basis` and `conversationId` are null for a fact you added |
| `brief.ready` | A [calendar brief](#calendar-briefs) was made for a meeting | `{ id, title, startsAt, people: [{ id, name }], text }` |
| `digest.ready` | A daily digest was made, by the schedule or on demand | `{ id, localDate, headline, overview }` |
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
| `list_conversations` | `since?`, `before?` (ISO 8601 with an offset, or a date), `tag?`, `limit?` (1 to 50, default 20) | `{ items: [{ id, startedAt, endedAt, title, summary, preview, tags }], nextBefore }` |
| `get_conversation` | `id` (UUID), `transcript?` (default true), `part?` (from 1, default 1) | `{ id, startedAt, endedAt, title, summary, tasks: [{ id, text, done, personId, personName }], transcript, truncated, part, parts, tags }` |
| `list_tasks` | `status?` (`open` or `done`), `conversationId?`, `before?` (a task id), `limit?` (1 to 200, default 50) | `{ items: [Task], nextBefore }` |
| `list_memories` | `before?` (a memory id), `limit?` (1 to 200, default 50) | `{ items: [Memory], nextBefore }` |
| `list_bookmarks` | `before?` (ISO 8601 with an offset, or a date), `beforeId?` (UUID), `limit?` (1 to 100, default 30) | `{ items: [{ id, at, note, source, conversationId }], nextBefore, nextBeforeId }` |
| `list_digests` | `before?` (a date, `yyyy-MM-dd`), `limit?` (1 to 100, default 30) | `{ items: [{ id, localDate, headline, overview, highlights: [{ text, conversationId }], decisions, openQuestions, createdAt }], nextBefore }` |
| `search` | `query`, `kinds?` (a list of `conversation`, `memory` and `person`), `tag?`, `limit?` (1 to 30, default 10) | `{ items: [Hit] }` |
| `list_people` | `tag?` | `{ items: [{ id, name, lastSeenAt, facts, tags, named }] }`, most recently heard first, then by name; `facts` counts live facts; `named` is false for a person known only by a [role](#roles) |
| `get_person` | `id` (UUID) or `name` (any case), one of the two | the [person page](#person-page) as `GET /api/v1/people/{id}` returns it, without `hasVoiceprint` and `voiceprintSamples`; a tool error ("No such person.") for an unknown one |
| `list_tags` | `query?` (names starting with it) | `{ items: [{ name, conversations, people, uses }] }`, most used first, as `GET /api/v1/tags` (see [Tags](#tags)) |
| `ask` | `question` (1 to 500 characters) | `{ answer, sources: [Source] }`, as `POST /api/v1/ask` (see [Ask](#ask)); a tool error when no model is set or it fails |

Every tool is read-only (`readOnlyHint`), declares an output schema and returns its result as
structured content and as JSON text. A tool returns the fields its REST endpoint returns, with the
limits and defaults in the table (`search` has no `offset`). Pass `nextBefore` as `before` to read the
next page. An unknown id is a tool error ("No such conversation."); a malformed id, time, status or
kind or tag is JSON-RPC error `-32602`. The transcript has one line per segment, in UTC and without a
speaker label when there is none: `[HH:mm:ss] Speaker: text`. It is cut at a line boundary after
60,000 characters into parts. `part` picks one (from 1; one outside `1` to `parts` is `-32602`),
`parts` counts them and `truncated` is true while a later part exists, so a client reads the rest with
`part: 2`, `3` and so on. Only the first 20 parts are read. `transcript` is null when it is false.

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

## Running the no-loss wear test

The milestone "Nothing is lost" asks for a week of wear with the official app uninstalled and no
lost audio. `GET /api/v1/coverage` answers how much of the audio the phone counted reached this
server, and lists the stretches that did not. The design and its limits are in
[docs/specs/coverage.md](docs/specs/coverage.md).

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

## API

Every request under `/api` carries `Authorization: Bearer <token>`; the Scope column says what it
needs. Errors are RFC 9457 problem details. A `400` about a field of a body adds `errors`, an object
from field to a list of messages; a search without a word, a bad chunk and a bad diagnostics body
carry a title only. Objects always carry every field, null ones too. The API is additive,
so `apiVersion` stays `1` and an app from an older version keeps working.

| Method | Path | Scope | Result |
|---|---|---|---|
| GET | `/healthz` | none | 200 when the database answers |
| GET | `/api/v1/info` | read | `{ serverVersion, apiVersion, scope, features }`; `scope` is the caller's; `features` holds `offline-sync`, `people`, `review`, `briefs`, `tags`, `tag-suggestions` and `roles` (name suggestions carry roles and people have `named`), `voice` when the speaker model is there, and `voice-groups` when [voice grouping](#voice-grouping) is on and works; `briefs` is listed whether or not a calendar feed is set, because the app reads `calendar.icsUrl` from `GET /api/v1/settings` |
| GET | `/api/v1/status` | admin | `{ pendingChunks, oldestPendingAt, lastError, lastErrorAt, lastSuccessAt, ai: { configured, pending, lastError, lastErrorAt } }`; a `lastError` is set only while it is current |
| POST | `/api/v1/chunks` | admin | Stores one chunk of Opus frames (`application/vnd.nytka.frames.v1`) |
| POST | `/api/v1/diagnostics` | admin | Stores 1 to 500 diagnostics samples (JSON array, at most 256 KiB); answers `{ accepted }` |
| GET | `/api/v1/diagnostics?since=&limit=` | admin | Samples oldest first: `{ items, nextSince }`; `limit` defaults to 500, caps at 5000 |
| GET | `/api/v1/coverage?from=&to=&bucket=&limit=` | admin | The "Nothing is lost" report: `{ from, to, timeZone, bucket, totals, buckets, gaps, gapsTotal, now, warnings }`; see [Running the no-loss wear test](#running-the-no-loss-wear-test); `bucket` is `day` (default) or `hour`, `to` defaults to now, `from` to six days before today; `limit` defaults to 500, caps at 5000; `400` for a range over 62 days (14 for hours) |
| GET | `/api/v1/conversations?before=&since=&tag=&limit=` | read | `{ items, nextBefore }`, newest first; an item is `{ id, startedAt, endedAt, status, preview, title, summary, aiStatus, bookmarks, source, tags }`, `bookmarks` being a count, `source` `nytka` or `omi`, `tags` sorted names; `since` keeps conversations that started at or after it; `tag` keeps those with that [tag](#tags) (`400` for a name that is none); `limit` defaults to 30, caps at 100 |
| GET | `/api/v1/conversations/{id}` | read | The item without `preview`, plus `titleEdited`, `aiMessage`, `aiUpdatedAt`, `tags`, `tasks`, `segments` (`{ id, startedAt, endedAt, text, speaker, speakerId, isUser, personId, personName, isUserSource }`, `isUserSource` being `manual`, `voice`, `provider` or null; see [Your voice](#your-voice)) and `bookmarks` (`{ id, at, note }`) |
| POST | `/api/v1/import/omi?overlapping=` | admin | Body: an Omi export file; `200` with the counts of [Import from Omi](#import-from-omi); `400` for a body that is no export; `413` above 100 MB |
| PATCH | `/api/v1/conversations/{id}` | admin | Body `{ title }`, 1 to 120 characters, or `null` for the generated title |
| POST | `/api/v1/conversations/{id}/enrich` | admin | Queues a summary run: `202 { aiStatus: "pending" }`; `409` while the conversation is open or no model is set |
| DELETE | `/api/v1/conversations/{id}` | admin | Deletes it with its transcript, audio, tasks and memories |
| GET | `/api/v1/conversations/{id}/transcriptions` | admin | Raw transcription responses |
| GET | `/api/v1/conversations/{id}/audio` | read | The conversation's speech as `audio/ogg` (Opus, packed without re-encoding); pauses are not stored, so they are not played; range requests work; `404` when no speech audio is stored |
| GET | `/api/v1/conversations/{id}/audio/index` | read | `{ durationMs, runs: [{ offsetMs, startedAt, endedAt }] }`: each stretch of continuous capture and where it starts in the stream; `404` as above |
| GET | `/api/v1/tasks?status=&conversationId=&before=&limit=` | read | `{ items, nextBefore }`, newest first; a task is `{ id, conversationId, conversationTitle, conversationStartedAt, text, done, doneAt, createdAt, personId, personName }`; `status` is `open` (default) or `done`; `limit` defaults to 50, caps at 200 |
| PATCH, DELETE | `/api/v1/tasks/{id}` | admin | PATCH body `{ text?, done?, personId? }`, `text` 1 to 200 characters, `personId` a person id or `null` (`404` for an unknown person); DELETE answers `204` |
| GET, PATCH | `/api/v1/settings` | admin | GET: `{ items: [{ key, type, value, isSet, source, locked, default }] }`. PATCH body `{ values: { "<key>": value or null } }`, all or nothing, `null` restores the default; `400` for an unknown key or a bad value, `409` for a locked key or any API key |
| POST | `/api/v1/tokens` | admin | Body `{ name, scope }`; `201` with the token's fields and `token`, shown once; `409` for a name in use |
| GET | `/api/v1/tokens` | admin | `{ items }`, newest first, revoked ones included: `{ id, name, scope, hint, createdAt, lastUsedAt, revokedAt }` |
| DELETE | `/api/v1/tokens/{id}` | admin | Revokes it: `204` |
| GET | `/api/v1/memories?before=&limit=` | read | `{ items, nextBefore }`, newest first; a memory is `{ id, text, source, conversationId, conversationTitle, conversationStartedAt, createdAt, updatedAt }`, `source` is `ai`, `user` or `omi`; `limit` defaults to 50, caps at 200 |
| POST | `/api/v1/memories` | admin | Body `{ text }`, 1 to 300 characters; `201` with the memory; `409` when a live memory holds the fact |
| PATCH, DELETE | `/api/v1/memories/{id}` | admin | PATCH body `{ text }`; DELETE answers `204` |
| GET | `/api/v1/bookmarks?before=&limit=` | read | `{ items, nextBefore }`, newest first; a bookmark is `{ id, at, note, source, conversationId }`, `source` is `pendant` or `app`; `before` is a time and `beforeId` the id of the last item of the previous page (`nextBefore`, `nextBeforeId`), so equal times are not skipped; `limit` defaults to 30, caps at 100 |
| POST | `/api/v1/bookmarks` | admin | Body `{ id, at, note?, source }`, `id` a UUID the client makes, `at` with an explicit offset, `note` up to 200 characters; `201` with the bookmark, or `200` with the stored one when the id exists |
| PATCH, DELETE | `/api/v1/bookmarks/{id}` | admin | PATCH body `{ note }`, `null` clears it; DELETE answers `204` |
| GET | `/api/v1/digests?before=&limit=` | read | `{ items, nextBefore }`, newest date first; a digest is `{ id, localDate, headline, overview, highlights: [{ text, conversationId }], decisions, openQuestions, createdAt }`; `before` is a date (`yyyy-MM-dd`) and keeps earlier ones, `nextBefore` is the last date of the page, set only when an earlier digest exists; `limit` defaults to 30, caps at 100 |
| GET | `/api/v1/digests/{id}` | read | One digest, as in the list |
| POST | `/api/v1/digests/run?date=` | admin | Queues a run for that local date that replaces its digest; `202 { localDate }`, `400` for a missing, malformed or future date, `409` without a model |
| GET | `/api/v1/export` | admin | Streams everything you own as NDJSON (`application/x-ndjson`); see [Export](#export) |
| GET | `/api/v1/search?q=&kinds=&tag=&limit=&offset=` | read | `{ items, nextOffset }`; a hit is `{ kind, id, score, title, snippet, at, conversationId }`; `kinds` is `conversation`, `memory` and `person` (a person's `id` is the person's, `title` the name) |
| POST | `/api/v1/webhooks` | admin | Body `{ url, events, description? }`, `description` up to 200 characters; `201` with the webhook and `secret`, shown once; `409` at 20 webhooks |
| GET | `/api/v1/webhooks` | admin | `{ items }`: `{ id, url, events, description, active, createdAt, lastDelivery }`, `lastDelivery` is `{ status, at }` or null |
| PATCH, DELETE | `/api/v1/webhooks/{id}` | admin | PATCH body with any of `url`, `events`, `description`, `active`; DELETE answers `204` and drops its deliveries |
| POST | `/api/v1/webhooks/{id}/test` | admin | Queues a `ping`: `202 { deliveryId }` |
| GET | `/api/v1/webhooks/{id}/deliveries?limit=` | admin | `{ items }`, newest first: `{ id, eventType, status, attempts, lastStatusCode, lastError, createdAt, deliveredAt }`; `limit` defaults to 30, caps at 100 |
| GET | `/api/v1/voice` | admin | `{ enrolled, enrolledAt, updatedAt, enrolledSamples, learnedSegments, modelAvailable }`; never the voiceprint |
| POST | `/api/v1/voice/enrollment?mode=` | admin | Body: one or more chunks of Opus frames back to back (`application/vnd.nytka.frames.v1`) or a 16 kHz mono 16-bit WAV (`audio/wav`), at most 120 s; `mode` is `replace` (default) or `add`. `200 { speechSeconds, samples, minAgreement }`; `422` with `reason` `too-little-speech`, `too-few-samples` or `samples-disagree` and the same three numbers; `413` over 120 s; `415` another content type; `400` unreadable audio; `409` `add` to a voiceprint of another model; `503` without the speaker model |
| POST | `/api/v1/voice/reset` | admin | Back to the enrolled voiceprint, forgetting what it learned; `200` as GET, `404` with no voice enrolled |
| DELETE | `/api/v1/voice` | admin | Forgets your voice: voiceprint, fingerprints, similarities and verdicts, and every voice group; your marks stay. `204`, also with nothing enrolled |
| GET | `/api/v1/voice/segments?since=&until=&limit=` | admin | `{ items, nextSince }`, oldest first, for choosing a threshold: `{ segmentId, conversationId, startedAt, endedAt, similarity, voiceIsUser, providerIsUser, manualIsUser }`, no text; `since` keeps segments that started after it; `limit` defaults to 500, caps at 5000 |
| PATCH | `/api/v1/segments/{id}` | admin | Body `{ isUser?, personId? }`, at least one: `isUser` is `true` ("this is me"), `false` or `null` (clears the mark); `personId` is a person, or `null` to clear the segment's own person, see [Speaker labels](#transcription-endpoints); `200` with the segment as a conversation shows it; `404` for an unknown segment or person |
| GET | `/api/v1/people?tag=` | read | `{ items }` by name: `{ id, name, note, createdAt, voices, segments, lastSeenAt, factCount, tags, named }`; `tag` keeps people with that [tag](#tags) (`400` for a name that is none); `lastSeenAt` is the newest segment of the person, as on the [person page](#person-page), null when never heard; `factCount` counts their live [facts](#facts-about-people) |
| PATCH | `/api/v1/people/{id}` | admin | Body `{ name?, note? }`, at least one: `name` 1 to 80 characters (and sets `named` to true), `note` up to 500, `null` clears it; `200` with the person, `409` for a name another person has |
| GET | `/api/v1/people/{id}` | read | The [person page](#person-page): `{ id, name, note, createdAt, lastSeenAt, voices, hasVoiceprint, voiceprintSamples, conversations: [{ id, title, startedAt }], facts: [Fact], openTasks: [Task], tags, named }`; `404` for an unknown person |
| GET | `/api/v1/people/suggestions?status=` | read | `{ items }`, newest first, at most 200; `status` is `pending` (default), `accepted` or `rejected`; an item is `{ id, conversationId, target, speakerId, groupId, name, role, named, personId, confidence, evidence: { segmentId, startedAt, text } }`, `target` being `speaker`, `label`, `group` (`groupId` is the voice group) or `person` (the voice of a person known only by a [role](#roles); `personId` is that person); `role` is a tag name or null, and `named` false means the role alone, `name` being its display form; see [People](#people) |
| POST | `/api/v1/people/suggestions/{id}/accept` | admin | Names the voice, the batch's segments or the voice group (as its card is named), creates the person known by a [role](#roles), or renames or merges the person of a `person` target; `200` with the person; `404` for an unknown suggestion; `409` when it is no longer pending |
| POST | `/api/v1/people/suggestions/revalidate` | admin | Deletes pending name suggestions the model made that fail the [name checks](#people); `200` with `{ checked, removed, kept }` |
| POST | `/api/v1/people/suggestions/{id}/reject` | admin | `204`; the name is never suggested again for that voice; `404` and `409` as accept |
| POST | `/api/v1/people/backfill?limit=&force=` | admin | Queues [name suggestions and facts](#backfill) for summarized conversations they have not read (`force=true`: also names runs made under older checks); `limit` 1 to 1000 (default 200), `400` outside it; `200` with `{ queued: { suggestNames, facts }, skipped, remaining }`; `409` without a language model |
| GET | `/api/v1/people/voice-eval?since=&until=&limit=` | admin | `{ items, nextSince }`, oldest first, for [voice grouping](#voice-grouping): `{ segmentId, conversationId, durationMs, groupId, personId, matchPersonId, similarity }`, no text and no vector; `limit` defaults to 500, caps at 5000 |
| GET | `/api/v1/people/cards` | admin | `{ items }`, at most 4 and at most 2 per conversation, newest first, empty while voice matching is off: `{ kind, id, conversationId, conversationTitle, personId, personName, similarity, clip: { from, until }, lines: [{ segmentId, startedAt, text }] }`; `kind` is `group` ("Who is this?", no person or similarity) or `match` ("Is this Olena?"); see [Voice grouping](#voice-grouping) |
| GET | `/api/v1/people/cards/{kind}/{id}/clip` | admin | The card's clip, `audio/ogg`, at most 10 s; `404` for an unknown card or when its audio is gone |
| POST | `/api/v1/people/cards/{kind}/{id}` | admin | Body `{ personId }`, `{ name }`, `{ skip: true }` or `{ reject: true }`, exactly one; naming or confirming answers `200` with the person, skipping (7 days) or rejecting `204`; `404` for an unknown card or person; `400` for a match named with someone else |
| GET | `/api/v1/review?limit=` | read | `{ items }`, newest first, `limit` 1 to 200 (default 50): `{ kind, id, conversationId, conversationTitle, at, text, proposal: { name, personId, confidence, similarity, isUser, tag, role, named } }`; `kind` is `name`, `voice` (none while voice matching is off), `label` or `tag`; see [Review](#review) |
| POST | `/api/v1/review/{kind}/{id}/accept` | admin | `name` and `voice`: `200` with the person; `label`: stores Nytka's verdict as your mark, `204`; `tag`: `200 { tags }`, the conversation's; `404` for an unknown kind or item; `409` for a name or tag that is no longer pending, or a tag when the conversation has 20 |
| POST | `/api/v1/review/{kind}/{id}/reject` | admin | `204`; a `label` stores the opposite of Nytka's verdict as your mark; `404` and `409` as accept |
| DELETE | `/api/v1/people/voiceprints` | admin | Deletes every voice group, every person voiceprint and every pending voice match; segment links stay. `204` |
| GET | `/api/v1/people/{id}/facts?before=&limit=` | read | `{ items, nextBefore }`, newest first; a fact is `{ id, personId, text, source, basis, conversationId, conversationTitle, segmentId, createdAt, updatedAt }`, `source` is `ai` or `user`, `basis` is `said`, `about`, `mentioned` or null; `limit` defaults to 50, caps at 200; `404` for an unknown person |
| POST | `/api/v1/people/{id}/facts` | admin | Body `{ text }`, 1 to 300 characters; `201` with the fact (`source: user`); `409` when a live fact of the person holds it; `404` for an unknown person |
| PATCH, DELETE | `/api/v1/people/{id}/facts/{factId}` | admin | PATCH body `{ text }`, `200` with the fact; DELETE answers `204`; `404` for an unknown fact |
| GET | `/api/v1/tags?q=` | read | `{ items: [{ name, conversations, people, uses }] }`, most used first, then by name; `q` keeps names that start with it; see [Tags](#tags) |
| PUT, DELETE | `/api/v1/conversations/{id}/tags/{name}` | admin | Adds or removes one tag: `200 { tags }`, the conversation's tags sorted; removing a tag it does not have is also `200`; `400` for a name that is none, `404` for an unknown conversation, `409` when adding a 21st tag |
| PUT, DELETE | `/api/v1/people/{id}/tags/{name}` | admin | The same for a person |
| POST | `/api/v1/tags/{name}/rename` | admin | Body `{ name }`; `200` with the tag; `404` for an unknown tag; `409` when the new name is another tag's (merge instead) |
| POST | `/api/v1/tags/{name}/merge` | admin | Body `{ into }`; moves every link to `into` (created when new), drops duplicates and the old tag; `200` with `into`; `404` for an unknown tag |
| GET | `/api/v1/tags/suggestions?status=` | read | `{ items: [{ id, conversationId, personId, personName, name, createdAt }] }`, newest first, at most 200; `status` is `pending` (default), `accepted` or `rejected`, else `400`; `personId` is null for a conversation's tag; see [Tags](#tags) |
| POST | `/api/v1/tags/suggestions/{id}/accept` | admin | Adds the proposed tag to its conversation, or to its person when `personId` is set: `200 { tags }`, the item's; `404` for an unknown proposal; `409` when it is no longer pending or the item has 20 tags |
| POST | `/api/v1/tags/suggestions/{id}/reject` | admin | Keeps the proposal as rejected, so the tag is not proposed again for that conversation or person: `204`; `404` and `409` as accept |
| DELETE | `/api/v1/tags/{name}` | admin | Removes the tag from every conversation and person: `204`; `404` for an unknown tag |
| GET | `/api/v1/briefs/upcoming?minutes=` | read | `{ items }`, soonest first: the [calendar events](#calendar-briefs) not over yet that start within `minutes` (1 to 1440, default 60, clamped): `{ uid, title, startsAt, endsAt, attendees: [{ name, personId }], brief: { id, text, createdAt } or null }`; `personId` is the person whose name equals the attendee's, else null |
| POST | `/api/v1/ask` | read | Body `{ question }`, 1 to 500 characters; `{ answer, sources }` (see [Ask](#ask)); `503` without a model, `504` on a model timeout, `502` on any other model failure |
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
- Daily digests stay until you delete their rows; they hold model-written text about your day, so a dump holds them too.
- Tokens are kept as a hash, and the settings the app saved as plain rows. An API key is never in the
  database. A webhook secret is, as plain text, because signing needs it.
- A person's name and your note on them, and the person you set on a segment, stay until you delete the person (the links go with them). A person known only by a [role](#roles) is marked `named: false` until a name is given.
- [Tags](#tags) (a name, and which conversations and people hold it) stay until you remove the last link, delete the tag, or delete what held it. They are words you chose and can be sensitive, so a dump holds them.
- Proposed tags (the name, the conversation it came from, the person for a person's tag, and your answer) stay until you delete that conversation or person; accepted and rejected ones too, which is how a rejected tag stays rejected. They hold model-chosen words about your day, so a dump holds them.
- Name suggestions (the name or role, the voice, the line that shows it and the model's confidence) stay until you delete the conversation they came from or the person they name; accepted and rejected ones too, which is how a rejected name stays rejected.
- Facts about a person stay until you delete them, their person or the conversation they were taken from. A deleted fact leaves a hidden row with its wording, so extraction does not add it again; it goes with the person.
- With a [calendar feed](#calendar-briefs) set, the events of the next 48 hours (uid, start, end, title and the attendees' display names, no address) stay until a day after they end, and so do the briefs the model wrote for them, which hold facts about the people; a brief goes with a person you delete. The feed's address is only in `.env`: never in the database, an API answer or a log.
- A webhook's delivery log keeps statuses only: no response body, and no payload once a delivery ends.
- Once you enroll [your voice](#your-voice): your voiceprint (192 numbers, plus the enrolled mean it
  resets to) until you delete it, and a fingerprint of each checked segment for as long as its speech
  audio stays (`RetentionDays`; with `0` not at all). The similarity and the verdict stay on the
  segment; they cannot be turned back into a voice. No API answer, webhook, MCP answer, export or log
  carries a voiceprint or a fingerprint; `pg_dump` does. Without an enrolled voice nothing is
  fingerprinted, and the enrollment's audio is never stored.
- With [voice grouping](#voice-grouping) on, the fingerprints of other people's segments are kept as long as
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
[gitleaks](https://github.com/gitleaks/gitleaks). See [CONTRIBUTING.md](CONTRIBUTING.md).

```bash
dotnet build
dotnet test
```

The search tests that need the Ukrainian dictionary skip themselves until you run
`scripts/fetch-uk-dictionary.sh --accept-licence`; CI runs it first and sets
`NYTKA_REQUIRE_DICTIONARY=1`, so a missing file fails the build. The same goes for the voice tests and
`scripts/fetch-speaker-model.sh`, which puts TitaNet-small in `src/Nytka.Audio/Models/` (run it before
`dotnet build`, which copies the model beside the binaries), and `NYTKA_REQUIRE_SPEAKER_MODEL=1`.

## License

Apache-2.0, see [LICENSE](LICENSE) and [NOTICE](NOTICE).

Nytka is an independent project, not affiliated with or endorsed by Based Hardware; "Omi" is a
trademark of its owner. Nytka records the people around the wearer: you are responsible for
following the recording and privacy laws where you use it.
