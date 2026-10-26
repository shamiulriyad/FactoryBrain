<div align="center">

# 🧵 FactoryBrain

**AI operations platform for Bangladeshi garment (RMG) factories**

Live line throughput · machine health and energy · QC defects · floor alerts ·
AI agents over a factory knowledge graph. Branded **BunonBrain** in the UI, in
English and বাংলা.

![Next.js](https://img.shields.io/badge/Next.js-15-000000?logo=nextdotjs&logoColor=white)
![React](https://img.shields.io/badge/React-18-61DAFB?logo=react&logoColor=black)
![.NET](https://img.shields.io/badge/.NET-9-512BD4?logo=dotnet&logoColor=white)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-pgvector-4169E1?logo=postgresql&logoColor=white)
![Python](https://img.shields.io/badge/Python-3.11-3776AB?logo=python&logoColor=white)
![License](https://img.shields.io/badge/license-MIT-green)

[Features](#-features) ·
[Architecture](#-architecture) ·
[Quick start](#-quick-start) ·
[Configuration](#-configuration) ·
[API](#-api-overview) ·
[Project structure](#-project-structure)

</div>

---

## ✨ Features

| | Feature | What it does |
|---|---|---|
| 📊 | **Overview** | Morning brief, line board (throughput and bottlenecks), QC summary, live sensor readings |
| 💬 | **Ask BunonBrain** | Answers questions from manuals and policies with hybrid RAG (vector + BM25) and cites its sources |
| 🤖 | **Agents** | Agent runs with a risk tier, and an approval queue for risky actions |
| 🏭 | **Production planning** | Orders, approval-gated production plans, recorded / imported output, planned vs actual, line efficiency from SMV, WIP, delivery risk, time-study bottlenecks and line-balancing proposals, all from the plant's own records |
| 🧾 | **Merchandising** | Buyer orders and milestones; upload POs, tech packs and spec sheets (PDF, DOCX, CSV, text) in versions; values extracted with their quoted source and checked; merchandisers correct and confirm them; conflicts between documents flagged and resolved; deadlines and missing information from the stored records |
| 🔍 | **Quality inspection** | Upload garment photos, AI defect detection with boxes (configurable vision provider), inspector review and corrections, inspection history, defect rates and DHU from reviewed records |
| 🚨 | **Floor alerts** | Alerts stored by the backend and broadcast over SignalR (`/hubs/factory`) |
| 🎭 | **Demo mode** | With no LLM key, Ask and agent runs return deterministic canned answers |
| 📄 | **PDF RAG (Python)** | Standalone LangChain + Qdrant + Gemini pipeline in [`rag/`](rag/) |

---

## 🏗 Architecture

```mermaid
flowchart LR
    user(["👷 Browser"])

    subgraph FE["Frontend · Next.js 15 · :3000"]
        ui["React pages + Zustand"]
        proxy["/api/* rewrite<br/>+ admin proxy"]
        ui --> proxy
    end

    subgraph BE["Backend · ASP.NET Core 9 · :5000"]
        ctrl["Controllers<br/>auth · validation · rate limit"]
        svc["Services<br/>Ask · RAG · Agents · Sensors · QC"]
        sim["Simulator<br/>hosted service"]
        hub["SignalR hub"]
        ctrl --> svc
        sim --> svc
        svc --> hub
    end

    db[("PostgreSQL<br/>+ pgvector")]
    ai["LLM and embeddings<br/>OpenAI · Anthropic · Gemini"]

    subgraph PY["rag/ · standalone"]
        py["Python PDF RAG"] --> qd[("Qdrant")]
    end

    user --> ui
    proxy --> ctrl
    svc --> db
    svc -.->|"optional"| ai
    py -.-> ai
```

- The browser talks only to Next.js, which rewrites `/api/*` to the .NET backend.
- The backend owns all data and every AI call. With no keys it still runs: demo
  answers and a local hash vectorizer.
- `rag/` is a separate tool. The backend does not call it.

📐 **Full diagrams** (backend layers, Ask request flow, live data, RAG, security):
[ARCHITECTURE.md](ARCHITECTURE.md)

---

## 🚀 Quick start

### Prerequisites

- Node.js 20+
- .NET 9 SDK
- PostgreSQL with the pgvector extension (local, Docker or Supabase)
- Python 3.11 (only for `rag/`)

### 1. Environment

One `.env` at the repo root configures everything (frontend, API and `rag/`):

```bash
cp .env.example .env        # then fill in FACTORYBRAIN_DB, keys, etc.
```

### 2. Database

Local Docker (matches the default `FACTORYBRAIN_DB`):

```bash
docker run -d --name fb-pg -p 5432:5432 \
  -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=factorybrain \
  pgvector/pgvector:pg16
```

Or use **Supabase**: take the *Session pooler* string from Dashboard → Connect
and use the Supabase `FACTORYBRAIN_DB` line in `.env`.

### 3. Backend API

```bash
cd FactoryBrain.Backend/FactoryBrain.Api
dotnet run                  # reads the repo-root .env
```

> [!NOTE]
> Only .NET 8 or 10 installed? Run `$env:DOTNET_ROLL_FORWARD = "Major"`
> (PowerShell) first. It has to be set in the shell, not in `.env`.

The API listens on `http://localhost:5000`. On startup it creates the `vector`
extension, applies EF Core migrations and seeds demo data.

- Health check: `GET /health`
- Swagger UI (Development only): `/swagger`

### 4. Frontend

```bash
cd frontend
npm install
npm run dev                 # reads the repo-root .env
```

Open **http://localhost:3000**. Requests to `/api/*` are proxied to the backend
URL in `NEXT_PUBLIC_API_URL` / `DOTNET_API_URL` (default `http://localhost:5000`).

### 5. PDF RAG (optional)

```bash
cd rag
py -3.11 -m venv venv && venv\Scripts\activate      # macOS/Linux: source venv/bin/activate
pip install -r requirements.txt -r requirements-local.txt
# set GOOGLE_API_KEY and the PYRAG_* keys in the repo-root .env
python ingest.py --pdf data/your-file.pdf --recreate
python ask.py "your question"
```

Details: [rag/README.md](rag/README.md)

---

## ⚙ Configuration

**One file for everything:** `.env` at the repo root (template:
[`.env.example`](.env.example), every option explained there). It is git-ignored.
Never commit real values. Real shell or Docker env vars always win over the file.

| App | How it finds the root `.env` | Main keys |
|---|---|---|
| Next.js | `frontend/next.config.mjs` loads it | `NEXT_PUBLIC_API_URL`, `DOTNET_API_URL` |
| .NET API | `Program.cs` walks up from its working directory | `FACTORYBRAIN_DB`, `JWT_SIGNING_KEY`, `SEED_ADMIN_*`, `LLM_*`, `RAG_EMBEDDING_*`, `CORS_ORIGINS`, `SIMULATOR_ENABLED` |
| Python RAG | `rag/config.py` loads `../.env` | `GOOGLE_API_KEY`, `PYRAG_*` |

The Python keys carry a `PYRAG_` prefix so they never clash with the API's
(for example, both have an LLM model setting).

- Every API route except `/health` and `/api/auth/{login,refresh,logout}`
  needs a signed-in user. Create the first account with `SEED_ADMIN_EMAIL` /
  `SEED_ADMIN_PASSWORD`, then sign in at `/login`. Roles are checked by the API:
  **Viewer** reads, **Operator** also runs agents, flags QC, ingests sensor data
  and acknowledges alerts, **Admin** also changes LLM settings and manages the
  RAG index. `ADMIN_API_TOKEN` is retired and no longer read.

- `JWT_SIGNING_KEY` (32+ bytes) is required in Production.
- `LLM_PROVIDER` can be `openai`, `gemini` or `anthropic`. An Admin can also
  set the provider, model and key in **Settings → Advanced**; saved settings
  are stored in the database (the key encrypted with
  `AI_SETTINGS_ENCRYPTION_KEY`), survive restarts, take effect from the next
  question, and from then on win over the `LLM_*` variables (which are read
  only at startup). Clearing them really switches Ask to demo mode.
- Embeddings are configured separately (`RAG_EMBEDDING_*`, or Settings →
  Advanced): an LLM key never changes them. With no embedding key, RAG uses a
  local hash vectorizer. Changing the embedding provider or dimensions, or
  reindexing, shows a plan (documents, vector size, cost) and runs only after an
  Admin confirms it; the API never re-embeds on its own at startup.
- Quality inspection: `QC_VISION_PROVIDER` = `llm` (the configured LLM provider's vision model)
  or `http` (your own detection model, contract in ARCHITECTURE.md §9). Unset, inspections
  are stored and marked "AI analysis not configured" and can be reviewed manually; nothing
  is predicted. Images are stored under `QC_IMAGE_STORAGE_PATH` (mount a volume in Docker).
- Production planning: set `PRODUCTION_TIME_ZONE` to the plant's zone (default UTC); lines, shifts and
  styles are added by an Admin on Production → Setup, nothing is seeded. Formulas and the approval
  workflow: ARCHITECTURE.md §10; other `PRODUCTION_*` settings: `.env.example`.
- Merchandising: `MERCH_EXTRACTION_PROVIDER` is `rules` by default (a label / table parser, not AI);
  `llm` uses the language model from Settings → AI; `none` turns extraction off. Originals are stored
  under `MERCH_DOCUMENT_STORAGE_PATH` (mount a volume in Docker). There is no OCR and no email
  integration. Details: ARCHITECTURE.md §11.
- `SIMULATOR_ENABLED=true` makes the API generate a live sensor event stream.
  The Overview page only reads it (`GET /api/sensors/snapshot`); without the
  simulator the data moves only when an Operator presses *Simulate tick*.

---

## 🔌 API overview

| Route | Purpose |
|---|---|
| `/api/auth/{login,refresh,logout,me}` | Cookie/JWT authentication |
| `/api/ask` | Ask a question (RAG + LLM) |
| `/api/agents`, `/api/agents/{id}/run` | List and run agents |
| `/api/approvals`, `/api/approvals/{id}/{approve,reject}` | Server-side approval queue (Operator/Admin to decide; High risk needs Admin; reject needs a reason) |
| `/api/audit` | Append-only audit log of decisions (Admin) |
| `/api/brief/morning` | Morning brief |
| `/api/line-board` | Line metrics, `POST /refresh` |
| `/api/sensors/{ingest,live,latest}` | Sensor readings |
| `/api/qc/inspections`, `/{id}`, `/{id}/image`, `/{id}/review`, `/{id}/retry` | Garment inspections: upload (multipart, `Idempotency-Key`), results, review |
| `/api/qc/summary`, `/api/qc/categories`, `/api/qc/vision/status` | Quality metrics, defect taxonomy (Admin edits), vision provider status |
| `/api/qc/{defects,flag}` | Seeded weekly QC grid (API only, not shown in the UI) |
| `/api/vision/analyze` | Retired simulated demo: `410 Gone` |
| `/api/floor-alerts` | Floor alerts, `PATCH /{id}` |
| `/api/rag/{ingest,reindex,documents,status}` | RAG index management |
| `/api/settings/llm` | LLM settings (admin) |
| `/hubs/factory` | SignalR: `sensorReading`, `lineBoardUpdated`, `floorAlert` |

---

## 🧪 Scripts

Dev and test scripts live in `frontend/scripts/`. Run them from `frontend/`:

```bash
npm run smoke         # static contract checks, Ask-mode logic, admin-proxy runtime check
npm run e2e           # browser E2E + authenticated RAG eval on a disposable stack (needs Docker)
npm run test:rag      # RAG golden-set eval (needs RAG_EVAL_BASE_URL + test credentials)
npm run probe         # hit every page on a running Next.js server (BASE, default :3000)
npm run sensor:live -- --entity M-101 --vibration 6.4 --temp 82   # push one sensor reading
npm run lint
```

Backend integration tests, from `FactoryBrain.Backend/`:

```bash
dotnet test FactoryBrain.Backend.sln
```

- They never read the repo-root `.env` and never touch your configured
  database. Hosts run in the `Testing` (no database) or `IntegrationTesting`
  environment, and `IntegrationTesting` refuses to start against a
  non-local database.
- With **Docker Desktop running**, the database tests start a disposable
  `pgvector/pgvector:pg16` container (random test-only credentials, removed
  afterwards). They then run migrations and seeding and cover login, refresh,
  logout, roles, and RAG ingest and retrieval on real pgvector. Without
  Docker those tests are reported as skipped.
- Every outbound HTTP call is intercepted, so no test can reach a paid LLM
  or embedding provider. The OpenAI paths run against local fakes.
- Only the .NET 10 runtime installed? The test project rolls forward on its
  own; the API itself still needs `DOTNET_ROLL_FORWARD=Major`.

Browser end-to-end tests (Playwright), from `frontend/`, with Docker Desktop running:

```bash
npx playwright install chromium   # once
npm run e2e
```

`npm run e2e` (`e2e/run-e2e.mjs`):

1. Starts a disposable `pgvector/pgvector:pg16` container (random credentials,
   removed afterwards).
2. Starts the built API on it in `IntegrationTesting` mode, which ignores
   `.env` and accepts only a local database.
3. Starts a local fake OpenAI-compatible model (`LLM_BASE_URL`), with every
   other outbound call blocked by a dead proxy.
4. Builds and starts Next.js without the root `.env`.
5. Runs the authenticated RAG golden-set eval (`scripts/test-rag.mjs`) and the
   Playwright suite in `e2e/tests`: login and protected routes, approvals and
   audit, and Ask's Live / Demo / No source labels.

It leaves `.next` as a test build, so run `npm run build` again afterwards.

`npm run test:rag` alone needs `RAG_EVAL_BASE_URL` (a local test API) and
`RAG_EVAL_EMAIL` / `RAG_EVAL_PASSWORD`. It refuses non-local targets and APIs
wired to a hosted LLM or hosted embeddings.

Python RAG tests, from `rag/`:

```bash
pip install -r requirements-dev.txt
python -m pytest -q
```

---

## 🐳 Docker

Build the API image from the repo root:

```bash
docker build -f FactoryBrain.Backend/FactoryBrain.Api/Dockerfile -t factorybrain-api:dev .
docker run -p 8080:8080 -e FACTORYBRAIN_DB="..." -e JWT_SIGNING_KEY="..." factorybrain-api:dev
```

The container listens on port 8080 and runs in Production mode. No secrets are
baked into the image.

---

## 📁 Project structure

```
FactoryBrain/
├── frontend/                  Next.js app
│   ├── src/app/               pages (/app, /app/ask, /app/qc, ...) and admin proxy routes
│   ├── src/components/        UI components
│   ├── src/store/             Zustand stores
│   ├── src/services/          client services and demo datasets
│   ├── src/i18n/              English and Bangla strings
│   └── scripts/               smoke, probe, RAG eval, sensor push
├── FactoryBrain.Backend/      .NET 9 solution
│   ├── FactoryBrain.Api/             controllers, SignalR hub, middleware, Program.cs
│   ├── FactoryBrain.Application/     interfaces, DTOs, validation, RAG scoring
│   ├── FactoryBrain.Infrastructure/  EF Core, migrations, seed, services, embeddings
│   └── FactoryBrain.Domain/          entities and enums
├── database/                  where the schema, migrations and seed data live
├── rag/                       standalone Python PDF RAG (LangChain + Qdrant + Gemini)
├── ARCHITECTURE.md            system diagrams
└── README.md
```

---

## 📄 License

[MIT](LICENSE)
