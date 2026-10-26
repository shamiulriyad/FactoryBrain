"use client";

// /app/production/plans/[id] — one plan: its inputs, the targets the API
// derived from them, planned vs actual to date, the approval state and the
// full change history. Workflow: draft → submit (approval queue) → activate
// after approval; changes to an active plan are requested, approved, then
// applied. The buttons call the API; the API decides whether each is allowed.

import { useState } from "react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { ArrowLeft, CheckCircle2, Pencil, Send, XCircle } from "lucide-react";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { ConfirmDialog } from "@/components/ui/Modal";
import { Input } from "@/components/ui/Input";
import { SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { announceDataChanged, useOnDataChanged, useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { canOperate, useAuthStore } from "@/lib/auth";
import { formatDateTime } from "@/lib/format";
import { fmt, fmtHours, fmtPct, fmtSigned, plantDateTime } from "@/lib/production";
import { planAction, productionPaths, resolvePlanChange } from "@/services/production.api";
import type { PlanActionResponse, PlanDetail } from "@/services/contracts";
import { PlanFormModal } from "@/components/production/forms";
import { ApprovalLine, KeyValue, NoteList, PlanStatusBadge, useMasterData, useProductionConfig } from "@/components/production/shared";

type Pending = { kind: "submit" | "complete" | "cancel"; title: string; needsReason: boolean } | null;

export default function PlanPage() {
  const id = useParams<{ id: string }>()?.id ?? "";
  const toast = useToast();
  const operator = canOperate(useAuthStore((s) => s.user));
  const res = useResource<PlanDetail>(id ? productionPaths.plan(id) : null);
  useOnDataChanged(() => void res.reload());
  const config = useProductionConfig();
  const md = useMasterData();
  const [editing, setEditing] = useState(false);
  const [busy, setBusy] = useState(false);
  const [pending, setPending] = useState<Pending>(null);
  const [reason, setReason] = useState("");
  const [lastMessage, setLastMessage] = useState<string | null>(null);
  const p = res.data;

  async function act(fn: () => Promise<PlanActionResponse>) {
    setBusy(true);
    try {
      const r = await fn();
      res.setData(r.plan);
      setLastMessage(r.message);
      toast.push({ title: r.message, tone: r.outcome.includes("rejected") || r.outcome.includes("withdrawn") || r.outcome === "returned_to_draft" || r.outcome === "change_superseded" ? "info" : "success" });
      announceDataChanged();
    } catch (e) {
      toast.push({ title: "Not done", description: errorMessage(e), tone: "error" });
      void res.reload();
    } finally {
      setBusy(false);
      setPending(null);
      setReason("");
    }
  }

  if (res.loading) return <div className="page"><SkeletonRows rows={6} /></div>;
  if (!p) {
    return (
      <div className="page">
        <Callout tone="danger" title="Could not load the plan" action={<Button size="sm" onClick={() => void res.reload()}>Retry</Button>}>{errorMessage(res.error)}</Callout>
      </div>
    );
  }
  const s = p.summary;
  const approval = p.activationApproval;
  const pendingChange = p.changes.find((c) => c.status === "pending");

  return (
    <div className="page" data-testid="plan-page">
      <Link href="/app/production?tab=plans" className="inline-flex items-center gap-1 text-caption text-fg-tertiary hover:text-fg-primary"><ArrowLeft size={13} /> Plans</Link>
      <PageHeader className="mt-2" eyebrow="Production plan" title={s.reference}
        description={<>Order <Link href={`/app/production/orders/${s.orderId}`} className="text-accent hover:underline">{s.orderNumber}</Link> on <Link href={`/app/production/lines/${encodeURIComponent(s.lineId)}`} className="text-accent hover:underline">{s.lineName}</Link>, {s.startDate} → {s.endDate}</>}
        meta={<><PlanStatusBadge status={s.status} />{s.revision > 0 && <Badge>revision {s.revision}</Badge>}</>}
        actions={operator && (
          <>
            {(s.status === "draft" || s.status === "active") && (
              <Button variant="secondary" iconLeft={<Pencil size={14} />} onClick={() => setEditing(true)} disabled={busy || (s.status === "active" && !!pendingChange)} data-testid="plan-edit">
                {s.status === "active" ? "Request change" : "Edit draft"}
              </Button>
            )}
            {s.status === "draft" && <Button variant="primary" iconLeft={<Send size={14} />} disabled={busy} data-testid="plan-submit"
              onClick={() => setPending({ kind: "submit", title: "Submit this plan for approval?", needsReason: false })}>Submit for approval</Button>}
            {s.status === "pending_approval" && approval?.status === "approved" && (
              <Button variant="success" iconLeft={<CheckCircle2 size={14} />} disabled={busy} data-testid="plan-activate"
                onClick={() => void act(() => planAction(s.id, "activate", { expectedVersion: p.version }))}>Activate plan</Button>
            )}
            {s.status === "pending_approval" && approval?.status !== "rejected" && (
              <Button variant="ghost" disabled={busy} data-testid="plan-withdraw"
                onClick={() => void act(() => planAction(s.id, "withdraw", { expectedVersion: p.version }))}>Withdraw to draft</Button>
            )}
            {s.status === "pending_approval" && approval?.status === "rejected" && (
              <Button variant="secondary" disabled={busy} data-testid="plan-return-draft"
                onClick={() => void act(() => planAction(s.id, "activate", { expectedVersion: p.version }))}>Return to draft</Button>
            )}
            {s.status === "active" && <Button variant="secondary" disabled={busy} data-testid="plan-complete"
              onClick={() => setPending({ kind: "complete", title: "Mark this plan completed?", needsReason: false })}>Complete</Button>}
            {(s.status === "draft" || s.status === "pending_approval" || s.status === "active") && (
              <Button variant="danger" iconLeft={<XCircle size={14} />} disabled={busy} data-testid="plan-cancel"
                onClick={() => setPending({ kind: "cancel", title: "Cancel this plan?", needsReason: s.status !== "draft" })}>Cancel plan</Button>
            )}
          </>
        )}
      />

      {lastMessage && <Callout tone="info" className="mt-4" testId="plan-message">{lastMessage}</Callout>}
      {s.status === "pending_approval" && (
        <Callout tone={approval?.status === "rejected" ? "danger" : approval?.status === "approved" ? "success" : "warning"} className="mt-4" testId="plan-approval-callout"
          title={approval?.status === "approved" ? "Approved: ready to activate" : approval?.status === "rejected" ? "Rejected" : "Waiting for approval"}>
          {approval?.status === "pending_approval" && "A reviewer decides in the approval queue. The plan has no operational effect until it is approved and then activated."}
          {approval?.status === "approved" && (config.data?.requireSeparateApprover ? "Activating checks that the approver is not the person who submitted it." : "Activate it to make it the line's live plan.")}
          {approval?.status === "rejected" && `Reason: ${approval.decisionReason ?? "—"}. Return it to draft to change and resubmit it.`}
          <div className="mt-1"><ApprovalLine approval={approval} label="Activation approval" /></div>
        </Callout>
      )}

      <div className="mt-4 grid grid-cols-1 xl:grid-cols-[minmax(0,1.3fr)_minmax(0,1fr)] gap-4">
        <Panel title="Planned vs actual to date" subtitle="Good sewing output for this order on this line within the plan's dates, against the planned output for exactly the same window.">
          <div className="grid grid-cols-2 sm:grid-cols-4 gap-3" data-testid="plan-progress">
            <KeyValue label="Planned to date">{fmt(p.progress.plannedToDate)} pcs</KeyValue>
            <KeyValue label="Actual to date">{fmt(p.progress.actualToDate)} pcs</KeyValue>
            <KeyValue label="Variance">{fmtSigned(p.progress.variance)} pcs</KeyValue>
            <KeyValue label="Achievement">{fmtPct(p.progress.achievementPct)}</KeyValue>
          </div>
          <p className="mt-2 text-caption text-fg-tertiary">
            {p.progress.comparedThrough ? `Compared through ${plantDateTime(p.progress.comparedThrough, config.offset)} (the last recorded interval, or now).` : "The plan hasn't started."}{" "}
            {fmt(p.progress.actualTotal)} recorded in total; {fmt(p.progress.remainingQuantity)} of the planned quantity remain.
          </p>
        </Panel>
        <Panel title="Planning inputs">
          <dl className="grid grid-cols-2 gap-3">
            <KeyValue label="Planned quantity">{fmt(p.values.plannedQuantity)} pcs</KeyValue>
            <KeyValue label="Operators">{p.values.operators}</KeyValue>
            <KeyValue label="Target efficiency">{fmtPct(p.values.targetEfficiencyPct)}</KeyValue>
            <KeyValue label="Shifts">{p.values.shiftCodes.join(", ")}{p.values.overtimeMinutesPerDay > 0 && ` + ${p.values.overtimeMinutesPerDay} min OT/day`}</KeyValue>
            <KeyValue label="Created">{p.createdByEmail} · {formatDateTime(p.createdAt)}</KeyValue>
            <KeyValue label="Activated">{p.activatedByEmail ? `${p.activatedByEmail} · ${formatDateTime(p.activatedAt)}` : "—"}</KeyValue>
          </dl>
          {p.values.notes && <p className="mt-2 text-caption text-fg-secondary">{p.values.notes}</p>}
          {p.closedReason && <p className="mt-2 text-caption text-fg-secondary">Closed: {p.closedReason}</p>}
        </Panel>
      </div>

      <Panel className="mt-4" title="Targets" subtitle={p.targets.formula}>
        <div className="grid grid-cols-2 sm:grid-cols-5 gap-3" data-testid="plan-targets">
          <KeyValue label="SMV">{p.targets.smv == null ? "missing" : `${fmt(p.targets.smv, 3)} min`} <span className="text-caption text-fg-tertiary">({p.targets.smvBasis.replace("_", " ")})</span></KeyValue>
          <KeyValue label="Hourly target">{p.targets.hourlyTarget == null ? "—" : `${fmt(p.targets.hourlyTarget, 1)} pcs/h`}</KeyValue>
          <KeyValue label="Scheduled time">{fmtHours(p.targets.scheduledMinutes)} over {p.targets.workingDays} days</KeyValue>
          <KeyValue label="Capacity at target">{p.targets.capacityAtTarget == null ? "—" : `${fmt(p.targets.capacityAtTarget)} pcs`}</KeyValue>
          <KeyValue label="Planned output method">{p.targets.plannedMethod === "target_rate" ? "Target rate" : "Even spread (no SMV)"}</KeyValue>
        </div>
        <NoteList title="Check these" items={p.targets.warnings} tone="warning" testId="plan-warnings" />
        {p.targets.daily.length > 0 && (
          <div className="mt-3 overflow-x-auto max-h-72">
            <table className="data-table">
              <thead><tr><th>Production date</th><th className="num">Scheduled</th><th className="num">Target</th></tr></thead>
              <tbody>{p.targets.daily.map((d) => <tr key={d.date}><td>{d.date}</td><td className="num">{fmtHours(d.scheduledMinutes)}</td><td className="num">{fmt(d.target)} pcs</td></tr>)}</tbody>
            </table>
          </div>
        )}
      </Panel>

      <Panel className="mt-4" title="Change history" subtitle="Every change with who, when, why and the values before and after. Changes to a live plan wait for approval." flush>
        <ul className="divide-y divide-border-subtle" data-testid="plan-changes">
          {p.changes.map((c) => (
            <li key={c.id} className="px-4 py-3" data-testid="plan-change" data-status={c.status}>
              <div className="flex flex-wrap items-center gap-2">
                <Badge tone={c.status === "pending" ? "warning" : c.status === "applied" ? "success" : c.status === "rejected" ? "danger" : "neutral"}>{c.kind} · {c.status}</Badge>
                <span className="text-caption text-fg-tertiary">{c.requestedByEmail} · {formatDateTime(c.requestedAt)}{c.decidedByEmail && ` · decided by ${c.decidedByEmail}`}</span>
                {operator && c.status === "pending" && (
                  <span className="ml-auto flex gap-2">
                    <Button size="sm" variant="ghost" disabled={busy} data-testid="plan-withdraw-change"
                      onClick={() => void act(() => resolvePlanChange(s.id, c.id, "withdraw"))}>Withdraw</Button>
                    {c.approval?.status !== "pending_approval" && (
                      <Button size="sm" variant={c.approval?.status === "approved" ? "success" : "secondary"} disabled={busy} data-testid="plan-resolve-change"
                        onClick={() => void act(() => resolvePlanChange(s.id, c.id))}>
                        {c.approval?.status === "approved" ? "Apply approved change" : "Record rejection"}
                      </Button>
                    )}
                  </span>
                )}
              </div>
              {c.reason && <p className="mt-1 text-caption text-fg-secondary">Reason: {c.reason}</p>}
              {c.diff.length > 0 && (
                <ul className="mt-1 text-caption text-fg-secondary">
                  {c.diff.map((d) => <li key={d.field}><span className="font-mono">{d.field}</span>: {d.from ?? "—"} → <span className="text-fg-primary">{d.to ?? "—"}</span></li>)}
                </ul>
              )}
              {c.approval && <div className="mt-1"><ApprovalLine approval={c.approval} /></div>}
            </li>
          ))}
        </ul>
      </Panel>

      <PlanFormModal open={editing} onClose={() => setEditing(false)} lines={md.lines.data ?? []} shifts={md.shifts.data ?? []} plan={p}
        onSaved={(_, message) => { setLastMessage(message); void res.reload(); }} />
      <ConfirmDialog open={!!pending} title={pending?.title} busy={busy} testId="plan-confirm"
        description={pending?.kind === "submit" ? "It goes to the approval queue (medium risk). It won't affect the line until it is approved and activated." : undefined}
        confirmLabel={pending?.kind === "cancel" ? "Cancel plan" : pending?.kind === "complete" ? "Complete" : "Submit"}
        tone={pending?.kind === "cancel" ? "danger" : "primary"} confirmDisabled={!!pending?.needsReason && !reason.trim()}
        onCancel={() => { setPending(null); setReason(""); }}
        onConfirm={() => pending && void act(() => planAction(s.id, pending.kind, { reason: reason.trim() || undefined, expectedVersion: p.version }))}>
        <Input aria-label="Reason" placeholder={pending?.needsReason ? "Reason (required)" : "Note (optional)"} value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} data-testid="plan-confirm-reason" />
      </ConfirmDialog>
    </div>
  );
}
