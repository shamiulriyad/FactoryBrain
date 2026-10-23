// ─────────────────────────────────────────────────────────────────────────────
// LEGACY — OFFLINE-ONLY FALLBACK
//
// This file used to be the production data plane for /api/ask. Since step 46
// the ASP.NET Core 9 backend (`FactoryBrain.Api/Services/Rag/`) is the
// authoritative RAG path; the Next.js frontend talks to it via the proxy in
// next.config.mjs. The TypeScript implementation is preserved here for
// offline tooling, tests, and local demos where the .NET backend is not
// running — it is NOT loaded by any live /api/ask code path. See
// docs/rag-research.md for the split rationale.
// ─────────────────────────────────────────────────────────────────────────────

// Public re-exports for the RAG module. The rest of the app should import
// from `./services/rag` rather than reaching into the individual files —
// keeps the surface area tidy and lets us refactor internals freely.

export {
  getVectorStore,
  pickEmbedder,
  localEmbedder,
  type RagChunk,
  type RagHit,
  type RagFilter,
  type RagDepartment,
  type RagCategory,
  type Embedder,
  type VectorStoreOptions
} from "./vector-store";

export {
  buildCorpus,
  chunkText,
  estimateTokens,
  type ChunkIngestReport
} from "./chunker";

export {
  ensureIndexed,
  reindex,
  autoIndexOnStartup,
  retrieve,
  retrieveAsEvidence,
  hitsToEvidenceRefs,
  lastIndexedAt,
  lastBuildReport
} from "./indexer";
