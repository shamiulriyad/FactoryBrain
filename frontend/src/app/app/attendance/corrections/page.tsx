"use client";

// Attendance corrections: requests to fix a session's punches (or add a day
// without punches). The original values are kept; a correction applies only
// when someone other than the requester approves it.

import { useMemo, useState } from "react";
import { FilePen, RefreshCw } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { Field, FilterSelect, TextArea } from "@/components/ui/Field";
import { Modal } from "@/components/ui/Modal";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { Pagination } from "@/components/access/shared";
import { useMyFactories } from "@/components/workers/shared";
import { AttendanceTabs, tableCls, useQueryState } from "@/components/attendance/shared";
import { useResource } from "@/lib/useResource";
import { useAuthStore, useCan } from "@/lib/auth";
import { apiJson, errorMessage } from "@/lib/api";
import { cn } from "@/lib/cn";
import { formatDate, formatDateTime } from "@/lib/format";
import { P, type PagedResult } from "@/lib/access";
import { clock, type Correction } from "@/lib/attendance";

export default function CorrectionsPage() {
  return (
    <RequirePermission perms={[P.AttendanceManage, P.AttendanceApprove]} what="Attendance corrections">
      <Corrections />
    </RequirePermission>
  );
}

function Corrections() {
  const q = useQueryState("/app/attendance/corrections");
  const { factories } = useMyFactories();
  const me = useAuthStore((s) => s.user?.email);
  const canDecide = useCan(P.AttendanceApprove);
  const status = q.get("status") || "Pending";
  const factory = q.get("factory");
  const [deciding, setDeciding] = useState<{ c: Correction; approve: boolean } | null>(null);
  const path = useMemo(() => {
    const p = new URLSearchParams({ page: String(q.page), pageSize: "50" });
    if (status !== "all") p.set("status", status);
    if (factory) p.set("factoryId", factory);
    return `/api/attendance/corrections?${p}`;
  }, [status, factory, q.page]);
  const list = useResource<PagedResult<Correction>>(path);

  return (
    <div className="page" data-testid="corrections-page">
      <PageHeader title="Attendance corrections" description="Request a correction from a record's Correct button, or Add missing day on a worker's page. A second person approves; you can't approve your own." />
      <AttendanceTabs className="mt-4" />
      <Panel className="mt-4" flush title="Corrections" icon={<FilePen size={14} />}
        subtitle={list.data ? `${list.data.total} request${list.data.total === 1 ? "" : "s"}` : undefined}
        right={<Button size="sm" variant="secondary" aria-label="Refresh" onClick={() => void list.reload()}><RefreshCw size={13} className={cn(list.refreshing && "animate-spin")} /></Button>}>
        <div className="p-4 flex flex-wrap items-center gap-3 border-b border-border-subtle">
          <FilterSelect label="Status" value={status} onChange={(v) => q.set({ status: v === "Pending" ? null : v })} testId="corrections-status">
            <option value="Pending">Pending</option>
            <option value="Approved">Approved</option>
            <option value="Rejected">Rejected</option>
            <option value="all">All</option>
          </FilterSelect>
          {(factories?.length ?? 0) > 1 && (
            <FilterSelect label="Factory" value={factory} onChange={(v) => q.set({ factory: v || null })}>
              <option value="">All factories</option>
              {factories!.map((x) => <option key={x.id} value={x.id}>{x.code} · {x.name}</option>)}
            </FilterSelect>
          )}
        </div>
        {list.loading ? <div className="p-4"><SkeletonRows rows={5} rowHeight="h-11" /></div>
          : list.error && !list.data ? <ErrorState className="m-4" title="Couldn't load corrections" body={errorMessage(list.error)} retryLabel="Try again" onRetry={() => void list.reload()} />
          : (list.data?.items.length ?? 0) === 0 ? <EmptyState className="m-4" icon={<FilePen size={18} />} title={status === "Pending" ? "No corrections waiting" : "No corrections"} body="Correction requests appear here." />
          : (
            <>
              <div className="overflow-x-auto">
                <table className="w-full min-w-[1000px]" data-testid="corrections-table">
                  <thead className="border-b border-border-subtle">
                    <tr>
                      {["Worker", "Day", "Original", "Proposed", "Reason", "Requested", "Status", ""].map((h, i) => <th key={i} className={tableCls.th}>{h || <span className="sr-only">Actions</span>}</th>)}
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-border-subtle">
                    {list.data!.items.map((c) => {
                      const own = !!me && c.requestedByEmail.toLowerCase() === me.toLowerCase();
                      return (
                        <tr key={c.id} data-testid={`correction-${c.workerCode}`}>
                          <td className={tableCls.td}><span className="font-mono">{c.workerCode}</span> {c.workerName}</td>
                          <td className={cn(tableCls.td, "whitespace-nowrap")}>{c.workDate ? formatDate(c.workDate) : "—"}{!c.sessionId && <Badge tone="info" className="ml-1">new day</Badge>}</td>
                          <td className={cn(tableCls.td, "whitespace-nowrap text-fg-tertiary")}>{c.sessionId ? `${clock(c.originalCheckIn)} – ${clock(c.originalCheckOut)}` : "no punches"}</td>
                          <td className={cn(tableCls.td, "whitespace-nowrap")}>{clock(c.proposedCheckIn)} – {clock(c.proposedCheckOut)}</td>
                          <td className={cn(tableCls.td, "max-w-[240px]")}>{c.reason}</td>
                          <td className={cn(tableCls.td, "text-fg-tertiary")}>{c.requestedByEmail}<br />{formatDateTime(c.requestedAt)}</td>
                          <td className={tableCls.td}>
                            <Badge tone={c.status === "Approved" ? "success" : c.status === "Rejected" ? "danger" : "warning"}>{c.status}</Badge>
                            {c.decidedByEmail && <p className="text-fg-tertiary mt-1">{c.decidedByEmail}{c.decisionNote ? `: ${c.decisionNote}` : ""}</p>}
                          </td>
                          <td className={cn(tableCls.td, "text-right whitespace-nowrap")}>
                            {c.status === "Pending" && canDecide && (own
                              ? <span className="text-fg-tertiary">Your request</span>
                              : <span className="inline-flex gap-1">
                                  <Button size="sm" variant="primary" onClick={() => setDeciding({ c, approve: true })} data-testid={`correction-approve-${c.workerCode}`}>Approve</Button>
                                  <Button size="sm" variant="secondary" onClick={() => setDeciding({ c, approve: false })}>Reject</Button>
                                </span>)}
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
              <Pagination page={list.data!.page} pageSize={list.data!.pageSize} total={list.data!.total} onPage={(p) => q.set({ page: String(p) }, false)} />
            </>
          )}
      </Panel>
      <DecideDialog target={deciding} onClose={() => setDeciding(null)} onDone={() => { setDeciding(null); void list.reload(); }} />
    </div>
  );
}

function DecideDialog({ target, onClose, onDone }: { target: { c: Correction; approve: boolean } | null; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  if (!target) return null;
  const { c, approve } = target;

  async function save() {
    setBusy(true);
    setError(null);
    try {
      await apiJson(`/api/attendance/corrections/${c.id}/${approve ? "approve" : "reject"}`, { method: "POST", body: { note: note.trim() || null } });
      toast.push({ tone: "success", title: `Correction ${approve ? "approved and applied" : "rejected"}`, description: `${c.workerCode} · ${c.workDate ?? ""}` });
      setNote("");
      onDone();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal open onClose={onClose} locked={busy} testId="correction-decide-dialog" title={`${approve ? "Approve" : "Reject"} correction · ${c.workerCode}`}
      description={`${c.workerName}${c.workDate ? ` · ${formatDate(c.workDate)}` : ""}`}
      footer={
        <div className="flex justify-end gap-2">
          <Button variant="ghost" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button variant={approve ? "primary" : "danger"} onClick={() => void save()} disabled={busy} data-testid="correction-decide-confirm">{busy ? "Saving…" : approve ? "Approve" : "Reject"}</Button>
        </div>
      }>
      <div className="space-y-3">
        <p className="text-caption">Original: {c.sessionId ? `${clock(c.originalCheckIn)} – ${clock(c.originalCheckOut)}` : "no punches"} → proposed: {clock(c.proposedCheckIn)} – {clock(c.proposedCheckOut)}</p>
        <p className="text-caption text-fg-secondary">Reason: {c.reason}</p>
        {approve && <Callout tone="info">Approving writes correction events and recalculates the session. The original punches stay on record.</Callout>}
        <Field label="Note (optional)">{({ id }) => <TextArea id={id} rows={2} maxLength={500} value={note} onChange={(e) => setNote(e.target.value)} />}</Field>
        {error && <Callout tone="danger" role="alert">{error}</Callout>}
      </div>
    </Modal>
  );
}
