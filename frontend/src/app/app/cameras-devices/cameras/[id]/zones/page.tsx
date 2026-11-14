"use client";

// Camera zone configuration: polygons over the camera's reference frame in
// normalised coordinates (0–1), for the future computer-vision service.
// Click the frame to add points, drag them to adjust. A zone that marks a
// workstation or line must be inside the camera's coverage. Every change
// is a new revision; zones are deactivated, never deleted.

import Link from "next/link";
import { useParams } from "next/navigation";
import { useMemo, useState } from "react";
import { History, Image as ImageIcon, Plus, Shapes, Undo2 } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { Field, Select, TextArea } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { ConfirmDialog, Modal } from "@/components/ui/Modal";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { BASE, CameraDeviceTabs, ZoneCanvas, polygonArea, selfIntersects, useMediaUrl } from "@/components/cameras/shared";
import { useResource } from "@/lib/useResource";
import { apiJson, errorMessage } from "@/lib/api";
import { cn } from "@/lib/cn";
import { formatDateTime } from "@/lib/format";
import { P, type PagedResult } from "@/lib/access";
import type { Workstation } from "@/lib/assignments";
import { AI_NOT_CONNECTED_NOTE, ZONE_TYPES, ZONE_TYPE_COLOR, ZONE_TYPE_LABEL, type Camera, type CameraZone, type ZonePoint, type ZoneRevision } from "@/lib/cameras";

export default function CameraZonesPage() {
  return (
    <RequirePermission perms={[P.CamerasView]} what="Camera zones">
      <Zones />
    </RequirePermission>
  );
}

type Draft = { id: string | null; version: number | null; name: string; zoneType: string; workstationId: string; lineId: string; color: string; notes: string; points: ZonePoint[]; isActive: boolean };

const empty = (): Draft => ({ id: null, version: null, name: "", zoneType: "Workstation", workstationId: "", lineId: "", color: "", notes: "", points: [], isActive: true });

