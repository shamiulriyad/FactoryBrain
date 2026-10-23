// Live Ask Factory Brain endpoint.
// When LLM_PROVIDER + LLM_API_KEY are set, the route forwards the question +
// a context pack to the model and adapts the response to the streaming shape
// the UI expects. Otherwise the demo mock is used.
//
// RAG: optional `department` + `category` filters restrict the retrieved
// citations to a slice of the indexed knowledge corpus. The synthesized
// answer is streamed back alongside the full evidence array so the
// `EvidenceBlock` component can render source attributions.

import { NextRequest } from "next/server";
import { z } from "zod";
import { askService, type AskRagFilter } from "@/services/ask.service";
import { insightService } from "@/services/insight.service";
import { agentService } from "@/services/agent.service";
import type { AskAnswer, StreamChunk, RiskTier } from "@/services/types";

export const runtime = "nodejs";

const PROVIDER = process.env.LLM_PROVIDER?.trim();
const KEY = process.env.LLM_API_KEY?.trim();
const MODEL = process.env.LLM_MODEL?.trim() || defaultModel();
const live = Boolean(PROVIDER && KEY);

function defaultModel(): string {
  switch (PROVIDER) {
    case "openai":
      return "gpt-4.1-mini";
    case "anthropic":
      return "claude-3-5-sonnet-latest";
    case "gemini":
      return "gemini-1.5-pro";
    default:
      return "";
  }
}

const RagDepartmentSchema = z.enum(["sewing", "cutting", "finishing", "qc", "general"]);
const RagCategorySchema = z.enum(["manuals", "compliance", "qc", "policy", "sop"]);

const RagFilterSchema = z
  .object({
    department: z
      .union([RagDepartmentSchema, z.array(RagDepartmentSchema)])
      .optional(),
    category: z
      .union([RagCategorySchema, z.array(RagCategorySchema)])
      .optional()
  })
  .optional();

const BodySchema = z.object({
  query: z.string().min(1).max(2000),
  agentId: z.string().min(1).max(64).optional(),
  /** RAG filter — restricts the retriever to one department / category. */
  filter: RagFilterSchema
});

export async function POST(req: NextRequest) {
  let body: unknown;
  try {
    body = await req.json();
  } catch {
    return Response.json({ error: "invalid_json" }, { status: 400 });
  }
  const parsed = BodySchema.safeParse(body);
  if (!parsed.success) {
    return Response.json({ error: "invalid_body", issues: parsed.error.issues }, { status: 400 });
  }

  const query = parsed.data.query;
  const agentId = parsed.data.agentId;
  const ragFilter = parsed.data.filter as AskRagFilter | undefined;
  const pack = await askService.buildContextPack(query, agentId, ragFilter);
  // When an agent is pre-selected, seed the demo stream with that agent's
  // most recent insight so the answer is visibly tailored.
  if (agentId) {
    const agentInsights = insightService.feed({ agentId });
    if (agentInsights.length > 0) {
      (pack as any).agentInsight = agentInsights[0];
      (pack as any).agentName = agentService.get(agentId)?.name ?? agentId;
    }
  }

  if (!live) {
    return mockStream(pack, agentId, undefined, ragFilter);
  }

  try {
    const answer = await callModel({
      query: pack.query,
      health: pack.health,
      relevantInsights: pack.relevantInsights,
      manualHits: (pack as any).manualHits,
      ragHits: (pack as any).ragHits
    });
    return Response.json(answer);
  } catch (err) {
    // Live failed — fall back to the deterministic mock so the UI never breaks.
    console.error("[ask] live model failed, falling back to demo", err);
    return mockStream(pack, agentId, { warning: "Live model unavailable, showing demo answer." }, ragFilter);
  }
}

