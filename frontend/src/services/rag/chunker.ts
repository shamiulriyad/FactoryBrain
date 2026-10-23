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

// Chunker for the factoryBrain RAG pipeline.
//
// Ingest structured knowledge from:
//   * src/data/manuals.ts       (factory manuals + sensor logs)
//   * src/data/policies.ts      (compliance + return / supplier policies)
//   * src/data/qc.defects.ts    (QC defect taxonomy)
//
// Output: semantic chunks of ~300–500 tokens with ~50-token overlap. Each
// chunk carries the metadata (department, category, source id, locale) that
// the vector store uses for filtering + UI attribution.
//
// We deliberately don't depend on any third-party tokenizer — a small, fast
// whitespace/punctuation splitter is enough for the synthetic factory corpus.
// Machine codes (e.g. "Juki DDL-8700", "AQL 2.5", "SMV 0.45", error codes
// like "E-12", "NLGI #2") are recognised explicitly so they survive chunk
// boundaries intact.

import { getManualCorpus } from "@/data/manuals";
import type { ManualDocT } from "@/services/sensors.schemas";
import { policies } from "@/data/policies";
import { OPERATIONS, recentWeekStarts, generateOperationLineWeeks } from "@/data/qc.defects";
import { LINES } from "@/services/sensors.server";
import type { RagCategory, RagChunk, RagDepartment } from "./vector-store";

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

/** Default chunk size in (approx) tokens. */
export const CHUNK_TOKEN_TARGET = 400;
/** Overlap in tokens between consecutive chunks. */
export const CHUNK_TOKEN_OVERLAP = 50;
/** Soft min / max — chunks can be slightly outside the band when needed. */
export const CHUNK_TOKEN_MIN = 300;
export const CHUNK_TOKEN_MAX = 500;

// Machine codes / domain tokens we want to preserve verbatim. The chunker
// keeps these tokens glued together across sentence boundaries.
const CODE_TOKENS = [
  // Machine models
  "Juki DDL-8700", "Juki DDL-9000C", "Juki LU-563", "Brother BAS-311H",
  "Brother DB2-B755", "Kansai Special FX-442",
  // AQL levels
  "AQL 0.65", "AQL 1.0", "AQL 1.5", "AQL 2.5", "AQL 4.0",
  // SMV / SAH / NLGI specs
  "SMV 0.45", "SMV 0.55", "SMV 0.65", "SMV 0.75", "SAH 22", "SAH 13",
  "NLGI #2",
  // Error codes (exemplars; preserved when present in source text)
  "E-01", "E-02", "E-12", "Err-401"
];

// ---------------------------------------------------------------------------
// Public types
// ---------------------------------------------------------------------------

export type ChunkIngestReport = {
  manuals: number;
  policies: number;
  qc: number;
  total: number;
};

// ---------------------------------------------------------------------------
// Token estimation
// ---------------------------------------------------------------------------

/** Rough tokenizer: count whitespace-separated spans, treat each as ~1 token.
 *  Good enough for the small synthetic corpus + gives stable chunk sizes. */
export function estimateTokens(text: string): number {
  if (!text) return 0;
  return text.trim().split(/\s+/).length;
}

/**
 * Split text into sentences while protecting machine codes from being split.
 * Codes are temporarily replaced with placeholders, the sentence split runs,
 * then placeholders are restored.
 */
function splitSentences(text: string): string[] {
  if (!text.trim()) return [];
  const placeholders = new Map<string, string>();
  let working = text;
  for (const code of CODE_TOKENS) {
    const ph = `__CODE_${placeholders.size}__`;
    if (working.includes(code)) {
      placeholders.set(ph, code);
      working = working.split(code).join(ph);
    }
  }
  const out: string[] = [];
  // Split on `.`, `!`, `?` followed by whitespace + capital letter / digit.
  // Keep the boundary char by using a lookahead split.
  const parts = working.split(/(?<=[.!?])\s+(?=[A-Z0-9\u0980-\u09FF])/);
  for (const p of parts) {
    const trimmed = p.trim();
    if (!trimmed) continue;
    let restored = trimmed;
    for (const [ph, code] of placeholders) restored = restored.split(ph).join(code);
    out.push(restored);
  }
  return out;
}

// ---------------------------------------------------------------------------
// Sliding-window chunker
// ---------------------------------------------------------------------------

/**
 * Group sentences into chunks whose token count is between MIN and MAX. Each
 * consecutive chunk shares `overlap` tokens with the previous one so context
 * is not lost at boundaries.
 */
