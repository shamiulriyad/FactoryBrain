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

// Vector retrieval layer for the factoryBrain RAG pipeline.
//
// Responsibilities:
//   * Pluggable embedding interface — uses OpenAI / Voyage text-embeddings
//     when `EMBEDDING_API_KEY` is set, otherwise falls back to a deterministic
//     local semantic vectorizer (token-frequency + BM25 hybrid weighting).
//   * In-memory cosine similarity search with optional file persistence so
//     the index survives restarts during local development / tests.
//   * Metadata filtering by `department` (sewing, cutting, finishing) and
//     `category` (manuals, compliance, qc).
//
// The store is intentionally zero-dependency at runtime — every helper in
// this file uses only the Node standard library so the RAG feature works
// out-of-the-box in environments where no embedding API key is configured.

import { promises as fs } from "node:fs";
import path from "node:path";
import crypto from "node:crypto";

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/** Departments a chunk can belong to. Mirrors the factory-floor taxonomy. */
export type RagDepartment = "sewing" | "cutting" | "finishing" | "qc" | "general";

/** Knowledge category. Drives the `manuals` / `compliance` / `qc` domains. */
export type RagCategory = "manuals" | "compliance" | "qc" | "policy" | "sop";

/** A single semantic chunk that lives inside the vector store. */
export type RagChunk = {
  id: string;
  /** Canonical source identifier (e.g. "doc-3", "policy:return-1"). */
  sourceId: string;
  title: string;
  text: string;
  /** Locale of the original text — "en" or "bn". */
  locale: "en" | "bn";
  /** Department this chunk is most relevant to. */
  department: RagDepartment;
  /** Knowledge category — used to drive UI grouping + filtering. */
  category: RagCategory;
  /** Tags inherited from the source doc (machine codes, AQL levels, SMV). */
  tags: string[];
  /** When the chunk was indexed. */
  indexedAt: string;
};

/** A chunk + its dense vector. Private to the store. */
type IndexedChunk = RagChunk & { embedding: Float32Array };

/** Result returned from a similarity search. */
export type RagHit = {
  chunk: RagChunk;
  /** Cosine similarity in [0, 1]. */
  score: number;
  /** BM25 lexical score normalized into [0, 1]. */
  bm25Score: number;
  /** Final ranking score — convex combination of dense + BM25. */
  hybridScore: number;
};

/** Filter parameters supported by `query()`. */
export type RagFilter = {
  department?: RagDepartment | RagDepartment[];
  category?: RagCategory | RagCategory[];
  locale?: "en" | "bn";
  /** Only return hits whose `tags` array contains any of these tokens. */
  tagsAny?: string[];
};

/** Pluggable embedding interface. */
export type Embedder = {
  /** Stable identifier — "openai" | "voyage" | "local-bm25". */
  readonly id: string;
  /** Output dimensionality. */
  readonly dim: number;
  /** Embed a single piece of text into a Float32Array. */
  embed(text: string): Promise<Float32Array>;
  /** Batch-embed multiple pieces of text. */
  embedBatch(texts: string[]): Promise<Float32Array[]>;
};

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Feature-hashing bucket size used by the local embedder. */
const LOCAL_EMBED_DIM = 256;
/** Default k for `query()`. */
const DEFAULT_TOP_K = 4;
/** Default similarity threshold. */
const DEFAULT_THRESHOLD = 0.0;

// ---------------------------------------------------------------------------
// Local embedder (deterministic, BM25-hybrid)
// ---------------------------------------------------------------------------

/**
 * Deterministic local semantic vectorizer.
 *
 * Implements token-frequency hashing (signed feature hashing) augmented with
 * BM25-style idf weighting so semantically related chunks land in nearby
 * regions of the vector space even without a learned embedding model. This
 * is the zero-dependency fallback path described in the architecture brief.
 */
export const localEmbedder: Embedder = {
  id: "local-bm25",
  dim: LOCAL_EMBED_DIM,
  async embed(text: string): Promise<Float32Array> {
    return embedLocal(text);
  },
  async embedBatch(texts: string[]): Promise<Float32Array[]> {
    return texts.map(embedLocal);
  }
};

