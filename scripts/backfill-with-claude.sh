#!/usr/bin/env bash
# Answers the server's model work with Claude Code on this machine (docs/how-to/backfill-with-claude.md).
# The server calls no model: it hands out each request built with its own prompt and stores each answer through its own
# validation. Run it yourself; it is never scheduled.
#
#   NYTKA_URL=https://nytka.example.com NYTKA_ADMIN_TOKEN=... scripts/backfill-with-claude.sh [--count] [--kinds recheck,enrich,memories]
#
# Needs curl, jq and the claude CLI signed in. Prints counts only, never transcript text or a token.
set -euo pipefail

: "${NYTKA_URL:?set NYTKA_URL}"
: "${NYTKA_ADMIN_TOKEN:?set NYTKA_ADMIN_TOKEN}"
kinds="recheck,enrich,memories"
count_only=false
model_args=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --count) count_only=true ;;
    --kinds) kinds="$2"; shift ;;
    --model) model_args=(--model "$2"); shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
  shift
done

# --retry covers 502, 503 and refused connections, with a doubling delay, so a deploy restart does not end the run.
api() {
  curl -fsS --retry 8 --retry-connrefused --retry-max-time 600 \
    -H "Authorization: Bearer $NYTKA_ADMIN_TOKEN" -H "Content-Type: application/json" "$@"
}

# Every conversation a backfill may run on, with how many memories a re-check would judge.
candidates="$(mktemp)"
trap 'rm -f "$candidates"' EXIT
after=""
while :; do
  page="$(api "$NYTKA_URL/api/v1/ai/backfill/conversations?limit=500${after:+&after=$after}")"
  jq -c '.items[]' <<<"$page" >>"$candidates"
  after="$(jq -r '.nextAfter // empty' <<<"$page")"
  [[ -n "$after" ]] || break
done

total="$(wc -l <"$candidates" | tr -d ' ')"
with_memories="$(jq -s '[.[] | select(.memories > 0)] | length' "$candidates")"
echo "conversations: $total; with memories to re-check: $with_memories"
if $count_only; then
  exit 0
fi

calls=0
ask() {
  # $1 = response of a step that is pending. Prints the model's JSON answer.
  local step="$1" system user schema out
  system="$(jq -r '.system' <<<"$step")"
  user="$(jq -r '.user' <<<"$step")"
  schema="$(jq -c '.schema' <<<"$step")"
  # stdin from /dev/null, so claude never reads the list of conversations the loop below is reading.
  out="$(claude -p ${model_args[@]+"${model_args[@]}"} --output-format json --max-turns 1 --system-prompt "$system" \
    "$user

Answer with one JSON object and nothing else, no code fence, that matches this JSON schema: $schema" </dev/null)" || true
  # A CLI failure (signed out, usage limit) is no answer: stop the run rather than spend the server's retries on it.
  if ! jq -e 'type == "object" and (.is_error | not)' >/dev/null 2>&1 <<<"$out"; then
    echo "claude CLI error: sign in again with \`claude\` and rerun" >&2
    return 1
  fi
  # Tolerate a code fence the model added anyway.
  jq -r '.result' <<<"$out" | sed -e 's/^```json//' -e 's/^```//' -e 's/```$//'
}

run() {
  # $1 = kind, $2 = conversation id. Loops until the server says done, busy or gives up after 3 invalid answers.
  local kind="$1" id="$2" answers='{}' step status key answer invalid=0
  while :; do
    step="$(api -X POST "$NYTKA_URL/api/v1/ai/backfill/$kind/$id" -d "$(jq -cn --argjson a "$answers" '{answers: $a}')")"
    status="$(jq -r '.status' <<<"$step")"
    case "$status" in
      done) return 0 ;;
      busy) echo "  $kind $id: busy, skipped"; return 0 ;;
      invalid)
        invalid=$((invalid + 1))
        echo "  $kind $id: invalid answer: $(jq -r '.reason // "no reason given"' <<<"$step")"
        [[ $invalid -lt 3 ]] || { echo "  $kind $id: 3 invalid answers, skipped"; return 0; }
        answers='{}' ;;
      pending)
        key="$(jq -r '.key' <<<"$step")"
        answer="$(ask "$step")" || exit 3
        calls=$((calls + 1))
        if ! jq -e . >/dev/null 2>&1 <<<"$answer"; then
          answer='{}'
        fi
        answers="$(jq -c --arg k "$key" --arg v "$answer" '. + {($k): $v}' <<<"$answers")" ;;
      *) echo "  $kind $id: unexpected status $status" >&2; return 1 ;;
    esac
  done
}

done_count=0
while read -r item; do
  id="$(jq -r '.id' <<<"$item")"
  memories="$(jq -r '.memories' <<<"$item")"
  for kind in ${kinds//,/ }; do
    if [[ "$kind" == recheck && "$memories" -eq 0 ]]; then
      continue
    fi
    run "$kind" "$id"
  done
  done_count=$((done_count + 1))
  echo "$done_count/$total conversations, $calls model calls"
done <"$candidates"
