"use client";

// Evidence references exactly as the API returned them: the domain that was
// read, how many records, the filter and the preview ids. Nothing is looked
// up or expanded locally, so no record is ever shown that the API didn't name.

import { Database } from "lucide-react";
import type { EvidenceRefPublic } from "@/services/types";

export function EvidenceBlock({ refs }: { refs: EvidenceRefPublic[] }) {
  if (!refs.length) return null;
  return (
    <div className="surface-2 p-3" data-testid="evidence-block">
      <p className="eyebrow flex items-center gap-1.5"><Database size={12} /> Evidence read</p>
      <ul className="mt-2 space-y-1.5">
        {refs.map((r, i) => (
          <li key={i} className="text-caption text-fg-secondary">
            <span className="text-fg-primary font-medium">{r.domain}</span> · {r.count} record{r.count === 1 ? "" : "s"}
            {r.filter && Object.keys(r.filter).length > 0 && (
              <span className="text-fg-tertiary"> · {Object.entries(r.filter).map(([k, v]) => `${k}: ${v}`).join(", ")}</span>
            )}
            {r.previewIds && r.previewIds.length > 0 && (
              <span className="block mt-0.5 font-mono text-[11px] text-fg-tertiary break-all">{r.previewIds.slice(0, 6).join(", ")}{r.previewIds.length > 6 ? ` +${r.previewIds.length - 6}` : ""}</span>
            )}
          </li>
        ))}
      </ul>
    </div>
  );
}
