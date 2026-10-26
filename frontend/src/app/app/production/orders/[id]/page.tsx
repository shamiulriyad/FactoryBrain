"use client";

// /app/production/orders/[id] — one production order: quantity, schedule,
// progress by stage, WIP, the plans that cover it and the delivery-risk
// assessment with the observations, assumptions and limits it rests on.

import { useState } from "react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { ArrowLeft, Pencil, Plus } from "lucide-react";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { SkeletonRows } from "@/components/ui/Status";
import { useOnDataChanged, useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { canOperate, useAuthStore } from "@/lib/auth";
import { ORDER_STATUS_LABEL, STAGE_LABEL, fmt, fmtHours, plantDateTime } from "@/lib/production";
import { productionPaths } from "@/services/production.api";
import type { OrderDetail } from "@/services/contracts";
import { OrderFormModal, PlanFormModal } from "@/components/production/forms";
import { PlanRows } from "@/components/production/panels";
import { KeyValue, RiskLevelBadge, WipList, useMasterData, useProductionConfig } from "@/components/production/shared";

export default function OrderPage() {
  const id = useParams<{ id: string }>()?.id ?? "";
  const operator = canOperate(useAuthStore((s) => s.user));
  const res = useResource<OrderDetail>(id ? productionPaths.order(id) : null);
  useOnDataChanged(() => void res.reload());
  const config = useProductionConfig();
  const md = useMasterData();
  const [editing, setEditing] = useState(false);
  const [planning, setPlanning] = useState(false);
  const d = res.data;

  if (res.loading) return <div className="page"><SkeletonRows rows={6} /></div>;
  if (!d) return <div className="page"><Callout tone="danger" title="Could not load the order" action={<Button size="sm" onClick={() => void res.reload()}>Retry</Button>}>{errorMessage(res.error)}</Callout></div>;
  const o = d.order;
  const r = d.risk;
  const sewn = d.stages.find((s) => s.stage === "sewing_output")?.good ?? 0;

  return (
    <div className="page" data-testid="order-page">
      <Link href="/app/production?tab=orders" className="inline-flex items-center gap-1 text-caption text-fg-tertiary hover:text-fg-primary"><ArrowLeft size={13} /> Orders</Link>
      <PageHeader className="mt-2" eyebrow="Production order" title={o.orderNumber}
        description={`${d.style.code} · ${d.style.name}${o.buyer ? ` · ${o.buyer}` : ""}`}
        meta={<><Badge>{ORDER_STATUS_LABEL[o.status]}</Badge>{o.status === "open" && <RiskLevelBadge level={r.level} testId="order-risk-level" />}</>}
        actions={operator && (
          <>
            <Button variant="secondary" iconLeft={<Pencil size={14} />} onClick={() => setEditing(true)} data-testid="order-edit">Edit</Button>
            {o.status === "open" && <Button variant="primary" iconLeft={<Plus size={14} />} onClick={() => setPlanning(true)} data-testid="order-plan">Plan this order</Button>}
          </>
        )}
      />

      <div className="mt-4 grid grid-cols-2 lg:grid-cols-5 gap-3">
        <div className="surface p-4"><KeyValue label="Quantity">{fmt(o.quantity)} pcs</KeyValue></div>
        <div className="surface p-4"><KeyValue label="Due (end of day, plant-local)">{o.dueDate}</KeyValue></div>
        <div className="surface p-4"><KeyValue label="Sewn, good">{fmt(sewn)} pcs</KeyValue></div>
        <div className="surface p-4"><KeyValue label="Remaining">{fmt(r.remainingQuantity)} pcs</KeyValue></div>
        <div className="surface p-4"><KeyValue label="SMV">{d.style.effectiveSmv == null ? "missing" : `${fmt(d.style.effectiveSmv, 3)} min`}</KeyValue></div>
      </div>

      {o.status === "open" && (
        <Panel className="mt-4" title="Delivery risk" testId="order-risk"
          subtitle={`Evaluated ${plantDateTime(r.evaluatedAt, config.offset)} (plant time) for line${r.lineIds.length === 1 ? "" : "s"} ${r.lineIds.join(", ") || "— (no active plan)"}. ${r.method}`}>
          <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-6 gap-3">
            <KeyValue label="Level"><RiskLevelBadge level={r.level} /></KeyValue>
            <KeyValue label="Required rate">{r.requiredRatePerLineHour == null ? "—" : `${fmt(r.requiredRatePerLineHour, 1)} pcs/line-h`}</KeyValue>
            <KeyValue label="Observed rate">{r.observedRatePerLineHour == null ? "—" : `${fmt(r.observedRatePerLineHour, 1)} pcs/line-h`}{r.observedRateLow != null && <span className="block text-caption text-fg-tertiary">daily {fmt(r.observedRateLow, 1)}–{fmt(r.observedRateHigh, 1)}</span>}</KeyValue>
            <KeyValue label="Planned rate">{r.plannedRatePerHour == null ? "—" : `${fmt(r.plannedRatePerHour, 1)} pcs/h`}</KeyValue>
            <KeyValue label="Scheduled time left">{fmtHours(r.remainingScheduledMinutes)}</KeyValue>
            <KeyValue label="Projected completion">
              {r.projectedCompletion ? plantDateTime(r.projectedCompletion, config.offset) : "—"}
              {r.projectedCompletionEarliest && <span className="block text-caption text-fg-tertiary">range {plantDateTime(r.projectedCompletionEarliest, config.offset)} – {r.projectedCompletionLatest ? plantDateTime(r.projectedCompletionLatest, config.offset) : "beyond the schedule"}</span>}
            </KeyValue>
          </div>
          {r.projectedShortfall != null && r.projectedShortfall > 0 && <p className="mt-2 text-caption text-[var(--risk-high)]">About {fmt(r.projectedShortfall)} pieces short at the due time if the observed rate holds.</p>}
          <div className="mt-3 grid grid-cols-1 lg:grid-cols-3 gap-4 text-caption">
            <div><p className="font-medium text-fg-primary">Observed</p><ul className="mt-1 list-disc pl-4 text-fg-secondary space-y-0.5">{r.observations.map((x, i) => <li key={i}>{x}</li>)}</ul></div>
            <div><p className="font-medium text-fg-primary">Assumptions</p><ul className="mt-1 list-disc pl-4 text-fg-secondary space-y-0.5">{r.assumptions.map((x, i) => <li key={i}>{x}</li>)}</ul></div>
            <div><p className="font-medium text-fg-primary">Limitations</p>
              {r.limitations.length === 0 && r.missingInputs.length === 0 ? <p className="mt-1 text-fg-tertiary">None noted.</p> : (
                <ul className="mt-1 list-disc pl-4 text-fg-secondary space-y-0.5">{[...r.missingInputs.map((m) => `Missing: ${m}`), ...r.limitations].map((x, i) => <li key={i}>{x}</li>)}</ul>
              )}
            </div>
          </div>
          {r.recommendations.length > 0 && (
            <div className="mt-4">
              <p className="text-body font-semibold text-fg-primary">Possible actions</p>
              <ul className="mt-2 space-y-2" data-testid="order-recommendations">
                {r.recommendations.map((x) => (
                  <li key={x.code} className="rounded-[var(--radius-control)] border border-border-subtle p-3">
                    <p className="text-body text-fg-primary">{x.action}</p>
                    <p className="mt-0.5 text-caption text-fg-secondary"><span className="font-medium">Why:</span> {x.reason}</p>
                    <p className="text-caption text-fg-tertiary"><span className="font-medium">Based on:</span> {x.evidence}</p>
                    <p className="text-caption text-fg-tertiary"><span className="font-medium">Check:</span> {x.caveat}</p>
                  </li>
                ))}
              </ul>
              <p className="mt-2 text-caption text-fg-tertiary">Suggestions from transparent rules over the recorded data. Nothing is changed automatically.</p>
            </div>
          )}
        </Panel>
      )}

      <div className="mt-4 grid grid-cols-1 xl:grid-cols-2 gap-4">
        <Panel title="Progress by stage" subtitle="All lines, live records only (corrections replace the records they correct).">
          <table className="data-table" data-testid="order-stages">
            <thead><tr><th>Stage</th><th className="num">Good</th><th className="num">Rework</th><th className="num">Reject</th><th className="num">Records</th></tr></thead>
            <tbody>{d.stages.map((s) => <tr key={s.stage}><td>{STAGE_LABEL[s.stage]}</td><td className="num">{fmt(s.good)}</td><td className="num">{fmt(s.rework)}</td><td className="num">{fmt(s.reject)}</td><td className="num">{s.records}</td></tr>)}</tbody>
          </table>
        </Panel>
        <div className="space-y-4">
          <Panel title="Work in process"><WipList buffers={d.wip} /></Panel>
          <Panel title="By line">
            {d.byLine.length === 0 ? <p className="text-caption text-fg-tertiary">No sewing output recorded yet.</p> : (
              <ul className="space-y-1 text-body">
                {d.byLine.map((l) => (
                  <li key={l.lineId} className="flex justify-between gap-2">
                    <Link href={`/app/production/lines/${encodeURIComponent(l.lineId)}`} className="font-mono text-[12px] text-accent hover:underline">{l.lineId}</Link>
                    <span className="tabular-nums text-fg-secondary">{fmt(l.good)} good · last interval {plantDateTime(l.lastIntervalEnd, config.offset)}</span>
                  </li>
                ))}
              </ul>
            )}
          </Panel>
        </div>
      </div>

      <div className="mt-4">
        <h2 className="text-[15px] font-semibold text-fg-primary mb-2">Plans</h2>
        {d.plans.length === 0 ? <Callout tone="info">No plan covers this order yet.{operator ? " Use “Plan this order”." : ""}</Callout> : <PlanRows plans={d.plans} />}
      </div>

      <OrderFormModal open={editing} onClose={() => setEditing(false)} styles={md.styles.data ?? []} order={o} onSaved={() => void res.reload()} />
      <PlanFormModal open={planning} onClose={() => setPlanning(false)} lines={md.lines.data ?? []} shifts={md.shifts.data ?? []} orderId={o.id}
        onSaved={() => void res.reload()} />
    </div>
  );
}
