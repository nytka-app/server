#!/usr/bin/env bash
# Fetches TitaNet-small (NVIDIA NeMo), in the ONNX export sherpa-onnx publishes, for matching the wearer's
# voice: src/Nytka.Audio/Models/nemo_en_titanet_small.onnx, which the build copies next to the binaries. The
# Docker build runs this, so the image carries the model; outside Docker, run it once. Without the file the
# server runs without voice matching. Licence and attribution: NOTICE.
#
#   scripts/fetch-speaker-model.sh [target-dir]
#
# target-dir defaults to src/Nytka.Audio/Models/. A file that already has the right SHA-256 is left alone.
# Needs curl and sha256sum or shasum.
set -euo pipefail

NAME=nemo_en_titanet_small.onnx
SHA256=ad4a1802485d8b34c722d2a9d04249662f2ece5d28a7a039063ca22f515a789e
URL="https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/${NAME}"

target="${1:-$(cd "$(dirname "$0")/.." && pwd)/src/Nytka.Audio/Models}"

sha256() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

if [ -f "$target/$NAME" ] && [ "$(sha256 "$target/$NAME")" = "$SHA256" ]; then
  echo "Already there: $target/$NAME."
  exit 0
fi

mkdir -p "$target"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

curl -fsSL --retry 3 -o "$work/$NAME" "$URL"
actual=$(sha256 "$work/$NAME")
if [ "$actual" != "$SHA256" ]; then
  echo "Checksum mismatch for $URL: expected $SHA256, got $actual." >&2
  exit 1
fi

install -m 0644 "$work/$NAME" "$target/$NAME"
echo "TitaNet-small is in $target/$NAME."
