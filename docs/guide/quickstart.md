---
title: "Quickstart"
description: "Run the Nytka server with Docker Compose, point it at a transcription endpoint and check that it answers."
order: 2
section: "Get started"
---

This page runs the server. The phone side is on [Install the Android app](android-app.md). The
steps follow the install section of the [README](../../README.md#install-in-15-minutes), which
puts the whole path at about 15 minutes once the prerequisites are in place.

## Before you start

- A machine with [Docker Compose](https://docs.docker.com/compose/install/) that runs while you
  wear the pendant. A laptop that sleeps loses nothing, because the app keeps the audio and
  uploads it later, but conversations then appear late.
- A transcription endpoint that supports `response_format=verbose_json`, and its key. The README
  uses [Groq](https://console.groq.com) with `whisper-large-v3-turbo`; OpenAI's `whisper-1` also
  works. Its limits and prices change, so read them at the provider.
- A way for the phone to reach the server over HTTPS, such as Tailscale or a reverse proxy.
- A pendant on consumer firmware 3.0.x and an Android 12+ phone.

## 1. Download the Compose file and the template

```bash title="Fetch the files"
mkdir nytka && cd nytka
curl -fsSLO https://raw.githubusercontent.com/nytka-app/server/main/docker-compose.yml
curl -fsSL -o .env https://raw.githubusercontent.com/nytka-app/server/main/.env.example
```

The Compose file runs `postgres:17-alpine` and `ghcr.io/nytka-app/server`
([`docker-compose.yml`](../../docker-compose.yml)). Pin a release with `NYTKA_VERSION`; it
defaults to `latest`.

## 2. Fill in `.env`

Three variables are required: `POSTGRES_PASSWORD`, `Nytka__AdminToken` and `Nytka__Stt__Url`
([`.env.example`](../../.env.example)). Generate the two secrets and set the Groq endpoint and
model:

```bash title="Write the required values"
sed -i.bak \
  -e "s|^POSTGRES_PASSWORD=.*|POSTGRES_PASSWORD=$(openssl rand -hex 24)|" \
  -e "s|^Nytka__AdminToken=.*|Nytka__AdminToken=$(openssl rand -hex 24)|" \
  -e "s|^Nytka__Stt__Url=.*|Nytka__Stt__Url=https://api.groq.com/openai/v1/audio/transcriptions|" \
  -e "s|^# Nytka__Stt__Model=.*|Nytka__Stt__Model=whisper-large-v3-turbo|" .env
```

Then open `.env` in an editor and set `Nytka__Stt__ApiKey` to your key. Do not paste the key into
a shared terminal log.

> [!IMPORTANT]
> Leave every other variable commented out. A variable that is set, even to its default, locks
> that setting and the app can no longer change it. An empty variable counts as unset
> ([configuration](configuration.md#how-settings-resolve)).

The admin token must be 32 characters or more, or the server refuses to start
([`Program.cs`](../../src/Nytka.Server/Program.cs)).

## 3. Start it and check

```bash title="Start the stack"
docker compose up -d --wait
curl http://127.0.0.1:8080/healthz
```

`healthz` answers `{"status":"healthy"}` when the database answers. If the server does not come
up, `docker compose logs server` names the reason.

The server listens on `127.0.0.1:8080` only (`NYTKA_BIND`, `NYTKA_PORT`). Give the phone an HTTPS
address next. With Tailscale installed and signed in on the server machine:

```bash title="Serve over HTTPS on the tailnet"
tailscale serve --bg --https=443 localhost:8080
tailscale serve status
```

`status` prints the `https://<machine>.<tailnet>.ts.net` address. A domain with a reverse proxy
that terminates HTTPS works too. On a VPN without HTTPS you can bind to the VPN address and turn
on the app's private-network switch, which sends audio unencrypted
([README](../../README.md#install-in-15-minutes)).

## 4. Read the status

Once the phone is paired, `GET /api/v1/status` reports problems:

```bash title="Status"
curl -H "Authorization: Bearer $NYTKA_ADMIN_TOKEN" https://<address>/api/v1/status
```

`lastError` names the cause, for example `The transcription endpoint answered 401.`, which means
the key, URL or model in `.env` is wrong. Fix `.env` and run `docker compose up -d`.

## Test without a pendant

`src/Nytka.Replay` sends a WAV file as one capture session ([README](../../README.md#test-without-a-pendant)):

```bash title="Replay a recording"
dotnet run --project src/Nytka.Replay -- --server http://127.0.0.1:8080 --wav speech.wav
```

The token comes from `--token` or `NYTKA_TOKEN`. The file must be 16 kHz, mono, 16-bit.

## Optional next steps

- A language model for titles, summaries, tasks and memories:
  [configuration](configuration.md#language-model).
- Back up the `postgres-data` volume and read the
  [upgrade notes](../../README.md#upgrading).
- The Ukrainian search dictionary is an extra download with a licence prompt
  ([README](../../README.md#ukrainian-search-optional)).
