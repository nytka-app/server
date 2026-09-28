# One Dockerfile, two images: build once, pick the entry project with --build-arg PROJECT.
ARG PROJECT=OmiPlatform.Ingest

# --platform=$BUILDPLATFORM pins the SDK to the machine doing the building, and TARGETARCH drives
# what it emits. Without it, `docker buildx --platform linux/amd64` on an arm64 laptop runs the
# whole SDK under QEMU, and without either the image silently matches the laptop and dies on an
# amd64 host with `exec format error`.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG PROJECT
ARG TARGETARCH
WORKDIR /src

# Restore against the project files alone so a source-only change does not re-download packages.
COPY Directory.Build.props ./
COPY src/OmiPlatform.Omi/*.csproj     src/OmiPlatform.Omi/
COPY src/OmiPlatform.Storage/*.csproj src/OmiPlatform.Storage/
COPY src/OmiPlatform.Ingest/*.csproj  src/OmiPlatform.Ingest/
COPY src/OmiPlatform.Mcp/*.csproj     src/OmiPlatform.Mcp/
# The RID has to be fixed at restore time too, or publish re-resolves and downloads again.
RUN dotnet restore "src/${PROJECT}/${PROJECT}.csproj" -a "${TARGETARCH}"

COPY src/ src/
# Migrations are embedded into OmiPlatform.Storage from here.
COPY db/ db/

RUN dotnet publish "src/${PROJECT}/${PROJECT}.csproj" \
        -c Release \
        -a "${TARGETARCH}" \
        -o /app \
        --no-restore

# No --platform here: the runtime image must match the target, which buildx supplies.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
ARG PROJECT
WORKDIR /app

# The runtime image ships neither curl nor wget, so the Compose healthcheck has nothing to probe
# /healthz with and the container reports unhealthy forever while serving fine.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# The runtime image ships a non-root `app` user (uid 1654).
USER app
COPY --from=build --chown=app:app /app .

ENV DOTNET_ENTRY_DLL="${PROJECT}.dll" \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0

# The trailing "--" becomes $0 so that CMD arguments land in "$@" and reach the app. Without it the
# shell drops them silently, which would make `docker compose run ingest --reproject` start the
# ingest loop instead of re-projecting.
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet \"$DOTNET_ENTRY_DLL\" \"$@\"", "--"]
