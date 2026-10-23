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
        admin["Admin proxy routes<br/>settings/llm · rag/ingest · rag/reindex<br/>adds X-Admin-Token"]
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
  admin routes are real Next.js route handlers, so the server can attach
  `ADMIN_API_TOKEN` without exposing it to the browser
  ([frontend/src/lib/adminProxy.ts](frontend/src/lib/adminProxy.ts)).
- The backend owns all data and all AI calls. With no LLM key it returns
  deterministic demo answers; with no embedding key RAG uses a local hash vectorizer.
- The SignalR hub broadcasts `sensorReading`, `lineBoardUpdated` and `floorAlert`,
  but the frontend does not subscribe yet. Panels poll REST endpoints instead.
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
    alt LLM_PROVIDER and LLM_API_KEY set
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
  `RAG_HEALTH_INTERVAL_SECONDS` (default 300) and runs a one-shot reindex when
  the provider recovers from degraded mode.
- Index management: `/api/rag/{ingest,reindex,documents,status}`. Ingest and
  reindex need the admin token.

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
    n["Next.js server<br/>holds ADMIN_API_TOKEN"]
    api["ASP.NET Core API"]

    b -->|"HttpOnly JWT cookie"| n
    n -->|"cookie forwarded"| api
    n -->|"X-Admin-Token<br/>admin routes only"| api
```

| Control | Where |
|---|---|
| Login, refresh, logout, current user | `/api/auth/*`, JWT in an HttpOnly cookie. `JWT_SIGNING_KEY` (32+ bytes) is required in Production |
| Admin-only routes | `POST /api/settings/llm`, `/api/rag/ingest`, `/api/rag/reindex`, guarded by `ADMIN_API_TOKEN` (`AdminTokenAuthorizationFilter`) |
| Login brute force | Per-IP fixed-window rate limiter (`login` policy) |
| Input validation | FluentValidation filter on every controller |
| Error leakage | `GlobalExceptionHandler` returns ProblemDetails with a trace id. The exception message is included only in Development |
| Python RAG service | Optional `PYRAG_API_KEY`, sent as `X-Rag-Api-Key` |

Known open findings are tracked in [docs/SECURITY_ANALYSIS.md](docs/SECURITY_ANALYSIS.md) (local docs folder).