function mockStream(
  pack: { query: string; agentInsight?: any; agentName?: string },
  agentId?: string,
  note?: { warning: string },
  ragFilter?: AskRagFilter
) {
  const encoder = new TextEncoder();
  const stream = new ReadableStream({
    async start(controller) {
      if (note) {
        controller.enqueue(encoder.encode(`event: notice\ndata: ${JSON.stringify(note)}\n\n`));
      }
      // Emit the RAG filter + retrieved citations up front so the UI can
      // render the `EvidenceBlock` immediately. Falls back to no event when
      // nothing was retrieved (e.g. demo path with no chunks matching).
      const ragHits = (pack as any).ragHits as Array<{
        chunk: { id: string; sourceId: string; title: string; department: string; category: string; tags: string[] };
        hybridScore: number;
        score: number;
        bm25Score: number;
      }> | undefined;
      if (ragHits && ragHits.length) {
        controller.enqueue(
          encoder.encode(
            `event: citations\ndata: ${JSON.stringify({
              filter: ragFilter ?? null,
              hits: ragHits.map((h) => ({
                id: h.chunk.id,
                sourceId: h.chunk.sourceId,
                title: h.chunk.title,
                department: h.chunk.department,
                category: h.chunk.category,
                tags: h.chunk.tags,
                hybridScore: Number(h.hybridScore.toFixed(4)),
                denseScore: Number(h.score.toFixed(4)),
                bm25Score: Number(h.bm25Score.toFixed(4))
              }))
            })}\n\n`
          )
        );
      } else if (ragFilter) {
        // Surface the filter echo even when no hits were returned so the UI
        // can label "0 sources" rather than leave the user guessing.
        controller.enqueue(
          encoder.encode(
            `event: citations\ndata: ${JSON.stringify({ filter: ragFilter, hits: [] })}\n\n`
          )
        );
      }
      for await (const chunk of askService.stream(pack as any)) {
        controller.enqueue(encoder.encode(`data: ${JSON.stringify(chunk)}\n\n`));
      }
      // When an agent is targeted, append a small "agent note" so the UI can
      // surface that the answer came from a specific sub-agent.
      if (agentId && pack.agentInsight) {
        controller.enqueue(
          encoder.encode(
            `event: agent\ndata: ${JSON.stringify({ agentId, agentName: pack.agentName, insightId: pack.agentInsight.id })}\n\n`
          )
        );
      }
      controller.enqueue(encoder.encode("event: end\ndata: {}\n\n"));
      controller.close();
    }
  });
  return new Response(stream, {
    headers: { "Content-Type": "text/event-stream", "Cache-Control": "no-cache" }
  });
}

async function callModel(pack: { query: string; health: unknown; relevantInsights: any[]; manualHits?: any[]; ragHits?: any[] }): Promise<AskAnswer> {
  const prompt = buildPrompt(pack);
  const raw = await dispatch(prompt);
  return adapt(raw, pack);
}

function buildPrompt(pack: { query: string; health: unknown; relevantInsights: any[]; manualHits?: any[]; ragHits?: any[] }) {
  const manualSnippet = (pack.manualHits ?? []).slice(0, 5).map((h) => ({
    id: h.id,
    title: h.title,
    snippet: h.snippet,
    source: h.source,
    score: h.score
  }));
  // RAG-retrieved chunks: dense + BM25 hybrid score, department + category
  // metadata, and the chunk text so the LLM can quote / cite accurately.
  const ragSnippet = (pack.ragHits ?? []).slice(0, 5).map((h: any) => ({
    id: h.chunk.id,
    sourceId: h.chunk.sourceId,
    title: h.chunk.title,
    snippet: (h.chunk.text ?? "").slice(0, 240),
    department: h.chunk.department,
    category: h.chunk.category,
    tags: h.chunk.tags,
    hybridScore: Number(h.hybridScore?.toFixed?.(3) ?? h.hybridScore),
    denseScore: Number(h.score?.toFixed?.(3) ?? h.score),
    bm25Score: Number(h.bm25Score?.toFixed?.(3) ?? h.bm25Score)
  }));
  return [
    "You are BunonBrain, the factory-floor operations assistant for a Bangladeshi RMG factory.",
    "Three specialist agents back you: line-throughput-agent (line efficiency + bottlenecks), maintenance-agent (machine telemetry + failure prediction), manager-agent (routes questions, drafts the morning brief).",
    "Answer the user's question using the context pack below. Cite the manuals below by `doc-N` id when relevant (use the `manuals` domain in `evidence`). When RAG hits include a specific chunk id, quote + cite that chunk.",
    "Return ONLY JSON matching this shape:",
    JSON.stringify(
      {
        taskId: "string",
        finding: "string (one sentence, plain language, can be Bangla if question is Bangla)",
        findingBn: "string (Bangla version of the finding)",
        factors: [
          { label: "string", labelBn: "string", magnitude: "string", magnitudeBn: "string" }
        ],
        evidence: [{ domain: "orders|customers|products|inventory|conversations|policies|suppliers|manuals", count: 0 }],
        recommendation: {
          title: "string",
          titleBn: "string",
          action: "string",
          actionBn: "string",
          riskTier: "low|medium|high"
        },
        confidence: 0.7
      },
      null,
      0
    ),
    "Question: " + pack.query,
    "Health: " + JSON.stringify(pack.health),
    "Relevant insights: " + JSON.stringify(pack.relevantInsights.map((i) => ({ id: i.id, title: i.title, finding: i.finding }))),
    "Manual corpus hits (cite these by id): " + JSON.stringify(manualSnippet),
    "RAG-retrieved chunks (cite by chunk id, prefer these over the legacy manual hits above): " + JSON.stringify(ragSnippet)
  ].join("\n\n");
}

