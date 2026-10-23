// Ask BunonBrain — answers free-text factory-floor questions with streaming reveal.
// Streaming blocks: analyzed → finding → factors → evidence → recommendation → done.
// Routes to the live slice API when a key is present, otherwise produces a deterministic
// derivation against the synthetic factory state.
//
// RAG: each query is run through the vector index (`./rag/indexer.ts`) so the
// generated answer can cite specific manuals / policies / QC procedures.
// Confidence + source attributions are passed through to the LLM prompt and
// surfaced in the streamed `evidence` blocks.
//
// The RAG modules pull in `node:fs` / `node:path` / `node:crypto` for
// persistence + deterministic hashing, which webpack refuses to bundle into
// the client. We lazy-load the rag indexer on first use (server-side only)
// so client components that import this file don't trip the bundler.

import { dataset } from "./dataset";
import { factoryTools } from "./factory.tools";
import type { AskAnswer, AskContextPack, StreamChunk, EvidenceRefPublic, RiskTier } from "./types";
import type { RagHit, RagFilter, RagDepartment, RagCategory } from "./rag/vector-store";

/** We type-narrow the RAG module shape so the require'd module can still be
 *  used as if it were statically imported. */
type RagModule = {
  ensureIndexed: (opts?: { force?: boolean }) => Promise<unknown>;
  retrieve: (
    query: string,
    opts?: { topK?: number; threshold?: number; filter?: RagFilter }
  ) => Promise<RagHit[]>;
  hitsToEvidenceRefs: (hits: RagHit[]) => Array<{
    domain: EvidenceRefPublic["domain"];
    count: number;
    previewIds?: string[];
    filter?: Record<string, string | number | boolean>;
  }>;
};

/** Optional metadata filter accepted from the /api/ask route. */
export type AskRagFilter = {
  department?: RagDepartment | RagDepartment[];
  category?: RagCategory | RagCategory[];
};

let _ragModule: RagModule | null = null;

/** Lazy server-side require of the RAG module. Cached after first call.
 *  Uses `require` so the client bundle never sees the `node:*` imports that
 *  live inside the rag implementation. */
function rag(): RagModule {
  if (_ragModule) return _ragModule;
  // Lazy server-side require; the @typescript-eslint/no-var-requires rule
  // isn't installed in this project's eslint config, so we don't need a
  // disable comment, but we leave the in-source justification for grep:
  _ragModule = require("./rag") as RagModule;
  return _ragModule;
}

function topKFromEnv(): number {
  const v = Number(process.env.RAG_TOP_K);
  return Number.isFinite(v) && v > 0 ? v : 4;
}

function thresholdFromEnv(): number {
  const v = Number(process.env.RAG_SIMILARITY_THRESHOLD);
  return Number.isFinite(v) ? v : 0.0;
}

/** Lazily resolve the factory store. */
function factoryState() {
  const mod = require("@/store/factory.store") as typeof import("@/store/factory.store");
  return mod.useFactoryStore.getState();
}

const SUGGESTIONS_EN = [
  "Which line is at risk of missing today's target and why?",
  "What's the efficiency on Line 3 right now?",
  "Which machines need maintenance this week?"
];

const SUGGESTIONS_BN = [
  "আজকের লক্ষ্য মিস করতে পারে এমন লাইন কোনটি এবং কেন?",
  "এই মুহূর্তে লাইন ৩ এর দক্ষতা কত?",
  "এই সপ্তাহে কোন মেশিনে রক্ষণাবেক্ষণ দরকার?"
];

