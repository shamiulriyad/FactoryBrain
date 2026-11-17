"use client";

// Daily production plans: one plan per factory and date, prepared as a
// draft, reviewed, approved by a second person and published so supervisors
// and monitoring operators see it. A published plan is changed by starting a
// revision; every step stays on the plan's audit trail.

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { CalendarDays, Plus } from "lucide-react";
import { Button } from "@/components/ui/Button";
import { EmptyState } from "@/components/ui/EmptyState";
import { Field, FilterSelect, Select, TextArea } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Modal } from "@/components/ui/Modal";
import { Panel } from "@/components/ui/Panel";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { DataTable } from "@/components/analytics/shared";
import { FormError, ModalFooter, PL, PlanStatusBadge, useSubmit, type PlanningScope } from "@/components/planning/shared";
import { dhaka } from "@/lib/monitoring";
import { announceDataChanged, useOnDataChanged, useResource } from "@/lib/useResource";
import { PLAN_STATUS_TEXT, addDays, num, planningApi, type DailyPlan, type DailyPlanListItem, type Paged } from "@/lib/planning";

export function PlansView({ scope }: { scope: PlanningScope }) {
  const { q, ctx, factoryId } = scope;
  const status = q.get("status");
  const from = q.get("from") || addDays(ctx.today, -7);
  const to = q.get("to") || addDays(ctx.today, 14);
  const focus = q.get("date");
  const [creating, setCreating] = useState(false);
  const res = useResource<Paged<DailyPlanListItem>>(planningApi.plans({ factoryId, status, from, to, pageSize: 100 }));
  useOnDataChanged(() => void res.reload());
  const d = res.data;
  const canPlan = ctx.capabilities.managePlans;
  return (
    <>
      <div className="surface p-3 mb-4 flex flex-wrap items-end gap-3">
        <label className="text-caption text-fg-secondary flex flex-col gap-1">From<Input type="date" className="h-9" value={from} onChange={(e) => q.set({ from: e.target.value || null })} /></label>
        <label className="text-caption text-fg-secondary flex flex-col gap-1">To<Input type="date" className="h-9" value={to} onChange={(e) => q.set({ to: e.target.value || null })} /></label>
        {ctx.capabilities.seesDrafts && (
          <FilterSelect label="Status" value={status} onChange={(v) => q.set({ status: v || null })} testId="plans-status">
            <option value="">All</option>{Object.entries(PLAN_STATUS_TEXT).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </FilterSelect>
        )}
        {canPlan && <Button className="ml-auto" variant="primary" iconLeft={<Plus size={14} />} onClick={() => setCreating(true)} data-testid="plan-new">New daily plan</Button>}
      </div>
      {!ctx.capabilities.seesDrafts && <p className="mb-3 text-caption text-fg-tertiary">You see plans once they are published.</p>}
      {res.error && !d ? <ErrorState title="Could not load plans" body={res.error.message} retryLabel="Try again" onRetry={() => void res.reload()} />
        : !d ? <SkeletonRows rows={5} />
        : d.items.length === 0 ? <div className="surface"><EmptyState icon={<CalendarDays size={20} />} title="No daily plans in this period" body={canPlan ? "Create a plan for a date, add order · line · shift items, then submit it for review." : "Published plans appear here."} /></div>
        : (
          <Panel flush ariaLabel="Daily plans">
            <DataTable testId="plans-table" columns={["Date", "Plan", "Revision", "Items", "Target", "Status", "Published", "Updated"]}
              rows={d.items.map((p) => [
                <span key="d" className={p.planDate === focus ? "font-semibold" : ""}>{p.planDate}</span>,
                <Link key="r" href={`${PL}/plans/${p.id}?factory=${factoryId}`} className="font-mono text-[12px] text-accent hover:underline" data-testid="plan-row">{p.reference}</Link>,
                p.revisionNo, p.items, num(p.totalTarget), <PlanStatusBadge key="s" status={p.status} />,
                p.publishedAt ? `${dhaka(p.publishedAt)} · ${p.publishedByEmail}` : "—", dhaka(p.updatedAt)
              ])} />
          </Panel>
        )}
      <NewPlanModal open={creating} onClose={() => setCreating(false)} scope={scope} plans={d?.items ?? []} />
    </>
  );
}

function NewPlanModal({ open, onClose, scope, plans }: { open: boolean; onClose: () => void; scope: PlanningScope; plans: DailyPlanListItem[] }) {
  const { ctx, factoryId } = scope;
  const router = useRouter();
  const toast = useToast();
  const s = useSubmit();
  const [date, setDate] = useState(ctx.today);
  const [copy, setCopy] = useState("");
  const [notes, setNotes] = useState("");
  useEffect(() => { if (open) { s.reset(); setDate(addDays(ctx.today, 1)); setCopy(""); setNotes(""); } }, [open]); // eslint-disable-line react-hooks/exhaustive-deps
  async function submit(e: React.FormEvent) {
    e.preventDefault();
    const plan = await s.run(() => planningApi.createPlan({ factoryId, planDate: date, notes: notes || undefined, copyFromPlanId: copy || undefined }));
    if (!plan) return;
    toast.push({ title: `Plan ${plan.reference} created`, description: plan.warnings.length ? plan.warnings[0] : undefined });
    announceDataChanged();
    onClose();
    router.push(`${PL}/plans/${plan.id}?factory=${factoryId}`);
  }
  return (
    <Modal open={open} onClose={onClose} locked={s.busy} title="New daily plan" description="One plan per factory and date. Plans can be made up to 60 days ahead."
      testId="plan-form" footer={<ModalFooter onCancel={onClose} busy={s.busy} label="Create plan" formId="planning-plan-form" testId="plan-save" />}>
      <form id="planning-plan-form" onSubmit={submit} className="space-y-3">
        <FormError error={s.error} />
        <Field label="Plan date (factory time)" error={s.fe("planDate")}>{(a) => <Input id={a.id} type="date" min={ctx.today} value={date} onChange={(e) => setDate(e.target.value)} required data-testid="plan-date" />}</Field>
        <Field label="Start from an earlier plan (optional)" hint="Items that are still valid for the new date are copied.">
          {(a) => <Select id={a.id} value={copy} onChange={(e) => setCopy(e.target.value)}><option value="">Empty plan</option>{plans.filter((p) => p.status !== "CANCELLED" && p.items > 0).map((p) => <option key={p.id} value={p.id}>{p.planDate} · {p.reference} ({p.items} items)</option>)}</Select>}
        </Field>
        <Field label="Notes (optional)">{(a) => <TextArea id={a.id} rows={2} value={notes} onChange={(e) => setNotes(e.target.value)} maxLength={1000} />}</Field>
      </form>
    </Modal>
  );
}

export type { DailyPlan };