function Zones() {
  const { id } = useParams<{ id: string }>();
  const toast = useToast();
  const cam = useResource<Camera>(`/api/cameras/${id}`);
  const [showInactive, setShowInactive] = useState(false);
  const zones = useResource<CameraZone[]>(`/api/cameras/${id}/zones?includeInactive=${showInactive}`);
  const c = cam.data;
  const frame = useMediaUrl(c?.referenceFrame ? `/api/cameras/${id}/reference-frame` : null, c?.referenceFrame?.updatedAt);
  const ws = useResource<PagedResult<Workstation>>(c ? `/api/workstations?factoryId=${c.factoryId}&pageSize=200` : null);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [toggle, setToggle] = useState<CameraZone | null>(null);
  const [history, setHistory] = useState<CameraZone | null>(null);

  const covered = useMemo(() => {
    if (!c) return { lines: [] as { id: string; name: string }[], workstations: [] as Workstation[] };
    const lineIds = new Set(c.coverage.filter((v) => v.kind === "Line").map((v) => v.line.id));
    if (c.line) lineIds.add(c.line.id);
    const wsIds = new Set(c.coverage.filter((v) => v.workstation).map((v) => v.workstation!.id));
    const workstations = (ws.data?.items ?? []).filter((w) => w.isActive && (wsIds.has(w.id) || lineIds.has(w.line.id)));
    const lines = new Map<string, string>();
    for (const v of c.coverage) lines.set(v.line.id, v.line.name);
    if (c.line) lines.set(c.line.id, c.line.name);
    return { lines: [...lines.entries()].map(([lid, name]) => ({ id: lid, name })), workstations };
  }, [c, ws.data]);

  if (cam.loading) return <div className="page"><SkeletonRows rows={8} /></div>;
  if (cam.error || !c) return <div className="page"><ErrorState title="Couldn't load the camera" body={errorMessage(cam.error)} retryLabel="Try again" onRetry={() => void cam.reload()} /></div>;
  const manage = c.can.manage;
  const list = zones.data ?? [];
  const pts = draft?.points ?? [];
  const crossing = pts.length >= 3 && selfIntersects(pts);
  const tooSmall = pts.length >= 3 && !crossing && polygonArea(pts) < 0.0001;
  const canSave = !!draft && draft.name.trim() && pts.length >= 3 && !crossing && !tooSmall && (draft.zoneType !== "Workstation" || draft.workstationId)
    && (draft.zoneType !== "ProductionLine" || draft.lineId || draft.workstationId);

  function edit(z: CameraZone) {
    setError(null);
    setDraft({ id: z.id, version: z.version, name: z.name, zoneType: z.zoneType, workstationId: z.workstation?.id ?? "", lineId: z.workstation ? "" : z.line?.id ?? "",
      color: z.color ?? "", notes: z.notes ?? "", points: z.points, isActive: z.isActive });
  }

  async function save() {
    if (!draft) return;
    setBusy(true);
    setError(null);
    const body = { name: draft.name.trim(), zoneType: draft.zoneType, points: draft.points, workstationId: draft.workstationId || null,
      lineId: draft.workstationId ? null : draft.lineId || null, color: draft.color || null, notes: draft.notes || null };
    try {
      const savedZone = draft.id
        ? await apiJson<CameraZone>(`/api/camera-zones/${draft.id}`, { method: "PUT", body: { ...body, isActive: draft.isActive, expectedVersion: draft.version } })
        : await apiJson<CameraZone>(`/api/cameras/${c!.id}/zones`, { method: "POST", body });
      toast.push({ tone: "success", title: draft.id ? `Zone saved (version ${savedZone.version})` : "Zone created" });
      setDraft(null);
      void zones.reload();
      void cam.reload();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  async function setActive(z: CameraZone, active: boolean) {
    try {
      await apiJson<CameraZone>(`/api/camera-zones/${z.id}`, {
        method: "PUT",
        body: { name: z.name, zoneType: z.zoneType, points: z.points, workstationId: z.workstation?.id ?? null, lineId: z.workstation ? null : z.line?.id ?? null,
          color: z.color ?? null, notes: z.notes ?? null, isActive: active, expectedVersion: z.version }
      });
      toast.push({ tone: "success", title: active ? "Zone reactivated" : "Zone deactivated" });
      if (draft?.id === z.id) setDraft(null);
      void zones.reload();
      void cam.reload();
    } catch (e) {
      toast.push({ tone: "error", title: "Not changed", description: errorMessage(e) });
    } finally {
      setToggle(null);
    }
  }

  const shapes = list.filter((z) => z.id !== draft?.id).map((z) => ({ id: z.id, name: z.name, zoneType: z.zoneType, points: z.points, color: z.color, isActive: z.isActive }));
  const draftColor = draft ? draft.color || ZONE_TYPE_COLOR[draft.zoneType] : undefined;

  return (
    <div className="page" data-testid="camera-zones-page">
      <PageHeader
        eyebrow={<Link href={`${BASE}/cameras/${c.id}`} className="hover:underline">{c.code} · {c.name}</Link>}
        title="Zone configuration"
        description="Polygons over the reference frame, stored as fractions of its width and height (0–1) so they survive a change of resolution."
        actions={manage && c.referenceFrame ? <Button size="sm" variant="primary" iconLeft={<Plus size={13} />} onClick={() => { setError(null); setDraft(empty()); }} data-testid="zone-new">New zone</Button> : undefined}
      />
      <CameraDeviceTabs className="mt-4" />
      <Callout className="mt-4" tone="info" title="Configuration only">{AI_NOT_CONNECTED_NOTE}</Callout>

      {!c.referenceFrame ? (
        <EmptyState className="mt-6" icon={<ImageIcon size={18} />} title="No reference frame yet"
          body={manage ? "Add a reference frame on the camera's page (upload an image or grab a frame from its recorded video), then come back to draw zones." : "Zones are drawn over the camera's reference frame, which hasn't been added yet."} />
      ) : (
        <div className="mt-6 grid gap-6 xl:grid-cols-[minmax(0,1fr)_380px]">
          <div className="space-y-2">
            {frame.url ? (
              <ZoneCanvas imageUrl={frame.url} width={c.referenceFrame.width} height={c.referenceFrame.height} zones={shapes}
                selectedId={draft?.id} onSelect={(zid) => { const z = list.find((x) => x.id === zid); if (z && manage) edit(z); }}
                draft={draft ? draft.points : null} draftColor={draftColor} onDraftChange={manage && draft ? (p) => setDraft({ ...draft, points: p }) : undefined} />
            ) : frame.error ? <ErrorState title="Couldn't load the frame" body={frame.error} retryLabel="Try again" onRetry={() => void cam.reload()} /> : <SkeletonRows rows={1} rowHeight="h-80" />}
            {draft && (
              <div className="flex flex-wrap items-center gap-2 text-caption">
                <span className={cn("text-fg-secondary", (crossing || tooSmall) && "text-[var(--risk-high)]")} data-testid="zone-draft-status">
                  {pts.length} point{pts.length === 1 ? "" : "s"}
                  {pts.length < 3 ? " · click the frame to add at least 3" : crossing ? " · edges cross: move the points" : tooSmall ? " · too small" : ` · ${(polygonArea(pts) * 100).toFixed(2)} % of the frame`}
                </span>
                <Button size="sm" variant="ghost" iconLeft={<Undo2 size={12} />} disabled={pts.length === 0} onClick={() => setDraft({ ...draft, points: pts.slice(0, -1) })}>Undo point</Button>
                <Button size="sm" variant="ghost" disabled={pts.length === 0} onClick={() => setDraft({ ...draft, points: [] })}>Clear</Button>
                <span className="text-fg-tertiary">Drag points to move them; focus one and use arrow keys (Shift = larger steps) or Delete.</span>
              </div>
            )}
          </div>

          <div className="space-y-6">
            {draft && manage && (
              <Panel title={draft.id ? `Edit zone (v${draft.version})` : "New zone"} icon={<Shapes size={14} />} testId="zone-editor">
                <div className="space-y-3">
                  {error && <Callout tone="danger" title="Not saved">{error}</Callout>}
                  <Field label="Name">
                    {(ids) => <Input id={ids.id} value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} placeholder="T-025 workstation" maxLength={80} data-testid="zone-name" />}
                  </Field>
                  <Field label="Zone type">
                    {(ids) => (
                      <Select id={ids.id} value={draft.zoneType} onChange={(e) => setDraft({ ...draft, zoneType: e.target.value })} data-testid="zone-type">
                        {ZONE_TYPES.map((t) => <option key={t} value={t}>{ZONE_TYPE_LABEL[t]}</option>)}
                      </Select>
                    )}
                  </Field>
                  <Field label="Workstation" hint={draft.zoneType === "Workstation" ? "Required. Only workstations this camera covers." : "Optional"}>
                    {(ids) => (
                      <Select id={ids.id} value={draft.workstationId} onChange={(e) => {
                        const w = covered.workstations.find((x) => x.id === e.target.value);
                        setDraft({ ...draft, workstationId: e.target.value, name: draft.name || (w ? `${w.code} workstation` : "") });
                      }} data-testid="zone-workstation">
                        <option value="">None</option>
                        {covered.workstations.map((w) => <option key={w.id} value={w.id}>{w.code} · {w.line.name}</option>)}
                      </Select>
                    )}
                  </Field>
                  {!draft.workstationId && (
                    <Field label="Production line" hint={draft.zoneType === "ProductionLine" ? "Required" : "Optional"}>
                      {(ids) => (
                        <Select id={ids.id} value={draft.lineId} onChange={(e) => setDraft({ ...draft, lineId: e.target.value })}>
                          <option value="">None</option>
                          {covered.lines.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
                        </Select>
                      )}
                    </Field>
                  )}
                  <Field label="Colour" hint="Optional; defaults to the zone type's colour">
                    {(ids) => <input id={ids.id} type="color" value={draft.color || ZONE_TYPE_COLOR[draft.zoneType]} onChange={(e) => setDraft({ ...draft, color: e.target.value.toUpperCase() })} className="h-8 w-16 rounded border border-border-subtle" />}
                  </Field>
                  <Field label="Notes" hint="Optional">
                    {(ids) => <TextArea id={ids.id} rows={2} value={draft.notes} onChange={(e) => setDraft({ ...draft, notes: e.target.value })} maxLength={500} />}
                  </Field>
                  <div className="flex justify-end gap-2">
                    <Button variant="ghost" onClick={() => setDraft(null)} disabled={busy}>Cancel</Button>
                    <Button variant="primary" onClick={() => void save()} disabled={busy || !canSave} data-testid="zone-save">{busy ? "Saving…" : "Save zone"}</Button>
                  </div>
                </div>
              </Panel>
            )}

            <Panel flush title="Zones" icon={<Shapes size={14} />} subtitle={`${list.filter((z) => z.isActive).length} active`}
              right={<label className="flex items-center gap-1.5 text-caption"><input type="checkbox" checked={showInactive} onChange={(e) => setShowInactive(e.target.checked)} />Show deactivated</label>}>
              {zones.loading ? <div className="p-4"><SkeletonRows rows={3} rowHeight="h-10" /></div>
                : zones.error ? <ErrorState className="m-4" title="Couldn't load zones" body={errorMessage(zones.error)} retryLabel="Try again" onRetry={() => void zones.reload()} />
                : list.length === 0 ? <EmptyState className="m-4" icon={<Shapes size={18} />} title="No zones yet" body={manage ? "Choose New zone and click around the area on the frame." : "No zones have been drawn on this camera."} />
                : (
                  <ul className="divide-y divide-border-subtle" data-testid="zone-list">
                    {list.map((z) => (
                      <li key={z.id} className={cn("px-4 py-2.5 text-caption", draft?.id === z.id && "bg-[var(--accent-soft)]")}>
                        <div className="flex items-center gap-2">
                          <span className="h-3 w-3 rounded-sm shrink-0" style={{ background: z.color ?? ZONE_TYPE_COLOR[z.zoneType] }} aria-hidden />
                          <span className="text-body text-fg-primary">{z.name}</span>
                          {!z.isActive && <Badge tone="neutral">Deactivated</Badge>}
                          <span className="ml-auto text-fg-tertiary">v{z.version}</span>
                        </div>
                        <div className="mt-0.5 text-fg-tertiary">
                          {ZONE_TYPE_LABEL[z.zoneType] ?? z.zoneType}{z.workstation ? ` · ${z.workstation.code}` : ""}{z.line && !z.workstation ? ` · ${z.line.name}` : ""} · {z.points.length} points · {(z.area * 100).toFixed(2)} %
                        </div>
                        <div className="mt-1.5 flex flex-wrap gap-1">
                          {manage && z.isActive && <Button size="sm" variant="ghost" onClick={() => edit(z)} data-testid={`zone-edit-${z.name}`}>Edit</Button>}
                          {manage && <Button size="sm" variant="ghost" onClick={() => setToggle(z)}>{z.isActive ? "Deactivate" : "Reactivate"}</Button>}
                          <Button size="sm" variant="ghost" iconLeft={<History size={12} />} onClick={() => setHistory(z)}>History</Button>
                        </div>
                      </li>
                    ))}
                  </ul>
                )}
            </Panel>
          </div>
        </div>
      )}

      <ConfirmDialog open={!!toggle} title={toggle?.isActive ? `Deactivate zone “${toggle?.name}”?` : `Reactivate zone “${toggle?.name}”?`}
        tone={toggle?.isActive ? "danger" : "primary"} confirmLabel={toggle?.isActive ? "Deactivate" : "Reactivate"}
        description={toggle?.isActive ? "The zone stays in the history and can be reactivated later." : "The zone must still be inside the camera's coverage."}
        onCancel={() => setToggle(null)} onConfirm={() => toggle && void setActive(toggle, !toggle.isActive)} testId="zone-toggle-dialog" />
      {history && <HistoryDialog zone={history} onClose={() => setHistory(null)} />}
    </div>
  );
}

function HistoryDialog({ zone, onClose }: { zone: CameraZone; onClose: () => void }) {
  const revs = useResource<ZoneRevision[]>(`/api/camera-zones/${zone.id}/history`);
  return (
    <Modal open onClose={onClose} title={`History of “${zone.name}”`} description="Every version is kept. Coordinates are fractions of the frame the zone was drawn on." testId="zone-history"
      footer={<Button variant="secondary" onClick={onClose}>Close</Button>}>
      {revs.loading ? <SkeletonRows rows={3} /> : revs.error ? <p className="text-caption text-[var(--risk-high)]">{errorMessage(revs.error)}</p> : (
        <ol className="space-y-3 text-caption">
          {(revs.data ?? []).map((r) => (
            <li key={r.id} className="border-l-2 border-border-subtle pl-3">
              <div className="flex flex-wrap items-center gap-2">
                <span className="font-mono">v{r.version}</span>
                <Badge tone={r.action === "Deactivated" ? "neutral" : r.action === "Created" ? "success" : "info"}>{r.action}</Badge>
                {!r.drawnOnCurrentFrame && <Badge tone="warning" title="The reference frame has changed since">Older frame</Badge>}
                <span className="ml-auto text-fg-tertiary">{formatDateTime(r.changedAt)}</span>
              </div>
              <div className="mt-0.5 text-fg-secondary">{r.name} · {ZONE_TYPE_LABEL[r.zoneType] ?? r.zoneType} · {r.changedByEmail}</div>
              <div className="mt-0.5 font-mono text-fg-tertiary break-all">{r.points.map((p) => `(${p.x.toFixed(3)}, ${p.y.toFixed(3)})`).join(" ")}</div>
            </li>
          ))}
        </ol>
      )}
    </Modal>
  );
}
