"use client";

// Factories and their production lines. A Super Admin creates, renames and
// (de)activates factories; a Factory Admin sees their own factory and
// attaches or detaches its lines. The API enforces both.

import { useState } from "react";
import { Building2, Factory, Link2, Plus, RefreshCw, Unlink } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Field, Select } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Modal, ConfirmDialog } from "@/components/ui/Modal";
import { Callout } from "@/components/ui/Callout";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { useResource } from "@/lib/useResource";
import { apiJson, errorMessage } from "@/lib/api";
import { useCan } from "@/lib/auth";
import { formatDate } from "@/lib/format";
import { useT } from "@/lib/useT";
import { cn } from "@/lib/cn";
import { P, type FactoryDto, type FactoryLine } from "@/lib/access";
import { OrgUnitsPanel } from "@/components/workers/OrgUnitsPanel";

export default function FactoriesPage() {
  return (
    <RequirePermission perms={[P.FactoriesManage, P.LinesManage, P.DepartmentsManage]} what="Factory management">
      <Factories />
    </RequirePermission>
  );
}

function Factories() {
  const { locale } = useT();
  const toast = useToast();
  const platform = useCan(P.FactoriesManage);
  const canDepartments = useCan(P.DepartmentsManage);
  const canFloors = useCan(P.LinesManage);
  const factories = useResource<FactoryDto[]>("/api/factories");
  const [editing, setEditing] = useState<FactoryDto | "new" | null>(null);
  const [toggling, setToggling] = useState<FactoryDto | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function toggle(f: FactoryDto) {
    setBusy(true);
    try {
      await apiJson(`/api/factories/${f.id}/status`, { method: "POST", body: { isActive: !f.isActive } });
      toast.push({ tone: "success", title: f.isActive ? "Factory deactivated" : "Factory reactivated" });
      await factories.reload();
    } catch (e) {
      toast.push({ tone: "error", title: "Not changed", description: errorMessage(e) });
    } finally {
      setBusy(false);
      setToggling(null);
    }
  }

  const current = factories.data?.find((f) => f.id === selected) ?? factories.data?.[0] ?? null;

  return (
    <div className="page" data-testid="factories-page">
      <PageHeader
        eyebrow="Administration"
        title="Factories, lines & departments"
        description={platform ? "Every factory on the platform. Factory-level users only ever reach the factories they are assigned to." : "Your factory and its production lines."}
        actions={platform && <Button variant="primary" iconLeft={<Plus size={14} />} onClick={() => setEditing("new")} data-testid="new-factory">New factory</Button>}
      />

      {factories.loading ? (
        <div className="mt-6"><SkeletonRows rows={3} rowHeight="h-16" /></div>
      ) : factories.error ? (
        <ErrorState className="mt-6" title="Couldn't load factories" body={errorMessage(factories.error)} retryLabel="Try again" onRetry={() => void factories.reload()} />
      ) : (factories.data?.length ?? 0) === 0 ? (
        <EmptyState className="mt-6" icon={<Building2 size={18} />} title="No factories yet" body={platform ? "Create the first factory, then add its Factory Admin under Users." : "You are not assigned to a factory."} />
      ) : (
        <div className="mt-6 grid gap-4 lg:grid-cols-[minmax(0,1fr)_minmax(0,1.2fr)]">
          <Panel flush title="Factories" icon={<Factory size={14} />}
            right={<Button size="sm" variant="secondary" aria-label="Refresh" onClick={() => void factories.reload()}><RefreshCw size={13} /></Button>}>
            <ul className="divide-y divide-border-subtle" data-testid="factory-list">
              {factories.data!.map((f) => (
                <li key={f.id}>
                  <button type="button" onClick={() => setSelected(f.id)}
                    className={cn("w-full text-left px-4 py-3 flex flex-wrap items-center gap-3 hover:bg-surface-2", current?.id === f.id && "bg-[var(--accent-soft)]")}>
                    <span className="font-mono text-caption px-1.5 py-0.5 rounded border border-border-subtle">{f.code}</span>
                    <span className="min-w-0 flex-1">
                      <span className="block text-body font-medium text-fg-primary truncate">{f.name}</span>
                      <span className="block text-caption text-fg-tertiary">{f.location ?? "—"} · {f.userCount} users · {f.lineCount} lines · since {formatDate(f.createdAt, locale)}</span>
                    </span>
                    <Badge tone={f.isActive ? "success" : "danger"} dot>{f.isActive ? "Active" : "Inactive"}</Badge>
                  </button>
                  {platform && current?.id === f.id && (
                    <div className="px-4 pb-3 flex gap-2">
                      <Button size="sm" variant="secondary" onClick={() => setEditing(f)}>Edit</Button>
                      <Button size="sm" variant={f.isActive ? "danger" : "success"} onClick={() => setToggling(f)} data-testid="toggle-factory">
                        {f.isActive ? "Deactivate" : "Reactivate"}
                      </Button>
                    </div>
                  )}
                </li>
              ))}
            </ul>
          </Panel>
          {current && <LinesPanel factory={current} onChanged={() => void factories.reload()} />}
          {current && (
            <div className="lg:col-span-2 grid gap-4 lg:grid-cols-2">
              <OrgUnitsPanel key={`d-${current.id}`} factoryId={current.id} factoryName={current.name} kind="departments" canManage={canDepartments} factoryActive={current.isActive} />
              <OrgUnitsPanel key={`f-${current.id}`} factoryId={current.id} factoryName={current.name} kind="floors" canManage={canFloors} factoryActive={current.isActive} />
            </div>
          )}
        </div>
      )}

      {editing && <FactoryForm factory={editing === "new" ? null : editing} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); void factories.reload(); }} />}
      <ConfirmDialog
        open={!!toggling}
        title={toggling?.isActive ? `Deactivate ${toggling?.name}?` : `Reactivate ${toggling?.name}?`}
        description={toggling?.isActive
          ? "Everyone assigned only to this factory loses access at once (their sessions refresh with no factory access). Data is kept."
          : "Its members regain access to it."}
        confirmLabel={toggling?.isActive ? "Deactivate" : "Reactivate"}
        tone={toggling?.isActive ? "danger" : "success"}
        busy={busy}
        onConfirm={() => toggling && void toggle(toggling)}
        onCancel={() => setToggling(null)}
      />
    </div>
  );
}

