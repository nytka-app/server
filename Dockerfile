# --platform=$BUILDPLATFORM pins the SDK to the machine doing the building, and TARGETARCH drives
# what it emits. Without it, a multi-arch buildx build runs the whole SDK under QEMU for the
# foreign architecture, and a plain build silently matches the builder's architecture.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG TARGETARCH
ARG VERSION=0.0.0-dev
WORKDIR /src

# Restore against the project files alone so a source-only change does not re-download packages.
COPY Directory.Build.props ./
COPY src/Nytka.Audio/*.csproj   src/Nytka.Audio/
COPY src/Nytka.Storage/*.csproj src/Nytka.Storage/
COPY src/Nytka.Server/*.csproj  src/Nytka.Server/
# The RID has to be fixed at restore time too, or publish re-resolves and downloads again.
RUN dotnet restore src/Nytka.Server/Nytka.Server.csproj -a "${TARGETARCH}"

COPY src/ src/
# Migrations are embedded into Nytka.Storage from here.
COPY db/ db/

RUN dotnet publish src/Nytka.Server/Nytka.Server.csproj \
        -c Release \
        -a "${TARGETARCH}" \
        -p:Version="${VERSION}" \
        -o /app \
        --no-restore

# No --platform here: the runtime image must match the target, which buildx supplies.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app

# The runtime image ships neither curl nor wget, so the Compose healthcheck has nothing to probe
# /healthz with and the container reports unhealthy forever while serving fine.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# The runtime image ships a non-root `app` user (uid 1654).
USER app
COPY --from=build --chown=app:app /app .

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080

ENTRYPOINT ["dotnet", "Nytka.Server.dll"]
