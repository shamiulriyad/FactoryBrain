# rag/

**Reserved for future RAG-specific work.** Nothing in this directory runs today.

It exists so that standalone retrieval-augmented-generation work has a clear
home when it starts. Examples would be an ingestion pipeline, evaluation
corpora, or an external vector-store service. Leave this directory empty
(apart from this file) until that work is scoped.

## Existing RAG code (live, not here)

The RAG features that ship today are part of the running applications. They
stay where they are:

| Area | Location | Role |
|---|---|---|
| Backend retrieval | `backend/FactoryBrain.Infrastructure/Rag/` | `RagService`, embedding providers (local hash, OpenAI, Gemini), health monitor, embedding-column admin |
| Backend scoring / chunking | `backend/FactoryBrain.Application/Rag/` | `TextChunker`, `HybridScorer`, `RagSynonyms` |
| Backend config | `backend/FactoryBrain.Infrastructure/Configuration/RagConfig.cs` | Top-k, thresholds, BM25/vector weights |
| Backend API | `backend/FactoryBrain.Api/Controllers/RagController.cs` | `/api/rag/ingest`, `/api/rag/reindex`, `/api/rag/documents`, `/api/rag/status` |
| Frontend proxies | `frontend/src/app/api/rag/{ingest,reindex}/route.ts` | Forward to the backend and attach the server-only `ADMIN_API_TOKEN` |
| Frontend in-process index | `frontend/src/services/rag/` | Chunker and vector store used by `frontend/src/services/ask.service.ts` |
| Eval script | `frontend/scripts/test-rag.mjs` | Golden-set check against the live `/api/ask` |
| Research notes | [`docs/rag-research.md`](../docs/rag-research.md) | Design rationale for chunking and hybrid scoring |

Before adding anything here, decide whether it replaces or extends the code
above. Update this README when that decision is made.