export function chunkText(rawText: string, opts: { target?: number; overlap?: number; min?: number; max?: number } = {}): string[] {
  const target = opts.target ?? CHUNK_TOKEN_TARGET;
  const overlap = opts.overlap ?? CHUNK_TOKEN_OVERLAP;
  const minT = opts.min ?? CHUNK_TOKEN_MIN;
  const maxT = opts.max ?? CHUNK_TOKEN_MAX;

  const sentences = splitSentences(rawText);
  if (!sentences.length) return [];

  // Pre-compute token cost per sentence.
  const tokens = sentences.map((s) => estimateTokens(s));

  const chunks: string[] = [];
  let buffer: string[] = [];
  let bufferTokens = 0;
  let lastOverlap: string[] = [];
  let lastOverlapTokens = 0;

  function flush() {
    if (!buffer.length) return;
    chunks.push(buffer.join(" "));
    // Compute the overlap suffix for the next chunk.
    const next: string[] = [];
    let acc = 0;
    for (let i = buffer.length - 1; i >= 0; i--) {
      const s = buffer[i];
      const cost = tokens[globalIdx(i)];
      if (acc + cost > overlap) break;
      next.unshift(s);
      acc += cost;
    }
    lastOverlap = next;
    lastOverlapTokens = acc;
    buffer = [];
    bufferTokens = 0;
  }
  // Map global sentence index → token cost. `tokens` is the per-sentence
  // array; `buffer` accumulates references, but we use the *original* index
  // via a parallel `bufferIdx` array to avoid confusion.
  function globalIdx(localBufIdx: number): number {
    // `buffer` was pushed in order, so the localIdx maps 1:1 with the original
    // sentence index tracked by `cursor`.
    return cursor - buffer.length + localBufIdx;
  }

  let cursor = 0;
  for (let i = 0; i < sentences.length; i++) {
    const s = sentences[i];
    const cost = tokens[i];

    if (buffer.length === 0 && lastOverlap.length) {
      for (const o of lastOverlap) {
        buffer.push(o);
      }
      bufferTokens = lastOverlapTokens;
    }

    if (bufferTokens >= minT) {
      flush();
      // Re-seed with overlap.
      for (const o of lastOverlap) {
        buffer.push(o);
      }
      bufferTokens = lastOverlapTokens;
    }

    // If a single sentence exceeds MAX on its own, push it as a standalone
    // chunk anyway so we never lose a code reference.
    if (cost > maxT && buffer.length === 0) {
      chunks.push(s);
      cursor++;
      continue;
    }

    buffer.push(s);
    bufferTokens += cost;
    cursor++;

    if (bufferTokens >= target) {
      flush();
    }
  }
  flush();

  // Defensive: drop empty chunks.
  return chunks.filter((c) => c.trim().length > 0);
}

// ---------------------------------------------------------------------------
// Department / category inference
// ---------------------------------------------------------------------------

const DEPARTMENT_PATTERNS: Array<{ department: RagDepartment; patterns: RegExp[] }> = [
  {
    department: "sewing",
    patterns: [/\bsewing\b/i, /\bbundle_scan\b/i, /\bneedle\b/i, /\bthread\b/i, /\bbobbin\b/i, /juki/i, /brother/i]
  },
  {
    department: "cutting",
    patterns: [/\bcutting\b/i, /\bspreading\b/i, /\bspread\b/i, /\bmarker\b/i, /\bfabric relaxation\b/i, /\bply\b/i]
  },
  {
    department: "finishing",
    patterns: [/\bfinishing\b/i, /\bbuttonhole\b/i, /\bbutton hole\b/i, /\btop[- ]?stitch\b/i, /\biron\b/i, /\bpress\b/i]
  },
  {
    department: "qc",
    patterns: [/\bqc\b/i, /\bquality\b/i, /\bdefect\b/i, /\baql\b/i, /\binspect\b/i, /\breject\b/i, /\bdefects?\b/i]
  }
];

export function inferDepartment(text: string, fallback: RagDepartment = "general"): RagDepartment {
  for (const { department, patterns } of DEPARTMENT_PATTERNS) {
    if (patterns.some((p) => p.test(text))) return department;
  }
  return fallback;
}

/** Preserve any machine codes from `CODE_TOKENS` that appear in the source. */
function extractTags(text: string, title: string, sourceTags: string[]): string[] {
  const tags = new Set<string>(sourceTags.map((t) => t.toLowerCase()));
  const combined = `${title}\n${text}`;
  for (const code of CODE_TOKENS) {
    if (combined.includes(code)) tags.add(code.toLowerCase());
  }
  // Quick SMV / AQL / SAH capture for additional granularity.
  const smvMatch = combined.match(/\bSMV\s*([0-9]+(?:\.[0-9]+)?)\b/i);
  if (smvMatch) tags.add(`smv:${smvMatch[1]}`);
  const aqlMatch = combined.match(/\bAQL\s*([0-9]+(?:\.[0-9]+)?)\b/i);
  if (aqlMatch) tags.add(`aql:${aqlMatch[1]}`);
  return Array.from(tags);
}

