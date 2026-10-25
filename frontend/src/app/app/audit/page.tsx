"use client";

import { useEffect, useMemo, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { ArrowDown, ArrowUp, RefreshCw, ScrollText, Search } from "lucide-react";
import { RequireRole } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge, type BadgeTone } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { FilterSelect } from "@/components/ui/Field";
import { Drawer } from "@/components/ui/Drawer";
import { EmptyState } from "@/components/ui/EmptyState";
import { SkeletonRows } from "@/components/ui/Status";
import { useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { formatDateTime, formatRelative } from "@/lib/format";
import { useT } from "@/lib/useT";
import { cn } from "@/lib/cn";
import type { AuditEntry } from "@/services/contracts";

/** Entity types the API writes today (ApprovalService, AccountService, settings and reindex). */
const ENTITY_TYPES = [
  { value: "", label: "All entities" },
  { value: "approval", label: "Approvals" },
  { value: "settings", label: "AI settings" },
  { value: "rag_index", label: "RAG index" },
  { value: "user", label: "Users" }
];
const LIMITS = [50, 100, 200];
const PAGE_SIZE = 25;

function actionTone(action: string): BadgeTone {
  if (/rejected|failed|cleared/.test(action)) return "danger";
  if (/approved|completed/.test(action)) return "success";
  if (/role_changed|updated|password/.test(action)) return "warning";
  return "neutral";
}

export default function AuditPage() {
  return (
    <RequireRole role="Admin" what="The audit log">
      <AuditLog />
    </RequireRole>
  );
}

function AuditLog() {
  const { locale } = useT();
  const router = useRouter();
  const search = useSearchParams();
  const entityType = search?.get("entityType") ?? "";
  const entityId = search?.get("entityId") ?? "";
  const limit = Number(search?.get("limit")) || 100;

  const [entityIdDraft, setEntityIdDraft] = useState(entityId);
  const [query, setQuery] = useState("");
  const [newestFirst, setNewestFirst] = useState(true);
  const [page, setPage] = useState(0);
  const [selected, setSelected] = useState<AuditEntry | null>(null);

  useEffect(() => setEntityIdDraft(entityId), [entityId]);

  const params = new URLSearchParams();
  if (entityType) params.set("entityType", entityType);
  if (entityId) params.set("entityId", entityId);
  params.set("limit", String(Math.min(200, Math.max(1, limit))));
  const audit = useResource<AuditEntry[]>(`/api/audit?${params.toString()}`);

  function setFilter(patch: Record<string, string>) {
    const next = new URLSearchParams(search?.toString() ?? "");
    for (const [k, v] of Object.entries(patch)) {
      if (v) next.set(k, v);
      else next.delete(k);
    }
    setPage(0);
    router.replace(`/app/audit${next.toString() ? `?${next}` : ""}`);
  }

  const rows = useMemo(() => {
    let list = (audit.data ?? []).slice();
    const q = query.trim().toLowerCase();
    if (q) {
      list = list.filter((e) =>
        [e.action, e.actorEmail, e.actorRole, e.entityId, e.reason ?? "", e.fromState ?? "", e.toState ?? ""]
          .some((s) => s.toLowerCase().includes(q))
      );
    }
    list.sort((a, b) => (newestFirst ? b.id - a.id : a.id - b.id));
    return list;
  }, [audit.data, query, newestFirst]);

  const pages = Math.max(1, Math.ceil(rows.length / PAGE_SIZE));
  const pageRows = rows.slice(page * PAGE_SIZE, (page + 1) * PAGE_SIZE);
  useEffect(() => {
    if (page > pages - 1) setPage(0);
  }, [page, pages]);

  return (
    <div className="page" data-testid="audit-page">
      <PageHeader
        eyebrow="Administration"
        title="Audit log"
        description="Every approval decision, AI-settings change, reindex and role change, as recorded by the API. The log is append-only: entries can't be edited or deleted."
        actions={
          <Button variant="secondary" size="sm" onClick={() => void audit.reload()} disabled={audit.refreshing}>
            <RefreshCw size={13} className={cn(audit.refreshing && "animate-spin")} /> Refresh
          </Button>
        }
      />

      <div className="mt-5 surface p-3 flex flex-wrap items-center gap-x-4 gap-y-2">
        <FilterSelect label="Entity" value={entityType} onChange={(v) => setFilter({ entityType: v })} testId="audit-entity-type">
          {ENTITY_TYPES.map((o) => (
            <option key={o.value} value={o.value}>{o.label}</option>
          ))}
        </FilterSelect>
        <form
          className="flex items-center gap-2"
          onSubmit={(e) => {
            e.preventDefault();
            setFilter({ entityId: entityIdDraft.trim() });
          }}
        >
          <label htmlFor="audit-entity-id" className="text-caption text-fg-tertiary whitespace-nowrap">Entity id</label>
          <input
            id="audit-entity-id"
            value={entityIdDraft}
            onChange={(e) => setEntityIdDraft(e.target.value)}
            placeholder="exact id, e.g. apr-…"
            className="control !h-8 !w-48 !text-caption font-mono"
          />
          <Button type="submit" size="sm" variant="secondary">Apply</Button>
          {entityId && (
            <Button type="button" size="sm" variant="ghost" onClick={() => setFilter({ entityId: "" })}>Clear</Button>
          )}
        </form>
        <FilterSelect label="Load" value={String(limit)} onChange={(v) => setFilter({ limit: v })}>
          {LIMITS.map((l) => (
            <option key={l} value={l}>newest {l}</option>
          ))}
        </FilterSelect>
        <div className="relative flex-1 min-w-[180px]">
          <Search size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-fg-tertiary" aria-hidden />
          <input
            type="search"
            value={query}
            onChange={(e) => {
              setQuery(e.target.value);
              setPage(0);
            }}
            placeholder="Filter loaded entries by action, actor or reason"
            aria-label="Filter loaded audit entries"
            className="control !h-8 !pl-8 !text-caption"
          />
        </div>
      </div>

      {audit.error && (
        <Callout tone="danger" className="mt-4" title="Could not load the audit log" action={<Button size="sm" onClick={() => void audit.reload()}>Retry</Button>}>
          {errorMessage(audit.error)}
        </Callout>
      )}

      <Panel
        className="mt-4"
        flush
        title="Entries"
        subtitle={
          audit.data
            ? `${rows.length} of ${audit.data.length} loaded entries${audit.data.length >= limit ? ` (the API returned its limit of ${limit}; load more or narrow the filter to see older entries)` : ""}`
            : undefined
        }
        right={
          <Button variant="ghost" size="sm" onClick={() => setNewestFirst((v) => !v)} aria-label={newestFirst ? "Sort oldest first" : "Sort newest first"}>
            {newestFirst ? <ArrowDown size={13} /> : <ArrowUp size={13} />} {newestFirst ? "Newest first" : "Oldest first"}
          </Button>
        }
      >
        {audit.loading ? (
          <div className="p-4"><SkeletonRows rows={8} rowHeight="h-10" /></div>
        ) : rows.length === 0 ? (
          <EmptyState
            icon={<ScrollText size={22} />}
            title={audit.data?.length ? "No loaded entries match this filter." : "No audit entries yet."}
            body={audit.data?.length ? "Clear the text filter or change the entity filter." : "Entries appear when someone decides an approval, changes AI settings, reindexes or changes a role."}
          />
        ) : (
          <div className="overflow-x-auto">
            <table className="data-table" data-testid="audit-table">
              <thead>
                <tr>
                  <th>When</th>
                  <th>Actor</th>
                  <th>Action</th>
                  <th>Target</th>
                  <th>Change</th>
                  <th className="hidden lg:table-cell">Reason / detail</th>
                </tr>
              </thead>
              <tbody>
                {pageRows.map((e) => (
                  <tr
                    key={e.id}
                    data-testid="audit-row"
                    data-action={e.action}
                    className="cursor-pointer"
                    tabIndex={0}
                    onClick={() => setSelected(e)}
                    onKeyDown={(ev) => (ev.key === "Enter" || ev.key === " ") && (ev.preventDefault(), setSelected(e))}
                    aria-label={`${e.action} by ${e.actorEmail}, ${formatDateTime(e.occurredAt, locale)}. Open details.`}
                  >
                    <td className="whitespace-nowrap">
                      <span className="text-fg-primary">{formatDateTime(e.occurredAt, locale)}</span>
                      <span className="block text-[11px] text-fg-tertiary">{formatRelative(e.occurredAt, locale)}</span>
                    </td>
                    <td>
                      <span className="block text-fg-primary truncate max-w-[200px]">{e.actorEmail}</span>
                      <span className="block text-[11px] text-fg-tertiary">{e.actorRole}</span>
                    </td>
                    <td><Badge tone={actionTone(e.action)}><span className="font-mono">{e.action}</span></Badge></td>
                    <td>
                      <span className="block text-fg-secondary">{e.entityType}</span>
                      <span className="block font-mono text-[11px] text-fg-tertiary truncate max-w-[180px]">{e.entityId}</span>
                    </td>
                    <td className="whitespace-nowrap font-mono text-[12px]">{e.fromState ?? "—"} → {e.toState ?? "—"}</td>
                    <td className="hidden lg:table-cell max-w-[320px]"><span className="line-clamp-2">{e.reason ?? "—"}</span></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {pages > 1 && (
          <div className="flex items-center justify-between gap-2 px-4 py-3 border-t border-border-subtle">
            <span className="text-caption text-fg-tertiary">Page {page + 1} of {pages}</span>
            <div className="flex gap-2">
              <Button size="sm" variant="secondary" onClick={() => setPage((p) => Math.max(0, p - 1))} disabled={page === 0}>Previous</Button>
              <Button size="sm" variant="secondary" onClick={() => setPage((p) => Math.min(pages - 1, p + 1))} disabled={page >= pages - 1}>Next</Button>
            </div>
          </div>
        )}
      </Panel>

      <Drawer open={!!selected} onClose={() => setSelected(null)} title={selected ? "Audit entry" : null} width="w-full sm:w-[30rem]">
        {selected && (
          <dl className="p-5 grid grid-cols-[auto_1fr] gap-x-4 gap-y-2.5 text-caption" data-testid="audit-detail">
            <dt className="text-fg-tertiary">Entry</dt><dd className="font-mono text-fg-primary">#{selected.id}</dd>
            <dt className="text-fg-tertiary">When</dt><dd className="text-fg-primary">{formatDateTime(selected.occurredAt, locale)}</dd>
            <dt className="text-fg-tertiary">Actor</dt><dd className="text-fg-primary break-all">{selected.actorEmail} ({selected.actorRole})</dd>
            <dt className="text-fg-tertiary">Actor id</dt><dd className="font-mono text-fg-secondary break-all">{selected.actorUserId ?? "—"}</dd>
            <dt className="text-fg-tertiary">Action</dt><dd className="font-mono text-fg-primary">{selected.action}</dd>
            <dt className="text-fg-tertiary">Entity</dt><dd className="text-fg-primary">{selected.entityType}</dd>
            <dt className="text-fg-tertiary">Entity id</dt><dd className="font-mono text-fg-secondary break-all">{selected.entityId}</dd>
            <dt className="text-fg-tertiary">From</dt><dd className="font-mono text-fg-secondary break-all">{selected.fromState ?? "—"}</dd>
            <dt className="text-fg-tertiary">To</dt><dd className="font-mono text-fg-secondary break-all">{selected.toState ?? "—"}</dd>
            <dt className="text-fg-tertiary">Reason / detail</dt><dd className="text-fg-secondary whitespace-pre-wrap break-words">{selected.reason ?? "—"}</dd>
            <dt className="text-fg-tertiary">Trace id</dt><dd className="font-mono text-fg-secondary break-all">{selected.traceId ?? "—"}</dd>
            {selected.entityType === "approval" && (
              <>
                <dt />
                <dd>
                  <a className="text-accent hover:underline" href={`/app/approvals?focus=${encodeURIComponent(selected.entityId)}`}>Open the approval</a>
                </dd>
              </>
            )}
          </dl>
        )}
      </Drawer>
    </div>
  );
}
