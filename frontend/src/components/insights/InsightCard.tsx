"use client";

import { useState } from "react";
import { motion, AnimatePresence } from "framer-motion";
import { ChevronDown, ExternalLink } from "lucide-react";
import { useT } from "@/lib/useT";
import { StageBadge, RiskPill } from "@/components/ui/StatusPill";
import { EvidenceBlock } from "@/components/evidence/EvidenceBlock";
import type { InsightPublic } from "@/services/types";
import { cn } from "@/lib/cn";

export function InsightCard({ insight, focused }: { insight: InsightPublic; focused?: boolean }) {
  const { t, locale } = useT();
  const [expanded, setExpanded] = useState(false);

  return (
    <article
      data-insight-id={insight.id}
      className={cn(
        "surface-2 transition-shadow",
        focused && "ring-2 ring-accent shadow-[0_0_0_3px_var(--accent-soft)]"
      )}
    >
      <header className="p-3">
        <div className="flex items-start justify-between gap-2">
          <div className="min-w-0">
            <p className="text-caption text-fg-tertiary">{insight.agentLabel}</p>
            <p className="mt-0.5 text-body text-fg-primary">{locale === "bn" ? insight.titleBn : insight.title}</p>
          </div>
          <div className="flex flex-col items-end gap-1.5 shrink-0">
            <RiskPill tier={insight.recommendation.riskTier} label={t(`risk.${insight.recommendation.riskTier}`)} />
            <StageBadge stage={insight.stage} label={t(`insights.stages.${insight.stage}`)} />
          </div>
        </div>

        <p className="mt-2 text-caption text-fg-secondary line-clamp-2">
          {locale === "bn" ? insight.findingBn : insight.finding}
        </p>

        <div className="mt-3 flex items-center justify-between">
          <span className="mono-pill text-fg-tertiary">conf {Math.round(insight.confidence * 100)}%</span>
          <button
            type="button"
            onClick={() => setExpanded((e) => !e)}
            className="text-caption text-fg-secondary hover:text-fg-primary inline-flex items-center gap-1"
          >
            {expanded ? "Less" : "More"}
            <ChevronDown size={12} className={"transition-transform " + (expanded ? "rotate-180" : "")} />
          </button>
        </div>
      </header>

      <AnimatePresence>
        {expanded && (
          <motion.div
            initial={{ height: 0, opacity: 0 }}
            animate={{ height: "auto", opacity: 1 }}
            exit={{ height: 0, opacity: 0 }}
            transition={{ duration: 0.18 }}
            className="border-t border-border-subtle overflow-hidden"
          >
            <div className="p-3 space-y-3">
              {insight.factors.length > 0 && (
                <div>
                  <p className="mono-pill text-fg-tertiary mb-1.5">Contributing factors</p>
                  <ul className="space-y-1">
                    {insight.factors.map((f, i) => (
                      <li key={i} className="flex items-center justify-between text-caption">
                        <span className="text-fg-secondary">{locale === "bn" ? f.labelBn : f.label}</span>
                        <span className="text-fg-primary mono-pill">{locale === "bn" ? f.magnitudeBn : f.magnitude}</span>
                      </li>
                    ))}
                  </ul>
                </div>
              )}
              <EvidenceBlock refs={insight.evidence} />
              <div className="surface p-3">
                <p className="mono-pill text-fg-tertiary mb-1">Recommended action</p>
                <p className="text-body text-fg-primary">{locale === "bn" ? insight.recommendation.titleBn : insight.recommendation.title}</p>
                <p className="mt-1 text-caption text-fg-secondary">{locale === "bn" ? insight.recommendation.actionBn : insight.recommendation.action}</p>
              </div>
            </div>
          </motion.div>
        )}
      </AnimatePresence>
    </article>
  );
}