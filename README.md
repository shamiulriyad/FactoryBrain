# FactoryBrain

AI operations platform for Bangladeshi garment (RMG) factories. It shows live line throughput, machine maintenance and energy data, QC defects and floor alerts, and runs AI agents over a factory knowledge graph. The UI is branded **BunonBrain** and supports English and Bangla.

**Stack:** Next.js 15 (React 18, Tailwind, Zustand) · ASP.NET Core 9 (EF Core, SignalR) · PostgreSQL + pgvector

## Features

- **Overview:** morning brief, line board (throughput and bottlenecks), QC summary, live sensor readings
- **Ask BunonBrain:** questions answered from manuals and policies through hybrid RAG (vector + BM25)
- **Agents:** agent runs with a risk tier and an approval queue for risky actions
- **QC and Vision:** defect logging and flagging, image analysis results
- **Floor alerts:** real-time alerts pushed over SignalR (`/hubs/factory`)
- **Demo mode:** without an LLM key, Ask and agent runs return deterministic canned answers

## Repository layout

| Path | Contents |
|---|---|
| `frontend/` | Next.js app (`src/app`, components, stores, services, i18n) |
| `FactoryBrain.Backend/` | .NET solution: `Domain`, `Application`, `Infrastructure`, `Api` |
| `database/` | Notes on where the schema, migrations and seed data live (EF Core code-first) |
| `rag/` | Reserved for future standalone RAG work; see its README |
| `scripts/` | Smoke test, RAG eval, sensor simulator scripts |

## Prerequisites

- Node.js 20+
- .NET 9 SDK
- PostgreSQL with the pgvector extension (local, Docker or Supabase)

Start Postgres with Docker:

```bash
docker run -d --name factorybrain-db -p 5432:5432 \
  -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=factorybrain \
  pgvector/pgvector:pg16
```

## Getting started

### 1. Backend API

```bash
cd FactoryBrain.Backend/FactoryBrain.Api
cp .env.example .env        # set FACTORYBRAIN_DB and other values
dotnet run
```

The API listens on `http://localhost:5000`. On startup it creates the `vector` extension, applies EF Core migrations and seeds demo data.

- Health check: `GET /health`
- Swagger UI (Development only): `/swagger`

### 2. Frontend

```bash
cd frontend
cp ../.env.example .env.local
npm install
npm run dev
```

Open `http://localhost:3000`. Requests to `/api/*` are proxied to the backend URL in `NEXT_PUBLIC_API_URL` / `DOTNET_API_URL` (default `http://localhost:5000`).

## Configuration

There are two env files. Never commit real values.

| File | Used by | Key variables |
|---|---|---|
| `frontend/.env.local` (template: `.env.example`) | Next.js | `NEXT_PUBLIC_API_URL`, `DOTNET_API_URL`, `ADMIN_API_TOKEN`, `LLM_*` |
| `FactoryBrain.Backend/FactoryBrain.Api/.env` (template next to it) | API | `FACTORYBRAIN_DB`, `JWT_SIGNING_KEY`, `ADMIN_API_TOKEN`, `LLM_*`, `RAG_EMBEDDING_*`, `CORS_ORIGINS`, `SIMULATOR_ENABLED` |

Notes:

- `ADMIN_API_TOKEN` must be the same in both files. It protects `POST /api/settings/llm`, `/api/rag/ingest` and `/api/rag/reindex`.
- `JWT_SIGNING_KEY` (32+ bytes) is required in Production.
- `LLM_PROVIDER` can be `openai`, `gemini` or `anthropic`. The key can also be entered in **Settings → Advanced**.
- With no embedding key, RAG falls back to a local hash vectorizer.
- `SIMULATOR_ENABLED=true` makes the API generate a live sensor event stream.

## API overview

| Route | Purpose |
|---|---|
| `/api/auth/{login,refresh,logout,me}` | Cookie/JWT authentication |
| `/api/ask` | Ask a question (RAG + LLM) |
| `/api/agents`, `/api/agents/{id}/run` | List and run agents |
| `/api/brief/morning` | Morning brief |
| `/api/line-board` | Line metrics, `POST /refresh` |
| `/api/sensors/{ingest,live,latest}` | Sensor readings |
| `/api/qc/{defects,flag}` | QC defects |
| `/api/vision/analyze` | Vision results |
| `/api/floor-alerts` | Floor alerts, `PATCH /{id}` |
| `/api/rag/{ingest,reindex,documents,status}` | RAG index management |
| `/api/settings/llm` | LLM settings (admin) |

## Scripts

Run these from `frontend/` (the API must be running for the RAG eval):

```bash
npm run smoke       # dataset, i18n and service contract checks + RAG eval
npm run test:rag    # RAG golden-set check against /api/ask
npm run lint
```

Push one live sensor reading:

```bash
node scripts/sensor-live.mjs --entity M-101 --vibration 6.4 --temp 82
```

## Docker

Build the API image from the repo root:

```bash
docker build -f FactoryBrain.Backend/FactoryBrain.Api/Dockerfile -t factorybrain-api:dev .
docker run -p 8080:8080 -e FACTORYBRAIN_DB="..." -e JWT_SIGNING_KEY="..." factorybrain-api:dev
```

The container listens on port 8080 and runs in Production mode. No secrets are baked into the image.

## License

[MIT](LICENSE)
