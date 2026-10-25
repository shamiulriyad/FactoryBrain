"use client";

import Link from "next/link";
import { ArrowUpRight, Bot, RefreshCw } from "lucide-react";
import { PageHeader } from "@/components/ui/PageHeader";
import { Button } from "@/components/ui/Button";
import { Badge, RISK_TONE } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { EmptyState } from "@/components/ui/EmptyState";
import { SkeletonRows } from "@/components/ui/Status";
import { AgentAvatar } from "@/components/agents/AgentAvatar";
import { RunButton, RunResult, useAgentRun } from "@/components/agents/AgentRun";
import { useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { canOperate, useAuthStore } from "@/lib/auth";
import { useT } from "@/lib/useT";
import { cn } from "@/lib/cn";
import type { AgentDefinition } from "@/services/contracts";

export default function AgentsPage() {
  const { t, locale } = useT();
  const operator = canOperate(useAuthStore((s) => s.user));
  const roster = useResource<AgentDefinition[]>("/api/agents");
  const { run, runningId, runs } = useAgentRun();

  return (
    <div className="page" data-tour="agents">
      <PageHeader
        eyebrow="Intelligence"
        title={t("agents.title")}
        description="The agents registered on the server. A run stores an insight, queues an approval when the risk isn't low, and raises a floor alert."
        actions={
          <Button variant="secondary" size="sm" onClick={() => void roster.reload()} disabled={roster.refreshing} aria-label="Refresh agents">
            <RefreshCw size={13} className={cn(roster.refreshing && "animate-spin")} /> Refresh
          </Button>
        }
      />

      <Callout tone="info" className="mt-5" title="Agent findings come from the RAG service">
        A run sends the agent&apos;s purpose and the current line and machine readings to the Python RAG service, which answers from the indexed PDFs. Nothing is stored when no document covers the question.
        {!operator && " Running an agent needs the Operator or Admin role."}
      </Callout>

      {roster.error && !roster.data && (
        <Callout tone="danger" className="mt-4" title="Could not load the agents" action={<Button size="sm" onClick={() => void roster.reload()}>Retry</Button>}>
          {errorMessage(roster.error)}
        </Callout>
      )}

      {roster.loading ? (
        <div className="mt-5"><SkeletonRows rows={3} rowHeight="h-40" /></div>
      ) : (roster.data?.length ?? 0) === 0 && !roster.error ? (
        <div className="mt-5 surface"><EmptyState icon={<Bot size={22} />} title="No agents are registered on the server." /></div>
      ) : (
        <ul className="mt-5 grid grid-cols-1 lg:grid-cols-2 gap-4" data-testid="agent-roster">
          {(roster.data ?? []).map((a) => (
            <li key={a.id} className="surface p-4 md:p-5 flex flex-col gap-4" data-testid="agent-card" data-agent-id={a.id}>
              <div className="flex items-start gap-3">
                <AgentAvatar agentId={a.id} size={40} />
                <div className="min-w-0 flex-1">
                  <div className="flex flex-wrap items-center gap-2">
                    <h2 className="text-[15px] leading-[22px] font-semibold text-fg-primary">{locale === "bn" ? a.nameBn : a.name}</h2>
                    <Badge tone={a.status === "ready" ? "success" : "neutral"} dot>{a.status}</Badge>
                  </div>
                  <p className="mt-1 text-body text-fg-secondary">{locale === "bn" ? a.purposeBn : a.purpose}</p>
                </div>
              </div>
              <dl className="grid grid-cols-3 gap-2 text-caption">
                <div className="surface-2 px-2.5 py-2"><dt className="text-fg-tertiary">Risk tier</dt><dd className="mt-0.5"><Badge tone={RISK_TONE[a.risk] ?? "neutral"}>{a.risk}</Badge></dd></div>
                <div className="surface-2 px-2.5 py-2"><dt className="text-fg-tertiary">Execution</dt><dd className="mt-0.5 text-fg-primary font-medium">{a.execution.replace(/_/g, " ")}</dd></div>
                <div className="surface-2 px-2.5 py-2 min-w-0"><dt className="text-fg-tertiary">Model label</dt><dd className="mt-0.5 text-fg-primary font-mono truncate" title={a.model}>{a.model}</dd></div>
              </dl>
              {a.contextSlices.length > 0 && (
                <div>
                  <p className="eyebrow mb-1.5">Reads</p>
                  <div className="flex flex-wrap gap-1.5">
                    {a.contextSlices.map((s) => <span key={s} className="badge">{s}</span>)}
                  </div>
                </div>
              )}
              <div className="mt-auto flex items-center justify-between gap-2 pt-3 border-t border-border-subtle">
                <Link href={`/app/agents/${encodeURIComponent(a.id)}`} className="text-caption text-accent hover:underline inline-flex items-center gap-1">
                  Details <ArrowUpRight size={12} />
                </Link>
                {operator ? (
                  <RunButton agent={a} running={runningId === a.id} disabled={runningId !== null} onRun={() => void run(a)} />
                ) : (
                  <span className="text-caption text-fg-tertiary" data-testid="agent-run-blocked">{t("auth.forbidden")}</span>
                )}
              </div>
              {(runs[a.id]?.length ?? 0) > 0 && <RunResult record={runs[a.id][0]} />}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
