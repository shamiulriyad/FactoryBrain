# Architecture

How FactoryBrain fits together: what runs where, how a request moves through the
system, and where data lives. The diagrams are [Mermaid](https://mermaid.js.org/)
and render on GitHub and in the VS Code Markdown preview.

- [1. System overview](#1-system-overview)
- [2. Backend layers](#2-backend-layers)
- [3. Ask BunonBrain request flow](#3-ask-bunonbrain-request-flow)
- [4. Live floor data](#4-live-floor-data)
- [5. Backend RAG](#5-backend-rag)
- [6. Standalone Python RAG (`rag/`)](#6-standalone-python-rag-rag)
- [7. Security boundaries](#7-security-boundaries)
- [8. Frontend pages and the API](#8-frontend-pages-and-the-api)
- [9. Quality inspection (QC & defect detection)](#9-quality-inspection-qc--defect-detection)
- [10. Production planning & line efficiency](#10-production-planning--line-efficiency)

---

## 1. System overview

```mermaid
flowchart LR
    user(["👷 Factory user<br/>browser"])

    subgraph FE["Frontend · Next.js 15 · :3000"]
        direction TB
        ui["React pages<br/>/app · /app/ask · /app/qc · ..."]
        stores["Zustand stores"]
        rewrite["Rewrite<br/>/api/* → backend"]
        admin["Admin pass-through routes<br/>settings/llm · rag/ingest · rag/reindex<br/>forward the caller's bearer token"]
        ui --- stores
    end

    subgraph BE["Backend · ASP.NET Core 9 · :5000"]
        direction TB
        mw["Middleware<br/>JWT cookie auth · admin token<br/>rate limit · validation"]
        ctrl["Controllers<br/>Ask · Agents · Brief · LineBoard<br/>Sensors · QC · Vision · FloorAlerts<br/>Rag · Settings · Auth"]
        svc["Services<br/>AskService · RagService · AgentRunService<br/>SensorService · LineBoardService · ..."]
        hosted["Hosted services<br/>Simulator · RagHealthMonitor"]
        hub["SignalR hub<br/>/hubs/factory"]
        mw --> ctrl --> svc
        hosted --> svc
        svc --> hub
    end

    db[("PostgreSQL + pgvector<br/>EF Core code-first")]

    subgraph EXT["External AI providers (optional)"]
        llm["LLM<br/>OpenAI · Anthropic · Gemini"]
        emb["Embeddings<br/>OpenAI · Gemini"]
    end

    subgraph PY["rag/ · standalone Python PDF RAG"]
        pycli["ingest.py · ask.py<br/>FastAPI main.py :8000"]
        qdrant[("Qdrant")]
        pycli --> qdrant
    end

    user --> ui
    ui -->|"fetch /api/*"| rewrite --> mw
    ui -->|"admin actions"| admin --> mw
    svc --> db
    svc -->|"LLM_PROVIDER + key"| llm
    svc -->|"RAG_EMBEDDING_*"| emb
    hub -.->|"no frontend client yet<br/>UI polls REST"| ui
    pycli -.->|"Gemini"| llm
    PY ~~~ BE
```

Notes:

- The browser only ever talks to Next.js. Every `/api/*` call is rewritten to the
  .NET backend ([frontend/next.config.mjs](frontend/next.config.mjs)). The three
  admin routes are Next.js route handlers that forward only the caller's own
  bearer token; the API decides access
  ([frontend/src/lib/adminProxy.ts](frontend/src/lib/adminProxy.ts)).
- The backend owns all data and all AI calls. With no LLM key it returns
  deterministic demo answers; with no embedding key RAG uses a local hash vectorizer.
- The SignalR hub broadcasts `sensorReading`, `lineBoardUpdated` and `floorAlert`,
  but the frontend does not subscribe yet. Panels poll read-only REST endpoints
  instead (`GET /api/sensors/snapshot`, `/api/line-board`, `/api/floor-alerts`).
- `rag/` is **not connected** to the backend. It is a separate tool with its own
  vector store (Qdrant).

---

## 2. Backend layers

The .NET solution in [FactoryBrain.Backend/](FactoryBrain.Backend/) follows a
clean-architecture split. Arrows point at the project being referenced.

```mermaid
flowchart TB
    api["<b>FactoryBrain.Api</b><br/>Controllers · Hubs · Middleware<br/>Program.cs wiring"]
    infra["<b>FactoryBrain.Infrastructure</b><br/>EF Core DbContext · Migrations · Seed<br/>Services · Rag · HostedServices"]
    app["<b>FactoryBrain.Application</b><br/>Service interfaces · DTOs<br/>Validation · Rag scoring and chunking"]
    domain["<b>FactoryBrain.Domain</b><br/>Entities · Enums"]

    api --> infra
    api --> app
    infra --> app
    app --> domain
    infra --> domain
```

| Layer | Holds |
|---|---|
| Domain | Entities such as `SensorReading`, `LineBoardMetric`, `QcDefect`, `FloorAlert`, `ManualDocument`, `PolicyDocument`, `AgentDefinition`, `ApprovalRequest`, `User` |
| Application | `IAskService`, `IRagService`, `IRealtimeNotifier` and other seams, request/response DTOs, FluentValidation rules, `TextChunker`, `HybridScorer`, `RagSynonyms` |
| Infrastructure | `FactoryBrainDbContext`, migrations, `DbInitializer` seed, service implementations, embedding providers, `SimulatorHostedService` |
| Api | Attribute-routed controllers, `FactoryHub`, auth and admin-token filters, global exception handler, rate limiter |

---

## 3. Ask BunonBrain request flow

What happens when someone types a question on **/app/ask**.

```mermaid
sequenceDiagram
    autonumber
    actor U as User
    participant N as Next.js
    participant C as AskController
    participant A as AskService
    participant R as RagService
    participant DB as PostgreSQL + pgvector
    participant L as LLM provider

    U->>N: POST /api/ask {query, filter}
    N->>C: rewrite to backend /api/ask
    C->>A: AskAsync(request)
    A->>R: RetrieveAsync(query, filter, topK=4)
    R->>DB: load chunks (vector + text)
    R-->>A: hits ranked by BM25 + cosine
    alt effective LLM settings are live (saved settings, else startup config)
        A->>L: prompt with hits as evidence
        L-->>A: answer
    else no key, or provider fails
        A->>A: deterministic demo answer from hits
    end
    A-->>C: finding · factors · evidence · recommendation
    C-->>N: JSON
    N-->>U: rendered answer with sources
```

---

## 4. Live floor data

Sensor readings either come from the built-in simulator or are pushed by a real
device or script. Panels on the Overview page poll for updates.

```mermaid
flowchart LR
    sim["SimulatorHostedService<br/>SIMULATOR_ENABLED=true"]
    dev["Device or script<br/>npm run sensor:live"]
    sens["SensorService"]
    lb["LineBoardService"]
    fa["FloorAlertService"]
    db[("PostgreSQL")]
    hub["SignalR /hubs/factory"]
    ui["Overview panels<br/>LineBoard · QC · Brief · Alerts"]

    sim -->|"every 5 s"| sens
    sim -->|"every 30 s"| lb
    dev -->|"POST /api/sensors/live"| sens
    sens --> db
    lb --> db
    fa --> db
    sens & lb & fa --> hub
    ui -->|"poll REST<br/>line board 6 s · alerts 7 s"| db
```

Details: [docs/SENSORS.md](docs/SENSORS.md) (local docs folder).

---

## 5. Backend RAG

The RAG that powers **Ask** lives inside the backend and stores vectors in the
same PostgreSQL database.

```mermaid
flowchart LR
    src["Manuals and policies<br/>ManualDocument · PolicyDocument"]
    chunk["TextChunker"]
    embed{"EmbeddingProviderResolver"}
    oa["OpenAI"]
    gm["Gemini"]
    hash["Local hash<br/>no key needed"]
    store[("DocumentChunks<br/>pgvector column")]
    score["HybridScorer<br/>BM25 + vector"]
    ask["AskService"]

    src -->|"POST /api/rag/ingest · reindex"| chunk --> embed
    embed --> oa & gm & hash
    oa & gm & hash --> store
    ask -->|"query"| score
    store --> score
    score -->|"top-k hits"| ask
```

- Weights for BM25 and vector scoring are configurable (`RagConfig`,
  `RAG_HYBRID_*` env vars). If the active embedder's dimension does not match
  the stored column, ranking falls back to BM25 only instead of failing.
- `RagHealthMonitor` re-probes the embedding provider every
  `RAG_HEALTH_INTERVAL_SECONDS` (default 300). It only probes: it never
  re-embeds.
- Index management: `/api/rag/{ingest,reindex,documents,status}`. Ingest and
  reindex need the Admin role. Reindexing (and any embedding provider / model /
  dimension change) is plan → confirm → apply: `GET /api/rag/reindex/plan`
  reports documents, chunks, the vector column before → after and the cost
  (or that it can't be estimated); `POST /api/rag/reindex {confirmPlanId}`
  applies it in one transaction. Startup never resizes the column or
  re-embeds.

### LLM and embedding settings

One `IAiSettingsProvider` decides the LLM and embedding settings for Ask,
agents and the embedder, per group: runtime override > saved settings
(`ai_settings` table, once that group has been saved) > startup configuration
(env, read once) > defaults. Saved API keys are AES-256-GCM encrypted with
`AI_SETTINGS_ENCRYPTION_KEY`, which never goes in the database. Writes lock the
single row, are audited, and apply from the next request (other instances:
within `AI_SETTINGS_REFRESH_SECONDS`). Design: `docs/AI_SETTINGS_DESIGN.md`.

---

## 6. Standalone Python RAG (`rag/`)

A separate PDF question-answering pipeline: LangChain, Qdrant and Gemini.
It does not share data with the backend.

```mermaid
flowchart LR
    pdf["PDF in rag/data/"]
    subgraph ING["ingest.py · steps 1-6"]
        s1["1 Load PDF"] --> s2["2 Extract text<br/>+ font repair"] --> s3["3 Clean"] --> s4["4 Chunk"] --> s5["5 Embed<br/>MiniLM · bge-m3 · Gemini"]
    end
    q[("Qdrant<br/>embedded or server")]
    subgraph ASK["ask.py · steps 7-11"]
        s7["7 Question"] --> s8["8 Embed query"] --> s9["9 Retrieve top-k"] --> s10["10 Gemini answer"] --> s11["11 Answer + sources"]
    end

    pdf --> s1
    s5 -->|"6 Store"| q
    q --> s9
```

- CLI: `python ingest.py --pdf data/file.pdf --recreate`, then `python ask.py "question"`.
- HTTP: `uvicorn main:app --port 8000` serves the same pipeline with one Qdrant
  collection per knowledge base. This needs Qdrant in server mode.
- Setup and details: [rag/README.md](rag/README.md).

---

## 7. Security boundaries

```mermaid
flowchart LR
    b["Browser"]
    n["Next.js server<br/>rewrite + pass-through, no credentials of its own"]
    api["ASP.NET Core API<br/>fallback policy: authenticated<br/>Operator / Admin role policies"]

    b -->|"Authorization: Bearer (in-memory access token)<br/>HttpOnly refresh cookie on /api/auth only"| n
    n -->|"same headers forwarded"| api
```

| Control | Where |
|---|---|
| Login, refresh, logout, current user | `/api/auth/*`. 15-minute access token in the response body (kept in memory by the frontend), 7-day rotating refresh token in an HttpOnly cookie scoped to `/api/auth`. `JWT_SIGNING_KEY` (32+ bytes) is required outside Development |
| Default access | Fallback authorization policy: every endpoint needs a valid JWT unless it is `[AllowAnonymous]` (`/health`, login, refresh, logout) |
| Roles | `Operator` policy (Admin or Operator) on operational writes; `Admin` policy on `POST /api/settings/llm`, `/api/rag/ingest`, `/api/rag/reindex`, the QC defect taxonomy (`POST/PUT /api/qc/categories`), production lines, shifts and styles (`POST/PUT /api/production/{lines,shifts,styles}`). The full matrix is asserted by `FactoryBrain.Api.Tests/EndpointAccessMatrixTests.cs` |
| Login brute force | Per-IP fixed-window rate limiter (`login` policy) |
| Simulation abuse | `tick` bounded (validator + at most 60 ticks per request); per-user rate limits on `POST /api/sensors/ingest`, `/api/line-board/refresh` (20/min) and `/api/sensors/live` (120/min) |
| Input validation | FluentValidation filter on every controller |
| Error leakage | `GlobalExceptionHandler` returns ProblemDetails with a trace id. The exception message is included only in Development |
| Python RAG service | Optional `PYRAG_API_KEY`, sent as `X-Rag-Api-Key` |

Known open findings are tracked in [docs/SECURITY_ANALYSIS.md](docs/SECURITY_ANALYSIS.md) (local docs folder).

---

## 8. Frontend pages and the API

Every page under `/app` reads and writes through the API with the signed-in
user's bearer token (`src/lib/api.ts` → `apiFetch`). Navigation, page access and
buttons follow the role (`src/components/layout/nav.ts`, `RequireRole`), but
the API's policies are the actual control.

| Page | Endpoints | Writes need |
|---|---|---|
| Overview `/app` | `GET /api/sensors/snapshot` (polled), `/api/line-board`, `/api/brief/morning`, `/api/approvals`, `/api/floor-alerts`, `/api/qc/summary?days=7`; `POST /api/sensors/ingest`, `/api/line-board/refresh` | Operator |
| Approvals | `GET /api/approvals`, `POST /api/approvals/{id}/approve\|reject`, `GET /api/audit?entityType=approval` (detail drawer) | Operator; High risk: Admin |
| Floor alerts `/app/activity` | `GET /api/floor-alerts`, `PATCH /api/floor-alerts/{id}` | Operator |
| Production `/app/production` (+ `/lines/{id}`, `/orders/{id}`, `/plans/{id}`, `/balancing/{id}`) | `GET /api/production/{config,lines,shifts,styles,orders,plans,output,summary,observations,layouts/current,balance/proposals}`, `/lines/{id}/detail`, `/orders/{id}/risk`; `POST /api/production/{orders,plans,output,output/import,observations,balance/analyze}`, plan `submit · withdraw · activate · complete · cancel`, `changes/{id}/resolve · withdraw`, proposal `submit · apply`; `PUT` orders, plans, `layouts/current` | Operator; lines, shifts, styles: Admin |
| Quality inspections `/app/qc`, `/app/qc/{id}` | `GET /api/qc/inspections` (search, filters, pages), `/api/qc/inspections/{id}`, `/{id}/image`, `/api/qc/summary`, `/api/qc/categories`; `POST /api/qc/inspections/{id}/review`, `/{id}/retry`; `POST/PUT /api/qc/categories` | Operator; taxonomy: Admin |
| Sensors | `GET /api/sensors/ingest`, `/latest`, `/snapshot`; `POST /api/sensors/ingest` | Operator |
| Ask | `POST /api/ask` (optional department/category filter), `GET /api/settings/llm` | any role |
| Knowledge base | `GET /api/rag/documents`, `/api/rag/status`, `/api/settings/embedding`; `POST /api/rag/ingest`; `GET /api/rag/reindex/plan` → `POST /api/rag/reindex {confirmPlanId}` | Admin |
| Agents | `GET /api/agents`, `POST /api/agents/{id}/run` | Operator |
| Inspect a garment `/app/vision` | `GET /api/qc/vision/status`, `/api/qc/categories`, `/api/line-board`; `POST /api/qc/inspections` (multipart, `Idempotency-Key`) | Operator |
| Audit log | `GET /api/audit` | Admin (page hidden for others) |
| Settings → AI & models | `GET/POST /api/settings/llm`, `GET/POST /api/settings/embedding`, `POST /api/settings/embedding/plan`, reindex as above | Admin |
| Settings → Users | `GET /api/users`, `PATCH /api/users/{id}/role` | Admin |

Insights, Factory graph and Integrations have no API behind them yet; they
render a browser-only demo dataset and say so on the page. Documents can't be
deleted or edited through the API yet, and agent runs are rule-based (the
configured language model is used by Ask only).


---

## 9. Quality inspection (QC & defect detection)

Garment images go through submit → analyse → review, and quality metrics are
calculated from the reviewed records. The workflow lives in
`FactoryBrain.Infrastructure/Qc/` and `QcInspectionsController`; the pure
rules (image sniffing, lifecycle, provider-output normalisation, metrics) are
in `FactoryBrain.Application/Qc/` and unit-tested.

```mermaid
sequenceDiagram
    autonumber
    actor O as Operator
    participant W as /app/vision
    participant C as QcInspectionsController
    participant S as QcInspectionService
    participant F as Image store (files)
    participant DB as PostgreSQL
    participant K as QcInspectionWorker
    participant P as Vision provider

    O->>W: photo + product, stage, line, order, categories
    W->>C: POST /api/qc/inspections (multipart, Idempotency-Key)
    C->>S: validate bytes (JPEG/PNG/WebP, size, dimensions) + metadata
    S->>F: save under a server-generated key
    S->>DB: qc_inspections (Pending, or Failed "provider_not_configured") + audit
    C-->>W: 201 inspection
    K->>DB: claim (UPDATE … FOR UPDATE SKIP LOCKED) → Processing
    K->>P: image + requested taxonomy
    alt result
        P-->>K: labels, confidence, boxes / masks, model, version
        K->>DB: findings (validated, mapped, ≥ threshold) → Completed + audit
    else timeout / error / invalid output
        K->>DB: Failed + code + safe message + audit (retryable)
    end
    O->>C: POST /{id}/review {expectedVersion, decisions, additions, outcome}
    C->>S: xmin check → Reviewed + audit (+ optional approval)
```

**Lifecycle.** `Pending → Processing → Completed | Failed`, `Failed → Pending`
(retry), `Completed | Failed | Reviewed → Reviewed` (review, manual review,
corrected review). A crashed worker's lease is reclaimed after the provider
timeout + 2 minutes, at most `QC_MAX_ATTEMPTS` times, then marked failed.

**Providers** (`QC_VISION_PROVIDER`, read at startup; see `.env.example`):

| Mode | What runs | Localisation | Confidence |
|---|---|---|---|
| `llm` | The vision-capable model of the configured LLM provider (OpenAI, Anthropic, Gemini), key from the AI settings | approximate boxes (`box_2d`) | the model's own estimate, not calibrated |
| `http` | Your detection model behind `QC_VISION_HTTP_URL` | boxes and polygons, if the model returns them | the model's score |
| unset | Nothing: inspections are stored as failed with `provider_not_configured`; inspectors can still review them manually | — | — |

No code path produces a prediction a provider did not return. Tests use a
deterministic detector served by the outbound-HTTP guard (backend) or the e2e
harness, and its model name says it is a test model.

**`http` contract.** `POST QC_VISION_HTTP_URL` with optional
`Authorization: Bearer QC_VISION_HTTP_API_KEY`:

```json
{ "image": { "contentType": "image/jpeg", "base64": "…", "width": 1024, "height": 768 },
  "productType": "t-shirt", "stage": "end_of_line",
  "categories": [ { "code": "skipped_stitch", "name": "Skipped stitch", "description": "…" } ] }
```

Response (coordinates are fractions of width / height, origin top-left;
`box`, `polygon`, `severity`, `description` optional):

```json
{ "model": "garment-defects-yolov8", "modelVersion": "2026.10.1", "localization": true,
  "defects": [ { "label": "skipped_stitch", "confidence": 0.91,
                 "box": { "x": 0.12, "y": 0.30, "width": 0.08, "height": 0.05 },
                 "polygon": [[0.12, 0.30], [0.20, 0.30], [0.20, 0.35]],
                 "severity": "major", "description": "…" } ] }
```

Anything else is stored as `invalid_output`. Out-of-range values are dropped,
never repaired: a box outside the image is removed (the defect is kept without
a location), a confidence outside 0–1 removes the defect, a label matching no
category is kept as "unmapped" for the inspector to correct or reject.

**Data.** `qc_inspections` (context, storage key, SHA-256, dimensions, status,
provider / model / version, threshold, review, `xmin` concurrency token,
per-user unique `Idempotency-Key`), `qc_inspection_findings` (the original
prediction is never modified; decisions and corrections are separate columns),
`qc_defect_categories` (seeded once with 13 garment defects, then managed by
Admins; deactivated, never deleted). Check constraints keep confidence and
boxes in range at the database level.

**Metrics** (`GET /api/qc/summary`): only reviewed inspections count. A
verified defect is an inspector-added finding or an accepted / corrected
prediction. Defective rate = reviewed inspections with ≥ 1 verified defect ÷
reviewed × 100; DHU = verified defects ÷ reviewed × 100. Days are UTC
submission days. Every response carries these definitions and the sample sizes.

**Security.** Viewers read; Operators submit, review and retry; Admins manage
the taxonomy. Images are served only through `GET /api/qc/inspections/{id}/image`
to signed-in users, with the validated type, `nosniff` and a sandbox CSP.
Storage keys are re-validated on every read and must resolve inside the store
root. Provider keys and response bodies are never logged or stored.

**Retired.** `/api/vision/analyze` (four staged files mapped to fixed labels
and confidences that also queued approvals and floor alerts) now answers
`410 Gone`. Existing `vision_results` rows are kept. `GET /api/qc/defects` and
`POST /api/qc/flag` (the seeded weekly operation × line grid) are still served
for API clients but no longer shown in the UI.

---

## 10. Production planning & line efficiency

Orders, plans, output and line analysis for sewing lines. The plant enters
its own lines, shift calendar and styles (with the factory-approved SMV and
operation bulletin); nothing is seeded, and every figure is calculated by the
API from those persisted rows at read time. The pure rules live in
`FactoryBrain.Application/Production/` (unit-tested); persistence and workflow
in `FactoryBrain.Infrastructure/Production/`; HTTP in `ProductionController`.

```mermaid
flowchart LR
    setup["Setup (Admin)<br/>lines · shifts · styles + SMV bulletin"]
    order["Order (Operator)"]
    draft["Plan: draft<br/>edited freely, audited"]
    queue["Approval queue<br/>/api/approvals (medium risk)"]
    live["Plan: active"]
    out["Output records<br/>manual · CSV import · integration"]
    read["Summary · line detail · order risk<br/>computed at read time"]
    setup --> order --> draft -->|"submit"| queue
    queue -->|"approved, then activate<br/>(another person)"| live
    queue -->|"rejected → back to draft"| draft
    live -->|"change request"| queue
    out --> read
    live --> read
```

**Plan lifecycle.** `draft → pending_approval → active → completed`;
`cancelled` from any non-final state; a pending plan can be withdrawn to
draft. Submitting queues an approval through the existing queue (an Insight
plus an `ApprovalRequest`); nothing in this module decides it. Activation
checks the decision, the separation of duties (`PRODUCTION_REQUIRE_SEPARATE_APPROVER`,
default on: the approver may not be the submitter) and that no other active
plan overlaps the line's dates, all under a per-line advisory lock. Changing a
live plan's inputs records a pending change with its own approval; the live
values change only when the approved change is applied (a change requested
against an older version is closed as superseded; a pending change can be
withdrawn). Notes-only edits apply directly. Every step writes
`production_plan_changes` (before / after JSON) and an audit row.

**Output.** One record = good / rework / reject pieces at one stage
(`cutting`, `sewing_input`, `sewing_output`, `finishing`, `packing`) for one
order on one line over one interval. Validation: end after start, at most
`PRODUCTION_MAX_INTERVAL_HOURS`, not in the future (5 min tolerance), not
older than `PRODUCTION_MAX_BACKFILL_DAYS`, not spanning two shifts, downtime ≤
interval. Production date and shift come from the interval start (an
overnight shift belongs to the date it started). Deduplication: `(source,
sourceRef)` is unique (a repeat with the same values returns the stored
record with `Idempotent-Replayed: true`); live records of one line, order and
stage may not overlap (checked under the line lock, backed by a partial
unique index). Corrections insert a replacement and mark the original
superseded, so late and corrected records change every summary that covers
them. CSV import is all-or-nothing; rows without a `source_ref` are keyed by
their natural key so a re-import is recognised. There is no ERP/MES
connector: a future one posts to `POST /api/production/output` with
`source=integration` and its own `sourceRef`; `GET /api/production/config`
reports it as not connected until such records arrive.

**Formulas** (units: pieces, minutes, pieces/hour; SMV minutes/piece;
percentages rounded half away from zero to 1 decimal; a ratio with a
non-positive denominator is not calculable and omitted, never 0):

| Metric | Definition |
|---|---|
| Hourly target | operators × 60 × target efficiency ÷ SMV |
| Planned output | target rate during the plan's scheduled minutes (shifts minus planned breaks, plus planned overtime), capped at the planned quantity; even spread when the style has no SMV |
| Output variance | actual − planned good sewing output, same line, order and window |
| Achievement % | actual ÷ planned × 100 (only when planned > 0); not efficiency |
| Line efficiency % | Σ good × SMV ÷ Σ operators present × (interval − planned break) × 100; downtime stays in the denominator (a second figure removes it); records without operators or SMV are excluded and counted |
| Comparison window | planned output runs to the line's last recorded interval (or now), so an hour not yet reported isn't counted as a shortfall; the response says how far it runs |
| WIP | good out of one stage − (good + reject) out of the next, per order, as of now; buffers are never summed; negative balances are reported as inconsistent |
| Line loading % | scheduled plan line-minutes ÷ calendar shift line-minutes of the active lines |
| Required rate | remaining quantity ÷ remaining scheduled line-hours before the end of the due date |
| Observed rate | good output ÷ scheduled line-hours over the last `PRODUCTION_RATE_LOOKBACK_DAYS` production days; the projection range uses the lowest / highest daily rate |
| Delay risk | high if the projection misses the due time, medium if the required rate is within `PRODUCTION_RISK_SLACK_PCT` of the observed rate, else low; `insufficient_data` without an active plan or recorded output. A rule-based level, not a probability |
| Bottleneck | the time-studied operation with the lowest capacity (3600 × operators ÷ mean cycle seconds), with queue counts and line output as supporting or contradicting evidence; without time studies, the busiest station of the recorded layout at standard minutes, labelled as such; otherwise none |
| Line balance | station load = Σ SMV ÷ stations sharing the operation; cycle time = largest load; balance efficiency = Σ SMV ÷ (stations × cycle) × 100; smoothness = √Σ(cycle − load)² |

**Line balancing.** Ranked Positional Weight (operation SMV + all
successors' SMV) with precedence, one machine type per workstation, parallel
stations for operations longer than the cycle time, and the line's declared
workstations as the limit. The smallest feasible cycle time is found by a
bounded two-pass search from the lower bound Σ SMV ÷ workstations; the result
is a heuristic and says so (the lower bound is reported). Missing or invalid
bulletins, cycles and too few workstations for the machine types are
explained and nothing is stored. A feasible result is stored as a proposal
next to the current layout; it is submitted as a **high-risk** approval
(Admins decide), and applying it records it as the line's current layout only
if the bulletin and the layout it was compared with are unchanged. It never
reassigns people, changes schedules or sends machine commands.

**Data.** `production_lines`, `production_shifts`, `production_styles`,
`production_style_operations`, `production_orders`, `production_plans`,
`production_plan_changes`, `production_output_events`,
`production_operation_observations`, `production_line_layouts` (+
`_stations`), `production_balance_proposals` (migration
`AddProductionPlanning`, additive only). Check constraints keep quantities,
intervals, percentages and codes valid; `xmin` guards concurrent edits.
