"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { ArrowLeft } from "lucide-react";
import { useT } from "@/lib/useT";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Badge, RISK_TONE } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { SkeletonRows } from "@/components/ui/Status";
import { AgentAvatar } from "@/components/agents/AgentAvatar";
import { RunButton, RunResult, useAgentRun } from "@/components/agents/AgentRun";
import { useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { canOperate, useAuthStore } from "@/lib/auth";
import type { AgentDefinition } from "@/services/contracts";

export default function AgentDetailPage() {
  const { id } = useParams<{ id: string }>();
  const { t, locale } = useT();
  const operator = canOperate(useAuthStore((s) => s.user));
  const roster = useResource<AgentDefinition[]>("/api/agents");
  const { run, runningId, runs } = useAgentRun();
  const agent = roster.data?.find((a) => a.id === id) ?? null;

  return (
    <div className="page page-narrow">
      <Link href="/app/agents" className="inline-flex items-center gap-1 text-caption text-fg-secondary hover:text-fg-primary mb-4">
        <ArrowLeft size={12} /> All agents
      </Link>
      {roster.loading ? (
        <SkeletonRows rows={2} rowHeight="h-32" />
      ) : roster.error && !roster.data ? (
        <Callout tone="danger" title="Could not load the agents">{errorMessage(roster.error)}</Callout>
      ) : !agent ? (
        <Callout tone="warning" title="Agent not found">No agent with id <span className="font-mono">{id}</span> is registered on the server.</Callout>
      ) : (
        <>
          <PageHeader
            eyebrow="Agent"
            title={
              <span className="inline-flex items-center gap-3">
                <AgentAvatar agentId={agent.id} size={36} />
                {locale === "bn" ? agent.nameBn : agent.name}
              </span>
            }
            description={locale === "bn" ? agent.purposeBn : agent.purpose}
            actions={
              operator ? (
                <RunButton agent={agent} running={runningId === agent.id} disabled={runningId !== null} onRun={() => void run(agent)} />
              ) : (
                <span className="text-caption text-fg-tertiary">{t("auth.forbidden")}</span>
              )
            }
          />
          <Panel className="mt-5" title="Configuration">
            <dl className="grid grid-cols-[auto_1fr] gap-x-6 gap-y-2 text-caption">
              <dt className="text-fg-tertiary">Id</dt><dd className="font-mono text-fg-secondary">{agent.id}</dd>
              <dt className="text-fg-tertiary">Status</dt><dd><Badge tone={agent.status === "ready" ? "success" : "neutral"} dot>{agent.status}</Badge></dd>
              <dt className="text-fg-tertiary">Risk tier</dt><dd><Badge tone={RISK_TONE[agent.risk] ?? "neutral"}>{agent.risk}</Badge></dd>
              <dt className="text-fg-tertiary">Execution</dt><dd className="text-fg-secondary">{agent.execution.replace(/_/g, " ")}</dd>
              <dt className="text-fg-tertiary">Answers from</dt><dd className="font-mono text-fg-secondary">{agent.model}</dd>
              <dt className="text-fg-tertiary">Reads</dt><dd className="text-fg-secondary">{agent.contextSlices.join(", ") || "—"}</dd>
              <dt className="text-fg-tertiary">Insights stored</dt><dd className="text-fg-secondary tabular-nums">{agent.tasksToday} today · {agent.recentCount} in total</dd>
            </dl>
            <p className="mt-4 text-caption text-fg-tertiary">A run asks the RAG service about this agent&apos;s readings; the finding is its answer and the passages it cites.</p>
          </Panel>
          {(runs[agent.id]?.length ?? 0) > 0 && (
            <Panel className="mt-4" title="Runs in this session">
              <div className="space-y-3">
                {runs[agent.id].map((r) => <RunResult key={r.at} record={r} />)}
              </div>
            </Panel>
          )}
        </>
      )}
    </div>
  );
}
