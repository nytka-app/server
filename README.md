# Nytka server

The self-hosted half of [Nytka](docs/vision.md), a companion app and server for the Omi AI
necklace. The Nytka Android app streams the pendant's audio here; the server drops silence,
sends speech to the transcription endpoint you choose, groups the transcript into conversations
and serves them back to the app. Omi's cloud sees none of it.

Status: v0.1, capture and transcription. What v0.1 does and does not do: [docs/specs/v0.1.md](docs/specs/v0.1.md).

## Run it

You need Docker Compose and an OpenAI-compatible transcription endpoint.

```bash
mkdir nytka && cd nytka
curl -fsSLO https://raw.githubusercontent.com/nytka-app/server/main/docker-compose.yml
curl -fsSL -o .env https://raw.githubusercontent.com/nytka-app/server/main/.env.example
# edit .env: POSTGRES_PASSWORD, Nytka__AdminToken (openssl rand -hex 24), Nytka__Stt__Url, ...
docker compose up -d
curl http://127.0.0.1:8080/healthz
```

The server listens on `127.0.0.1:8080`. The app needs to reach it: put a reverse proxy with HTTPS
in front, or set `NYTKA_BIND` to an address on your VPN (Tailscale, WireGuard) and turn on the
app's private-network switch, which allows plain HTTP.

## Configuration

| Variable | Required | Default | Meaning |
|---|---|---|---|
| `POSTGRES_PASSWORD` | yes | | Password of the bundled Postgres |
| `Nytka__AdminToken` | yes | | Token the app sends, 32 characters or more. The server refuses to start without it. |
| `Nytka__Stt__Url` | yes | | Full URL of the transcription endpoint |
| `Nytka__Stt__ApiKey` | no | | Sent as a bearer token |
| `Nytka__Stt__Model` | no | | Sent as `model` |
| `Nytka__Stt__Language` | no | detect | ISO 639-1 code sent as `language` |
| `Nytka__Conversations__Gap` | no | `00:02:00` | Silence that ends a conversation |
| `Nytka__Audio__RetentionDays` | no | `14` | Days to keep speech audio; `0` deletes it once transcribed |
| `NYTKA_BIND`, `NYTKA_PORT` | no | `127.0.0.1`, `8080` | Where Compose publishes the server |
| `NYTKA_VERSION` | no | `latest` | Image tag |

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

## API

Every request under `/api` carries `Authorization: Bearer <token>`. Errors are RFC 9457 problem
details.

| Method | Path | Result |
|---|---|---|
| GET | `/healthz` | 200 when the database answers (no token) |
| GET | `/api/v1/info` | `{ serverVersion, apiVersion }` |
| GET | `/api/v1/status` | `{ pendingChunks, oldestPendingAt, lastError }` |
| POST | `/api/v1/chunks` | Stores one chunk of Opus frames (`application/vnd.nytka.frames.v1`) |
| POST | `/api/v1/diagnostics` | Stores 1 to 500 diagnostics samples (JSON array, at most 256 KiB); answers `{ accepted }` |
| GET | `/api/v1/diagnostics?since=&limit=` | Samples oldest first: `{ items, nextSince }`; `limit` defaults to 500, caps at 5000 |
| GET | `/api/v1/conversations?before=&limit=` | Newest first, with a preview |
| GET | `/api/v1/conversations/{id}` | One conversation with its segments |
| DELETE | `/api/v1/conversations/{id}` | Deletes it with its transcript and audio |
| GET | `/api/v1/conversations/{id}/transcriptions` | Raw transcription responses |

Diagnostics are health samples from the app (link quality, queue and upload state) that the app
sends only while its developer switch is on: no audio, no transcripts, no token. Samples older than
30 days are deleted daily; there is nothing to configure. The chunk format and the upload answers are specified in [docs/specs/v0.1.md](docs/specs/v0.1.md).

## What it stores

Everything lives in Postgres, so `pg_dump` backs it up. Silence is dropped as soon as a chunk is
processed. Speech audio (Opus, about 14 MB per hour of speech) stays for `RetentionDays`. With
`RetentionDays=0` it goes as soon as its transcript exists; audio whose transcription failed stays,
so you can see what failed, until you delete its conversation. The server logs no audio, no
transcript text and no token.

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

## License

Apache-2.0, see [LICENSE](LICENSE) and [NOTICE](NOTICE).

Nytka is an independent project, not affiliated with or endorsed by Based Hardware; "Omi" is a
trademark of its owner. Nytka records the people around the wearer: you are responsible for
following the recording and privacy laws where you use it.
