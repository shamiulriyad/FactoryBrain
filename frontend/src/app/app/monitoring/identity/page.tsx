"use client";

// Worker identity: which camera tracks are verified as which worker, and why
// the rest are not. Tabs: overview (current tracks and recent changes), the
// review queue, RFID readers and their location mappings, recent reads
// (cards masked), and the development RFID simulator. Live over SignalR.

import { useCallback, useEffect, useRef, useState } from "react";
import { Fingerprint, History, Radio, ScanLine, ShieldAlert, Wand2 } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { Field, FilterSelect, Select } from "@/components/ui/Field";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { useFactoryOrg, useMyFactories } from "@/components/workers/shared";
import { useQueryState } from "@/components/attendance/shared";
import { LiveIndicator, MonitoringTabs } from "@/components/monitoring/shared";
import { IdentityBadge, IdentityCaseDrawer, ReaderMappingDialog, TrackIdentityCard } from "@/components/monitoring/identity";
import { useResource } from "@/lib/useResource";
import { useMonitoringHub } from "@/lib/useMonitoringHub";
import { useCan } from "@/lib/auth";
import { P, type PagedResult } from "@/lib/access";
import { apiJson, errorMessage } from "@/lib/api";
import { MON, ago, dhaka, dhakaTime, qs, type MonitoringCamera } from "@/lib/monitoring";
import {
  IDENTITY_DISCLAIMER, IDENTITY_LABEL, SIM_SCENARIOS,
  type IdentityOverview, type RfidReader, type RfidRead, type RfidSimulationResult
} from "@/lib/identity";

const BASE = `${MON}/identity`;
const TABS = [
  { id: "overview", label: "Overview" },
  { id: "review", label: "Review queue" },
  { id: "readers", label: "RFID readers" },
  { id: "reads", label: "Reads" },
  { id: "simulator", label: "Simulator" }
] as const;

export default function IdentityPage() {
  return (
    <RequirePermission perms={[P.MonitoringView]} what="Worker identity">
      <IdentityRoom />
    </RequirePermission>
  );
}

