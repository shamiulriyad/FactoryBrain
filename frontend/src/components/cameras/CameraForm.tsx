"use client";

// Register / edit a camera, and the coverage picker (lines and workstations
// the camera observes). Only workstations on the camera's floor are offered;
// the API checks every relationship again.

import { useEffect, useMemo, useState } from "react";
import { useRouter } from "next/navigation";
import { Search } from "lucide-react";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Field, Select, TextArea } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Panel } from "@/components/ui/Panel";
import { SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { useMyFactories, useFactoryOrg } from "@/components/workers/shared";
import { useResource } from "@/lib/useResource";
import { apiJson, errorMessage, isApiError } from "@/lib/api";
import { cn } from "@/lib/cn";
import type { PagedResult } from "@/lib/access";
import type { Workstation } from "@/lib/assignments";
import { CAMERA_TYPES, CAMERA_TYPE_LABEL, type Camera } from "@/lib/cameras";
import { BASE } from "@/components/cameras/shared";

/** Lines and workstations a camera covers. Workstations are limited to one floor. */
export function CoveragePicker({ factoryId, floorId, lineIds, workstationIds, onChange }: {
  factoryId: string;
  floorId: string;
  lineIds: string[];
  workstationIds: string[];
  onChange: (next: { lineIds: string[]; workstationIds: string[] }) => void;
}) {
  const { lines } = useFactoryOrg(factoryId || null);
  const [search, setSearch] = useState("");
  const ws = useResource<PagedResult<Workstation>>(factoryId ? `/api/workstations?factoryId=${factoryId}&pageSize=200&includeInactive=false` : null);
  const options = useMemo(() => {
    const term = search.trim().toLowerCase();
    return (ws.data?.items ?? [])
      .filter((w) => !floorId || !w.floor || w.floor.id === floorId)
      .filter((w) => !term || w.code.toLowerCase().includes(term) || w.line.name.toLowerCase().includes(term));
  }, [ws.data, floorId, search]);
  const byLine = useMemo(() => {
    const m = new Map<string, Workstation[]>();
    for (const w of options) m.set(w.line.name, [...(m.get(w.line.name) ?? []), w]);
    return [...m.entries()];
  }, [options]);
  const toggle = <T,>(list: T[], v: T) => (list.includes(v) ? list.filter((x) => x !== v) : [...list, v]);

  return (
    <div className="space-y-4" data-testid="coverage-picker">
      <div>
        <p className="field-label">Whole lines</p>
        {(lines.data?.length ?? 0) === 0 ? <p className="text-caption text-fg-tertiary">This factory has no lines yet.</p> : (
          <div className="flex flex-wrap gap-2">
            {lines.data!.filter((l) => l.isActive || lineIds.includes(l.id)).map((l) => (
              <label key={l.id} className={cn("inline-flex items-center gap-2 h-8 px-2.5 rounded-[var(--radius-control)] border text-caption cursor-pointer",
                lineIds.includes(l.id) ? "border-[var(--accent)] bg-[var(--accent-soft)]" : "border-border-subtle")}>
                <input type="checkbox" checked={lineIds.includes(l.id)} onChange={() => onChange({ lineIds: toggle(lineIds, l.id), workstationIds })} />
                {l.name}
              </label>
            ))}
          </div>
        )}
      </div>
      <div>
        <div className="flex flex-wrap items-center gap-2">
          <p className="field-label !mb-0">Workstations{floorId ? " on this floor" : ""}</p>
          <span className="text-caption text-fg-tertiary">{workstationIds.length} selected</span>
          <div className="relative ml-auto w-48">
            <Search size={13} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-fg-tertiary" aria-hidden />
            <Input aria-label="Search workstations" placeholder="e.g. T-025" value={search} onChange={(e) => setSearch(e.target.value)} className="!h-8 !pl-8 !text-caption" />
          </div>
        </div>
        {ws.loading ? <SkeletonRows rows={2} rowHeight="h-8" /> : ws.error ? <p className="text-caption text-[var(--risk-high)]">{errorMessage(ws.error)}</p>
          : byLine.length === 0 ? <p className="mt-2 text-caption text-fg-tertiary">No active workstations{floorId ? " on this floor" : ""}. Administrators add them under Assignments → Workstations.</p>
          : (
            <div className="mt-2 max-h-64 overflow-y-auto rounded-[var(--radius-control)] border border-border-subtle divide-y divide-border-subtle">
              {byLine.map(([line, items]) => (
                <div key={line} className="p-2.5">
                  <p className="text-caption text-fg-tertiary mb-1.5">{line}</p>
                  <div className="flex flex-wrap gap-1.5">
                    {items.map((w) => (
                      <label key={w.id} className={cn("inline-flex items-center gap-1.5 h-7 px-2 rounded-sm border font-mono text-caption cursor-pointer",
                        workstationIds.includes(w.id) ? "border-[var(--accent)] bg-[var(--accent-soft)]" : "border-border-subtle")} data-testid={`cover-ws-${w.code}`}>
                        <input type="checkbox" checked={workstationIds.includes(w.id)} onChange={() => onChange({ lineIds, workstationIds: toggle(workstationIds, w.id) })} />
                        {w.code}
                      </label>
                    ))}
                  </div>
                </div>
              ))}
            </div>
          )}
        <p className="mt-1.5 field-hint">A camera usually covers several workstations. Zones for a workstation can only be drawn on cameras that cover it (or its line).</p>
      </div>
    </div>
  );
}

type FormState = {
  factoryId: string; code: string; name: string; cameraType: string; floorId: string; departmentId: string; lineId: string;
  location: string; description: string; installedOn: string; healthDevice: string; lineIds: string[]; workstationIds: string[];
};

export function CameraForm({ camera }: { camera?: Camera }) {
  const router = useRouter();
  const toast = useToast();
  const { factories } = useMyFactories();
  const editing = !!camera;
  const [f, setF] = useState<FormState>(() => ({
    factoryId: camera?.factoryId ?? "", code: camera?.code ?? "", name: camera?.name ?? "", cameraType: camera?.cameraType ?? "IpCamera",
    floorId: camera?.floor.id ?? "", departmentId: camera?.department?.id ?? "", lineId: camera?.line?.id ?? "", location: camera?.location ?? "",
    description: camera?.description ?? "", installedOn: camera?.installedOn ?? "", healthDevice: "Simulated", lineIds: [], workstationIds: []
  }));
  useEffect(() => {
    if (!editing && !f.factoryId && factories?.length === 1) setF((s) => ({ ...s, factoryId: factories[0].id }));
  }, [factories, editing, f.factoryId]);
  const { departments, floors, lines } = useFactoryOrg(f.factoryId || null);
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [problem, setProblem] = useState<string | null>(null);
  const set = (k: keyof FormState, v: string) => setF((s) => ({ ...s, [k]: v }));

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    const local: Record<string, string> = {};
    if (!f.factoryId) local.factoryId = "Choose the factory.";
    if (!f.name.trim()) local.name = "Enter a name.";
    if (!f.floorId) local.floorId = "Choose the production floor the camera is on.";
    setErrors(local);
    if (Object.keys(local).length > 0) return;
    setBusy(true);
    setProblem(null);
    try {
      const common = {
        name: f.name.trim(), cameraType: f.cameraType, floorId: f.floorId, departmentId: f.departmentId || null, lineId: f.lineId || null,
        location: f.location || null, description: f.description || null, installedOn: f.installedOn || null
      };
      const saved = editing
        ? await apiJson<Camera>(`/api/cameras/${camera!.id}`, { method: "PUT", body: common })
        : await apiJson<Camera>("/api/cameras", {
            method: "POST",
            body: { ...common, factoryId: f.factoryId, code: f.code.trim() || null, healthDevice: f.healthDevice, coverageLineIds: f.lineIds, workstationIds: f.workstationIds }
          });
      toast.push({ tone: "success", title: editing ? "Camera saved" : `Camera ${saved.code} registered` });
      router.push(`${BASE}/cameras/${saved.id}`);
    } catch (err) {
      if (isApiError(err) && err.fieldErrors) setErrors(Object.fromEntries(Object.entries(err.fieldErrors).map(([k, v]) => [k, v[0]])));
      setProblem(errorMessage(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={submit} className="mt-6 space-y-6" noValidate data-testid="camera-form">
      {problem && <Callout tone="danger" title="Not saved">{problem}</Callout>}
      <Panel title="Camera">
        <div className="grid gap-4 md:grid-cols-2">
          {!editing && (
            <Field label="Factory" error={errors.factoryId}>
              {(ids) => (
                <Select id={ids.id} aria-describedby={ids.describedBy} value={f.factoryId} onChange={(e) => setF((s) => ({ ...s, factoryId: e.target.value, floorId: "", departmentId: "", lineId: "", lineIds: [], workstationIds: [] }))} data-testid="camera-factory">
                  <option value="">Choose…</option>
                  {(factories ?? []).map((x) => <option key={x.id} value={x.id}>{x.code} · {x.name}</option>)}
                </Select>
              )}
            </Field>
          )}
          <Field label="Camera ID" hint={editing ? "The camera ID can't change." : "Leave empty for the next CAM-### number."} error={errors.code}>
            {(ids) => <Input id={ids.id} aria-describedby={ids.describedBy} value={f.code} disabled={editing} onChange={(e) => set("code", e.target.value.toUpperCase())} placeholder="CAM-001" className="font-mono" data-testid="camera-code" />}
          </Field>
          <Field label="Name" error={errors.name}>
            {(ids) => <Input id={ids.id} aria-describedby={ids.describedBy} value={f.name} onChange={(e) => set("name", e.target.value)} placeholder="Line 3 overhead" maxLength={120} data-testid="camera-name" />}
          </Field>
          <Field label="Camera type" error={errors.cameraType}>
            {(ids) => (
              <Select id={ids.id} value={f.cameraType} onChange={(e) => set("cameraType", e.target.value)} data-testid="camera-type">
                {CAMERA_TYPES.map((t) => <option key={t} value={t}>{CAMERA_TYPE_LABEL[t]}</option>)}
              </Select>
            )}
          </Field>
          <Field label="Installed on" hint="Optional">
            {(ids) => <Input id={ids.id} type="date" value={f.installedOn} onChange={(e) => set("installedOn", e.target.value)} />}
          </Field>
        </div>
      </Panel>

      <Panel title="Placement" subtitle="Where the camera is installed. Floor, department and line must belong to the factory.">
        <div className="grid gap-4 md:grid-cols-3">
          <Field label="Production floor" error={errors.floorId}>
            {(ids) => (
              <Select id={ids.id} aria-describedby={ids.describedBy} value={f.floorId} onChange={(e) => setF((s) => ({ ...s, floorId: e.target.value, workstationIds: [] }))} disabled={!f.factoryId} data-testid="camera-floor">
                <option value="">Choose…</option>
                {(floors.data ?? []).filter((x) => x.isActive || x.id === f.floorId).map((x) => <option key={x.id} value={x.id}>{x.code} · {x.name}</option>)}
              </Select>
            )}
          </Field>
          <Field label="Department" hint="Optional" error={errors.departmentId}>
            {(ids) => (
              <Select id={ids.id} value={f.departmentId} onChange={(e) => set("departmentId", e.target.value)} disabled={!f.factoryId}>
                <option value="">None</option>
                {(departments.data ?? []).filter((x) => x.isActive || x.id === f.departmentId).map((x) => <option key={x.id} value={x.id}>{x.name}</option>)}
              </Select>
            )}
          </Field>
          <Field label="Main production line" hint="Optional: the line it mainly watches" error={errors.lineId}>
            {(ids) => (
              <Select id={ids.id} value={f.lineId} onChange={(e) => set("lineId", e.target.value)} disabled={!f.factoryId} data-testid="camera-line">
                <option value="">None</option>
                {(lines.data ?? []).filter((x) => x.isActive || x.id === f.lineId).map((x) => <option key={x.id} value={x.id}>{x.name}</option>)}
              </Select>
            )}
          </Field>
          <Field label="Location / description of view" className="md:col-span-3" error={errors.location}>
            {(ids) => <Input id={ids.id} value={f.location} onChange={(e) => set("location", e.target.value)} placeholder="Ceiling above T-025, facing the line towards the exit" maxLength={200} />}
          </Field>
          <Field label="Notes" className="md:col-span-3" hint="Optional" error={errors.description}>
            {(ids) => <TextArea id={ids.id} rows={2} value={f.description} onChange={(e) => set("description", e.target.value)} maxLength={1000} />}
          </Field>
        </div>
      </Panel>

      {!editing && (
        <>
          <Panel title="Coverage" subtitle="What the camera observes. You can change this later on the camera's page.">
            {f.factoryId && f.floorId
              ? <CoveragePicker factoryId={f.factoryId} floorId={f.floorId} lineIds={f.lineIds} workstationIds={f.workstationIds} onChange={(n) => setF((s) => ({ ...s, ...n }))} />
              : <p className="text-caption text-fg-tertiary">Choose the factory and floor first.</p>}
          </Panel>
          <Panel title="Health tracking" subtitle="A camera's health comes from a linked device that sends heartbeats.">
            <div className="grid gap-2 sm:grid-cols-3" role="radiogroup" aria-label="Health device">
              {[
                ["Simulated", "Simulated device", "For demonstrations: the device simulator sends its heartbeats. Always shown as SIMULATED."],
                ["Real", "Real device", "Physical hardware or its gateway will send heartbeats. Shows 'Never connected' until it does."],
                ["None", "No health tracking", "Register the camera only. A device can be linked later."]
              ].map(([v, label, hint]) => (
                <label key={v} className={cn("p-3 rounded-[var(--radius-control)] border cursor-pointer text-caption", f.healthDevice === v ? "border-[var(--accent)] bg-[var(--accent-soft)]" : "border-border-subtle")}>
                  <span className="flex items-center gap-2 text-body text-fg-primary"><input type="radio" name="health" checked={f.healthDevice === v} onChange={() => set("healthDevice", v)} />{label}</span>
                  <span className="mt-1 block text-fg-tertiary">{hint}</span>
                </label>
              ))}
            </div>
          </Panel>
        </>
      )}

      <div className="flex justify-end gap-2">
        <Button type="button" variant="ghost" onClick={() => router.back()} disabled={busy}>Cancel</Button>
        <Button type="submit" variant="primary" disabled={busy} data-testid="camera-save">{busy ? "Saving…" : editing ? "Save changes" : "Register camera"}</Button>
      </div>
    </form>
  );
}
