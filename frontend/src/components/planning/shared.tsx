"use client";

// Production planning building blocks: the section tabs, the factory picker
// and planning context (shifts, lines, what the caller may do), status and
// basis badges, the "actual" cell that never shows missing data as zero, a
// reason dialog, and the submit helper the forms share.

import Link from "next/link";
import { usePathname, useSearchParams } from "next/navigation";
import { useEffect, useState, type ReactNode } from "react";
import { Info } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { Badge, type BadgeTone } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { FilterSelect, Field, TextArea } from "@/components/ui/Field";
import { Modal } from "@/components/ui/Modal";
import { useQueryState } from "@/components/attendance/shared";
import { useMyFactories } from "@/components/workers/shared";
import { useResource } from "@/lib/useResource";
import { errorMessage, isApiError } from "@/lib/api";
import { useCan } from "@/lib/auth";
import { P } from "@/lib/access";
import { cn } from "@/lib/cn";
import {
  BASIS_TEXT, FORECAST_TEXT, ITEM_STATE_TEXT, ORDER_STATUS_TEXT, PLAN_STATUS_TEXT, planningApi,
  type ForecastStatus, type ItemState, type PlanItemActual, type PlanStatus, type PlanningContext, type ProductionException, type Target
} from "@/lib/planning";
import type { OrderStatus } from "@/services/contracts";

export const PL = "/app/planning";

const SECTIONS: { href: string; label: string; exact?: boolean }[] = [
  { href: PL, label: "Monitoring", exact: true },
  { href: `${PL}/orders`, label: "Orders" },
  { href: `${PL}/styles`, label: "Styles" },
  { href: `${PL}/allocations`, label: "Line allocations" },
  { href: `${PL}/plans`, label: "Daily plans" },
  { href: `${PL}/targets`, label: "Targets" },
  { href: `${PL}/capacity`, label: "Capacity" },
  { href: `${PL}/exceptions`, label: "Exceptions" },
  { href: `${PL}/reports`, label: "Reports" },
  { href: `${PL}/setup`, label: "Setup" }
];

export function PlanningTabs() {
  const path = usePathname() ?? "";
  const params = useSearchParams();
  const factory = params?.get("factory");
  const search = factory ? `?factory=${factory}` : "";
  return (
    <nav aria-label="Planning sections" className="mb-4 flex gap-1 overflow-x-auto border-b border-border-subtle" data-testid="planning-tabs">
      {SECTIONS.map((s) => {
        const active = s.exact ? path === s.href : path.startsWith(s.href);
        return (
          <Link key={s.href} href={`${s.href}${search}`}
            className={cn("px-3 h-9 inline-flex items-center text-caption border-b-2 -mb-px whitespace-nowrap",
              active ? "border-[var(--accent)] text-fg-primary font-medium" : "border-transparent text-fg-secondary hover:text-fg-primary")}>{s.label}</Link>
        );
      })}
    </nav>
  );
}

/** The factory in the URL (or the caller's only one) and its planning context. */
export function usePlanningScope(base: string) {
  const q = useQueryState(base);
  const { factories, error } = useMyFactories();
  // The chosen factory, else the first one in reach (a person with several can switch with the picker).
  const factoryId = q.get("factory") || (factories && factories.length > 0 ? factories[0].id : "");
  const ctx = useResource<PlanningContext>(factoryId ? planningApi.context(factoryId) : null);
  return { q, factories, factoriesError: error, factoryId, ctx, caps: ctx.data?.capabilities ?? null };
}

export function FactoryPicker({ factories, value, onChange }: { factories: { id: string; code: string; name: string }[] | null; value: string; onChange: (id: string) => void }) {
  if (!factories || factories.length <= 1) return null;
  return (
    <FilterSelect label="Factory" value={value} onChange={onChange} testId="planning-factory">
      <option value="">Choose a factory</option>
      {factories.map((f) => <option key={f.id} value={f.id}>{f.code} · {f.name}</option>)}
    </FilterSelect>
  );
}

/** Shown when no factory is chosen yet (several in reach) or none is available. */
export function NeedFactory({ factories, error }: { factories: unknown[] | null; error: string | null }) {
  if (error) return <Callout tone="danger" title="Could not load your factories">{error}</Callout>;
  if (factories && factories.length === 0) return <Callout tone="warning" title="No factory">Your account has no factory assigned, so there is nothing to plan. Ask an administrator.</Callout>;
  return <Callout tone="info">Choose a factory to continue.</Callout>;
}

// ── Badges ──────────────────────────────────────────────────────────────────

const ORDER_TONE: Record<OrderStatus, BadgeTone> = { draft: "neutral", planned: "info", in_progress: "accent", on_hold: "warning", completed: "success", cancelled: "danger" };
export function OrderStatusBadge({ status }: { status: OrderStatus }) {
  return <Badge tone={ORDER_TONE[status]} testId="order-status">{ORDER_STATUS_TEXT[status]}</Badge>;
}