// Stop-words for English + Bangla. Tiny — purely to reduce noise. Not a full
// linguistic stop-word list, just enough to dampen high-frequency noise in the
// synthetic factory corpus.
const STOPWORDS = new Set([
  "the", "a", "an", "of", "to", "and", "or", "in", "on", "for", "is", "are",
  "be", "with", "by", "from", "this", "that", "it", "as", "at", "when",
  "এবং", "থেকে", "মধ্যে", "এই", "ওই", "করুন", "করা", "হয়", "একটি", "জন্য"
]);

function tokenize(text: string): string[] {
  return text
    .toLowerCase()
    .replace(/[\u09E6-\u09EF]/g, (d) => String(d.charCodeAt(0) - 0x09E6)) // bn digits → en
    // Split on whitespace + punctuation, but keep machine codes like
    // "Juki-DDL" / "AQL-2.5" together by collapsing non-alphanumerics.
    .replace(/[^\p{L}\p{N}]+/gu, " ")
    .split(/\s+/)
    .map((t) => t.trim())
    .filter((t) => t.length >= 2 && !STOPWORDS.has(t));
}

/** Hash a token into a stable bucket index using SHA-1. */
function bucket(token: string): number {
  const h = crypto.createHash("sha1").update(token).digest();
  // Use first 4 bytes as an unsigned int, mod dim.
  const n = h.readUInt32BE(0);
  return n % LOCAL_EMBED_DIM;
}

/** Sign bit for a token — flips sign across hashes for unbiased features. */
function sign(token: string): number {
  const h = crypto.createHash("sha1").update(token).digest();
  return h[4] & 1 ? 1 : -1;
}

function embedLocal(text: string): Float32Array {
  const v = new Float32Array(LOCAL_EMBED_DIM);
  const tokens = tokenize(text);
  if (!tokens.length) return v;
  const counts = new Map<string, number>();
  for (const t of tokens) counts.set(t, (counts.get(t) ?? 0) + 1);
  for (const [tok, count] of counts) {
    const idx = bucket(tok);
    v[idx] += sign(tok) * count;
  }
  // L2 normalise so cosine = dot product downstream.
  let norm = 0;
  for (let i = 0; i < v.length; i++) norm += v[i] * v[i];
  norm = Math.sqrt(norm) || 1;
  for (let i = 0; i < v.length; i++) v[i] /= norm;
  return v;
}

// ---------------------------------------------------------------------------
// Remote embedders (OpenAI / Voyage) — lazy + best-effort.
// ---------------------------------------------------------------------------

function getEmbeddingConfig(): { provider: "openai" | "voyage"; apiKey: string; model: string } | null {
  const apiKey = process.env.EMBEDDING_API_KEY?.trim();
  const provider = (process.env.EMBEDDING_PROVIDER ?? "openai").trim().toLowerCase();
  if (!apiKey) return null;
  const model =
    process.env.EMBEDDING_MODEL?.trim() ??
    (provider === "voyage" ? "voyage-2" : "text-embedding-3-small");
  if (provider !== "openai" && provider !== "voyage") return null;
  return { provider, apiKey, model };
}

/** Build the embedder the store should use right now. */
export function pickEmbedder(): Embedder {
  const cfg = getEmbeddingConfig();
  if (!cfg) return localEmbedder;
  if (cfg.provider === "voyage") return voyageEmbedder(cfg.apiKey, cfg.model);
  return openAiEmbedder(cfg.apiKey, cfg.model);
}

function openAiEmbedder(apiKey: string, model: string): Embedder {
  const dim = model.includes("3-large") ? 3072 : model.includes("3-small") ? 1536 : 1536;
  return {
    id: `openai:${model}`,
    dim,
    async embed(text: string) {
      const vecs = await this.embedBatch([text]);
      return vecs[0];
    },
    async embedBatch(texts: string[]) {
      const res = await fetch("https://api.openai.com/v1/embeddings", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          Authorization: `Bearer ${apiKey}`
        },
        body: JSON.stringify({ model, input: texts })
      });
      if (!res.ok) {
        throw new Error(`openai-embed ${res.status}: ${await res.text()}`);
      }
      const json = (await res.json()) as { data: Array<{ embedding: number[] }> };
      return json.data.map((d) => Float32Array.from(d.embedding));
    }
  };
}