function IdentityRoom() {
  const q = useQueryState(BASE);
  const { factories } = useMyFactories();
  const factory = q.get("factory");
  const tab = q.get("tab") || "overview";
  const canSimulate = useCan(P.DevicesSimulate);
  const overview = useResource<IdentityOverview>(`/api/identity/overview${qs({ factoryId: factory })}`, { pollMs: 30_000 });
  const o = overview.data;
  const [open, setOpen] = useState<string | null>(null);

  const pending = useRef<ReturnType<typeof setTimeout> | null>(null);
  const reloadSoon = useCallback(() => {
    if (pending.current) return;
    pending.current = setTimeout(() => { pending.current = null; void overview.reload(); }, 1500);
  }, [overview]);
  useEffect(() => () => { if (pending.current) clearTimeout(pending.current); }, []);
  const hub = useMonitoringHub(factory || null, {
    onIdentity: () => reloadSoon(),
    onEvent: (e) => { if (e.eventType === "ZONE_ENTERED" || e.eventType === "PERSON_LOST" || e.eventType === "PERSON_DETECTED") reloadSoon(); },
    onResync: () => void overview.reload()
  });

  return (
    <div className="page" data-testid="identity-page">
      <PageHeader eyebrow="Central monitoring" title="Worker identity"
        description="Which camera tracks are verified as which worker, from RFID card reads, reader locations, workstation assignments and camera zones."
        meta={<LiveIndicator state={hub.state} lastMessageAt={hub.lastMessageAt} />}
        actions={factories && factories.length > 1 ? (
          <FilterSelect label="Factory" value={factory} onChange={(v) => q.set({ factory: v || null })} testId="identity-factory">
            <option value="">All my factories</option>
            {factories.map((f) => <option key={f.id} value={f.id}>{f.code} · {f.name}</option>)}
          </FilterSelect>
        ) : undefined} />
      <MonitoringTabs className="mb-4" />
      <Callout tone="info" className="mb-4" testId="identity-disclaimer">{IDENTITY_DISCLAIMER}</Callout>

      {o && (
        <section aria-label="Identity status" className="grid gap-3 grid-cols-2 lg:grid-cols-5 mb-4" data-testid="identity-counts">
          <Count label="Verified now" value={o.counts.verifiedNow} hint="Valid VERIFIED associations" />
          <Count label="Unverified tracks" value={o.counts.unverifiedTracks} hint="Visible tracks without a confirmed worker" />
          <Count label="Ambiguous tracks" value={o.counts.ambiguousTracks} hint="Several candidates or contradictions" bad={o.counts.ambiguousTracks > 0} />
          <Count label="Waiting for review" value={o.counts.pendingReview} hint={`${o.counts.pendingReads} read(s) still in their time window`} bad={o.counts.pendingReview > 0} />
          <Count label="RFID readers online" value={`${o.counts.readersOnline} / ${o.counts.readers}`} hint="Heartbeat-based device health" />
        </section>
      )}

      <div role="tablist" aria-label="Identity sections" className="mb-4 flex flex-wrap gap-1 border-b border-border-subtle">
        {TABS.filter((t) => t.id !== "simulator" || canSimulate).map((t) => (
          <button key={t.id} type="button" role="tab" aria-selected={tab === t.id} onClick={() => q.set({ tab: t.id === "overview" ? null : t.id })}
            className={`px-3 h-9 text-caption border-b-2 -mb-px ${tab === t.id ? "border-[var(--accent)] text-fg-primary font-medium" : "border-transparent text-fg-secondary hover:text-fg-primary"}`}
            data-testid={`identity-tab-${t.id}`}>
            {t.label}{t.id === "review" && o?.counts.pendingReview ? ` (${o.counts.pendingReview})` : ""}
          </button>
        ))}
      </div>

      {overview.error && !o ? <ErrorState retryLabel="Try again" title="Could not load identity" body={overview.error.message} onRetry={() => void overview.reload()} />
        : !o ? <SkeletonRows rows={6} />
        : tab === "review" ? <ReviewQueue o={o} onOpen={setOpen} factory={factory} />
        : tab === "readers" ? <Readers readers={o.readers} onChanged={() => void overview.reload()} />
        : tab === "reads" ? <Reads factory={factory} />
        : tab === "simulator" && canSimulate ? <Simulator readers={o.readers} factory={factory} onDone={() => void overview.reload()} />
        : <Overview o={o} onOpen={setOpen} />}

      <IdentityCaseDrawer id={open} onClose={() => setOpen(null)} onChanged={() => void overview.reload()} />
    </div>
  );
}

function Count({ label, value, hint, bad }: { label: string; value: number | string; hint: string; bad?: boolean }) {
  return (
    <div className={`rounded-[var(--radius-card,12px)] border bg-surface-1 px-4 py-3 ${bad ? "border-[var(--risk-medium)]" : "border-border-subtle"}`}>
      <p className="text-caption text-fg-secondary">{label}</p>
      <p className="text-2xl font-semibold tabular-nums">{value}</p>
      <p className="text-caption text-fg-tertiary truncate" title={hint}>{hint}</p>
    </div>
  );
}

