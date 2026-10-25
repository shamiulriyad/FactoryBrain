"use client";

// The configurable defect taxonomy (GET/POST/PUT /api/qc/categories).
// Everyone can read it; Admins add categories, rename them, change the
// default severity and deactivate them. Categories are never deleted because
// stored findings refer to them.

import { useState } from "react";
import { Plus } from "lucide-react";
import { Panel } from "@/components/ui/Panel";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Field, Select } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Modal } from "@/components/ui/Modal";
import { SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { useResource } from "@/lib/useResource";
import { errorMessage, isApiError } from "@/lib/api";
import { SEVERITY_TONE } from "@/lib/qcInspection";
import { createCategory, updateCategory } from "@/services/qc.api";
import { QC_SEVERITIES, type QcCategory, type QcSeverity } from "@/services/contracts";

const CODE = /^[a-z][a-z0-9_]{1,39}$/;

export function CategoryManager({ admin }: { admin: boolean }) {
  const toast = useToast();
  const res = useResource<QcCategory[]>(admin ? "/api/qc/categories?includeInactive=true" : "/api/qc/categories");
  const [editing, setEditing] = useState<QcCategory | "new" | null>(null);
  const [form, setForm] = useState({ code: "", name: "", description: "", severity: "major" as QcSeverity });
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  function open(c: QcCategory | "new") {
    setEditing(c);
    setError(null);
    setForm(c === "new" ? { code: "", name: "", description: "", severity: "major" } : { code: c.code, name: c.name, description: c.description ?? "", severity: c.defaultSeverity });
  }

  async function save() {
    if (busy || !editing) return;
    if (editing === "new" && !CODE.test(form.code)) return setError("Code: 2–40 lowercase letters, digits or underscores, starting with a letter.");
    if (!form.name.trim()) return setError("A name is required.");
    setBusy(true);
    setError(null);
    try {
      if (editing === "new") await createCategory({ code: form.code, name: form.name.trim(), description: form.description.trim() || undefined, defaultSeverity: form.severity });
      else await updateCategory(editing.code, { expectedVersion: editing.version, name: form.name.trim(), description: form.description.trim(), defaultSeverity: form.severity });
      setEditing(null);
      await res.reload();
      toast.push({ title: "Category saved", tone: "success" });
    } catch (e) {
      setError(isApiError(e) && e.kind === "conflict" ? `${e.message} Reload and try again.` : errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  async function toggle(c: QcCategory) {
    try {
      await updateCategory(c.code, { expectedVersion: c.version, isActive: !c.isActive });
      await res.reload();
    } catch (e) {
      toast.push({ title: "Could not update the category", description: errorMessage(e), tone: "error" });
    }
  }

  return (
    <Panel
      title="Defect taxonomy"
      subtitle="The categories inspectors and the vision model use. Changes apply to new inspections; stored findings keep their category."
      right={admin && <Button size="sm" variant="secondary" onClick={() => open("new")} data-testid="qc-category-add"><Plus size={12} /> Add category</Button>}
      flush
      testId="qc-taxonomy"
    >
      {!admin && <p className="px-4 pt-3 text-caption text-fg-tertiary">Only an Admin can change the taxonomy.</p>}
      {res.loading ? (
        <div className="p-4"><SkeletonRows rows={5} rowHeight="h-9" /></div>
      ) : res.error ? (
        <div className="p-4"><Callout tone="danger" title="Could not load the categories" action={<Button size="sm" onClick={() => void res.reload()}>Retry</Button>}>{errorMessage(res.error)}</Callout></div>
      ) : (
        <div className="overflow-x-auto">
          <table className="data-table">
            <thead><tr><th>Category</th><th>Code</th><th>Default severity</th><th>Status</th>{admin && <th><span className="sr-only">Actions</span></th>}</tr></thead>
            <tbody>
              {(res.data ?? []).map((c) => (
                <tr key={c.code} data-testid="qc-category-row" data-code={c.code}>
                  <td className="text-fg-primary">
                    {c.name}
                    {c.description && <span className="block text-caption text-fg-tertiary">{c.description}</span>}
                  </td>
                  <td className="font-mono text-[12px]">{c.code}</td>
                  <td><Badge tone={SEVERITY_TONE[c.defaultSeverity]}>{c.defaultSeverity}</Badge></td>
                  <td>{c.isActive ? <Badge tone="success">Active</Badge> : <Badge>Inactive</Badge>}</td>
                  {admin && (
                    <td className="num whitespace-nowrap">
                      <Button size="sm" variant="ghost" onClick={() => open(c)}>Edit</Button>
                      <Button size="sm" variant="ghost" onClick={() => void toggle(c)}>{c.isActive ? "Deactivate" : "Activate"}</Button>
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <Modal
        open={editing !== null}
        onClose={() => !busy && setEditing(null)}
        locked={busy}
        title={editing === "new" ? "Add a defect category" : "Edit category"}
        testId="qc-category-dialog"
        footer={
          <>
            <Button variant="ghost" onClick={() => setEditing(null)} disabled={busy}>Cancel</Button>
            <Button variant="primary" onClick={() => void save()} disabled={busy} data-testid="qc-category-save">{busy ? "Saving…" : "Save"}</Button>
          </>
        }
      >
        <div className="space-y-3">
          {editing === "new" && (
            <Field label="Code" hint="Stable id, e.g. button_missing. Can't be changed later.">
              {(p) => <Input id={p.id} aria-describedby={p.describedBy} value={form.code} onChange={(e) => setForm({ ...form, code: e.target.value.trim() })} maxLength={40} />}
            </Field>
          )}
          <Field label="Name">{(p) => <Input id={p.id} value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} maxLength={80} />}</Field>
          <Field label="Description (shown to inspectors and sent to the vision model)">
            {(p) => <Input id={p.id} value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} maxLength={500} />}
          </Field>
          <Field label="Default severity">
            {(p) => (
              <Select id={p.id} value={form.severity} onChange={(e) => setForm({ ...form, severity: e.target.value as QcSeverity })}>
                {QC_SEVERITIES.map((s) => <option key={s} value={s}>{s}</option>)}
              </Select>
            )}
          </Field>
          {error && <Callout tone="danger">{error}</Callout>}
        </div>
      </Modal>
    </Panel>
  );
}