// ---------------------------------------------------------------------------
// Manual → chunks
// ---------------------------------------------------------------------------

function manualsToChunks(): RagChunk[] {
  const out: RagChunk[] = [];
  for (const doc of getManualCorpus()) {
    for (const locale of ["en", "bn"] as const) {
      const title = locale === "en" ? doc.titleEn : doc.titleBn;
      const body = locale === "en" ? doc.bodyEn : doc.bodyBn;
      const pieces = chunkText(body);
      pieces.forEach((piece, idx) => {
        const department = inferDepartment(`${title} ${piece}`, "general");
        const category: RagCategory = doc.source === "sensor_log" ? "sop" : "manuals";
        out.push({
          id: `${doc.id}::${locale}::${idx + 1}`,
          sourceId: doc.id,
          title,
          text: piece,
          locale,
          department,
          category,
          tags: extractTags(piece, title, doc.tags),
          indexedAt: new Date().toISOString()
        });
      });
    }
  }
  return out;
}

// ---------------------------------------------------------------------------
// Policy → chunks
// ---------------------------------------------------------------------------

function policiesToChunks(): RagChunk[] {
  const out: RagChunk[] = [];
  for (const p of policies) {
    for (const locale of ["en", "bn"] as const) {
      const title = locale === "en" ? p.title : p.titleBn;
      const body = locale === "en" ? p.body : p.bodyBn;
      const pieces = chunkText(body);
      pieces.forEach((piece, idx) => {
        out.push({
          id: `${p.id}::${locale}::${idx + 1}`,
          sourceId: p.id,
          title,
          text: piece,
          locale,
          department: inferDepartment(`${title} ${piece}`, "general"),
          category: "compliance",
          tags: extractTags(piece, title, [p.type]),
          indexedAt: new Date().toISOString()
        });
      });
    }
  }
  return out;
}

// ---------------------------------------------------------------------------
// QC defect taxonomy → chunks
// ---------------------------------------------------------------------------

function qcToChunks(): RagChunk[] {
  const out: RagChunk[] = [];
  const weekStarts = recentWeekStarts(2);

  // Defect operations & category: each operation gets a baseline SOP chunk
  // + an aggregated weekly stats chunk per line.
  for (const op of OPERATIONS) {
    const title = `QC defect procedure — ${op}`;
    const body =
      `Operation: ${op}. ` +
      `Reject categories: stitch skip (> 3 mm gap), open seam (> 5 mm), ` +
      `oil stain (> 5 mm), shading, measurement out-of-tolerance (> 4 mm). ` +
      `When a category spikes > 25% week-on-week, file a defect cluster insight ` +
      `and reroute the next two PO bundles to inline QC. AQL 2.5 is the default ` +
      `sampling level for general apparel export.`;
    out.push({
      id: `qc:${op}::en::1`,
      sourceId: `qc:${op}`,
      title,
      text: body,
      locale: "en",
      department: "qc",
      category: "qc",
      tags: extractTags(body, title, ["qc", op]),
      indexedAt: new Date().toISOString()
    });
  }

  // Per-line aggregated stats chunk — gives the retriever something concrete
  // for queries like "Line 3 defects last week".
  for (const line of LINES) {
    for (const op of OPERATIONS) {
      const { totals } = generateOperationLineWeeks(op, line, weekStarts);
      const title = `QC defects — ${line} ${op} (last 2 weeks)`;
      const body =
        `Line ${line}, operation ${op}: inspected ${totals.inspected}, ` +
        `defects ${totals.defects} (major ${totals.major}, minor ${totals.minor}), ` +
        `rework ${totals.rework}. AQL 2.5 inspection level applies.`;
      out.push({
        id: `qc:${line}:${op}::en::1`,
        sourceId: `qc:${line}:${op}`,
        title,
        text: body,
        locale: "en",
        department: "qc",
        category: "qc",
        tags: extractTags(body, title, ["qc", op, line.toLowerCase()]),
        indexedAt: new Date().toISOString()
      });
    }
  }

  return out;
}

// ---------------------------------------------------------------------------
// Public ingest API
// ---------------------------------------------------------------------------

/**
 * Build the canonical chunk corpus. Safe to call repeatedly — chunks are
 * deterministic given the underlying seed data.
 */
export function buildCorpus(): { chunks: RagChunk[]; report: ChunkIngestReport } {
  const manuals = manualsToChunks();
  const policiesArr = policiesToChunks();
  const qc = qcToChunks();
  const all = [...manuals, ...policiesArr, ...qc];
  return {
    chunks: all,
    report: {
      manuals: manuals.length,
      policies: policiesArr.length,
      qc: qc.length,
      total: all.length
    }
  };
}