function FactoryForm({ factory, onClose, onSaved }: { factory: FactoryDto | null; onClose: () => void; onSaved: () => void }) {
  const toast = useToast();
  const [code, setCode] = useState(factory?.code ?? "");
  const [name, setName] = useState(factory?.name ?? "");
  const [location, setLocation] = useState(factory?.location ?? "");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function save() {
    setBusy(true);
    setError(null);
    try {
      if (factory) await apiJson(`/api/factories/${factory.id}`, { method: "PUT", body: { name: name.trim(), location: location.trim() || null } });
      else await apiJson("/api/factories", { method: "POST", body: { code: code.trim(), name: name.trim(), location: location.trim() || null } });
      toast.push({ tone: "success", title: factory ? "Factory saved" : "Factory created", description: factory ? undefined : "Next: create its Factory Admin under Users." });
      onSaved();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal open onClose={onClose} title={factory ? `Edit ${factory.name}` : "New factory"} testId="factory-form"
      footer={
        <div className="flex justify-end gap-2">
          <Button variant="secondary" onClick={onClose}>Cancel</Button>
          <Button variant="primary" disabled={busy || !name.trim() || (!factory && !code.trim())} onClick={() => void save()} data-testid="save-factory">
            {busy ? "Saving…" : factory ? "Save" : "Create factory"}
          </Button>
        </div>
      }>
      <div className="space-y-3">
        <Field label="Code" hint={factory ? "The code can't be changed." : "Short and unique, e.g. DHK-1 (letters, digits, hyphens)."}>
          {({ id }) => <Input id={id} value={code} onChange={(e) => setCode(e.target.value.toUpperCase())} disabled={!!factory} maxLength={16} className="font-mono" data-testid="factory-code" />}
        </Field>
        <Field label="Name">
          {({ id }) => <Input id={id} value={name} onChange={(e) => setName(e.target.value)} maxLength={120} data-testid="factory-name" />}
        </Field>
        <Field label="Location" hint="Optional">
          {({ id }) => <Input id={id} value={location} onChange={(e) => setLocation(e.target.value)} maxLength={200} />}
        </Field>
        {error && <Callout tone="danger" role="alert">{error}</Callout>}
      </div>
    </Modal>
  );
}

function LinesPanel({ factory, onChanged }: { factory: FactoryDto; onChanged: () => void }) {
  const toast = useToast();
  const canManage = useCan(P.LinesManage);
  const lines = useResource<FactoryLine[]>(`/api/factories/${factory.id}/lines`);
  const unassigned = useResource<FactoryLine[]>(canManage ? "/api/factories/unassigned-lines" : null);
  const [pick, setPick] = useState("");
  const [detaching, setDetaching] = useState<FactoryLine | null>(null);
  const [busy, setBusy] = useState(false);

  async function attach() {
    if (!pick) return;
    setBusy(true);
    try {
      await apiJson(`/api/factories/${factory.id}/lines/${encodeURIComponent(pick)}`, { method: "PUT" });
      toast.push({ tone: "success", title: "Line attached" });
      setPick("");
      await Promise.all([lines.reload(), unassigned.reload()]);
      onChanged();
    } catch (e) {
      toast.push({ tone: "error", title: "Not attached", description: errorMessage(e) });
    } finally {
      setBusy(false);
    }
  }

  async function detach(line: FactoryLine) {
    setBusy(true);
    try {
      await apiJson(`/api/factories/${factory.id}/lines/${encodeURIComponent(line.id)}`, { method: "DELETE" });
      toast.push({ tone: "success", title: "Line detached", description: "Supervisors assigned to it lost access." });
      await Promise.all([lines.reload(), unassigned.reload()]);
      onChanged();
    } catch (e) {
      toast.push({ tone: "error", title: "Not detached", description: errorMessage(e) });
    } finally {
      setBusy(false);
      setDetaching(null);
    }
  }

  return (
    <Panel flush title={`Lines · ${factory.name}`} subtitle="Production Supervisors are assigned to these lines on their user page." icon={<Link2 size={14} />}>
      {lines.loading ? (
        <div className="p-4"><SkeletonRows rows={3} rowHeight="h-10" /></div>
      ) : lines.error ? (
        <ErrorState className="m-4" title="Couldn't load lines" body={errorMessage(lines.error)} retryLabel="Try again" onRetry={() => void lines.reload()} />
      ) : (lines.data?.length ?? 0) === 0 ? (
        <EmptyState className="m-4" title="No lines in this factory" body={canManage ? "Attach a production line below. New lines are created in Production planning → Setup." : undefined} />
      ) : (
        <table className="data-table" data-testid="factory-lines">
          <thead><tr><th>Line</th><th className="num">Workstations</th><th>Status</th>{canManage && <th />}</tr></thead>
          <tbody>
            {lines.data!.map((l) => (
              <tr key={l.id}>
                <td><span className="text-fg-primary">{l.name}</span> <span className="font-mono text-caption text-fg-tertiary">{l.id}</span></td>
                <td className="num">{l.workstations}</td>
                <td><Badge tone={l.isActive ? "success" : "neutral"}>{l.isActive ? "Active" : "Inactive"}</Badge></td>
                {canManage && (
                  <td className="text-right">
                    <Button size="sm" variant="ghost" iconLeft={<Unlink size={12} />} onClick={() => setDetaching(l)}>Detach</Button>
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {canManage && factory.isActive && (
        <div className="p-4 border-t border-border-subtle flex flex-wrap items-end gap-2">
          <Field label="Attach a line with no factory" className="flex-1 min-w-[220px]">
            {({ id }) => (
              <Select id={id} value={pick} onChange={(e) => setPick(e.target.value)} disabled={!unassigned.data?.length}>
                <option value="">{unassigned.data?.length ? "Choose a line…" : "No unattached lines"}</option>
                {(unassigned.data ?? []).map((l) => <option key={l.id} value={l.id}>{l.name} ({l.id})</option>)}
              </Select>
            )}
          </Field>
          <Button variant="secondary" disabled={!pick || busy} onClick={() => void attach()} data-testid="attach-line">Attach</Button>
        </div>
      )}
      <ConfirmDialog
        open={!!detaching}
        title={`Detach ${detaching?.name}?`}
        description="Supervisors assigned to this line lose access to it. Lines with workers on them can't be detached."
        confirmLabel="Detach"
        tone="danger"
        busy={busy}
        onConfirm={() => detaching && void detach(detaching)}
        onCancel={() => setDetaching(null)}
      />
    </Panel>
  );
}
