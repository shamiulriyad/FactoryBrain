"use client";

// Departments and production floors of one factory: the reference data
// worker records point at. Never deleted (workers keep pointing at them),
// only renamed or deactivated; a deactivated one takes no new workers.

import { useState } from "react";
import { Building, Layers3, Plus } from "lucide-react";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Field } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Modal } from "@/components/ui/Modal";
import { Callout } from "@/components/ui/Callout";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { useResource } from "@/lib/useResource";
import { apiJson, errorMessage } from "@/lib/api";
import type { OrgUnit } from "@/lib/workers";

type Kind = "departments" | "floors";
const LABEL: Record<Kind, { one: string; many: string; codeHint: string }> = {
  departments: { one: "department", many: "Departments", codeHint: "Short code, e.g. SEW, CUT, FIN." },
  floors: { one: "production floor", many: "Production floors", codeHint: "Short code, e.g. F1, F2, GF." }
};

export function OrgUnitsPanel({ factoryId, factoryName, kind, canManage, factoryActive }: {
  factoryId: string; factoryName: string; kind: Kind; canManage: boolean; factoryActive: boolean;
}) {
  const toast = useToast();
  const units = useResource<OrgUnit[]>(`/api/factories/${factoryId}/${kind}`);
  const [editing, setEditing] = useState<OrgUnit | "new" | null>(null);
  const label = LABEL[kind];

  async function toggle(u: OrgUnit) {
    try {
      await apiJson(`/api/factories/${factoryId}/${kind}/${u.id}`, { method: "PUT", body: { name: u.name, isActive: !u.isActive } });
      toast.push({ tone: "success", title: u.isActive ? `${u.name} deactivated` : `${u.name} reactivated` });
      await units.reload();
    } catch (e) {
      toast.push({ tone: "error", title: "Not changed", description: errorMessage(e) });
    }
  }

  return (
    <Panel flush title={`${label.many} · ${factoryName}`} icon={kind === "departments" ? <Building size={14} /> : <Layers3 size={14} />}
      subtitle={kind === "departments" ? "Every worker belongs to one department." : "Optional placement of workers and lines."}
      right={canManage && factoryActive && (
        <Button size="sm" variant="secondary" iconLeft={<Plus size={12} />} onClick={() => setEditing("new")} data-testid={`new-${kind}`}>Add</Button>
      )}
      testId={`org-${kind}`}>
      {units.loading ? (
        <div className="p-4"><SkeletonRows rows={2} rowHeight="h-9" /></div>
      ) : units.error ? (
        <ErrorState className="m-4" title={`Couldn't load ${label.many.toLowerCase()}`} body={errorMessage(units.error)} retryLabel="Try again" onRetry={() => void units.reload()} />
      ) : (units.data?.length ?? 0) === 0 ? (
        <EmptyState className="m-2" title={`No ${label.many.toLowerCase()} yet`} body={canManage ? `Add the first ${label.one}.` : undefined} />
      ) : (
        <table className="data-table">
          <thead><tr><th>Code</th><th>Name</th><th className="num">Workers</th><th>Status</th>{canManage && <th />}</tr></thead>
          <tbody>
            {units.data!.map((u) => (
              <tr key={u.id}>
                <td className="font-mono text-caption">{u.code}</td>
                <td className="text-fg-primary">{u.name}</td>
                <td className="num tabular-nums">{u.workerCount}</td>
                <td><Badge tone={u.isActive ? "success" : "neutral"}>{u.isActive ? "Active" : "Inactive"}</Badge></td>
                {canManage && (
                  <td className="text-right whitespace-nowrap">
                    <Button size="sm" variant="ghost" onClick={() => setEditing(u)}>Rename</Button>
                    <Button size="sm" variant="ghost" onClick={() => void toggle(u)}>{u.isActive ? "Deactivate" : "Reactivate"}</Button>
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {editing && (
        <OrgUnitForm factoryId={factoryId} kind={kind} unit={editing === "new" ? null : editing}
          onClose={() => setEditing(null)} onSaved={() => { setEditing(null); void units.reload(); }} />
      )}
    </Panel>
  );
}

function OrgUnitForm({ factoryId, kind, unit, onClose, onSaved }: { factoryId: string; kind: Kind; unit: OrgUnit | null; onClose: () => void; onSaved: () => void }) {
  const toast = useToast();
  const label = LABEL[kind];
  const [code, setCode] = useState(unit?.code ?? "");
  const [name, setName] = useState(unit?.name ?? "");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const codeOk = !!unit || /^[A-Za-z0-9][A-Za-z0-9\-_ ]{0,15}$/.test(code.trim());

  async function save() {
    setBusy(true);
    setError(null);
    try {
      if (unit) await apiJson(`/api/factories/${factoryId}/${kind}/${unit.id}`, { method: "PUT", body: { name: name.trim(), isActive: unit.isActive } });
      else await apiJson(`/api/factories/${factoryId}/${kind}`, { method: "POST", body: { code: code.trim(), name: name.trim() } });
      toast.push({ tone: "success", title: unit ? "Saved" : `${name.trim()} added` });
      onSaved();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal open onClose={onClose} locked={busy} title={unit ? `Rename ${unit.name}` : `New ${label.one}`} testId={`${kind}-form`}
      footer={
        <div className="flex justify-end gap-2">
          <Button variant="ghost" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button variant="primary" onClick={() => void save()} disabled={busy || !name.trim() || !codeOk} data-testid={`save-${kind}`}>{busy ? "Saving…" : "Save"}</Button>
        </div>
      }>
      <div className="space-y-3">
        <Field label="Code" hint={unit ? "The code can't be changed." : label.codeHint} error={code && !codeOk ? "Letters, digits, spaces, '-' and '_', up to 16 characters." : null}>
          {({ id, describedBy, invalid }) => (
            <Input id={id} value={code} maxLength={16} disabled={!!unit} className="font-mono" aria-invalid={invalid} aria-describedby={describedBy}
              onChange={(e) => setCode(e.target.value.toUpperCase())} data-autofocus={!unit || undefined} data-testid={`${kind}-code`} />
          )}
        </Field>
        <Field label="Name">
          {({ id }) => <Input id={id} value={name} maxLength={80} onChange={(e) => setName(e.target.value)} data-testid={`${kind}-name`} />}
        </Field>
        {error && <Callout tone="danger" role="alert">{error}</Callout>}
      </div>
    </Modal>
  );
}
