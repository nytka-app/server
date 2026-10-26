#!/usr/bin/env bash
# Prints the `dotnet test --filter` for one CI shard (the `test` job of .github/workflows/ci.yml). The test classes of Nytka.Server.Tests are
# grouped by namespace into shards 1 to 3 below; shard 4 is everything else, so a namespace nobody listed still runs.
# Keep the shards near 600 tests each; move a namespace between the lists when one run drifts far from the others.
set -euo pipefail

ns='Nytka.Server.Tests'
shard1=(Ai Speech Storage Coverage Rollups Topics Privacy)
shard2=(People Settings Voice Usage)
shard3=(Api Memories Import Auth Labeling Ask)

clause() { local sep="$1" op="$2"; shift 2; local out=""; for n in "$@"; do out+="${out:+$sep}FullyQualifiedName${op}${ns}.${n}."; done; printf '%s' "$out"; }

case "${1:?usage: ci-shard.sh 1|2|3|4}" in
  1) clause '|' '~' "${shard1[@]}" ;;
  2) clause '|' '~' "${shard2[@]}" ;;
  3) clause '|' '~' "${shard3[@]}" ;;
  4) clause '&' '!~' "${shard1[@]}" "${shard2[@]}" "${shard3[@]}" ;;
  *) echo "shard must be 1, 2, 3 or 4" >&2; exit 2 ;;
esac