function Overview({ o, onOpen }: { o: IdentityOverview; onOpen: (id: string) => void }) {
  return (
    <div className="grid gap-4 xl:grid-cols-3">
      <Panel className="xl:col-span-2" title="Tracks in view" subtitle={`Identity of every track seen in the last few minutes · ${dhaka(o.asOf)} (Dhaka)`} icon={<ScanLine size={15} />} testId="identity-tracks">
        {o.tracks.length === 0 ? <p className="p-4 text-caption text-fg-tertiary">No camera is reporting tracks right now.</p> : (
          <div className="p-4 grid gap-2 sm:grid-cols-2 2xl:grid-cols-3">
            {o.tracks.map((t) => (
              <div key={`${t.cameraId}/${t.trackId}`}>
                <p className="text-[11px] text-fg-tertiary mb-0.5">{t.cameraCode} · seen {ago(t.lastSeenAt)}</p>
                <TrackIdentityCard t={t} onOpen={onOpen} />
              </div>
            ))}
          </div>
        )}
      </Panel>
      <Panel title="Recent association changes" icon={<History size={15} />} flush testId="identity-changes">
        {o.recentChanges.length === 0 ? <p className="px-4 py-6 text-caption text-fg-tertiary">No changes yet.</p> : (
          <ul className="divide-y divide-border-subtle max-h-[32rem] overflow-y-auto">
            {o.recentChanges.map((c) => (
              <li key={c.id}>
                <button type="button" className="w-full text-left px-4 py-2.5 hover:bg-surface-2 text-caption" onClick={() => onOpen(c.associationId)}>
                  <div className="flex flex-wrap items-center gap-1.5">
                    <span className="font-mono text-fg-tertiary">{c.associationCode}</span>
                    <span className="font-mono">{c.cameraCode} · {c.trackId}</span>
                    <IdentityBadge status={c.toStatus} simulated={c.isSimulated} />
                    <span className="ml-auto tabular-nums text-fg-tertiary">{dhakaTime(c.at)}</span>
                  </div>
                  <p className="text-fg-secondary truncate" title={c.reason}>{c.action.toLowerCase().replace(/_/g, " ")} · {c.isManual ? c.actorEmail : "identity engine"} · {c.reason}</p>
                </button>
              </li>
            ))}
          </ul>
        )}
      </Panel>
      <p className="xl:col-span-3 text-caption text-fg-tertiary" data-testid="identity-note">{o.note}</p>
    </div>
  );
}

function ReviewQueue({ o, onOpen, factory }: { o: IdentityOverview; onOpen: (id: string) => void; factory: string }) {
  const history = useResource<PagedResult<IdentityOverview["reviewQueue"][number]>>(`/api/identity/associations${qs({ factoryId: factory, pageSize: 50 })}`);
  return (
    <div className="grid gap-4 xl:grid-cols-2">
      <Panel title="Waiting for a person" subtitle="Ambiguous identities and contradictions" icon={<ShieldAlert size={15} />} flush testId="identity-review-queue">
        {o.reviewQueue.length === 0 ? <p className="px-4 py-6 text-caption text-fg-tertiary">Nothing to review.</p> : (
          <ul className="divide-y divide-border-subtle">
            {o.reviewQueue.map((a) => (
              <li key={a.id}>
                <button type="button" className="w-full text-left px-4 py-3 hover:bg-surface-2 text-caption" onClick={() => onOpen(a.id)} data-testid="review-case">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="font-mono text-fg-tertiary">{a.code}</span><IdentityBadge status={a.status} simulated={a.isSimulated} />
                    <span className="font-mono">{a.camera.code} · {a.trackId}</span>
                    <span className="ml-auto text-fg-tertiary">{ago(a.updatedAt)}</span>
                  </div>
                  <p className="text-fg-secondary mt-0.5">{a.reasonText} {a.candidateCount > 0 && `${a.candidateCount} candidate worker(s).`}</p>
                </button>
              </li>
            ))}
          </ul>
        )}
      </Panel>
      <Panel title="Association timeline" subtitle="Every identity decision, newest first" icon={<History size={15} />} flush testId="identity-timeline">
        {!history.data ? <div className="p-4"><SkeletonRows rows={4} /></div> : history.data.items.length === 0 ? <p className="px-4 py-6 text-caption text-fg-tertiary">No associations yet.</p> : (
          <ul className="divide-y divide-border-subtle max-h-[36rem] overflow-y-auto">
            {history.data.items.map((a) => (
              <li key={a.id}>
                <button type="button" className="w-full text-left px-4 py-2.5 hover:bg-surface-2 text-caption" onClick={() => onOpen(a.id)}>
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="font-mono text-fg-tertiary">{a.code}</span><IdentityBadge status={a.status} simulated={a.isSimulated} />
                    <span className="font-mono">{a.camera.code} · {a.trackId}</span>
                    {a.worker && <Badge tone="accent">{a.worker.code}</Badge>}
                    <span className="ml-auto tabular-nums text-fg-tertiary">{dhaka(a.updatedAt)}</span>
                  </div>
                  <p className="text-fg-tertiary truncate">{a.reasonText}</p>
                </button>
              </li>
            ))}
          </ul>
        )}
      </Panel>
    </div>
  );
}

