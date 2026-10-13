#!/usr/bin/env bash
# One-command install: creates a directory, writes docker-compose.yml and a .env with generated
# secrets, and starts Nytka with the `local` Compose profile, which needs no transcription account
# and no API key (a speech-to-text server and a language model server run alongside Postgres and
# the server, reached over the Compose network). Local models are slower and less accurate than a
# hosted provider (README, "Install in 15 minutes"); .env comments the lines that switch to one.
#
#   curl -fsSL https://raw.githubusercontent.com/nytka-app/server/main/scripts/install.sh | bash
#
# Pass a directory name to use something other than ./nytka:
#   curl -fsSL .../install.sh | bash -s -- my-nytka
#
# Needs a Linux or macOS host, Docker and Docker Compose v2 (the `docker compose` subcommand).
# Images are not pulled here beyond what `docker compose up` itself pulls; the local profile's
# language model is fetched separately, into the Ollama container's own volume, on first run.
set -euo pipefail

repo_raw="${NYTKA_INSTALL_REPO_RAW:-https://raw.githubusercontent.com/nytka-app/server/main}"
dir="${1:-nytka}"
local_llm_model="llama3.2:1b"

case "$(uname -s)" in
  Linux | Darwin) ;;
  *)
    echo "install.sh supports Linux and macOS. On Windows, use WSL2 with Docker Desktop and run it there." >&2
    exit 1
    ;;
esac

for cmd in curl openssl; do
  if ! command -v "$cmd" >/dev/null 2>&1; then
    echo "$cmd is required." >&2
    exit 1
  fi
done
if ! command -v docker >/dev/null 2>&1; then
  echo "Docker is required: https://docs.docker.com/get-docker/" >&2
  exit 1
fi
if ! docker compose version >/dev/null 2>&1; then
  echo "Docker Compose v2 is required (the 'docker compose' subcommand, not the older docker-compose)." >&2
  exit 1
fi

if [ -e "$dir" ]; then
  echo "$dir already exists; remove it or choose another directory: $0 <directory>" >&2
  exit 1
fi
mkdir -p "$dir"
cd "$dir"

echo "Fetching docker-compose.yml and .env.example from $repo_raw..."
curl -fsSL -o docker-compose.yml "$repo_raw/docker-compose.yml"
curl -fsSL -o .env.tmp "$repo_raw/.env.example"
mv .env.tmp .env
chmod 600 .env

admin_token=$(openssl rand -hex 24)
postgres_password=$(openssl rand -hex 24)

# Generate the two required secrets and wire the local profile's Nytka__Stt__Url and
# Nytka__Llm__BaseUrl, so the stack starts without an account or a key. Nothing here is printed:
# the admin token is shown once, at the end; the Postgres password never is.
sed -i.bak \
  -e "s|^POSTGRES_PASSWORD=.*|POSTGRES_PASSWORD=${postgres_password}|" \
  -e "s|^Nytka__AdminToken=.*|Nytka__AdminToken=${admin_token}|" \
  -e "s|^Nytka__Stt__Url=.*|Nytka__Stt__Url=http://stt-local:8000/v1/audio/transcriptions|" \
  -e "s|^# Nytka__Llm__BaseUrl=.*|Nytka__Llm__BaseUrl=http://llm-local:11434/v1|" \
  -e "s|^# Nytka__Llm__Model=.*|Nytka__Llm__Model=${local_llm_model}|" \
  -e "s|^# Nytka__Llm__JsonMode=schema|Nytka__Llm__JsonMode=object|" \
  -e "s|^# COMPOSE_PROFILES=local|COMPOSE_PROFILES=local|" \
  .env
rm -f .env.bak

echo "Starting Postgres, the server and the local speech-to-text and language model (first start pulls their images)..."
docker compose up -d --wait

echo "Pulling the local language model ($local_llm_model, about 1.3 GB; first run only, needs network)..."
docker compose exec -T llm-local ollama pull "$local_llm_model"

echo
echo "Nytka is up at http://127.0.0.1:8080/healthz"
echo
echo "Admin token (copy it now; nothing shows it again):"
echo "  ${admin_token}"
echo
echo "Local speech-to-text and language models are slower and less accurate than a hosted"
echo "provider (Groq, OpenAI). To switch, edit ${dir}/.env (README: 'Transcription endpoints',"
echo "'The language model') and run 'docker compose up -d' again."
echo "Next: give the phone an HTTPS address (README, 'Install in 15 minutes', step 4)."