export const askService = {
  suggestions(): string[] {
    return [...SUGGESTIONS_EN, ...SUGGESTIONS_BN];
  },
  async buildContextPack(query: string, agentId?: string | null, ragFilter?: AskRagFilter): Promise<AskContextPack> {
    const q = query.toLowerCase();
    const wantRisk = /risk|miss|at.risk|ঝুঁকি|মিস/.test(q);
    const wantEfficiency = /efficiency|target|দক্ষতা|লক্ষ্য/.test(q);
    const wantMaintenance = /maintenance|machine|broken|fix|রক্ষণাবেক্ষণ|মেশিন/.test(q);

    const insights = dataset.insights.filter((i) => {
      if (wantRisk) return i.agentId === "line-throughput-agent" || i.agentId === "manager-agent";
      if (wantEfficiency) return i.agentId === "line-throughput-agent";
      if (wantMaintenance) return i.agentId === "maintenance-agent";
      return true;
    });
    const state = factoryState();
    const nodes = [...state.lines, ...state.machines].map((n) => ({
      id: n.id,
      kind: "line" as const,
      label: (n as any).name ?? (n as any).id,
      labelBn: (n as any).name ?? (n as any).id,
      status: n.status as any
    }));
    // Manual corpus citations — drive the `manuals` evidence domain in the
    // demo answer + the live LLM prompt. Locale follows the query shape.
    const locale: "en" | "bn" = /[\u0980-\u09FF]/.test(query) ? "bn" : "en";
    const manualHits = factoryTools.search_manual(query, { locale, limit: 5 }).hits;
    // RAG retrieval — runs the question through the vector index, returns
    // top-k chunks with cosine + BM25 hybrid scores. Filterable by
    // department/category from the API caller.
    const mod = rag();
    await mod.ensureIndexed();
    const ragFilterResolved: RagFilter = ragFilter
      ? {
          department: ragFilter.department,
          category: ragFilter.category,
          locale
        }
      : { locale };
    const ragHits: RagHit[] = await mod.retrieve(query, {
      topK: topKFromEnv(),
      threshold: thresholdFromEnv(),
      filter: ragFilterResolved
    });
    const ragEvidence: EvidenceRefPublic[] = mod.hitsToEvidenceRefs(ragHits);
    return {
      query,
      scope: agentId ? [agentId] : undefined,
      health: dataset.health,
      relevantInsights: insights,
      relevantEntities: nodes,
      // Attach manual hits for the live prompt to surface; the demo path
      // re-derives them per branch below so it can dedupe by domain.
      manualHits,
      // RAG hits: dense + BM25 hybrid score, with chunk metadata so the
      // prompt builder can attribute findings to specific manuals.
      ragHits,
      ragEvidence
    } as AskContextPack & {
      manualHits: typeof manualHits;
      ragHits: RagHit[];
      ragEvidence: EvidenceRefPublic[];
    };
  },
  // Returns a streaming generator. Each yielded chunk is one of the answer blocks.
  async *stream(pack: AskContextPack): AsyncGenerator<StreamChunk> {
    const q = pack.query.toLowerCase();
    const wantRisk = /risk|miss|at.risk|ঝুঁকি|মিস/.test(q);
    const wantEfficiency = /efficiency|target|দক্ষতা|লক্ষ্য/.test(q);
    const wantMaintenance = /maintenance|machine|broken|fix|রক্ষণাবেক্ষণ|মেশিন/.test(q);
    const locale: "en" | "bn" = /[\u0980-\u09FF]/.test(pack.query) ? "bn" : "en";
    const manualHits = (pack as any).manualHits as ReturnType<typeof factoryTools.search_manual>["hits"] | undefined
      ?? factoryTools.search_manual(pack.query, { locale, limit: 5 }).hits;
    const ragHits: RagHit[] | undefined = (pack as any).ragHits;
    const ragEvidence: EvidenceRefPublic[] | undefined = (pack as any).ragEvidence;

    // Build a single evidence block from `manualHits` (if any) so the demo
    // answer can include citations in its `evidence` array regardless of
    // branch. RAG-derived evidence is merged in (de-duped by source id).
    const manualEvidence: EvidenceRefPublic[] = manualHits.length
      ? [{
          domain: "manuals",
          count: manualHits.length,
          previewIds: manualHits.map((h) => h.id)
        }]
      : [];
    const combinedEvidence: EvidenceRefPublic[] = mergeEvidence(manualEvidence, ragEvidence);

    if (wantMaintenance || pack.scope?.includes("maintenance-agent")) {
      const machines = factoryTools.get_machine_health();
      yield { type: "block", index: 0, payload: { kind: "analyzed", items: [
        { domain: "inventory", count: factoryState().machines.length },
        { domain: "policies", count: dataset.policies.length }
      ]}};
      await sleep(180);
      yield { type: "block", index: 1, payload: { kind: "finding",
        en: `${machines.length} machines show warning-band telemetry. Top priorities: ${machines.slice(0, 2).map((m) => `${m.machine.id} (${m.reason.toLowerCase()})`).join("; ")}.`,
        bn: `${bn(machines.length)}টি মেশিন সতর্কতা ব্যান্ডের টেলিমেট্রি দেখাচ্ছে। শীর্ষ অগ্রাধিকার: ${machines.slice(0, 2).map((m) => `${m.machine.id} (${m.reason})`).join("; ")}।`
      }};
      await sleep(160);
      const factors = machines.slice(0, 4).map((m) => ({
        label: `${m.machine.id} RUL`,
        labelBn: `${m.machine.id} RUL`,
        magnitude: `${m.rulEstimate}d`,
        magnitudeBn: `${bn(m.rulEstimate)} দিন`
      }));
      yield { type: "factor", index: 2, payload: factors };
      await sleep(140);
      const evidence: EvidenceRefPublic[] = [
        { domain: "inventory", count: machines.length, previewIds: machines.slice(0, 6).map((m) => m.machine.id) },
        { domain: "policies", count: 1, previewIds: ["policy:maintenance-sop-1"] },
        ...combinedEvidence
      ];
      yield { type: "evidence", index: 3, payload: evidence };
      await sleep(140);
      yield { type: "recommendation", index: 4, payload: {
        title: "Open work orders for the at-risk machines",
        titleBn: "ঝুঁকিপূর্ণ মেশিনগুলোর জন্য ওয়ার্ক অর্ডার খুলুন",
        action: "Pause non-critical production on those units ~30 minutes; book the maintenance window.",
        actionBn: "ঐ ইউনিটগুলোতে অ-জরুরি উৎপাদন ~৩০ মিনিটের জন্য বন্ধ করুন; রক্ষণাবেক্ষণ উইন্ডো বুক করুন।",
        riskTier: "medium" as RiskTier
      }};
      yield { type: "done", index: 5, payload: { confidence: 0.82 } };
      return;
    }

    if (wantEfficiency || pack.scope?.includes("line-throughput-agent")) {
      const lines = factoryTools.get_line_status();
      const focus = lines.find((l) => /line-?3/.test(pack.query.toLowerCase())) ?? lines[0];
      yield { type: "block", index: 0, payload: { kind: "analyzed", items: [
        { domain: "orders", count: dataset.orders.length },
        { domain: "inventory", count: factoryState().lines.length }
      ]}};
      await sleep(180);
      yield { type: "block", index: 1, payload: { kind: "finding",
        en: `${focus.line.name} is running at ${Math.round(focus.line.efficiency * 100)}% line efficiency against a ${Math.round(focus.line.targetEfficiency * 100)}% target. Bottleneck: ${focus.bottleneck}.`,
        bn: `${focus.line.name} ${Math.round(focus.line.efficiency * 100)}% দক্ষতায় চলছে, লক্ষ্য ${Math.round(focus.line.targetEfficiency * 100)}% এর বিপরীতে। বটলনেক: ${focus.bottleneck}।`
      }};
      await sleep(160);
      const factors = [
        { label: "Efficiency", labelBn: "দক্ষতা", magnitude: `${Math.round(focus.line.efficiency * 100)}%`, magnitudeBn: `${bn(Math.round(focus.line.efficiency * 100))}%` },
        { label: "Target", labelBn: "লক্ষ্য", magnitude: `${Math.round(focus.line.targetEfficiency * 100)}%`, magnitudeBn: `${bn(Math.round(focus.line.targetEfficiency * 100))}%` },
        { label: "Status", labelBn: "অবস্থা", magnitude: focus.line.status, magnitudeBn: focus.line.status }
      ];
      yield { type: "factor", index: 2, payload: factors };
      await sleep(140);
      const evidence: EvidenceRefPublic[] = [
        { domain: "orders", count: focus.ordersAtRisk, filter: { lineId: focus.line.id } },
        { domain: "inventory", count: 1, filter: { machineScope: focus.line.id } },
        ...combinedEvidence
      ];
      yield { type: "evidence", index: 3, payload: evidence };
      await sleep(140);
      yield { type: "recommendation", index: 4, payload: {
        title: "Re-balance operators towards the bottleneck operation",
        titleBn: "বটলনেক অপারেশনের দিকে অপারেটর পুনর্ভারসাম্য করুন",
        action: "Move 1–2 helpers from Finishing; reassess at next shift handover.",
        actionBn: "ফিনিশিং থেকে ১–২ জন সহায়ক সরান; পরবর্তী শিফট হ্যান্ডওভারে পুনর্মূল্যায়ন।",
        riskTier: "low" as RiskTier
      }};
      yield { type: "done", index: 5, payload: { confidence: 0.85 } };
      return;
    }

    if (wantRisk || pack.scope?.includes("manager-agent")) {
      const lines = factoryTools.get_line_status();
      const atRisk = lines.filter((l) => l.line.efficiency + 0.05 < l.line.targetEfficiency || l.line.status !== "healthy");
      yield { type: "block", index: 0, payload: { kind: "analyzed", items: [
        { domain: "orders", count: factoryState().orders.length },
        { domain: "inventory", count: factoryState().lines.length }
      ]}};
      await sleep(180);
      yield { type: "block", index: 1, payload: { kind: "finding",
        en: `${atRisk.length} of ${lines.length} lines are below target. The first concern is ${atRisk[0]?.line.name ?? "none"} — ${atRisk[0]?.bottleneck ?? "all on target"}. ${focusSummary(atRisk)}.`,
        bn: `${lines.length} লাইনের মধ্যে ${bn(atRisk.length)}টি লক্ষ্যের নিচে। প্রথম উদ্বেগ হলো ${atRisk[0]?.line.name ?? "কোনোটি নয়"} — ${atRisk[0]?.bottleneck ?? "সব লক্ষ্যে"}।`
      }};
      await sleep(160);
      const factors = atRisk.slice(0, 4).map((l) => ({
        label: `${l.line.name} gap`,
        labelBn: `${l.line.name} ব্যবধান`,
        magnitude: `${Math.round((l.line.targetEfficiency - l.line.efficiency) * 100)}pp`,
        magnitudeBn: `${bn(Math.round((l.line.targetEfficiency - l.line.efficiency) * 100))} পয়েন্ট`
      }));
      yield { type: "factor", index: 2, payload: factors.length ? factors : [{ label: "No lines at risk", labelBn: "কোনো লাইন ঝুঁকিতে নেই", magnitude: "—", magnitudeBn: "—" }] };
      await sleep(140);
      const evidence: EvidenceRefPublic[] = [
        { domain: "orders", count: atRisk.reduce((a, l) => a + l.ordersAtRisk, 0), previewIds: atRisk.slice(0, 4).map((l) => l.line.id) },
        ...combinedEvidence
      ];
      yield { type: "evidence", index: 3, payload: evidence };
      await sleep(140);
      yield { type: "recommendation", index: 4, payload: {
        title: "Re-balance the at-risk line; alert buyer if needed",
        titleBn: "ঝুঁকিপূর্ণ লাইন পুনর্ভারসাম্য করুন; প্রয়োজনে ক্রেতাকে সতর্ক করুন",
        action: "Open an internal re-balance and proactively message the affected buyer about possible delay.",
        actionBn: "অভ্যন্তরীণ পুনর্ভারসাম্য খুলুন এবং সম্ভাব্য বিলম্ব সম্পর্কে প্রভাবিত ক্রেতাকে সক্রিয়ভাবে জানান।",
        riskTier: "medium" as RiskTier
      }};
      yield { type: "done", index: 5, payload: { confidence: 0.79 } };
      return;
    }

    // Default → orchestrator summary
    {
      const lines = factoryTools.get_line_status();
      const machines = factoryTools.get_machine_health();
      yield { type: "block", index: 0, payload: { kind: "analyzed", items: [
        { domain: "orders", count: factoryState().orders.length },
        { domain: "inventory", count: factoryState().lines.length }
      ]}};
      await sleep(180);
      yield { type: "block", index: 1, payload: { kind: "finding",
        en: `Quick read: ${lines.filter((l) => l.line.status !== "healthy").length} line(s) need attention, and ${machines.length} machine(s) are trending toward failure. Ask about either and I'll route to the right agent.`,
        bn: `সংক্ষিপ্ত পড়া: ${bn(lines.filter((l) => l.line.status !== "healthy").length)}টি লাইনে মনোযোগ দরকার, এবং ${bn(machines.length)}টি মেশিন ব্যর্থতার দিকে এগোচ্ছে। যেকোনো একটি সম্পর্কে জিজ্ঞাসা করুন, আমি সঠিক এজেন্টে রাউট করব।`
      }};
      await sleep(160);
      const factors: { label: string; labelBn: string; magnitude: string; magnitudeBn: string }[] = [];
      yield { type: "factor", index: 2, payload: factors };
      yield { type: "evidence", index: 3, payload: combinedEvidence };
      yield { type: "recommendation", index: 4, payload: {
        title: "Ask a specific question next",
        titleBn: "পরবর্তীতে একটি নির্দিষ্ট প্রশ্ন জিজ্ঞাসা করুন",
        action: "Try the three sample questions in the suggestions below.",
        actionBn: "নিচের পরামর্শগুলো থেকে তিনটি নমুনা প্রশ্ন ব্যবহার করুন।",
        riskTier: "low" as RiskTier
      }};
      yield { type: "done", index: 5, payload: { confidence: 0.6 } };
    }
  }
};