function adapt(raw: string, pack: { query: string }): AskAnswer {
  let parsed: any;
  try {
    // Pull the JSON object out of any prose.
    const m = raw.match(/\{[\s\S]*\}/);
    parsed = JSON.parse(m ? m[0] : raw);
  } catch {
    // Couldn't parse — wrap the prose as the finding.
    return {
      taskId: "live-" + Date.now(),
      analyzed: [],
      finding: raw.slice(0, 240),
      findingBn: raw.slice(0, 240),
      factors: [],
      evidence: [],
      recommendation: {
        title: "Review the raw model answer",
        titleBn: "কাঁচা উত্তর পর্যালোচনা",
        action: raw.slice(0, 240),
        actionBn: raw.slice(0, 240),
        riskTier: "low" as RiskTier
      },
      createdAt: new Date().toISOString(),
      confidence: 0.5
    };
  }
  return {
    taskId: parsed.taskId ?? "live-" + Date.now(),
    analyzed: (parsed.analyzed ?? []) as AskAnswer["analyzed"],
    finding: String(parsed.finding ?? ""),
    findingBn: String(parsed.findingBn ?? parsed.finding ?? ""),
    factors: parsed.factors ?? [],
    evidence: parsed.evidence ?? [],
    recommendation: {
      title: String(parsed.recommendation?.title ?? ""),
      titleBn: String(parsed.recommendation?.titleBn ?? parsed.recommendation?.title ?? ""),
      action: String(parsed.recommendation?.action ?? ""),
      actionBn: String(parsed.recommendation?.actionBn ?? parsed.recommendation?.action ?? ""),
      riskTier: (parsed.recommendation?.riskTier ?? "low") as RiskTier
    },
    createdAt: new Date().toISOString(),
    confidence: Number(parsed.confidence ?? 0.6)
  };
}

async function dispatch(prompt: string): Promise<string> {
  switch (PROVIDER) {
    case "openai":
      return openai(prompt);
    case "anthropic":
      return anthropic(prompt);
    case "gemini":
      return gemini(prompt);
    default:
      throw new Error(`Unknown provider: ${PROVIDER}`);
  }
}

async function openai(prompt: string): Promise<string> {
  const res = await fetch("https://api.openai.com/v1/chat/completions", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${KEY}`
    },
    body: JSON.stringify({
      model: MODEL,
      messages: [{ role: "user", content: prompt }],
      temperature: 0.2
    })
  });
  if (!res.ok) throw new Error(`openai ${res.status}`);
  const json = (await res.json()) as any;
  return json.choices?.[0]?.message?.content ?? "";
}

async function anthropic(prompt: string): Promise<string> {
  const res = await fetch("https://api.anthropic.com/v1/messages", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "x-api-key": KEY!,
      "anthropic-version": "2023-06-01"
    },
    body: JSON.stringify({
      model: MODEL,
      max_tokens: 1024,
      messages: [{ role: "user", content: prompt }]
    })
  });
  if (!res.ok) throw new Error(`anthropic ${res.status}`);
  const json = (await res.json()) as any;
  return json.content?.[0]?.text ?? "";
}

async function gemini(prompt: string): Promise<string> {
  const url = `https://generativelanguage.googleapis.com/v1beta/models/${encodeURIComponent(MODEL)}:generateContent?key=${encodeURIComponent(KEY!)}`;
  const res = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      contents: [{ role: "user", parts: [{ text: prompt }] }],
      generationConfig: { temperature: 0.2 }
    })
  });
  if (!res.ok) throw new Error(`gemini ${res.status}`);
  const json = (await res.json()) as any;
  return json.candidates?.[0]?.content?.parts?.[0]?.text ?? "";
}