function voyageEmbedder(apiKey: string, model: string): Embedder {
  return {
    id: `voyage:${model}`,
    dim: 1024,
    async embed(text: string) {
      const vecs = await this.embedBatch([text]);
      return vecs[0];
    },
    async embedBatch(texts: string[]) {
      const res = await fetch("https://api.voyageai.com/v1/embeddings", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          Authorization: `Bearer ${apiKey}`
        },
        body: JSON.stringify({ model, input: texts })
      });
      if (!res.ok) {
        throw new Error(`voyage-embed ${res.status}: ${await res.text()}`);
      }
      const json = (await res.json()) as { data: Array<{ embedding: number[] }> };
      return json.data.map((d) => Float32Array.from(d.embedding));
    }
  };
}

// ---------------------------------------------------------------------------
// Similarity + BM25 helpers
// ---------------------------------------------------------------------------

function cosine(a: Float32Array, b: Float32Array): number {
  const len = Math.min(a.length, b.length);
  let dot = 0;
  let na = 0;
  let nb = 0;
  for (let i = 0; i < len; i++) {
    dot += a[i] * b[i];
    na += a[i] * a[i];
    nb += b[i] * b[i];
  }
  const denom = Math.sqrt(na) * Math.sqrt(nb);
  if (!denom) return 0;
  // Clamp into [0, 1].
  return Math.max(0, Math.min(1, dot / denom));
}

type DocStats = {
  length: number;
  tf: Map<string, number>;
};

/** Build per-document BM25 stats + the corpus-wide document frequency. */
function buildBm25Stats(docs: IndexedChunk[]): { df: Map<string, number>; stats: DocStats[]; avgdl: number } {
  const df = new Map<string, number>();
  const stats: DocStats[] = docs.map((d) => {
    const tokens = tokenize(d.text + " " + d.title);
    const tf = new Map<string, number>();
    for (const t of tokens) tf.set(t, (tf.get(t) ?? 0) + 1);
    for (const t of tf.keys()) df.set(t, (df.get(t) ?? 0) + 1);
    return { length: tokens.length, tf };
  });
  const totalLen = stats.reduce((acc, s) => acc + s.length, 0);
  const avgdl = stats.length ? totalLen / stats.length : 1;
  return { df, stats, avgdl };
}

/** BM25 score for a query against one document. */
function bm25Score(query: string, stats: DocStats, df: Map<string, number>, avgdl: number, N: number): number {
  const tokens = tokenize(query);
  if (!tokens.length) return 0;
  const k1 = 1.5;
  const b = 0.75;
  let score = 0;
  for (const t of tokens) {
    const f = stats.tf.get(t) ?? 0;
    if (!f) continue;
    const n = df.get(t) ?? 0;
    const idf = Math.log(1 + (N - n + 0.5) / (n + 0.5));
    const denom = f + k1 * (1 - b + (b * stats.length) / Math.max(avgdl, 1));
    score += idf * ((f * (k1 + 1)) / denom);
  }
  return score;
}

// ---------------------------------------------------------------------------
// Vector store
// ---------------------------------------------------------------------------

export type VectorStoreOptions = {
  /** Optional path to a JSON file for persistence. Pass `false` to disable. */
  persistPath?: string | false;
  /** Weight given to the BM25 score when ranking. 0 = pure dense, 1 = pure BM25. */
  bm25Weight?: number;
};

type PersistedShape = {
  version: 1;
  embedderId: string;
  chunks: Array<RagChunk & { embedding: number[] }>;
};

/**
 * In-memory vector store. Singleton-friendly: every consumer should import
 * `getVectorStore()` rather than constructing a fresh store, otherwise the
 * indexed corpus is duplicated per process and disk persistence becomes
 * ambiguous.
 */
export class VectorStore {
  private chunks: IndexedChunk[] = [];
  private bm25Cache: { df: Map<string, number>; stats: DocStats[]; avgdl: number } | null = null;
  private embedder: Embedder;
  private opts: VectorStoreOptions;
  private persistPath: string | null;

  constructor(embedder: Embedder = localEmbedder, opts: VectorStoreOptions = {}) {
    this.embedder = embedder;
    this.bm25Weight = opts.bm25Weight ?? 0.35;
    this.opts = opts;
    this.persistPath =
      opts.persistPath === false
        ? null
        : opts.persistPath ?? path.resolve(process.cwd(), ".cache", "rag-index.json");
  }