function focusSummary(atRisk: ReturnType<typeof factoryTools.get_line_status>): string {
  if (!atRisk.length) return "All lines are on target";
  return atRisk.map((l) => `${l.line.name} at ${Math.round(l.line.efficiency * 100)}%`).join(", ");
}

/**
 * Merge legacy manual-search evidence with RAG-derived evidence. The two
 * arrays reference the same domain (`manuals` / `policies`) but the RAG
 * evidence is freshly grouped by category + department, so we union
 * `previewIds` per domain + take the larger count.
 */
function mergeEvidence(
  legacy: EvidenceRefPublic[] | undefined,
  rag: EvidenceRefPublic[] | undefined
): EvidenceRefPublic[] {
  const out: EvidenceRefPublic[] = [];
  const byKey = new Map<string, EvidenceRefPublic>();
  const add = (ref: EvidenceRefPublic) => {
    const key = ref.domain + (ref.filter?.department ? `:${ref.filter.department}` : "");
    const prev = byKey.get(key);
    if (!prev) {
      byKey.set(key, { ...ref });
    } else {
      const merged: EvidenceRefPublic = {
        domain: prev.domain,
        count: prev.count + ref.count,
        previewIds: dedupe([...(prev.previewIds ?? []), ...(ref.previewIds ?? [])]),
        filter: prev.filter ?? ref.filter
      };
      byKey.set(key, merged);
    }
  };
  for (const r of legacy ?? []) add(r);
  for (const r of rag ?? []) add(r);
  for (const v of byKey.values()) out.push(v);
  return out;
}

function dedupe(arr: string[]): string[] {
  return Array.from(new Set(arr));
}

function sleep(ms: number) {
  return new Promise<void>((r) => setTimeout(r, ms));
}

function bn(n: number) {
  const map: Record<number, string> = { 0: "০", 1: "১", 2: "২", 3: "৩", 4: "৪", 5: "৫", 6: "৬", 7: "৭", 8: "৮", 9: "৯" };
  return String(n).replace(/[0-9]/g, (d) => map[Number(d)]);
}
