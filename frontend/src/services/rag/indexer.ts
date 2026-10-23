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

// Indexer — turns the chunked corpus into a queryable vector index.
//
// Auto-runs on first import (idempotent), can also be invoked manually with
// `reindex()` to force a refresh after source-data changes. The indexer is
// the single source of truth for "what's in the vector store right now" —
// both the ask-service retrieval path and the RAG test script depend on it.

import { buildCorpus, type ChunkIngestReport } from "./chunker";
import { getVectorStore, type RagHit, type RagChunk } from "./vector-store";

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------

type IndexState = {
  /** Wall-clock timestamp of the most recent successful index build. */
  builtAt: string | null;
  /** Counts per source the last time the index was built. */
  report: ChunkIngestReport | null;
  /** True when an auto-index is in progress (avoids duplicate work). */
  building: boolean;
  /** Resolved when the in-flight build finishes (success or failure). */
  current: Promise<void> | null;
};

const state: IndexState = {
  builtAt: null,
  report: null,
  building: false,
  current: null
};

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

/** Last successful build timestamp (ISO) or null if never indexed. */
export function lastIndexedAt(): string | null {
  return state.builtAt;
}

/** Build report from the most recent index, or null if never built. */
export function lastBuildReport(): ChunkIngestReport | null {
  return state.report;
}

/**
 * Index the canonical chunk corpus into the vector store. Idempotent: a
 * second call after a successful build is a no-op. `force: true` rebuilds
 * from scratch (clears existing rows first).
 *
 * Returns when the index is ready to query.
 */
export async function ensureIndexed(opts: { force?: boolean } = {}): Promise<ChunkIngestReport> {
  const store = getVectorStore();
  if (state.current) await state.current;
  if (state.builtAt && !opts.force && store.size() > 0) {
    return state.report ?? { manuals: 0, policies: 0, qc: 0, total: 0 };
  }
  const job = (async () => {
    state.building = true;
    try {
      const { chunks, report } = buildCorpus();
      if (opts.force) store.clear();
      await store.upsertBatch(chunks);
      store.warmIndex();
      await persistSafely();
      state.builtAt = new Date().toISOString();
      state.report = report;
    } finally {
      state.building = false;
    }
  })();
  state.current = job;
  try {
    await job;
  } finally {
    state.current = null;
  }
  return state.report ?? { manuals: 0, policies: 0, qc: 0, total: 0 };
}

/** Force-rebuild the index, clearing existing rows first. */
export async function reindex(): Promise<ChunkIngestReport> {
  return ensureIndexed({ force: true });
}

/**
 * Trigger an auto-index on first import. We deliberately don't await here —
 * the first retrieval call awaits `ensureIndexed()` anyway and we'd rather
 * not block module load. Failures are logged but non-fatal so the rest of
 * the app keeps working.
 */
export function autoIndexOnStartup(): void {
  if (state.builtAt) return;
  void ensureIndexed().catch((err) => {
    // eslint-disable-next-line no-console
    console.error("[rag] auto-index failed:", err);
  });
}

/** Retrieve top-k chunks for a query (with optional metadata filter). */
export async function retrieve(
  query: string,
  opts: { topK?: number; threshold?: number; filter?: Parameters<typeof getVectorStore>[never] extends never ? never : import("./vector-store").RagFilter } = {}
): Promise<RagHit[]> {
  await ensureIndexed();
  return getVectorStore().query(query, opts);
}

/**
 * Retrieve and shape hits as evidence refs that match the
 * `EvidenceRefPublic["domain"] === "manuals"` shape used by the ask service.
 */
export async function retrieveAsEvidence(
  query: string,
  opts: Parameters<typeof retrieve>[1] = {}
): Promise<{ hits: RagHit[]; evidence: import("@/services/types").EvidenceRefPublic[] }> {
  const hits = await retrieve(query, opts);
  const evidence = hitsToEvidenceRefs(hits);
  return { hits, evidence };
}

/** Convert RagHit[] → evidence refs grouped by source id. */
export function hitsToEvidenceRefs(hits: RagHit[]): import("@/services/types").EvidenceRefPublic[] {
  if (!hits.length) return [];
  // Dedupe by source id so the same manual isn't referenced twice in the
  // expanded EvidenceBlock list.
  const bySource = new Map<string, { count: number; score: number; category: RagChunk["category"]; department: RagChunk["department"] }>();
  for (const hit of hits) {
    const sid = hit.chunk.sourceId;
    const prev = bySource.get(sid);
    const score = Math.max(prev?.score ?? 0, hit.hybridScore);
    if (!prev) {
      bySource.set(sid, { count: 1, score, category: hit.chunk.category, department: hit.chunk.department });
    } else {
      prev.count += 1;
      prev.score = score;
    }
  }
  // Build one evidence block per category so the UI can render the badges.
  const byDomain = new Map<string, { count: number; ids: string[]; filter: Record<string, string> }>();
  for (const [sid, info] of bySource.entries()) {
    const domain = mapDomain(info.category);
    const entry = byDomain.get(domain) ?? { count: 0, ids: [], filter: {} as Record<string, string> };
    entry.count += info.count;
    entry.ids.push(sid);
    entry.filter["department"] = info.department;
    byDomain.set(domain, entry);
  }
  const evidence: import("@/services/types").EvidenceRefPublic[] = [];
  for (const [domain, entry] of byDomain.entries()) {
    evidence.push({
      domain: domain as import("@/services/types").EvidenceRefPublic["domain"],
      count: entry.count,
      previewIds: entry.ids,
      filter: entry.filter
    });
  }
  return evidence;
}

function mapDomain(category: RagChunk["category"]): "manuals" | "policies" {
  if (category === "compliance" || category === "policy") return "policies";
  return "manuals";
}

// ---------------------------------------------------------------------------
// Persistence
// ---------------------------------------------------------------------------

async function persistSafely(): Promise<void> {
  try {
    await getVectorStore().persist();
  } catch (err) {
    // Persistence is best-effort — don't break the in-memory index if the
    // cache directory isn't writable.
    // eslint-disable-next-line no-console
    console.warn("[rag] persist failed:", (err as Error).message);
  }
}

// ---------------------------------------------------------------------------
// Auto-index trigger
// ---------------------------------------------------------------------------

// Fire and forget — the first retrieval call awaits `ensureIndexed()` so the
// caller still gets a fully-warmed index even if this promise is still
// resolving.
autoIndexOnStartup();
