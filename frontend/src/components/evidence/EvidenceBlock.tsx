"use client";

import { useState } from "react";
import { motion, AnimatePresence } from "framer-motion";
import { FileText, ChevronRight } from "lucide-react";
import { useT } from "@/lib/useT";
import { evidenceService } from "@/services/evidence.service";
import type { EvidenceRefPublic } from "@/services/types";

export function EvidenceBlock({ refs }: { refs: EvidenceRefPublic[] }) {
  const { t, locale } = useT();
  const [openIdx, setOpenIdx] = useState<number | null>(null);
  return (
    <div className="surface p-3">
      <div className="flex items-center gap-2 text-caption text-fg-tertiary">
        <FileText size={12} /> Evidence
      </div>
      <ul className="mt-2 space-y-1.5">
        {refs.map((r, i) => {
          const expanded = openIdx === i;
          const rows = expanded ? evidenceService.rows(r, 0, 8) : null;
          return (
            <li key={i} className="border border-border-subtle rounded-md overflow-hidden">
              <button
                type="button"
                onClick={() => setOpenIdx(expanded ? null : i)}
                className="w-full flex items-center justify-between px-3 py-2 text-body text-fg-secondary hover:text-fg-primary hover:bg-surface-2 transition-colors"
              >
                <span className="flex items-center gap-2">
                  <ChevronRight size={12} className={"transition-transform " + (expanded ? "rotate-90" : "")} />
                  <span className="text-fg-primary">{r.domain}</span>
                  <span className="text-fg-tertiary">·</span>
                  <span className="text-fg-secondary">{r.count} records</span>
                  {r.filter && (
                    <span className="text-fg-tertiary">
                      · {Object.entries(r.filter).map(([k, v]) => `${k}:${v}`).join(", ")}
                    </span>
                  )}
                </span>
                <span className="mono-pill text-fg-tertiary text-[10px]">{expanded ? "Close" : "View sources"}</span>
              </button>
              <AnimatePresence>
                {expanded && rows && (
                  <motion.div
                    initial={{ height: 0, opacity: 0 }}
                    animate={{ height: "auto", opacity: 1 }}
                    exit={{ height: 0, opacity: 0 }}
                    transition={{ duration: 0.18 }}
                    className="border-t border-border-subtle bg-surface overflow-hidden"
                  >
                    <div className="overflow-x-auto">
                      <table className="w-full text-caption">
                        <thead className="text-fg-tertiary">
                          <tr>
                            {rows.columns.map((c) => (
                              <th key={c} className="px-3 py-2 text-left font-medium">{c}</th>
                            ))}
                          </tr>
                        </thead>
                        <tbody>
                          {rows.rows.map((row, idx) => (
                            <tr key={idx} className="border-t border-border-subtle">
                              {rows.columns.map((c) => (
                                <td key={c} className="px-3 py-2 text-fg-secondary whitespace-nowrap">{String(row[c] ?? "")}</td>
                              ))}
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  </motion.div>
                )}
              </AnimatePresence>
            </li>
          );
        })}
      </ul>
    </div>
  );
}