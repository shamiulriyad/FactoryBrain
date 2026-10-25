"use client";

// Running an agent: POST /api/agents/{agentId}/run (Operator). The API stores
// the resulting insight, queues an approval when the risk tier isn't low and
// raises a floor alert. Runs are rule-based demo runs today (the response's
// `source` is "demo"); the UI says so instead of implying a model answered.

import { useState } from "react";
import Link from "next/link";
import { Play } from "lucide-react";
import { Button } from "@/components/ui/Button";
import { Badge, RISK_TONE } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { useToast } from "@/components/ui/Toast";
import { apiJson, errorMessage } from "@/lib/api";
import { announceDataChanged } from "@/lib/useResource";
import { refreshPendingApprovals } from "@/services/approvals.api";
import { refreshUnreadAlerts } from "@/services/floorAlerts.api";
import { useT } from "@/lib/useT";
import type { AgentDefinition, AgentRunResponse } from "@/services/contracts";

export type RunRecord = { at: string; result: AgentRunResponse };

export function useAgentRun() {
  const toast = useToast();
  const [runningId, setRunningId] = useState<string | null>(null);
  const [runs, setRuns] = useState<Record<string, RunRecord[]>>({});

  async function run(agent: AgentDefinition) {
    if (runningId) return;
    setRunningId(agent.id);
    try {
      const result = await apiJson<AgentRunResponse>(`/api/agents/${encodeURIComponent(agent.id)}/run`, { method: "POST", body: {} });
      setRuns((cur) => ({ ...cur, [agent.id]: [{ at: new Date().toISOString(), result }, ...(cur[agent.id] ?? [])].slice(0, 5) }));
      toast.push({
        title: `${agent.name} finished`,
        description: result.approvalPending ? "An approval is waiting in the queue." : "Insight stored; no approval needed."
      });
      announceDataChanged();
      void refreshPendingApprovals();
      void refreshUnreadAlerts();
    } catch (e) {
      toast.push({ tone: "error", title: `${agent.name} did not run`, description: errorMessage(e) });
    } finally {
      setRunningId(null);
    }
  }

  return { run, runningId, runs };
}

export function RunButton({ agent, running, disabled, onRun }: { agent: AgentDefinition; running: boolean; disabled: boolean; onRun: () => void }) {
  return (
    <Button variant="primary" size="sm" onClick={onRun} disabled={disabled || running} data-testid={`agent-run-${agent.id}`}>
      <Play size={13} /> {running ? "Running…" : "Run now"}
    </Button>
  );
}

export function RunResult({ record }: { record: RunRecord }) {
  const { t, locale } = useT();
  const r = record.result;
  const ins = r.insight;
  return (
    <div className="surface-2 p-3.5 space-y-2.5" data-testid="agent-run-result">
      <div className="flex flex-wrap items-center gap-2">
        <Badge tone="accent" title="Agent findings are rule-based today; no language model is called.">{r.source === "demo" ? "Rule-based demo run" : r.source}</Badge>
        <Badge tone={RISK_TONE[ins.riskTier]}>{t(`risk.${ins.riskTier}`)}</Badge>
        {r.approvalPending ? <Badge tone="warning" dot>Approval pending</Badge> : <Badge tone="success" dot>No approval needed</Badge>}
        <span className="text-caption text-fg-tertiary">{new Date(record.at).toLocaleTimeString(locale === "bn" ? "bn-BD" : "en-GB")}</span>
      </div>
      <div>
        <p className="text-body font-medium text-fg-primary">{locale === "bn" ? ins.titleBn : ins.title}</p>
        <p className="mt-0.5 text-caption text-fg-secondary">{locale === "bn" ? ins.findingBn : ins.finding}</p>
        <p className="mt-1 text-caption text-fg-primary">→ {ins.recommendationAction || ins.recommendationTitle}</p>
      </div>
      {r.energyInsight && (
        <p className="text-caption text-fg-secondary">Also raised an energy insight (duty score {r.energyInsight.score.toFixed(2)}).</p>
      )}
      {r.warning && <Callout tone="warning">{r.warning}</Callout>}
      <div className="flex flex-wrap gap-3 text-caption">
        {r.approvalPending && <Link className="text-accent hover:underline" href={`/app/approvals?focus=${encodeURIComponent(ins.id)}`}>Open the approval</Link>}
        {r.floorAlertId && <Link className="text-accent hover:underline" href="/app/activity">See the floor alert</Link>}
      </div>
    </div>
  );
}
