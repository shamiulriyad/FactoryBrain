"use client";

// /app/merchandising — buyer orders and their documents: an overview of
// deadlines, documents awaiting review and conflicts; the order list with
// search and filters; and every document with its processing state. All of
// it is read from /api/merchandising/**; nothing extracted becomes order data
// until a person confirms it on the review page.

import { useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { AlertTriangle, CalendarClock, ChevronLeft, ChevronRight, FileSearch, FileWarning, FolderOpen, GitCompare, PlusCircle, RefreshCw, Search } from "lucide-react";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { EmptyState } from "@/components/ui/EmptyState";
import { FilterSelect } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { StatCard } from "@/components/ui/StatCard";
import { SkeletonRows } from "@/components/ui/Status";
import { Tabs } from "@/components/ui/Tabs";
import { DueText, ExtractionNotice, OrderStatusBadge, VersionStatusBadge, useMerchConfig } from "@/components/merchandising/shared";
import { OrderFormModal } from "@/components/merchandising/forms";
import { useOnDataChanged, useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { canOperate, useAuthStore } from "@/lib/auth";
import { formatDateTime } from "@/lib/format";
import { cn } from "@/lib/cn";
import { ORDER_STATUS_LABEL, VERSION_STATUS_LABEL, days, describeCode, fmtQty, isInProgress } from "@/lib/merchandising";
import { merchPaths, type MerchOrderFilters } from "@/services/merchandising.api";
import {
  MERCH_DOCUMENT_TYPES,
  MERCH_ORDER_STATUSES,
  MERCH_VERSION_STATUSES,
  type MerchDocumentListItem,
  type MerchOrderListItem,
  type MerchOverview,
  type Paged
} from "@/services/contracts";

type Tab = "overview" | "orders" | "documents";
const TABS: Tab[] = ["overview", "orders", "documents"];
const PAGE_SIZE = 20;

export default function MerchandisingPage() {
  const user = useAuthStore((s) => s.user);
  const operator = canOperate(user);
  const router = useRouter();
  const [tab, setTab] = useState<Tab>("overview");
  const [registering, setRegistering] = useState(false);
  const config = useMerchConfig();

  useEffect(() => {
    const t = new URLSearchParams(window.location.search).get("tab") as Tab | null;
    if (t && TABS.includes(t)) setTab(t);
  }, []);

  function changeTab(t: Tab) {
    setTab(t);
    const url = new URL(window.location.href);
    if (t === "overview") url.searchParams.delete("tab");
    else url.searchParams.set("tab", t);
    window.history.replaceState(null, "", url);
  }

  return (
    <div className="page" data-testid="merch-page">
      <PageHeader
        eyebrow="Operations"
        title="Merchandising"
        description="Buyer orders, their documents and milestones. Values read from documents are checked against their source text and only become order data when a merchandiser confirms them."
        actions={operator && (
          <Button variant="primary" iconLeft={<PlusCircle size={14} />} onClick={() => setRegistering(true)} data-testid="merch-register">Register order</Button>
        )}
      />
      {config.data && <ExtractionNotice extraction={config.data.extraction} className="mt-3" />}
      {config.error && !config.data && (
        <Callout tone="danger" className="mt-4" title="Merchandising data is unavailable" action={<Button size="sm" onClick={() => void config.reload()}>Retry</Button>}>
          {errorMessage(config.error)}
        </Callout>
      )}
      <Tabs<Tab>
        className="mt-5"
        label="Merchandising views"
        value={tab}
        onChange={changeTab}
        items={[
          { id: "overview", label: "Overview", testId: "merch-tab-overview" },
          { id: "orders", label: "Orders", testId: "merch-tab-orders" },
          { id: "documents", label: "Documents", testId: "merch-tab-documents" }
        ]}
      />
      <div className="mt-4" role="tabpanel">
        {tab === "overview" && <Overview />}
        {tab === "orders" && <OrdersList operator={operator} onRegister={() => setRegistering(true)} />}
        {tab === "documents" && <DocumentsList />}
      </div>
      <OrderFormModal open={registering} onClose={() => setRegistering(false)} config={config.data}
        onSaved={(d) => router.push(`/app/merchandising/orders/${d.order.id}`)} />
    </div>
  );
}

// ── Overview ────────────────────────────────────────────────────────────────

function Overview() {
  const res = useResource<MerchOverview>(merchPaths.overview, { pollMs: 15_000 });
  useOnDataChanged(() => void res.reload());
  const o = res.data;
  if (res.loading) return <SkeletonRows rows={5} rowHeight="h-20" />;
  if (res.error && !o)
    return <Callout tone="danger" title="Could not load the overview" action={<Button size="sm" onClick={() => void res.reload()}>Retry</Button>}>{errorMessage(res.error)}</Callout>;
  if (!o) return null;
  return (
    <div className="space-y-4" data-testid="merch-overview">
      <div className="grid grid-cols-2 lg:grid-cols-3 xl:grid-cols-6 gap-3">
        <StatCard label="Active orders" value={o.activeOrders} icon={<FolderOpen size={14} />} href="/app/merchandising?tab=orders" testId="merch-stat-active" />
        <StatCard label="Awaiting review" value={o.documentsAwaitingReview} tone={o.documentsAwaitingReview ? "warn" : "neutral"} icon={<FileSearch size={14} />} testId="merch-stat-review" />
        <StatCard label="Open conflicts" value={o.openConflicts} tone={o.openConflicts ? "bad" : "neutral"} icon={<GitCompare size={14} />} testId="merch-stat-conflicts" />
        <StatCard label="Overdue milestones" value={o.overdueMilestones} tone={o.overdueMilestones ? "bad" : "neutral"} icon={<CalendarClock size={14} />} testId="merch-stat-overdue" />
        <StatCard label="Missing documents" value={o.missingRequiredDocuments} tone={o.missingRequiredDocuments ? "warn" : "neutral"} icon={<AlertTriangle size={14} />} hint="Required, not uploaded" testId="merch-stat-missing" />
        <StatCard label="Failed processing" value={o.failedDocuments} tone={o.failedDocuments ? "warn" : "neutral"} icon={<FileWarning size={14} />} testId="merch-stat-failed" />
      </div>
      <div className="grid grid-cols-1 xl:grid-cols-2 gap-4 items-start">
        <Panel title={`Deadlines (next ${o.horizonDays} days and overdue)`} subtitle={`Plant date ${o.today}. Pending milestones and delivery dates of active orders.`} testId="merch-deadlines">
          {o.deadlines.length === 0 ? <p className="text-caption text-fg-tertiary">No deadlines in this window.</p> : (
            <ul className="divide-y divide-border-subtle">
              {o.deadlines.map((d) => (
                <li key={`${d.kind}-${d.milestoneId ?? d.orderId}`} className="py-2 flex flex-wrap items-baseline justify-between gap-2" data-testid="merch-deadline" data-overdue={d.overdue}>
                  <span className="min-w-0">
                    <Link className="text-body text-fg-primary hover:underline" href={`/app/merchandising/orders/${d.orderId}`}>{d.name}</Link>
                    <span className="block text-caption text-fg-tertiary">{d.buyer} PO {d.buyerOrderNumber} · <span className="font-mono">{d.orderReference}</span></span>
                  </span>
                  <DueText date={d.dueDate} daysUntil={d.daysUntilDue} overdue={d.overdue} />
                </li>
              ))}
            </ul>
          )}
        </Panel>
        <div className="space-y-4">
          <Panel title="Documents awaiting review" subtitle="Oldest first. Includes failed documents, which can be reviewed by hand." testId="merch-awaiting">
            {o.awaitingReview.length === 0 ? <p className="text-caption text-fg-tertiary">Nothing is waiting.</p> : <DocumentRows items={o.awaitingReview} compact />}
          </Panel>
          <Panel title="Open conflicts" testId="merch-open-conflicts">
            {o.conflicts.length === 0 ? <p className="text-caption text-fg-tertiary">No conflicting values.</p> : (
              <ul className="space-y-2">
                {o.conflicts.map((c) => (
                  <li key={c.id} className="flex flex-wrap items-baseline justify-between gap-2" data-testid="merch-conflict-row">
                    <Link className="text-body text-fg-primary hover:underline" href={`/app/merchandising/orders/${c.orderId}#conflicts`}>{c.label}</Link>
                    <span className="text-caption text-fg-tertiary">{c.values.map((v) => v.display).join(" vs ")} · <span className="font-mono">{c.orderReference}</span></span>
                  </li>
                ))}
              </ul>
            )}
          </Panel>
        </div>
      </div>
    </div>
  );
}

// ── Orders ──────────────────────────────────────────────────────────────────

const ATTENTION_LABEL: Record<string, string> = {
  needs_review: "Documents to review", conflicts: "Open conflicts", overdue: "Overdue", missing_documents: "Missing documents", failed: "Failed processing"
};

function OrdersList({ operator, onRegister }: { operator: boolean; onRegister: () => void }) {
  const [filters, setFilters] = useState<MerchOrderFilters>({ page: 1, pageSize: PAGE_SIZE, status: "active" });
  const [search, setSearch] = useState("");
  const [buyer, setBuyer] = useState("");
  useEffect(() => {
    const t = setTimeout(() => setFilters((f) => (f.q === search && f.buyer === buyer ? f : { ...f, q: search, buyer, page: 1 })), 300);
    return () => clearTimeout(t);
  }, [search, buyer]);
  const path = useMemo(() => merchPaths.orders(filters), [filters]);
  const res = useResource<Paged<MerchOrderListItem>>(path);
  useOnDataChanged(() => void res.reload());
  const data = res.data;
  const set = (patch: Partial<MerchOrderFilters>) => setFilters((f) => ({ ...f, ...patch, page: patch.page ?? 1 }));
  const filtered = Boolean(filters.q || filters.buyer || filters.attention || filters.deliveryFrom || filters.deliveryTo || filters.status !== "active");

  return (
    <div className="space-y-3">
      <div className="surface p-3 flex flex-wrap items-center gap-3" role="search">
        <div className="relative w-full sm:w-60">
          <Search size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-fg-tertiary" aria-hidden />
          <Input aria-label="Search orders" placeholder="Reference, PO, style, product…" className="pl-8" value={search} maxLength={100}
            onChange={(e) => setSearch(e.target.value)} data-testid="merch-search" />
        </div>
        <Input aria-label="Buyer" placeholder="Buyer" className="!w-40 !h-8 !text-caption" value={buyer} maxLength={120} onChange={(e) => setBuyer(e.target.value)} data-testid="merch-filter-buyer" />
        <FilterSelect label="Status" value={filters.status ?? ""} onChange={(v) => set({ status: v })} testId="merch-filter-status">
          <option value="">All</option>
          {MERCH_ORDER_STATUSES.map((s) => <option key={s} value={s}>{ORDER_STATUS_LABEL[s]}</option>)}
        </FilterSelect>
        <FilterSelect label="Needs" value={filters.attention ?? ""} onChange={(v) => set({ attention: v })} testId="merch-filter-attention">
          <option value="">Anything</option>
          {Object.entries(ATTENTION_LABEL).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
        </FilterSelect>
        <label className="flex items-center gap-2 text-caption text-fg-tertiary">
          Delivery from <input type="date" className="control !h-8 !w-auto !text-caption" value={filters.deliveryFrom ?? ""} onChange={(e) => set({ deliveryFrom: e.target.value })} data-testid="merch-filter-from" />
        </label>
        <label className="flex items-center gap-2 text-caption text-fg-tertiary">
          to <input type="date" className="control !h-8 !w-auto !text-caption" value={filters.deliveryTo ?? ""} onChange={(e) => set({ deliveryTo: e.target.value })} data-testid="merch-filter-to" />
        </label>
        {filtered && <Button size="sm" variant="ghost" onClick={() => { setSearch(""); setBuyer(""); setFilters({ page: 1, pageSize: PAGE_SIZE, status: "active" }); }}>Clear</Button>}
        <Button size="sm" variant="ghost" className="ml-auto" onClick={() => void res.reload()} disabled={res.refreshing} aria-label="Refresh">
          <RefreshCw size={13} className={cn(res.refreshing && "animate-spin")} />
        </Button>
      </div>
      {res.error && data && <Callout tone="warning">Showing the last loaded list: {errorMessage(res.error)}</Callout>}
      {res.loading ? <SkeletonRows rows={6} /> : res.error && !data ? (
        <Callout tone="danger" title="Could not load orders" action={<Button size="sm" onClick={() => void res.reload()}>Retry</Button>}>{errorMessage(res.error)}</Callout>
      ) : data && data.items.length === 0 ? (
        <div className="surface" data-testid="merch-orders-empty">
          <EmptyState icon={<FolderOpen size={20} />} title={filtered ? "No orders match these filters" : "No buyer orders yet"}
            body={filtered ? "Change or clear the filters." : operator ? "Register the first buyer order, then upload its documents." : "Orders registered by merchandisers appear here."} />
          {!filtered && operator && <div className="pb-8 text-center"><Button size="sm" variant="secondary" onClick={onRegister}>Register order</Button></div>}
        </div>
      ) : data ? (
        <Panel flush ariaLabel="Buyer orders">
          <div className="overflow-x-auto">
            <table className="data-table" data-testid="merch-orders-table">
              <thead>
                <tr>
                  <th>Order</th>
                  <th>Style</th>
                  <th className="num">Quantity</th>
                  <th>Delivery</th>
                  <th>Next milestone</th>
                  <th>Needs attention</th>
                  <th>Status</th>
                </tr>
              </thead>
              <tbody>
                {data.items.map((o) => (
                  <tr key={o.id} data-testid="merch-order-row">
                    <td>
                      <Link href={`/app/merchandising/orders/${o.id}`} className="text-accent hover:underline">{o.buyer} · PO {o.buyerOrderNumber}</Link>
                      <span className="block font-mono text-[11px] text-fg-tertiary">{o.reference}</span>
                    </td>
                    <td>{o.styleRef ?? <span className="text-fg-tertiary">—</span>}</td>
                    <td className="num">{fmtQty(o.quantity, o.unit)}</td>
                    <td className="whitespace-nowrap">{o.deliveryDate ? <DueText date={o.deliveryDate} daysUntil={o.daysToDelivery ?? 0} overdue={o.status === "active" && (o.daysToDelivery ?? 0) < 0} /> : <span className="text-fg-tertiary">not confirmed</span>}</td>
                    <td>{o.nextMilestone ? <>{o.nextMilestone.name}<span className="block text-caption text-fg-tertiary">{o.nextMilestone.dueDate} ({days(o.nextMilestone.daysUntilDue)})</span></> : <span className="text-fg-tertiary">—</span>}</td>
                    <td>
                      <div className="flex flex-wrap gap-1">
                        {o.overdueMilestones > 0 && <Badge tone="danger">{o.overdueMilestones} overdue</Badge>}
                        {o.openConflicts > 0 && <Badge tone="danger">{o.openConflicts} conflict{o.openConflicts === 1 ? "" : "s"}</Badge>}
                        {o.awaitingReview > 0 && <Badge tone="warning">{o.awaitingReview} to review</Badge>}
                        {o.failedDocuments > 0 && <Badge tone="warning">{o.failedDocuments} failed</Badge>}
                        {o.missingDocuments > 0 && <Badge tone="neutral">{o.missingDocuments} missing doc{o.missingDocuments === 1 ? "" : "s"}</Badge>}
                        {o.overdueMilestones + o.openConflicts + o.awaitingReview + o.failedDocuments + o.missingDocuments === 0 && <span className="text-caption text-fg-tertiary">—</span>}
                      </div>
                    </td>
                    <td><OrderStatusBadge status={o.status} /></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <Pager page={data.page} totalPages={data.totalPages} total={data.total} noun="order" onPage={(p) => set({ page: p })} />
        </Panel>
      ) : null}
    </div>
  );
}

function Pager({ page, totalPages, total, noun, onPage }: { page: number; totalPages: number; total: number; noun: string; onPage: (p: number) => void }) {
  return (
    <div className="flex flex-wrap items-center justify-between gap-2 px-4 py-3 border-t border-border-subtle text-caption text-fg-tertiary">
      <span data-testid="merch-page-info">Page {page} of {Math.max(1, totalPages)} · {total} {noun}{total === 1 ? "" : "s"}</span>
      <div className="flex gap-2">
        <Button size="sm" variant="secondary" disabled={page <= 1} onClick={() => onPage(page - 1)} aria-label="Previous page"><ChevronLeft size={13} /></Button>
        <Button size="sm" variant="secondary" disabled={page >= totalPages} onClick={() => onPage(page + 1)} aria-label="Next page"><ChevronRight size={13} /></Button>
      </div>
    </div>
  );
}

// ── Documents ───────────────────────────────────────────────────────────────

const DOC_TYPE_LABEL: Record<string, string> = {
  purchase_order: "Purchase order", tech_pack: "Tech pack", measurement_sheet: "Measurement sheet", buyer_instruction: "Buyer instruction", other: "Other"
};

function DocumentsList() {
  const [filters, setFilters] = useState<{ q?: string; status?: string; type?: string; page: number; pageSize: number }>({ page: 1, pageSize: PAGE_SIZE });
  const [search, setSearch] = useState("");
  useEffect(() => {
    const t = setTimeout(() => setFilters((f) => (f.q === search ? f : { ...f, q: search, page: 1 })), 300);
    return () => clearTimeout(t);
  }, [search]);
  const [poll, setPoll] = useState(false);
  const res = useResource<Paged<MerchDocumentListItem>>(merchPaths.documents(filters), { pollMs: poll ? 4000 : undefined });
  useOnDataChanged(() => void res.reload());
  const data = res.data;
  useEffect(() => setPoll(!!data?.items.some((d) => d.currentVersion && isInProgress(d.currentVersion.status))), [data]);
  const set = (patch: Partial<typeof filters>) => setFilters((f) => ({ ...f, ...patch, page: patch.page ?? 1 }));

  return (
    <div className="space-y-3">
      <div className="surface p-3 flex flex-wrap items-center gap-3" role="search">
        <div className="relative w-full sm:w-64">
          <Search size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-fg-tertiary" aria-hidden />
          <Input aria-label="Search documents" placeholder="Title, file name, order, buyer…" className="pl-8" value={search} maxLength={100}
            onChange={(e) => setSearch(e.target.value)} data-testid="merch-doc-search" />
        </div>
        <FilterSelect label="State" value={filters.status ?? ""} onChange={(v) => set({ status: v })} testId="merch-doc-filter-status">
          <option value="">All</option>
          {MERCH_VERSION_STATUSES.map((s) => <option key={s} value={s}>{VERSION_STATUS_LABEL[s]}</option>)}
        </FilterSelect>
        <FilterSelect label="Type" value={filters.type ?? ""} onChange={(v) => set({ type: v })}>
          <option value="">All</option>
          {MERCH_DOCUMENT_TYPES.map((t) => <option key={t} value={t}>{DOC_TYPE_LABEL[t]}</option>)}
        </FilterSelect>
      </div>
      {res.loading ? <SkeletonRows rows={6} /> : res.error && !data ? (
        <Callout tone="danger" title="Could not load documents" action={<Button size="sm" onClick={() => void res.reload()}>Retry</Button>}>{errorMessage(res.error)}</Callout>
      ) : data && data.items.length === 0 ? (
        <div className="surface"><EmptyState icon={<FileSearch size={20} />} title="No documents" body="Documents are uploaded from an order's page." /></div>
      ) : data ? (
        <Panel flush ariaLabel="Documents">
          <DocumentRows items={data.items} />
          <Pager page={data.page} totalPages={data.totalPages} total={data.total} noun="document" onPage={(p) => set({ page: p })} />
        </Panel>
      ) : null}
    </div>
  );
}

function DocumentRows({ items, compact }: { items: MerchDocumentListItem[]; compact?: boolean }) {
  return (
    <div className="overflow-x-auto">
      <table className="data-table" data-testid="merch-documents-table">
        <thead>
          <tr>
            <th>Document</th>
            {!compact && <th>Order</th>}
            <th>Current version</th>
            <th>State</th>
          </tr>
        </thead>
        <tbody>
          {items.map((d) => {
            const v = d.currentVersion;
            return (
              <tr key={d.id} data-testid="merch-document-row" data-status={v?.status}>
                <td>
                  {v ? <Link className="text-accent hover:underline" href={`/app/merchandising/versions/${v.id}`}>{d.title}</Link> : d.title}
                  <span className="block text-caption text-fg-tertiary">{d.typeLabel}{compact ? ` · ${d.buyer} PO ${d.buyerOrderNumber}` : ""}</span>
                </td>
                {!compact && <td><Link className="hover:underline" href={`/app/merchandising/orders/${d.orderId}`}>{d.buyer} PO {d.buyerOrderNumber}</Link></td>}
                <td className="whitespace-nowrap">
                  {v ? <>v{v.versionNumber} · {v.originalFileName ?? v.format}<span className="block text-caption text-fg-tertiary">{formatDateTime(v.createdAt)} · {v.uploadedByEmail}</span></> : "—"}
                </td>
                <td>
                  {v && <VersionStatusBadge status={v.status} />}
                  {v?.failureCode && <span className="block text-caption text-fg-tertiary" title={describeCode(v.failureCode)}>{v.failureCode}</span>}
                  {v?.status === "needs_review" && v.blockingIssues > 0 && <span className="block text-caption text-[var(--risk-medium)]">{v.blockingIssues} invalid value(s)</span>}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
