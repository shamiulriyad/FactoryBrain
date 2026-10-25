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
      return { label: "Live", title: "Answered by the configured language model from the cited sources", isLive: true };
    case "no_source":
      return { label: "No source", title: "Nothing relevant was found, so no model was asked", isLive: false };
    default:
      return { label: "Demo", title: "Not a generated answer: a fixed template over the retrieved passages", isLive: false };
  }
}

/** Confidence is only meaningful for a generated answer. */
export function shouldShowConfidence(mode: AskMode): boolean {
  return mode === "live";
}
