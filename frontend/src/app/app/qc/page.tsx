"use client";

// /app/qc — quality inspections: history (search, filters, pagination),
// quality summary and the defect taxonomy. Everything is read from the API
// (/api/qc/inspections, /api/qc/summary, /api/qc/categories); new
// inspections are submitted on /app/vision.

import { useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { ChevronLeft, ChevronRight, ClipboardCheck, RefreshCw, ScanLine, Search } from "lucide-react";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { FilterSelect } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Tabs } from "@/components/ui/Tabs";
import { EmptyState } from "@/components/ui/EmptyState";
import { SkeletonRows } from "@/components/ui/Status";
import { QualitySummary } from "@/components/qc/QualitySummary";
import { CategoryManager } from "@/components/qc/CategoryManager";
import { useOnDataChanged, useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { canOperate, isAdmin, useAuthStore } from "@/lib/auth";
import { formatDateTime } from "@/lib/format";
import { cn } from "@/lib/cn";
import { OUTCOME_LABEL, OUTCOME_TONE, STAGE_LABEL, STATUS_LABEL, STATUS_TONE, formatConfidence, isInProgress } from "@/lib/qcInspection";
import { inspectionsPath, type InspectionFilters } from "@/services/qc.api";
import { QC_OUTCOMES, QC_STAGES, QC_STATUSES, type QcInspectionPage } from "@/services/contracts";

type Tab = "inspections" | "summary" | "taxonomy";
const PAGE_SIZE = 20;

export default function QcPage() {
  const user = useAuthStore((s) => s.user);
  const operator = canOperate(user);
  const admin = isAdmin(user);
  const [tab, setTab] = useState<Tab>("inspections");

  useEffect(() => {
    const t = new URLSearchParams(window.location.search).get("tab");
    if (t === "summary" || t === "taxonomy") setTab(t);
  }, []);

  function changeTab(t: Tab) {
    setTab(t);
    const url = new URL(window.location.href);
    if (t === "inspections") url.searchParams.delete("tab");
    else url.searchParams.set("tab", t);
    window.history.replaceState(null, "", url);
  }

  return (
    <div className="page" data-tour="qc">
      <PageHeader
        eyebrow="Operations"
        title="Quality inspections"
        description="Garment inspections with the vision model's predictions and the inspectors' verified outcomes, and quality metrics calculated from them."
        actions={
          operator && (
            <Link href="/app/vision" data-testid="qc-new-inspection"
              className="inline-flex h-9 items-center gap-1.5 px-3.5 rounded-[var(--radius-control)] border border-[var(--btn-primary-border)] bg-[var(--btn-primary-bg)] text-body font-medium text-[var(--btn-primary-fg)] hover:bg-[var(--btn-primary-bg-hover)]">
              <ScanLine size={14} /> New inspection
            </Link>
          )
        }
      />
      <Tabs<Tab>
        className="mt-5"
        label="Quality views"
        value={tab}
        onChange={changeTab}
        items={[
          { id: "inspections", label: "Inspections", testId: "qc-tab-inspections" },
          { id: "summary", label: "Quality summary", testId: "qc-tab-summary" },
          { id: "taxonomy", label: "Defect taxonomy", testId: "qc-tab-taxonomy" }
        ]}
      />
      <div className="mt-4" role="tabpanel">
        {tab === "inspections" && <InspectionList operator={operator} />}
        {tab === "summary" && <QualitySummary />}
        {tab === "taxonomy" && <CategoryManager admin={admin} />}
      </div>
    </div>
  );
}

function InspectionList({ operator }: { operator: boolean }) {
  const [filters, setFilters] = useState<InspectionFilters>({ page: 1, pageSize: PAGE_SIZE });
  const [search, setSearch] = useState("");

  // Search waits for a pause in typing.
  useEffect(() => {
    const t = setTimeout(() => setFilters((f) => (f.q === search ? f : { ...f, q: search, page: 1 })), 300);
    return () => clearTimeout(t);
  }, [search]);

  const path = useMemo(() => inspectionsPath(filters), [filters]);
  const [poll, setPoll] = useState(false);
  const res = useResource<QcInspectionPage>(path, { pollMs: poll ? 5000 : undefined });
  useOnDataChanged(() => void res.reload());
  const data = res.data;
  useEffect(() => setPoll(!!data?.items.some((i) => isInProgress(i.status))), [data]);

  const set = (patch: Partial<InspectionFilters>) => setFilters((f) => ({ ...f, ...patch, page: patch.page ?? 1 }));
  const filtered = Boolean(filters.q || filters.status || filters.stage || filters.lineId || filters.outcome || filters.from || filters.to);

  return (
    <div className="space-y-3">
      <div className="surface p-3 flex flex-wrap items-center gap-3" role="search">
        <div className="relative w-full sm:w-64">
          <Search size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-fg-tertiary" aria-hidden />
          <Input aria-label="Search inspections" placeholder="Reference, product, order, line…" className="pl-8" value={search}
            onChange={(e) => setSearch(e.target.value)} maxLength={100} data-testid="qc-search" />
        </div>
        <FilterSelect label="Status" value={filters.status ?? ""} onChange={(v) => set({ status: v as InspectionFilters["status"] })} testId="qc-filter-status">
          <option value="">All</option>
          {QC_STATUSES.map((s) => <option key={s} value={s}>{STATUS_LABEL[s]}</option>)}
        </FilterSelect>
        <FilterSelect label="Stage" value={filters.stage ?? ""} onChange={(v) => set({ stage: v as InspectionFilters["stage"] })}>
          <option value="">All</option>
          {QC_STAGES.map((s) => <option key={s} value={s}>{STAGE_LABEL[s]}</option>)}
        </FilterSelect>
        <FilterSelect label="Outcome" value={filters.outcome ?? ""} onChange={(v) => set({ outcome: v as InspectionFilters["outcome"] })}>
          <option value="">All</option>
          {QC_OUTCOMES.map((o) => <option key={o} value={o}>{OUTCOME_LABEL[o]}</option>)}
        </FilterSelect>
        <label className="flex items-center gap-2 text-caption text-fg-tertiary">
          From <input type="date" className="control !h-8 !w-auto !text-caption" value={filters.from ?? ""} onChange={(e) => set({ from: e.target.value })} />
        </label>
        <label className="flex items-center gap-2 text-caption text-fg-tertiary">
          To <input type="date" className="control !h-8 !w-auto !text-caption" value={filters.to ?? ""} onChange={(e) => set({ to: e.target.value })} />
        </label>
        {filtered && (
          <Button size="sm" variant="ghost" onClick={() => { setSearch(""); setFilters({ page: 1, pageSize: PAGE_SIZE }); }}>Clear</Button>
        )}
        <Button size="sm" variant="ghost" className="ml-auto" onClick={() => void res.reload()} disabled={res.refreshing} aria-label="Refresh">
          <RefreshCw size={13} className={cn(res.refreshing && "animate-spin")} />
        </Button>
      </div>

      {res.error && data && <Callout tone="warning">Showing the last loaded list: {errorMessage(res.error)}</Callout>}

      {res.loading ? (
        <SkeletonRows rows={6} />
      ) : res.error && !data ? (
        <Callout tone="danger" title="Could not load inspections" action={<Button size="sm" onClick={() => void res.reload()}>Retry</Button>}>
          {errorMessage(res.error)}
        </Callout>
      ) : data && data.items.length === 0 ? (
        <div className="surface" data-testid="qc-empty">
          <EmptyState
            icon={<ClipboardCheck size={20} />}
            title={filtered ? "No inspections match these filters" : "No inspections yet"}
            body={filtered ? "Change or clear the filters." : operator ? "Submit the first garment photo on the Inspect page." : "Inspections submitted by operators will appear here."}
          />
          {!filtered && operator && (
            <div className="pb-8 text-center"><Link href="/app/vision" className="text-caption text-accent hover:underline">Inspect a garment →</Link></div>
          )}
        </div>
      ) : data ? (
        <Panel flush ariaLabel="Inspection history">
          <div className="overflow-x-auto">
            <table className="data-table" data-testid="qc-inspection-table">
              <thead>
                <tr>
                  <th>Reference</th>
                  <th>Submitted</th>
                  <th>Product · stage</th>
                  <th>Line</th>
                  <th>Status</th>
                  <th className="num">Predictions</th>
                  <th className="num">Verified defects</th>
                  <th>Outcome</th>
                </tr>
              </thead>
              <tbody>
                {data.items.map((i) => (
                  <tr key={i.id} data-testid="qc-inspection-row" data-status={i.status}>
                    <td><Link href={`/app/qc/${i.id}`} className="font-mono text-[12px] text-accent hover:underline">{i.reference}</Link></td>
                    <td className="whitespace-nowrap">{formatDateTime(i.createdAt)}</td>
                    <td className="text-fg-primary">{i.productType}<span className="block text-caption text-fg-tertiary">{STAGE_LABEL[i.stage]}{i.productionOrder ? ` · ${i.productionOrder}` : ""}</span></td>
                    <td className="font-mono text-[12px]">{i.lineId ?? "—"}</td>
                    <td><Badge tone={STATUS_TONE[i.status]} title={i.failureCode ?? undefined}>{STATUS_LABEL[i.status]}</Badge></td>
                    <td className="num">{i.predictionCount}{i.maxConfidence != null && <span className="block text-caption text-fg-tertiary">max {formatConfidence(i.maxConfidence)}</span>}</td>
                    <td className="num">{i.status === "reviewed" ? i.verifiedDefectCount : "—"}</td>
                    <td>{i.outcome ? <Badge tone={OUTCOME_TONE[i.outcome]}>{OUTCOME_LABEL[i.outcome]}</Badge> : <span className="text-fg-tertiary">—</span>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="flex flex-wrap items-center justify-between gap-2 px-4 py-3 border-t border-border-subtle text-caption text-fg-tertiary">
            <span data-testid="qc-page-info">Page {data.page} of {Math.max(1, data.totalPages)} · {data.total} inspection{data.total === 1 ? "" : "s"}</span>
            <div className="flex gap-2">
              <Button size="sm" variant="secondary" disabled={data.page <= 1} onClick={() => set({ page: data.page - 1 })} aria-label="Previous page"><ChevronLeft size={13} /></Button>
              <Button size="sm" variant="secondary" disabled={data.page >= data.totalPages} onClick={() => set({ page: data.page + 1 })} aria-label="Next page" data-testid="qc-next-page"><ChevronRight size={13} /></Button>
            </div>
          </div>
        </Panel>
      ) : null}
    </div>
  );
}
