#!/usr/bin/env bash
# Fetches the Ukrainian Hunspell dictionary (dict_uk, Andriy Rysin and others) that Nytka's full-text search
# uses, and lays it out the way Postgres wants it: tsearch_data/uk_ua.dict and tsearch_data/uk_ua.affix, which
# are uk_UA.dic and uk_UA.aff renamed. docker-compose.yml mounts that directory into the Postgres container.
#
# The files are never committed, bundled or redistributed by Nytka: they come from the upstream release at
# the moment you run this, onto your own machine. Search works without them (Cyrillic words then match
# exactly), so this step is optional.
#
# LICENCE, read before you run it. Upstream says two different things (brown-uk/dict_uk at v6.8.6):
#   - The Hunspell package's own README (distr/hunspell/header/README_uk_UA.txt, saved next to the files)
#     says the dictionary is licensed under GPL 3.0 or above, LGPL 2.1 or above and MPL 1.1; the
#     distr/hunspell/README.md says MPL 1.1.
#   - The project README says the dictionary DATA is CC BY-NC-SA 4.0 (non-commercial, share-alike) and its
#     software GPL 3.0 or above, and that the derivative projects under distr/ have their own licences.
# uk_UA.dic is generated from that data, and upstream does not say how the two statements fit together.
# Personal, non-commercial self-hosting is covered either way. For commercial use, or any redistribution of
# the files, ask the upstream authors first. This is not legal advice. See NOTICE.
#
#   scripts/fetch-uk-dictionary.sh [--force] [target-dir]
#
# target-dir defaults to tsearch_data/ at the repository root. Without --force an existing pair is
# left alone. Needs curl, unzip, iconv and sha256sum or shasum.
set -euo pipefail

VERSION=6.8.6
ZIP_SHA256=043ae50d3a30beda0d4aa67562fdbee576e2f862eb6120357c1e5ae5ed3d5e9e
ZIP_URL="https://github.com/brown-uk/dict_uk/releases/download/v${VERSION}/hunspell-uk_UA_${VERSION}.zip"
README_SHA256=4634b0a40900cbb4bca4d758cbe6884834745414950d5761eaf0e4d935092b37
README_URL="https://raw.githubusercontent.com/brown-uk/dict_uk/v${VERSION}/distr/hunspell/header/README_uk_UA.txt"

force=0
if [ "${1:-}" = "--force" ]; then
  force=1
  shift
fi
target="${1:-$(cd "$(dirname "$0")/.." && pwd)/tsearch_data}"

if [ "$force" = 0 ] && [ -s "$target/uk_ua.dict" ] && [ -s "$target/uk_ua.affix" ]; then
  echo "Already there: $target/uk_ua.dict and uk_ua.affix (--force fetches again)."
  exit 0
fi

sha256() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

fetch() { # url sha256 file
  curl -fsSL --retry 3 -o "$3" "$1"
  local actual
  actual=$(sha256 "$3")
  if [ "$actual" != "$2" ]; then
    echo "Checksum mismatch for $1: expected $2, got $actual." >&2
    exit 1
  fi
}

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

fetch "$ZIP_URL" "$ZIP_SHA256" "$work/dict.zip"
fetch "$README_URL" "$README_SHA256" "$work/README_uk_UA.txt"
unzip -q -o "$work/dict.zip" uk_UA.aff uk_UA.dic -d "$work"

for f in uk_UA.aff uk_UA.dic; do
  if [ ! -s "$work/$f" ]; then
    echo "$f is missing from the release archive." >&2
    exit 1
  fi
  # Postgres reads the files in the database encoding; the dictionary must be UTF-8.
  if ! iconv -f UTF-8 -t UTF-8 "$work/$f" >/dev/null 2>&1; then
    echo "$f is not valid UTF-8." >&2
    exit 1
  fi
done
if ! head -n 1 "$work/uk_UA.aff" | grep -q '^SET UTF-8'; then
  echo "uk_UA.aff does not declare SET UTF-8." >&2
  exit 1
fi

mkdir -p "$target"
# The postgres user inside the container reads the mounts, whoever owns them here.
install -m 0644 "$work/uk_UA.dic" "$target/uk_ua.dict"
install -m 0644 "$work/uk_UA.aff" "$target/uk_ua.affix"
# The upstream licence notice travels with the files.
install -m 0644 "$work/README_uk_UA.txt" "$target/README_uk_UA.txt"

echo "Ukrainian dictionary ${VERSION} is in $target (uk_ua.dict, uk_ua.affix)."
