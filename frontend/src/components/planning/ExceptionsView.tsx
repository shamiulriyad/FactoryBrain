"use client";

// Production exceptions: rule-based detections (output behind the published
// target, delivery at risk, line unavailable, capacity shortage, missing
// counter data, excess allocation, unplanned interruption) with the evidence
// they used and what to look into. They never name a person as the cause.

import { useState } from "react";
import { AlertTriangle, RefreshCw } from "lucide-react";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { EmptyState } from "@/components/ui/EmptyState";
import { FilterSelect } from "@/components/ui/Field";
import { Panel } from "@/components/ui/Panel";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { ExceptionStatusBadge, ReasonDialog, SeverityBadge, useSubmit, type PlanningScope } from "@/components/planning/shared";
import { dhaka } from "@/lib/monitoring";
import { announceDataChanged, useOnDataChanged, useResource } from "@/lib/useResource";
import { planningApi, type Paged, type ProductionException } from "@/lib/planning";

const TYPES: [string, string][] = [
  ["OUTPUT_BEHIND_TARGET", "Output behind the published target"], ["DELIVERY_AT_RISK", "Delivery deadline at risk"], ["LINE_UNAVAILABLE", "Production line unavailable"],
  ["CAPACITY_SHORTAGE", "Capacity shortage"], ["MISSING_COUNTER_DATA", "Production counter data missing"], ["EXCESS_ALLOCATION", "Excess production allocation"],
  ["PRODUCTION_INTERRUPTION", "Unplanned production interruption"]
];

export function ExceptionsView({ scope }: { scope: PlanningScope }) {
  const { q, ctx, factoryId } = scope;
  const status = q.get("status") || "";
  const type = q.get("type");
  const severity = q.get("severity");
  const line = q.get("line");
  const toast = useToast();
  const run = useSubmit();
  const [resolving, setResolving] = useState<ProductionException | null>(null);
  const res = useResource<Paged<ProductionException>>(planningApi.exceptions({ factoryId, status: status === "active" ? undefined : status, type, severity, lineId: line, pageSize: 100 }), { pollMs: 60_000 });
  useOnDataChanged(() => void res.reload());
  const d = res.data;
  const items = d?.items.filter((e) => status !== "active" || e.status !== "RESOLVED") ?? [];
  const canReview = ctx.capabilities.reviewExceptions;

  async function evaluate() {
    const r = await run.run(() => planningApi.evaluateExceptions({ factoryId }));
    if (!r) return;
    announceDataChanged();
    toast.push({ title: "Exception check done", description: `${r.raised} new, ${r.refreshed} refreshed, ${r.autoResolved} closed (${r.checked} things checked).` });
  }
  async function ack(e: ProductionException) {
    const r = await run.run(() => planningApi.acknowledgeException(e.id, { expectedVersion: e.version }));
    if (r) announceDataChanged();
  }

  return (
    <>
      <div className="surface p-3 mb-4 flex flex-wrap items-end gap-3">
        <FilterSelect label="Status" value={status || "active"} onChange={(v) => q.set({ status: v === "active" ? null : v })} testId="exc-status">
          <option value="active">Open and acknowledged</option><option value="OPEN">Open</option><option value="ACKNOWLEDGED">Acknowledged</option><option value="RESOLVED">Resolved</option>
        </FilterSelect>
        <FilterSelect label="Type" value={type} onChange={(v) => q.set({ type: v || null })}><option value="">All</option>{TYPES.map(([k, v]) => <option key={k} value={k}>{v}</option>)}</FilterSelect>
        <FilterSelect label="Severity" value={severity} onChange={(v) => q.set({ severity: v || null })}><option value="">All</option><option value="HIGH">High</option><option value="MEDIUM">Medium</option><option value="LOW">Low</option></FilterSelect>
        <FilterSelect label="Line" value={line} onChange={(v) => q.set({ line: v || null })}><option value="">All</option>{ctx.lines.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}</FilterSelect>
        {canReview && !ctx.capabilities.lineScoped && <Button className="ml-auto" variant="secondary" iconLeft={<RefreshCw size={13} className={run.busy ? "animate-spin" : ""} />} onClick={() => void evaluate()} disabled={run.busy} data-testid="exc-evaluate">Run the check now</Button>}
      </div>
      <Callout tone="info" className="mb-4">The check also runs every ten minutes for factories with a published plan. A condition that clears closes its exception automatically. These are evidence from counters, machines and capacity — not a judgement of any person.</Callout>
      {run.error != null && <Callout tone="danger" className="mb-3">{(run.error as Error).message}</Callout>}
      {res.error && !d ? <ErrorState title="Could not load exceptions" body={res.error.message} retryLabel="Try again" onRetry={() => void res.reload()} />
        : !d ? <SkeletonRows rows={5} />
        : items.length === 0 ? <div className="surface"><EmptyState icon={<AlertTriangle size={20} />} title="No exceptions" body="Nothing matching is open. Run the check to evaluate today's published plan." /></div>
        : (
          <div className="space-y-3" data-testid="exc-list">
            {items.map((e) => (
              <Panel key={e.id} testId="exc-item" title={<span className="flex flex-wrap items-center gap-2"><SeverityBadge severity={e.severity} /><ExceptionStatusBadge status={e.status} />{e.title}</span>}
                subtitle={`${e.typeLabel} · detected ${dhaka(e.detectedAt)} · last seen ${dhaka(e.lastDetectedAt)}${e.occurrences > 1 ? ` (${e.occurrences} checks)` : ""} · ${e.sourceType === "SIMULATED" ? "simulated data" : "real data"}`}
                right={canReview && e.status !== "RESOLVED" && (
                  <span className="flex gap-2">{e.status === "OPEN" && <Button size="sm" variant="secondary" onClick={() => void ack(e)} data-testid="exc-ack">Acknowledge</Button>}
                    <Button size="sm" variant="primary" onClick={() => setResolving(e)} data-testid="exc-resolve">Resolve</Button></span>
                )}>
                <div className="p-4 space-y-3 text-caption">
                  <p><b>Why:</b> {e.reason}</p>
                  <dl className="grid grid-cols-2 md:grid-cols-4 gap-x-4 gap-y-1">{e.data.map((f) => <div key={f.label}><dt className="text-fg-tertiary">{f.label}</dt><dd className="tabular-nums">{f.value}</dd></div>)}</dl>
                  <p><b>Look into:</b> {e.suggestedInvestigation}</p>
                  {e.acknowledgedByEmail && <p className="text-fg-tertiary">Acknowledged by {e.acknowledgedByEmail} {dhaka(e.acknowledgedAt)}</p>}
                  {e.status === "RESOLVED" && <p className="text-fg-tertiary">Resolved by {e.resolvedByEmail}{e.autoResolved ? " (condition cleared)" : ""} {dhaka(e.resolvedAt)}: {e.resolutionNote}</p>}
                </div>
              </Panel>
            ))}
          </div>
        )}
      <ReasonDialog open={resolving !== null} onClose={() => setResolving(null)} title="Resolve this exception" label="What was found or done" confirmLabel="Resolve" testId="exc-resolve-dialog"
        description={resolving?.title} onConfirm={async (note) => { await planningApi.resolveException(resolving!.id, { note, expectedVersion: resolving!.version }); announceDataChanged(); }} />
    </>
  );
}