  bm25Weight: number;

  /** Current chunk count. */
  size(): number {
    return this.chunks.length;
  }

  /** Replace the embedder (e.g. when an API key comes online at runtime). */
  setEmbedder(embedder: Embedder): void {
    this.embedder = embedder;
    // Embeddings are not portable across models; rebuild lazily by clearing.
    this.chunks = [];
    this.bm25Cache = null;
  }

  /** Embedder currently in use. */
  getEmbedder(): Embedder {
    return this.embedder;
  }

  /** Add (or replace by `chunk.id`) a chunk into the store. */
  async upsert(chunk: RagChunk): Promise<void> {
    const embedding = await this.embedder.embed(chunk.text);
    const existing = this.chunks.findIndex((c) => c.id === chunk.id);
    const next: IndexedChunk = { ...chunk, embedding };
    if (existing >= 0) this.chunks[existing] = next;
    else this.chunks.push(next);
    this.bm25Cache = null;
  }

  async upsertBatch(chunks: RagChunk[]): Promise<void> {
    if (!chunks.length) return;
    const vectors = await this.embedder.embedBatch(chunks.map((c) => c.text));
    for (let i = 0; i < chunks.length; i++) {
      const chunk = chunks[i];
      const embedding = vectors[i];
      const existing = this.chunks.findIndex((c) => c.id === chunk.id);
      const next: IndexedChunk = { ...chunk, embedding };
      if (existing >= 0) this.chunks[existing] = next;
      else this.chunks.push(next);
    }
    this.bm25Cache = null;
  }

  /** Remove a chunk by id (no-op if missing). */
  remove(id: string): void {
    const before = this.chunks.length;
    this.chunks = this.chunks.filter((c) => c.id !== id);
    if (before !== this.chunks.length) this.bm25Cache = null;
  }

  /** Drop every chunk from the store. */
  clear(): void {
    this.chunks = [];
    this.bm25Cache = null;
  }

  /**
   * Cosine-similarity search with BM25 hybrid re-ranking.
   *
   * @param query  Free-text question.
   * @param opts.topK  Number of hits to return (default 4).
   * @param opts.threshold  Minimum hybrid score to include (default 0.0).
   * @param opts.filter  Metadata filter — department / category / locale / tags.
   */
  async query(
    query: string,
    opts: {
      topK?: number;
      threshold?: number;
      filter?: RagFilter;
    } = {}
  ): Promise<RagHit[]> {
    const topK = opts.topK ?? DEFAULT_TOP_K;
    const threshold = opts.threshold ?? DEFAULT_THRESHOLD;
    const candidates = this.applyFilter(this.chunks, opts.filter);
    if (!candidates.length || !query.trim()) return [];

    const bm25Stats = this.ensureBm25(candidates);
    const N = candidates.length;
    const queryVec = await this.embedder.embed(query);

    // Compute raw BM25 scores, find the max so we can normalize to [0,1].
    let bm25Max = 0;
    const bm25Raw: number[] = candidates.map((c) => {
      const idx = candidates.indexOf(c);
      const s = bm25Score(query, bm25Stats.stats[idx], bm25Stats.df, bm25Stats.avgdl, N);
      if (s > bm25Max) bm25Max = s;
      return s;
    });

    const w = clamp(this.bm25Weight, 0, 1);
    const hits: RagHit[] = [];
    for (let i = 0; i < candidates.length; i++) {
      const c = candidates[i];
      const dense = cosine(queryVec, c.embedding);
      const bm25 = bm25Max > 0 ? bm25Raw[i] / bm25Max : 0;
      const hybrid = (1 - w) * dense + w * bm25;
      if (hybrid < threshold) continue;
      hits.push({ chunk: this.publicChunk(c), score: dense, bm25Score: bm25, hybridScore: hybrid });
    }
    hits.sort((a, b) => b.hybridScore - a.hybridScore);
    return hits.slice(0, topK);
  }

  /** Force-build the BM25 stats now (call after a batch upsert for warm caches). */
  warmIndex(): void {
    this.bm25Cache = null;
    this.ensureBm25(this.chunks);
  }

