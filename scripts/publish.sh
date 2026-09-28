#!/usr/bin/env bash
# Builds both images and pushes them to GHCR, then prints each digest for the record.
#
# The homelab deliberately does not build first-party source at deploy time: it pulls a tagged
# image like every third-party one. This is the other half of that — same convention as
# oura-platform/scripts/publish.sh.
#
#   ./scripts/publish.sh 0.1.0
#
# Needs `docker login ghcr.io -u Egoushka` first, with a token carrying write:packages.
set -euo pipefail

VERSION="${1:?usage: publish.sh <version>   e.g. publish.sh 0.1.0}"
REGISTRY="${OMI_REGISTRY:-ghcr.io/egoushka}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

cd "$REPO_ROOT"

# A dirty tree means the pushed image does not correspond to any commit, which defeats the point of
# pinning it.
if [[ -n "$(git status --porcelain)" ]]; then
  echo "Working tree is dirty. Commit first — a pushed image must map to a commit." >&2
  exit 1
fi

COMMIT="$(git rev-parse --short HEAD)"
echo "Building ${VERSION} from ${COMMIT}"

# The tests gate the push rather than the build: shipping an image whose models no longer match
# Omi's shape is exactly the failure docs/omi-api-notes.md exists to catch early.
dotnet test tests/OmiPlatform.Omi.Tests --nologo -v q

# image:project pairs, not an associative array — macOS ships bash 3.2, where `declare -A` silently
# makes an ordinary array instead of failing loudly.
PROJECTS="omi-ingest:OmiPlatform.Ingest omi-mcp:OmiPlatform.Mcp"

for pair in $PROJECTS; do
  image="${pair%%:*}"
  project="${pair##*:}"
  ref="${REGISTRY}/${image}:${VERSION}"
  echo "==> ${ref}"

  # buildx, not plain build: this Mac may be arm64 and the VPS is amd64. The Dockerfile
  # cross-compiles via $BUILDPLATFORM/TARGETARCH, and a plain `docker build --platform` runs the
  # whole SDK under QEMU instead.
  docker buildx build \
    --platform linux/amd64 \
    --build-arg "PROJECT=${project}" \
    --label "org.opencontainers.image.revision=${COMMIT}" \
    --label "org.opencontainers.image.version=${VERSION}" \
    --label "org.opencontainers.image.source=https://github.com/Egoushka/omi-platform" \
    -t "$ref" \
    --push .
done

echo
# Both images are stateless, so the homelab pins them by semver tag, not by digest. The digests
# are printed as provenance for the release notes.
echo "Published (digest for the record):"
for pair in $PROJECTS; do
  image="${pair%%:*}"
  ref="${REGISTRY}/${image}:${VERSION}"
  digest="$(docker buildx imagetools inspect "$ref" --format '{{.Manifest.Digest}}')"
  printf '  %s:%s@%s\n' "${REGISTRY}/${image}" "$VERSION" "$digest"
done
