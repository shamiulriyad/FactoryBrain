"use client";

// Factory holiday calendar. On a holiday nobody is expected, so attendance
// statistics, analytics and the HR documents (attendance, late / absent,
// payroll input) count nobody absent; anyone who works is still present.
// Approved leave (Leave Record documents) is handled the same way per worker.
// Holidays that have started can't be removed: the attendance already reported
// for them stays as reported.

import Link from "next/link";
import { useState } from "react";
import { CalendarOff, Plus } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { Field, FilterSelect, Select } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Modal } from "@/components/ui/Modal";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { DataTable } from "@/components/analytics/shared";
import { useMyFactories } from "@/components/workers/shared";
import { AttendanceTabs, useQueryState } from "@/components/attendance/shared";
import { useResource } from "@/lib/useResource";
import { useCan } from "@/lib/auth";
import { apiJson, errorMessage } from "@/lib/api";
import { formatDate } from "@/lib/format";
import { P } from "@/lib/access";

type Holiday = { id: string; factoryId: string; date: string; name: string; kind: "PUBLIC" | "FACTORY"; createdAt: string; createdByEmail: string; canRemove: boolean };

export default function HolidaysPage() {
  return (
    <RequirePermission perms={[P.AttendanceView]} what="Holidays">
      <Holidays />
    </RequirePermission>
  );
}

function Holidays() {
  const q = useQueryState("/app/attendance/holidays");
  const { factories } = useMyFactories();
  const factoryId = q.get("factory") || (factories && factories.length > 0 ? factories[0].id : "");
  const year = Number(q.get("year")) || new Date().getFullYear();
  const list = useResource<Holiday[]>(factoryId ? `/api/attendance/holidays?factoryId=${factoryId}&from=${year}-01-01&to=${year}-12-31` : null);
  const canManage = useCan(P.AttendanceManage);
  const toast = useToast();
  const [adding, setAdding] = useState(false);
  const [removing, setRemoving] = useState<string | null>(null);

  async function remove(h: Holiday) {
    setRemoving(h.id);
    try {
      await apiJson(`/api/attendance/holidays/${h.id}`, { method: "DELETE" });
      toast.push({ title: `${h.name} removed` });
      void list.reload();
    } catch (e) { toast.push({ title: errorMessage(e) }); } finally { setRemoving(null); }
  }

  return (
    <div className="page" data-testid="holidays-page">
      <PageHeader eyebrow="Attendance" title="Holidays"
        description="Days the factory is closed. Nobody is expected on them, so nobody is counted absent. Approved leave records work the same way for each worker."
        actions={canManage ? <Button size="sm" iconLeft={<Plus size={14} />} onClick={() => setAdding(true)} data-testid="holiday-add">Add holiday</Button> : undefined} />
      <AttendanceTabs className="mb-4" />
      <div className="surface p-3 flex flex-wrap items-end gap-3">
        {factories && factories.length > 1 && (
          <FilterSelect label="Factory" value={factoryId} onChange={(v) => q.set({ factory: v || null })}>
            {factories.map((f) => <option key={f.id} value={f.id}>{f.code} · {f.name}</option>)}
          </FilterSelect>
        )}
        <FilterSelect label="Year" value={String(year)} onChange={(v) => q.set({ year: v || null })}>
          {[year - 1, year, year + 1].map((y) => <option key={y} value={y}>{y}</option>)}
        </FilterSelect>
        <Link href="/app/documents/list?type=hr.leave" className="ml-auto text-caption text-accent hover:underline">Leave records</Link>
      </div>
      <Panel className="mt-4" flush title={`Holidays ${year}`} icon={<CalendarOff size={14} />} subtitle="Past holidays stay as they were reported.">
        {list.loading && !list.data ? <div className="p-4"><SkeletonRows rows={3} /></div>
          : list.error && !list.data ? <ErrorState className="m-4" title="Couldn't load holidays" body={errorMessage(list.error)} retryLabel="Try again" onRetry={() => void list.reload()} />
          : (list.data?.length ?? 0) === 0 ? <EmptyState className="m-4" icon={<CalendarOff size={18} />} title="No holidays this year" body={canManage ? "Add public and factory holidays so nobody is counted absent on them." : "HR adds public and factory holidays here."} />
          : <DataTable testId="holiday-table" columns={["Date", "Holiday", "Kind", "Added by", ""]}
              rows={list.data!.map((h) => [
                formatDate(h.date), h.name, <Badge key="k" tone={h.kind === "PUBLIC" ? "info" : "neutral"}>{h.kind === "PUBLIC" ? "Public" : "Factory"}</Badge>,
                <span key="by" className="text-fg-secondary">{h.createdByEmail}</span>,
                canManage && h.canRemove ? <Button key="r" size="sm" variant="ghost" disabled={removing === h.id} onClick={() => void remove(h)}>Remove</Button> : ""
              ])} />}
      </Panel>
      {adding && <AddHoliday factoryId={factoryId} onClose={() => setAdding(false)} onSaved={() => { setAdding(false); void list.reload(); }} />}
    </div>
  );
}

function AddHoliday({ factoryId, onClose, onSaved }: { factoryId: string; onClose: () => void; onSaved: () => void }) {
  const toast = useToast();
  const [v, setV] = useState({ date: "", name: "", kind: "PUBLIC" });
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const past = v.date !== "" && v.date <= new Date().toISOString().slice(0, 10);
  async function save() {
    setBusy(true); setError(null);
    try {
      await apiJson("/api/attendance/holidays", { method: "POST", body: { factoryId, date: v.date, name: v.name, kind: v.kind } });
      toast.push({ title: `${v.name} added` });
      onSaved();
    } catch (e) { setError(errorMessage(e)); } finally { setBusy(false); }
  }
  return (
    <Modal open onClose={onClose} title="Add holiday"
      footer={<><Button variant="ghost" onClick={onClose}>Cancel</Button><Button onClick={() => void save()} disabled={busy || !v.date || !v.name.trim()} data-testid="holiday-save">Save</Button></>}>
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label="Date">{(ids) => <Input id={ids.id} type="date" value={v.date} onChange={(e) => setV({ ...v, date: e.target.value })} />}</Field>
        <Field label="Kind">{(ids) => <Select id={ids.id} value={v.kind} onChange={(e) => setV({ ...v, kind: e.target.value })}><option value="PUBLIC">Public / festival</option><option value="FACTORY">Factory closure</option></Select>}</Field>
        <Field label="Name" className="sm:col-span-2">{(ids) => <Input id={ids.id} value={v.name} maxLength={120} onChange={(e) => setV({ ...v, name: e.target.value })} placeholder="e.g. Victory Day" />}</Field>
      </div>
      {past && <Callout tone="warning" className="mt-3">This day has already started: its attendance figures and the HR reports generated afterwards will no longer count anyone absent, and the holiday can&apos;t be removed later.</Callout>}
      {error && <Callout tone="danger" className="mt-3">{error}</Callout>}
    </Modal>
  );
}
