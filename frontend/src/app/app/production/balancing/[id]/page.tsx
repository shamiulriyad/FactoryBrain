"use client";

// /app/production/balancing/[id] — one line-balancing proposal next to the
// line's current layout: workload per station, balance metrics, constraints,
// assumptions and problems. It becomes the recorded layout only after an
// Admin approves it in the approval queue and someone applies it here.

import { useState } from "react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { ArrowLeft, CheckCircle2, Send } from "lucide-react";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { announceDataChanged, useOnDataChanged, useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { canOperate, useAuthStore } from "@/lib/auth";
import { cn } from "@/lib/cn";
import { fmt, fmtPct, plantDateTime } from "@/lib/production";
import { applyProposal, productionPaths, submitProposal } from "@/services/production.api";
import type { BalanceProposal, ProposalActionResponse, WorkloadMetrics } from "@/services/contracts";
import { MetricsRow, WorkloadBars } from "@/components/production/analysis";
import { ApprovalLine, NoteList, ProposalStatusBadge, useProductionConfig } from "@/components/production/shared";

export default function ProposalPage() {
  const id = useParams<{ id: string }>()?.id ?? "";
  const toast = useToast();
  const operator = canOperate(useAuthStore((s) => s.user));
  const config = useProductionConfig();
  const res = useResource<BalanceProposal>(id ? productionPaths.proposal(id) : null);
  useOnDataChanged(() => void res.reload());
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const p = res.data;

  async function act(fn: () => Promise<ProposalActionResponse>) {
    setBusy(true);
    try {
      const r = await fn();
      res.setData(r.proposal);
      setMessage(r.message);
      toast.push({ title: r.message, tone: r.outcome === "applied" || r.outcome === "submitted" ? "success" : "info" });
      announceDataChanged();
    } catch (e) {
      toast.push({ title: "Not done", description: errorMessage(e), tone: "error" });
      void res.reload();
    } finally {
      setBusy(false);
    }
  }

  if (res.loading) return <div className="page"><SkeletonRows rows={6} /></div>;
  if (!p) return <div className="page"><Callout tone="danger" title="Could not load the proposal" action={<Button size="sm" onClick={() => void res.reload()}>Retry</Button>}>{errorMessage(res.error)}</Callout></div>;
  const proposed = p.result.proposed;
  const cmp = p.comparison;
  const approval = p.approval;

  return (
    <div className="page" data-testid="proposal-page">
      <Link href="/app/production?tab=balancing" className="inline-flex items-center gap-1 text-caption text-fg-tertiary hover:text-fg-primary"><ArrowLeft size={13} /> Line balancing</Link>
      <PageHeader className="mt-2" eyebrow="Line-balance proposal" title={p.reference}
        description={<><Link href={`/app/production/lines/${encodeURIComponent(p.lineId)}`} className="text-accent hover:underline">{p.lineName}</Link> · {p.styleCode} {p.styleName} · {p.workstations} workstations{p.targetOutputPerHour ? ` · target ${fmt(p.targetOutputPerHour, 1)} pcs/h` : ""}</>}
        meta={<ProposalStatusBadge status={p.status} />}
        actions={operator && (
          <>
            {p.status === "proposed" && <Button variant="primary" iconLeft={<Send size={14} />} disabled={busy || p.inputsChanged} data-testid="proposal-submit"
              onClick={() => void act(() => submitProposal(p.id, { expectedVersion: p.version }))}>Submit for approval</Button>}
            {p.status === "pending_approval" && approval?.status === "approved" && (
              <Button variant="success" iconLeft={<CheckCircle2 size={14} />} disabled={busy} data-testid="proposal-apply" onClick={() => void act(() => applyProposal(p.id))}>Apply as current layout</Button>
            )}
            {p.status === "pending_approval" && approval?.status === "rejected" && (
              <Button variant="secondary" disabled={busy} data-testid="proposal-close" onClick={() => void act(() => applyProposal(p.id))}>Record rejection</Button>
            )}
          </>
        )}
      />

      {message && <Callout tone="info" className="mt-4" testId="proposal-message">{message}</Callout>}
      {p.statusReason && <Callout tone={p.status === "rejected" ? "danger" : "warning"} className="mt-4">{p.statusReason}</Callout>}
      {(p.inputsChanged || p.baselineChanged) && p.status !== "applied" && (
        <Callout tone="warning" className="mt-4" title="This proposal is out of date" testId="proposal-stale">
          {p.inputsChanged ? "The style's operation bulletin changed after the analysis." : "The line's recorded layout changed after the analysis."} It can&apos;t be applied; run the analysis again.
        </Callout>
      )}
      {p.status === "pending_approval" && (
        <Callout tone="warning" className="mt-4" title={approval?.status === "approved" ? "Approved: ready to apply" : approval?.status === "rejected" ? "Rejected" : "Waiting for an Admin's decision"}>
          Applying records this distribution as the line&apos;s current layout. It does not reassign operators, change schedules or send anything to machines.
          <div className="mt-1"><ApprovalLine approval={approval} /></div>
        </Callout>
      )}

      {!proposed ? (
        <NoteList title="No valid proposal" items={p.result.problems} tone="warning" />
      ) : (
        <>
          <Panel className="mt-4" title="Current vs proposed" subtitle="At standard minutes (100 % performance). Lower cycle time and higher balance efficiency are better." testId="proposal-comparison">
            <div className="overflow-x-auto">
              <table className="data-table">
                <thead><tr><th></th><th className="num">Workstations</th><th className="num">Cycle time</th><th className="num">Capacity</th><th className="num">Balance efficiency</th><th className="num">Smoothness</th><th>Busiest station</th></tr></thead>
                <tbody>
                  <MetricRow label="Current layout" m={cmp?.current ?? null} bottleneck={cmp?.currentBottleneck ?? null} />
                  <MetricRow label="Proposed" m={proposed} bottleneck={cmp?.proposedBottleneck ?? null} strong />
                </tbody>
              </table>
            </div>
            {cmp?.current ? (
              <p className="mt-2 text-caption text-fg-secondary">
                Cycle time {cmp.cycleTimeChangePct == null ? "—" : `${cmp.cycleTimeChangePct > 0 ? "+" : ""}${fmt(cmp.cycleTimeChangePct, 1)} %`},
                capacity {cmp.capacityChangePct == null ? "—" : `${cmp.capacityChangePct > 0 ? "+" : ""}${fmt(cmp.capacityChangePct, 1)} %`},
                balance efficiency {cmp.balanceEfficiencyChangePts == null ? "—" : `${cmp.balanceEfficiencyChangePts > 0 ? "+" : ""}${fmt(cmp.balanceEfficiencyChangePts, 1)} points`}.
              </p>
            ) : <p className="mt-2 text-caption text-fg-tertiary">No current layout is recorded for this style on this line, so there is nothing to compare with.</p>}
            <p className="mt-1 text-caption text-fg-tertiary">
              Lower bound: {fmt(p.result.lowerBoundCycleTimeMinutes, 3)} min per piece (Σ SMV ÷ workstations).
              {p.result.targetCycleTimeMinutes != null && ` Target cycle ${fmt(p.result.targetCycleTimeMinutes, 3)} min: ${p.result.targetMet ? "met" : `not met (about ${p.result.stationsNeededForTarget} workstations needed)`}.`}
            </p>
          </Panel>

          <div className="mt-4 grid grid-cols-1 xl:grid-cols-2 gap-4">
            <Panel title="Current workload">
              {p.current ? <><MetricsRow m={p.current.metrics} /><div className="mt-3"><WorkloadBars stations={p.current.stations} metrics={p.current.metrics} testId="current-bars" /></div></>
                : <p className="text-caption text-fg-tertiary">Not recorded.</p>}
            </Panel>
            <Panel title="Proposed workload">
              <MetricsRow m={proposed} />
              <div className="mt-3"><WorkloadBars stations={p.proposedStations} metrics={proposed} testId="proposed-bars" /></div>
            </Panel>
          </div>
        </>
      )}

      <div className="mt-4 grid grid-cols-1 xl:grid-cols-2 gap-4">
        <Panel title="Method and assumptions">
          <p className="text-caption text-fg-secondary">{p.result.method}</p>
          <ul className="mt-2 list-disc pl-4 text-caption text-fg-secondary space-y-0.5">{p.result.assumptions.map((a, i) => <li key={i}>{a}</li>)}</ul>
        </Panel>
        <Panel title="Review trail">
          <ul className="text-caption text-fg-secondary space-y-1">
            <li>Computed by {p.createdByEmail} · {plantDateTime(p.createdAt, config.offset)}</li>
            {p.submittedByEmail && <li>Submitted by {p.submittedByEmail} · {plantDateTime(p.submittedAt, config.offset)}</li>}
            {approval && <li><ApprovalLine approval={approval} /></li>}
            {p.appliedByEmail && <li>Applied by {p.appliedByEmail} · {plantDateTime(p.appliedAt, config.offset)}</li>}
          </ul>
          {p.result.problems.length > 0 && <div className="mt-3"><NoteList title="Problems" items={p.result.problems} tone="warning" /></div>}
        </Panel>
      </div>
    </div>
  );
}

function MetricRow({ label, m, bottleneck, strong }: { label: string; m: WorkloadMetrics | null; bottleneck: string | null; strong?: boolean }) {
  return (
    <tr className={cn(strong && "font-medium")}>
      <td>{label}</td>
      <td className="num">{m ? m.stations : "—"}</td>
      <td className="num">{m ? `${fmt(m.cycleTimeMinutes, 3)} min` : "—"}</td>
      <td className="num">{m ? `${fmt(m.capacityPerHour, 1)}/h` : "—"}</td>
      <td className="num">{m ? fmtPct(m.balanceEfficiencyPct) : "—"}</td>
      <td className="num">{m ? fmt(m.smoothnessIndex, 3) : "—"}</td>
      <td className="text-caption">{bottleneck ?? "—"}</td>
    </tr>
  );
}