function Readers({ readers, onChanged }: { readers: RfidReader[]; onChanged: () => void }) {
  const [editing, setEditing] = useState<RfidReader | null>(null);
  const factory = editing?.factoryId ?? null;
  const org = useFactoryOrg(factory);
  const ws = useResource<PagedResult<{ id: string; code: string; name?: string | null; lineId: string }>>(factory ? `/api/workstations?factoryId=${factory}&pageSize=200` : null);
  const cams = useResource<MonitoringCamera[]>(factory ? `/api/monitoring/cameras?factoryId=${factory}` : null);
  const [zones, setZones] = useState<{ id: string; name: string; cameraCode: string }[]>([]);
  useEffect(() => {
    const list = cams.data;
    if (!list) return;
    let stop = false;
    void Promise.all(list.map((c) => apiJson<{ id: string; name: string; isActive?: boolean }[]>(`/api/cameras/${c.id}/zones`).then((z) => z.filter((x) => x.isActive !== false).map((x) => ({ id: x.id, name: x.name, cameraCode: c.code }))).catch(() => [])))
      .then((all) => { if (!stop) setZones(all.flat()); });
    return () => { stop = true; };
  }, [cams.data]);
  return (
    <Panel title="RFID readers" subtitle="Status and location mapping. Only workstation-level mappings can verify an identity." icon={<Radio size={15} />} flush testId="identity-readers">
      {readers.length === 0 ? <p className="px-4 py-6 text-caption text-fg-tertiary">No RFID readers are registered. Register them in Cameras &amp; devices (category RFID reader).</p> : (
        <div className="overflow-x-auto">
          <table className="w-full min-w-[52rem] text-caption">
            <thead><tr className="text-left text-fg-tertiary"><th className="px-3 py-2">Reader</th><th className="px-3 py-2">Status</th><th className="px-3 py-2">Mapping</th><th className="px-3 py-2">Cameras on its workstation</th><th className="px-3 py-2">Reads 1 h</th><th className="px-3 py-2"></th></tr></thead>
            <tbody>
              {readers.map((r) => (
                <tr key={r.deviceId} className="border-t border-border-subtle" data-testid="reader-row">
                  <td className="px-3 py-2"><span className="font-mono">{r.code}</span> · {r.name}{r.isSimulated && <span className="ml-1 text-[10px] font-semibold text-[var(--risk-medium)]">SIMULATED</span>}<p className="text-fg-tertiary">{r.factoryCode}</p></td>
                  <td className="px-3 py-2"><Badge tone={r.health === "Online" ? "success" : r.health === "Offline" ? "danger" : "neutral"}>{r.health}</Badge>{r.adminStatus !== "Active" && <span className="ml-1 text-fg-tertiary">{r.adminStatus}</span>}</td>
                  <td className="px-3 py-2">
                    {r.mapping ? (
                      <>
                        <Badge tone={r.mapping.workstationLevel ? "success" : "warning"}>{r.mapping.precision}</Badge>{" "}
                        {r.mapping.workstation?.code ?? r.mapping.zoneName ?? r.mapping.line?.name ?? r.mapping.floorName ?? r.mapping.location ?? ""}
                        {!r.mapping.workstationLevel && <p className="text-fg-tertiary">Not workstation-level: reads can&apos;t verify identity.</p>}
                      </>
                    ) : <span className="text-fg-tertiary">Not mapped: its reads can&apos;t place anyone.</span>}
                  </td>
                  <td className="px-3 py-2">{r.cameraCodes.length ? r.cameraCodes.join(", ") : <span className="text-fg-tertiary">{r.mapping?.workstationLevel ? "No camera zone covers it" : "—"}</span>}</td>
                  <td className="px-3 py-2 tabular-nums">{r.readsLastHour}{r.lastReadAt && <p className="text-fg-tertiary">last {ago(r.lastReadAt)}</p>}</td>
                  <td className="px-3 py-2">{r.canConfigure && <Button size="sm" variant="secondary" onClick={() => setEditing(r)} data-testid="reader-map">Map</Button>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <ReaderMappingDialog reader={editing} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); onChanged(); }}
        org={{ lines: (org.lines.data ?? []).map((l) => ({ id: l.id, name: l.name })), floors: (org.floors.data ?? []).map((f) => ({ id: f.id, name: f.name })) }}
        workstations={ws.data?.items ?? []} zones={zones} />
    </Panel>
  );
}

function Reads({ factory }: { factory: string }) {
  const reads = useResource<PagedResult<RfidRead>>(`/api/identity/reads${qs({ factoryId: factory, pageSize: 100 })}`, { pollMs: 30_000 });
  return (
    <Panel title="RFID reads" subtitle="Newest first · cards shown by their last four characters only" icon={<Fingerprint size={15} />} flush testId="identity-reads">
      {!reads.data ? <div className="p-4"><SkeletonRows rows={5} /></div> : reads.data.items.length === 0 ? <p className="px-4 py-6 text-caption text-fg-tertiary">No reads yet.</p> : (
        <div className="overflow-x-auto">
          <table className="w-full min-w-[48rem] text-caption">
            <thead><tr className="text-left text-fg-tertiary"><th className="px-3 py-2">Time (Dhaka)</th><th className="px-3 py-2">Reader</th><th className="px-3 py-2">Card</th><th className="px-3 py-2">Worker</th><th className="px-3 py-2">Outcome</th><th className="px-3 py-2">Identity</th></tr></thead>
            <tbody>
              {reads.data.items.map((r) => (
                <tr key={r.id} className="border-t border-border-subtle" data-testid="read-row">
                  <td className="px-3 py-2 tabular-nums">{dhaka(r.occurredAt)}</td>
                  <td className="px-3 py-2 font-mono">{r.readerCode}{r.isSimulated && <span className="ml-1 text-[10px] font-semibold text-[var(--risk-medium)]">SIMULATED</span>}</td>
                  <td className="px-3 py-2 font-mono">{r.card}{r.cardLabel && <span className="text-fg-tertiary"> ({r.cardLabel})</span>}</td>
                  <td className="px-3 py-2">{r.worker ? `${r.worker.code}` : "—"}</td>
                  <td className="px-3 py-2"><Badge tone={r.outcome === "ACCEPTED" ? "success" : "danger"}>{r.outcome.toLowerCase()}</Badge>{r.outcomeCode && <span className="ml-1 text-fg-tertiary">{r.outcomeCode.replace(/_/g, " ")}</span>}</td>
                  <td className="px-3 py-2">{r.evaluation ? <><IdentityBadge status={r.evaluation} /><p className="text-fg-tertiary">{r.evaluationText}</p></> : "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  );
}

function Simulator({ readers, factory, onDone }: { readers: RfidReader[]; factory: string; onDone: () => void }) {
  const toast = useToast();
  const simReaders = readers.filter((r) => r.isSimulated && (!factory || r.factoryId === factory));
  const [reader, setReader] = useState("");
  const [other, setOther] = useState("");
  const [scenario, setScenario] = useState("READ");
  const [worker, setWorker] = useState("");
  const [second, setSecond] = useState("");
  const [withTrack, setWithTrack] = useState(false);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<RfidSimulationResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const factoryId = simReaders.find((r) => r.deviceId === reader)?.factoryId ?? factory;
  const workers = useResource<PagedResult<{ id: string; workerCode: string; fullName: string }>>(factoryId ? `/api/workers?factoryId=${factoryId}&pageSize=200&status=Active` : null);
  const needs = SIM_SCENARIOS.find((s) => s.id === scenario)?.needs ?? [];

  async function run() {
    setBusy(true);
    setError(null);
    setResult(null);
    try {
      const r = await apiJson<RfidSimulationResult>("/api/identity/simulator", {
        method: "POST",
        body: { factoryId, deviceId: reader, scenario, workerId: worker || null, secondWorkerId: second || null, otherDeviceId: other || null, withSimulatedTrack: withTrack }
      });
      setResult(r);
      toast.push({ tone: "success", title: `Simulated ${r.reads.length} read(s)` });
      onDone();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Panel title="RFID simulator" subtitle="Development only · simulated readers · every read is SIMULATED and goes through the real pipeline" icon={<Wand2 size={15} />} testId="identity-simulator">
      <div className="p-4 space-y-4">
        <Callout tone="demo">Simulated reads only combine with SIMULATED track observations (the monitoring simulator). They never verify a real camera track and never create associations directly.</Callout>
        {simReaders.length === 0 ? <p className="text-caption text-fg-tertiary">No simulated RFID reader in this scope. Register one (Cameras &amp; devices → device, category RFID reader, simulated) and map it.</p> : (
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label="Scenario">{(ids) => <Select id={ids.id} value={scenario} onChange={(e) => setScenario(e.target.value)}>{SIM_SCENARIOS.map((s) => <option key={s.id} value={s.id}>{s.label}</option>)}</Select>}</Field>
            <Field label="Simulated reader">{(ids) => (
              <Select id={ids.id} value={reader} onChange={(e) => setReader(e.target.value)} data-testid="sim-reader">
                <option value="">Choose…</option>
                {simReaders.map((r) => <option key={r.deviceId} value={r.deviceId}>{r.code}{r.mapping ? ` · ${r.mapping.precision} ${r.mapping.workstation?.code ?? r.mapping.zoneName ?? ""}` : " · not mapped"}</option>)}
              </Select>
            )}</Field>
            {(needs as readonly string[]).includes("worker") && (
              <Field label="Worker (their card)">{(ids) => (
                <Select id={ids.id} value={worker} onChange={(e) => setWorker(e.target.value)}>
                  <option value="">Choose…</option>
                  {(workers.data?.items ?? []).map((w) => <option key={w.id} value={w.id}>{w.workerCode} · {w.fullName}</option>)}
                </Select>
              )}</Field>
            )}
            {(needs as readonly string[]).includes("second") && (
              <Field label="Second worker">{(ids) => (
                <Select id={ids.id} value={second} onChange={(e) => setSecond(e.target.value)}>
                  <option value="">Choose…</option>
                  {(workers.data?.items ?? []).filter((w) => w.id !== worker).map((w) => <option key={w.id} value={w.id}>{w.workerCode} · {w.fullName}</option>)}
                </Select>
              )}</Field>
            )}
            {(needs as readonly string[]).includes("other") && (
              <Field label="Second reader (another location)">{(ids) => (
                <Select id={ids.id} value={other} onChange={(e) => setOther(e.target.value)}>
                  <option value="">Choose…</option>
                  {simReaders.filter((r) => r.deviceId !== reader).map((r) => <option key={r.deviceId} value={r.deviceId}>{r.code}</option>)}
                </Select>
              )}</Field>
            )}
            <label className="sm:col-span-2 inline-flex items-center gap-2 text-caption text-fg-secondary">
              <input type="checkbox" checked={withTrack} onChange={(e) => setWithTrack(e.target.checked)} />
              First send a SIMULATED track into the reader&apos;s workstation zone (monitoring simulator)
            </label>
            <div className="sm:col-span-2"><Button variant="primary" disabled={busy || !reader} onClick={() => void run()} data-testid="sim-run">{busy ? "Running…" : "Run scenario"}</Button></div>
          </div>
        )}
        {error && <Callout tone="danger">{error}</Callout>}
        {result && (
          <div className="space-y-2 text-caption" data-testid="sim-result">
            {result.notes.map((n) => <p key={n} className="text-fg-secondary">{n}</p>)}
            {result.reads.map((r) => (
              <div key={r.read.id} className="rounded-[var(--radius-control)] border border-border-subtle px-3 py-2">
                <span className="font-mono">{r.read.readerCode}</span> · card {r.read.card} · {r.outcome}
                {r.read.outcomeCode && <> · {r.read.outcomeCode.replace(/_/g, " ")}</>}
                {r.read.evaluation && <> · <IdentityBadge status={r.read.evaluation} simulated /> {r.read.evaluationText}</>}
                {r.associations.length > 0 && <p className="text-fg-tertiary">Associations: {r.associations.map((a) => `${a.code} ${IDENTITY_LABEL[a.status] ?? a.status} (track ${a.trackId})`).join(", ")}</p>}
              </div>
            ))}
          </div>
        )}
      </div>
    </Panel>
  );
}