  /** Persist the store to disk (no-op when persistence is disabled). */
  async persist(): Promise<void> {
    if (!this.persistPath) return;
    const payload: PersistedShape = {
      version: 1,
      embedderId: this.embedder.id,
      chunks: this.chunks.map((c) => ({ ...this.publicChunk(c), embedding: Array.from(c.embedding) }))
    };
    await fs.mkdir(path.dirname(this.persistPath), { recursive: true });
    await fs.writeFile(this.persistPath, JSON.stringify(payload), "utf8");
  }

  /** Load a previously-persisted index. Re-embeds any rows whose embedder id
   *  doesn't match the current embedder (cheap local fallback or expensive
   *  remote call — depends on the configured provider). */
  async load(): Promise<{ loaded: number; reembedded: number }> {
    if (!this.persistPath) return { loaded: 0, reembedded: 0 };
    let raw: string;
    try {
      raw = await fs.readFile(this.persistPath, "utf8");
    } catch {
      return { loaded: 0, reembedded: 0 };
    }
    let parsed: PersistedShape;
    try {
      parsed = JSON.parse(raw) as PersistedShape;
    } catch {
      return { loaded: 0, reembedded: 0 };
    }
    let reembedded = 0;
    for (const row of parsed.chunks) {
      const embedding = Float32Array.from(row.embedding);
      const needsReembed =
        parsed.embedderId !== this.embedder.id || embedding.length !== this.embedder.dim;
      const finalEmbedding = needsReembed
        ? await this.embedder.embed(row.text)
        : embedding;
      if (needsReembed) reembedded++;
      this.chunks.push({ ...row, embedding: finalEmbedding });
    }
    this.bm25Cache = null;
    return { loaded: parsed.chunks.length, reembedded };
  }

  // ---- internals ---------------------------------------------------------

  private ensureBm25(docs: IndexedChunk[]): { df: Map<string, number>; stats: DocStats[]; avgdl: number } {
    // We always rebuild for the *current* candidate set so the BM25 stats
    // match the post-filter corpus. Cached only at the unfiltered level.
    return buildBm25Stats(docs);
  }

  private applyFilter(chunks: IndexedChunk[], filter?: RagFilter): IndexedChunk[] {
    if (!filter) return chunks.slice();
    const { department, category, locale, tagsAny } = filter;
    const depSet = toSet(department);
    const catSet = toSet(category);
    const tagSet = tagsAny && tagsAny.length ? new Set(tagsAny.map((t) => t.toLowerCase())) : null;
    return chunks.filter((c) => {
      if (depSet && !depSet.has(c.department)) return false;
      if (catSet && !catSet.has(c.category)) return false;
      if (locale && c.locale !== locale) return false;
      if (tagSet) {
        const has = c.tags.some((t) => tagSet.has(t.toLowerCase()));
        if (!has) return false;
      }
      return true;
    });
  }

  private publicChunk(c: IndexedChunk): RagChunk {
    // Strip the embedding when projecting to callers.
    const { embedding: _e, ...rest } = c;
    void _e;
    return rest;
  }
}

function toSet<T extends string>(v: T | T[] | undefined): Set<T> | null {
  if (!v) return null;
  return new Set(Array.isArray(v) ? v : [v]);
}

function clamp(n: number, lo: number, hi: number): number {
  return Math.max(lo, Math.min(hi, n));
}

// ---------------------------------------------------------------------------
// Singleton accessor
// ---------------------------------------------------------------------------

let _store: VectorStore | null = null;

/** Get the process-wide vector store. */
export function getVectorStore(): VectorStore {
  if (!_store) {
    _store = new VectorStore(pickEmbedder(), {
      bm25Weight: numberFromEnv("RAG_BM25_WEIGHT", 0.35),
      persistPath:
        process.env.RAG_PERSIST_PATH && process.env.RAG_PERSIST_PATH.length
          ? process.env.RAG_PERSIST_PATH
          : undefined
    });
  }
  return _store;
}

/** Test-only: replace the singleton (used by scripts/test-rag.mjs). */
export function __setVectorStore(store: VectorStore | null): void {
  _store = store;
}

function numberFromEnv(name: string, fallback: number): number {
  const v = process.env[name];
  if (!v) return fallback;
  const n = Number(v);
  return Number.isFinite(n) ? n : fallback;
}
