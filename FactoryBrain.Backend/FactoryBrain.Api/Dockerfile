# syntax=docker/dockerfile:1.7
# Multi-stage Dockerfile for the FactoryBrain.Api service.
#
# Build it from the REPO ROOT so the root .dockerignore applies:
#
#     docker build -f FactoryBrain.Backend/FactoryBrain.Api/Dockerfile -t factorybrain-api:dev .
#
# Runtime contract:
#   - Listens on http://+:8080 (so it works for both `docker run -p` and
#     inside Kubernetes pods). Override with ASPNETCORE_URLS if needed.
#   - Runs as a non-root user ("app") for least-privilege.
#   - Uses the released aspnet image, not the SDK image.
#   - Copies only the published output. No .env, no
#     appsettings.Development.json, no source, no node_modules.
#
# Environment the container reads at startup:
#   FACTORYBRAIN_DB   — Postgres connection string
#   ADMIN_API_TOKEN   — gates POST /api/{settings/llm,rag/reindex,rag/ingest}
#   RAG_EMBEDDING_*   — provider/model/dims/key for the embedding service
#   LLM_API_KEY etc.  — used by AskService for live calls
#
# The image does NOT bake any of these in. They must be supplied at
# `docker run` time (or via Compose / k8s Secret) — which is also why the
# .dockerignore at the repo root blocks .env*.

# ─────────────────────────────────────────────────────────────────────────
# Stage 1 — restore + publish
# ─────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy the whole backend solution (csproj files + sources for all 4
# projects: Domain, Application, Infrastructure, Api). Copying csproj
# files first lets the restore layer cache when only sources change.
COPY FactoryBrain.Backend/FactoryBrain.Backend.sln FactoryBrain.Backend/
COPY FactoryBrain.Backend/FactoryBrain.Domain/FactoryBrain.Domain.csproj FactoryBrain.Backend/FactoryBrain.Domain/
COPY FactoryBrain.Backend/FactoryBrain.Application/FactoryBrain.Application.csproj FactoryBrain.Backend/FactoryBrain.Application/
COPY FactoryBrain.Backend/FactoryBrain.Infrastructure/FactoryBrain.Infrastructure.csproj FactoryBrain.Backend/FactoryBrain.Infrastructure/
COPY FactoryBrain.Backend/FactoryBrain.Api/FactoryBrain.Api.csproj FactoryBrain.Backend/FactoryBrain.Api/
RUN dotnet restore "FactoryBrain.Backend/FactoryBrain.Backend.sln"

# Now copy the rest of the API source and publish. We exclude
# appsettings.Development.json from the publish output — the image runs
# in Production (ASPNETCORE_ENVIRONMENT=Production) and Development-
# specific settings (like the dev "Urls" override) must never end up
# inside the container.
COPY FactoryBrain.Backend/ FactoryBrain.Backend/
RUN dotnet publish "FactoryBrain.Backend/FactoryBrain.Api/FactoryBrain.Api.csproj" \
        -c "$BUILD_CONFIGURATION" \
        -o /app/publish \
        --no-restore \
        /p:UseAppHost=false

# Belt-and-braces: even if a future config glob drags the dev file in,
# remove it before the runtime stage is built.
RUN rm -f /app/publish/appsettings.Development.json

# ─────────────────────────────────────────────────────────────────────────
# Stage 2 — runtime
# ─────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime

# Bind on 8080 inside the container. ASPNETCORE_URLS is read by
# Kestrel/HttpSys; the leading "+" means "all interfaces".
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=true

EXPOSE 8080

# Create the non-root user before COPY so file ownership is correct from
# the first layer (no "chown after copy" pattern that bloats the image).
RUN groupadd --system --gid 1001 app \
    && useradd  --system --uid 1001 --gid app --home /app --shell /sbin/nologin app \
    && mkdir -p /app \
    && chown -R app:app /app

WORKDIR /app
COPY --from=build --chown=app:app /app/publish .

USER app
ENTRYPOINT ["dotnet", "FactoryBrain.Api.dll"]