const PLAN_TONE: Record<PlanStatus, BadgeTone> = { DRAFT: "neutral", UNDER_REVIEW: "warning", APPROVED: "info", PUBLISHED: "success", REVISED: "neutral", CANCELLED: "danger" };
export function PlanStatusBadge({ status }: { status: PlanStatus }) {
  return <Badge tone={PLAN_TONE[status]} testId="plan-status">{PLAN_STATUS_TEXT[status]}</Badge>;
}

const STATE_TONE: Record<ItemState, BadgeTone> = { NOT_STARTED: "neutral", ON_TRACK: "success", AHEAD: "success", BEHIND: "danger", COMPLETE: "success", MISSED: "danger", NO_DATA: "warning" };
export function StateBadge({ state }: { state: ItemState }) {
  return <Badge tone={STATE_TONE[state]}>{ITEM_STATE_TEXT[state]}</Badge>;
}

export function SeverityBadge({ severity }: { severity: ProductionException["severity"] }) {
  return <Badge tone={severity === "HIGH" ? "danger" : severity === "MEDIUM" ? "warning" : "neutral"}>{severity === "HIGH" ? "High" : severity === "MEDIUM" ? "Medium" : "Low"}</Badge>;
}

export function ExceptionStatusBadge({ status }: { status: ProductionException["status"] }) {
  return <Badge tone={status === "OPEN" ? "warning" : status === "ACKNOWLEDGED" ? "info" : "success"}>{status === "OPEN" ? "Open" : status === "ACKNOWLEDGED" ? "Acknowledged" : "Resolved"}</Badge>;
}

export function BasisBadge({ basis }: { basis: string }) {
  const tone: BadgeTone = basis === "COUNTER" ? "success" : basis === "RECORDED" ? "info" : basis === "MIXED" ? "accent" : "warning";
  return <Badge tone={tone} title={basis === "COUNTER" ? "Pieces counted by the line's designated output counter" : basis === "RECORDED" ? "Entered by people in the production output records; not verified by a counter" : "No evidence of output"}>{BASIS_TEXT[basis] ?? basis}</Badge>;
}

const FORECAST_TONE: Record<ForecastStatus, BadgeTone> = { COMPLETE: "success", ON_TRACK: "success", AT_RISK: "danger", OVERDUE: "danger", INSUFFICIENT_DATA: "neutral" };
export function ForecastBadge({ status }: { status: ForecastStatus }) {
  return <Badge tone={FORECAST_TONE[status]}>{FORECAST_TEXT[status]}</Badge>;
}

export function ApprovalBadge({ t }: { t: Target }) {
  if (t.approvalStatus === "NOT_REQUIRED") return <Badge tone="neutral">No approval needed</Badge>;
  return <Badge tone={t.approvalStatus === "APPROVED" ? "success" : t.approvalStatus === "PENDING" ? "warning" : "danger"}>{t.approvalStatus === "PENDING" ? "Awaiting approval" : t.approvalStatus === "APPROVED" ? "Approved" : "Rejected"}</Badge>;
}

/** An item's output: the number with how it was measured, or "No data" — never zero for missing evidence. */
export function ActualCell({ a, compact }: { a?: PlanItemActual | null; compact?: boolean }) {
  if (!a) return <span className="text-fg-tertiary">—</span>;
  if (a.actual == null) return <span className="text-fg-tertiary" title={a.note}>{a.state === "NOT_STARTED" ? "Not started" : "No data"}</span>;
  return (
    <span title={a.note} className="tabular-nums">
      {a.actual.toLocaleString("en-US")}
      {!compact && <span className="block text-caption text-fg-tertiary">{a.verified ? "verified" : a.basis === "RECORDED" ? "recorded" : ""}{a.achievementPct != null ? `${a.verified || a.basis === "RECORDED" ? " · " : ""}${a.achievementPct} %` : ""}</span>}
    </span>
  );
}

export function Legend({ children }: { children?: ReactNode }) {
  return (
    <p className="flex items-start gap-1.5 text-caption text-fg-tertiary" data-testid="planning-legend">
      <Info size={13} className="mt-0.5 shrink-0" aria-hidden />
      <span>{children ?? "Verified = counted by a line's designated output counter (each message once; resets handled; corrections audited). Recorded = entered by people. No data is not zero. AI activity is never production."}</span>
    </p>
  );
}

// ── Forms ───────────────────────────────────────────────────────────────────

export function useSubmit() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  async function run<T>(fn: () => Promise<T>): Promise<T | null> {
    setBusy(true);
    setError(null);
    try {
      return await fn();
    } catch (e) {
      setError(e);
      return null;
    } finally {
      setBusy(false);
    }
  }
  const fieldErrors = isApiError(error) ? error.fieldErrors : {};
  const fe = (k: string) => fieldErrors[k]?.join(" ") ?? null;
  return { busy, error, run, fe, reset: () => setError(null) };
}

