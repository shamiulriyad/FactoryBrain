"use client";

import { motion } from "framer-motion";
import { ShieldCheck, Sparkles } from "lucide-react";
import { useT } from "@/lib/useT";
import { activityService } from "@/services/activity.service";
import { Panel } from "@/components/ui/Panel";
import { EmptyState } from "@/components/ui/EmptyState";
import { formatRelative } from "@/lib/format";
import { WhatsAppAlert } from "@/components/activity/WhatsAppAlert";
import { FloorAlertsPanel } from "@/components/activity/FloorAlertsPanel";

const outcomeStyles: Record<string, string> = {
  approved: "text-risk-low bg-risk-low/15 border-risk-low/30",
  rejected: "text-risk-high bg-risk-high/15 border-risk-high/30",
  executed: "text-stage-executing bg-stage-executing/15 border-stage-executing/30",
  completed: "text-stage-done bg-stage-done/15 border-stage-done/30",
  failed: "text-risk-high bg-risk-high/15 border-risk-high/30",
  flagged: "text-stage-pending bg-stage-pending/15 border-stage-pending/30"
};

export default function ActivityPage() {
  const { t, locale } = useT();
  const items = activityService.recent();
  return (
    <div className="px-6 md:px-8 py-6 max-w-4xl mx-auto" data-tour="activity">
      <h1 className="text-display font-semibold tracking-tight">{t("activity.title")}</h1>
      <p className="mt-1 text-caption text-fg-tertiary">{t("activity.subtitle")}</p>

      <div className="mt-6">
        <WhatsAppAlert />
      </div>

      <div className="mt-6">
        <FloorAlertsPanel />
      </div>

      <Panel className="mt-6" title={t("activity.title")}>
        {items.length === 0 ? (
          <EmptyState title={t("activity.empty")} />
        ) : (
          <ul className="divide-y divide-border-subtle">
            {items.map((a, i) => (
              <motion.li
                key={a.id}
                initial={{ opacity: 0, y: 4 }}
                animate={{ opacity: 1, y: 0 }}
                transition={{ duration: 0.18, delay: i * 0.02 }}
                className="py-3 flex items-center gap-3"
              >
                <span className="size-7 rounded-md bg-surface-2 flex items-center justify-center text-fg-tertiary shrink-0">
                  {a.actor === "user" ? <ShieldCheck size={14} /> : <Sparkles size={14} />}
                </span>
                <div className="flex-1 min-w-0">
                  <p className="text-body text-fg-primary">
                    <span className="text-fg-secondary">{a.actorLabel}</span>{" "}
                    <span>{locale === "bn" ? a.verbBn : a.verb}</span>
                    {a.target && (
                      <>
                        {" "}
                        <span className="text-fg-secondary">{locale === "bn" ? a.targetBn : a.target}</span>
                      </>
                    )}
                  </p>
                </div>
                {a.outcome && (
                  <span className={`mono-pill border px-2 py-1 rounded-sm shrink-0 ${outcomeStyles[a.outcome]}`}>
                    {a.outcome}
                  </span>
                )}
                <span className="text-caption text-fg-tertiary shrink-0">{formatRelative(a.isoDate, locale)}</span>
              </motion.li>
            ))}
          </ul>
        )}
      </Panel>
    </div>
  );
}