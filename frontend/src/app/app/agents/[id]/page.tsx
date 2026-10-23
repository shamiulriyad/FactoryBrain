"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { ArrowLeft, Bot } from "lucide-react";
import { useT } from "@/lib/useT";
import { agentService } from "@/services/agent.service";
import { insightService } from "@/services/insight.service";
import { activityService } from "@/services/activity.service";
import { Panel } from "@/components/ui/Panel";
import { RiskPill } from "@/components/ui/StatusPill";
import { InsightCard } from "@/components/insights/InsightCard";
import { formatRelative } from "@/lib/format";

export default function AgentDetailPage() {
  const { id } = useParams<{ id: string }>();
  const { t, locale } = useT();
  const agent = agentService.get(id);
  if (!agent) {
    return (
      <div className="px-6 py-12 max-w-2xl mx-auto">
        <p className="text-body text-fg-secondary">Agent not found.</p>
        <Link href="/app/agents" className="mt-4 inline-flex items-center gap-1 text-caption text-accent">
          <ArrowLeft size={12} /> Back to roster
        </Link>
      </div>
    );
  }

  const insights = insightService.feed({ agentId: agent.id }).slice(0, 3);
  const activity = activityService.recent({ limit: 5 }).filter((a) => a.actor === agent.id || a.actorLabel === agent.name);

  return (
    <div className="px-6 md:px-8 py-6 max-w-4xl mx-auto">
      <Link href="/app/agents" className="inline-flex items-center gap-1 text-caption text-fg-tertiary hover:text-fg-primary mb-6">
        <ArrowLeft size={12} /> Back to roster
      </Link>

      <header className="flex items-start gap-4">
        <div className="size-12 rounded-md bg-surface-2 flex items-center justify-center text-fg-primary">
          <Bot size={20} />
        </div>
        <div className="min-w-0">
          <h1 className="text-display font-semibold tracking-tight">{locale === "bn" ? agent.nameBn : agent.name}</h1>
          <p className="mt-1 text-body text-fg-secondary">{locale === "bn" ? agent.purposeBn : agent.purpose}</p>
          <div className="mt-3 flex items-center gap-2 flex-wrap">
            <RiskPill tier={agent.risk === "per_action" ? "low" : agent.risk} label={t(`risk.${agent.risk === "per_action" ? "low" : agent.risk}`)} />
            <span className="mono-pill text-fg-tertiary border border-border-subtle bg-surface px-2 py-1 rounded-sm">{agent.status}</span>
          </div>
        </div>
      </header>

      <div className="mt-8 grid grid-cols-1 md:grid-cols-2 gap-4">
        <Panel title={t("agents.detail.contextSlices")}>
          <div className="flex flex-wrap gap-1.5">
            {agent.contextSlices.map((s) => (
              <span key={s} className="mono-pill text-fg-secondary border border-border-subtle bg-surface px-2 py-1 rounded-sm">
                {s}
              </span>
            ))}
          </div>
        </Panel>
        <Panel title={t("agents.detail.model")}>
          <p className="text-body text-fg-primary">{agent.model}</p>
          <p className="mt-2 text-caption text-fg-tertiary">
            {agent.execution === "auto" && t("agents.riskNote.auto")}
            {agent.execution === "approval_required" && t("agents.riskNote.approval")}
            {agent.execution === "auto_suggest_with_threshold" && t("agents.riskNote.approval") + " (threshold-based)"}
            {agent.execution === "per_policy" && t("agents.riskNote.perPolicy")}
          </p>
        </Panel>
      </div>

      <Panel className="mt-4" title={t("agents.detail.tasksToday")}>
        {insights.length === 0 ? (
          <p className="text-caption text-fg-tertiary">No active tasks.</p>
        ) : (
          <div className="space-y-3">
            {insights.map((i) => <InsightCard key={i.id} insight={i} />)}
          </div>
        )}
      </Panel>

      <Panel className="mt-4" title={t("agents.detail.recentActivity")}>
        {activity.length === 0 ? (
          <p className="text-caption text-fg-tertiary">No activity.</p>
        ) : (
          <ul className="divide-y divide-border-subtle">
            {activity.map((a) => (
              <li key={a.id} className="py-2.5 flex items-center justify-between gap-3 text-caption">
                <span className="text-fg-secondary min-w-0 truncate">
                  <span className="text-fg-primary">{a.actorLabel}</span> {locale === "bn" ? a.verbBn : a.verb}
                  {a.target && <span className="text-fg-tertiary"> — {locale === "bn" ? a.targetBn : a.target}</span>}
                </span>
                <span className="text-fg-tertiary shrink-0">{formatRelative(a.isoDate, locale)}</span>
              </li>
            ))}
          </ul>
        )}
      </Panel>
    </div>
  );
}