export function FormError({ error }: { error: unknown }) {
  if (!error) return null;
  return (
    <Callout tone="danger" title={isApiError(error) && error.kind === "conflict" ? "Not saved: conflict" : "Not saved"} testId="form-error">
      <p>{errorMessage(error)}</p>
    </Callout>
  );
}

export function ModalFooter({ onCancel, busy, label, formId, testId, disabled }: { onCancel: () => void; busy: boolean; label: ReactNode; formId: string; testId?: string; disabled?: boolean }) {
  return (
    <>
      <Button variant="ghost" onClick={onCancel} disabled={busy}>Cancel</Button>
      <Button variant="primary" type="submit" form={formId} disabled={busy || disabled} data-testid={testId}>{busy ? "Saving…" : label}</Button>
    </>
  );
}

/** Asks for a reason, then runs the action. The reason is kept on the record. */
export function ReasonDialog({ open, onClose, title, description, label, required = true, confirmLabel, tone = "primary", onConfirm, testId }: {
  open: boolean; onClose: () => void; title: string; description?: ReactNode; label?: string; required?: boolean; confirmLabel: string; tone?: "primary" | "danger";
  onConfirm: (reason: string) => Promise<unknown>; testId?: string;
}) {
  const s = useSubmit();
  const [reason, setReason] = useState("");
  useEffect(() => { if (open) { setReason(""); s.reset(); } }, [open]); // eslint-disable-line react-hooks/exhaustive-deps
  async function go(e: React.FormEvent) {
    e.preventDefault();
    const ok = await s.run(async () => { await onConfirm(reason.trim()); return true; });
    if (ok) onClose();
  }
  return (
    <Modal open={open} onClose={onClose} locked={s.busy} title={title} description={description} testId={testId}
      footer={<><Button variant="ghost" onClick={onClose} disabled={s.busy}>Cancel</Button>
        <Button variant={tone === "danger" ? "danger" : "primary"} type="submit" form="reason-form" disabled={s.busy || (required && reason.trim().length === 0)} data-testid="reason-confirm">{s.busy ? "Working…" : confirmLabel}</Button></>}>
      <form id="reason-form" onSubmit={go} className="space-y-3">
        <FormError error={s.error} />
        <Field label={label ?? (required ? "Reason (kept on record)" : "Reason (optional)")}>
          {(a) => <TextArea id={a.id} rows={3} value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} required={required} data-testid="reason-text" />}
        </Field>
      </form>
    </Modal>
  );
}

export function useCanPlan() {
  return {
    orders: useCan(P.PlanningOrdersManage, P.ProductionManage),
    styles: useCan(P.PlanningStylesManage, P.ProductionSetup),
    allocations: useCan(P.PlanningAllocationsManage),
    targets: useCan(P.PlanningTargetsManage),
    approve: useCan(P.PlanningPlansApprove),
    exceptions: useCan(P.PlanningExceptionsReview),
    setup: useCan(P.PlanningSetup),
    exportReports: useCan(P.PlanningReportsExport)
  };
}

export const PLANNING_VIEW = [P.PlanningView];

export type PlanningScope = Omit<ReturnType<typeof usePlanningScope>, "ctx" | "factoryId"> & { factoryId: string; ctx: PlanningContext };

/**
 * The frame every planning page shares: permission gate, header, section tabs,
 * the factory picker, and the factory's planning context loaded before the
 * page's own content renders.
 */
export function PlanningPage({ base, title, description, actions, testId, children }: {
  base: string; title: string; description: string; actions?: ReactNode; testId: string; children: (scope: PlanningScope) => ReactNode;
}) {
  return (
    <RequirePermission perms={PLANNING_VIEW} what="Production planning">
      <Frame base={base} title={title} description={description} actions={actions} testId={testId}>{children}</Frame>
    </RequirePermission>
  );
}

function Frame({ base, title, description, actions, testId, children }: {
  base: string; title: string; description: string; actions?: ReactNode; testId: string; children: (scope: PlanningScope) => ReactNode;
}) {
  const scope = usePlanningScope(base);
  const { factoryId, factories, factoriesError, ctx, q } = scope;
  return (
    <div className="page" data-testid={testId}>
      <PageHeader eyebrow="Production planning" title={title} description={description} actions={actions} />
      <PlanningTabs />
      {factories && factories.length > 1 && (
        <div className="mb-4 flex flex-wrap items-end gap-2"><FactoryPicker factories={factories} value={factoryId} onChange={(id) => q.set({ factory: id || null })} /></div>
      )}
      {!factoryId ? <NeedFactory factories={factories} error={factoriesError} />
        : ctx.error && !ctx.data ? <ErrorState title="Could not load the planning context" body={ctx.error.message} retryLabel="Try again" onRetry={() => void ctx.reload()} />
        : !ctx.data ? <SkeletonRows rows={6} />
        : children({ ...scope, factoryId, ctx: ctx.data })}
    </div>
  );
}
