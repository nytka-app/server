#!/usr/bin/env bash
# Fetches YAMNet (Google, Apache-2.0), in the ONNX export andrelgomes/yamnet-onnx publishes, and its class map, for
# telling a TV or a narrator from a person in the room (docs/specs/speech-kind.md): src/Nytka.Audio/Models/yamnet.onnx
# and yamnet_class_map.csv, which the build copies next to the binaries. The Docker build runs this, so the image
# carries them; outside Docker, run it once. Without the files the server still guesses, from structure alone.
# Licence and attribution: NOTICE.
#
#   scripts/fetch-audio-tagger.sh [target-dir]
#
# target-dir defaults to src/Nytka.Audio/Models/. A file that already has the right SHA-256 is left alone.
# Needs curl and sha256sum or shasum.
set -euo pipefail

MODEL=yamnet.onnx
MODEL_SHA256=1510041dce24a2e9e84ec546807ac408ae496da6d1ed41bc3ccba649623f8e19
MODEL_URL="https://huggingface.co/andrelgomes/yamnet-onnx/resolve/8a03a1572569685c42fdbef54ff36435dbaaf689/${MODEL}"

MAP=yamnet_class_map.csv
MAP_SHA256=cdf24d193e196d9e95912a2667051ae203e92a2ba09449218ccb40ef787c6df2
MAP_URL="https://raw.githubusercontent.com/tensorflow/models/dfffd623b6be8d1d9744b8e261fbac370d17c46d/research/audioset/yamnet/${MAP}"
# A header line and one line per class.
MAP_LINES=522

target="${1:-$(cd "$(dirname "$0")/.." && pwd)/src/Nytka.Audio/Models}"

sha256() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

mkdir -p "$target"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# fetch <name> <sha256> <url>
fetch() {
  if [ -f "$target/$1" ] && [ "$(sha256 "$target/$1")" = "$2" ]; then
    echo "Already there: $target/$1."
    return
  fi
  curl -fsSL --retry 3 -o "$work/$1" "$3"
  local actual
  actual=$(sha256 "$work/$1")
  if [ "$actual" != "$2" ]; then
    echo "Checksum mismatch for $3: expected $2, got $actual." >&2
    exit 1
  fi
  install -m 0644 "$work/$1" "$target/$1"
  echo "$1 is in $target."
}

fetch "$MODEL" "$MODEL_SHA256" "$MODEL_URL"
fetch "$MAP" "$MAP_SHA256" "$MAP_URL"

lines=$(wc -l < "$target/$MAP" | tr -d ' ')
if [ "$lines" != "$MAP_LINES" ]; then
  echo "$MAP has $lines lines; expected $MAP_LINES (521 classes and a header)." >&2
  exit 1
fi
