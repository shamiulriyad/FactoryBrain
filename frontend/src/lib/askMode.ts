// How an Ask answer was produced, as reported by the API's `mode` field.
//
// Kept dependency-free (no "@/..." imports, no JSX) so scripts/smoke.mjs can
// import it directly with Node's TypeScript type stripping and test it.
//
// The rule: an answer is only ever shown as "Live" when the API explicitly
// says a language model produced it. A missing, unknown or misspelled mode
// is treated as demo; that keeps an older or misbehaving backend from
// making the UI claim a live answer.

export type AskMode = "live" | "demo" | "no_source";

export function resolveAskMode(response: unknown): AskMode {
  const mode = (response as { mode?: unknown } | null | undefined)?.mode;
  if (mode === "live") return "live";
  if (mode === "no_source") return "no_source";
  return "demo";
}

export type AskModeLabel = { label: string; title: string; isLive: boolean };

export function askModeLabel(mode: AskMode): AskModeLabel {
  switch (mode) {
    case "live":
      return { label: "Live", title: "Answered by the RAG service from the cited document passages", isLive: true };
    case "no_source":
      return { label: "No source", title: "The RAG service answered without citing any indexed document", isLive: false };
    default:
      // Only older saved history has this mode; the API no longer produces it.
      return { label: "Demo", title: "Saved from an earlier version: not a RAG answer", isLive: false };
  }
}

/** The source-match score only exists when the answer cites a document. */
export function shouldShowConfidence(mode: AskMode): boolean {
  return mode === "live";
}
