"use client";

// Monitoring event timeline: every event in scope, searchable and filterable,
// with a detail view. Raw events are never changed: a review decision is
// recorded beside the event. New events arrive live; on the first page they
// are announced and shown on request so the list doesn't jump while reading.

import Link from "next/link";
import { useMemo, useState } from "react";
import { ArrowUp, ClipboardCheck, Filter, Lock } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Drawer } from "@/components/ui/Drawer";
import { Field, FilterSelect, Select, TextArea } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Badge } from "@/components/ui/Badge";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { Pagination } from "@/components/access/shared";
import { useFactoryOrg, useMyFactories } from "@/components/workers/shared";
import { SearchBox, tableCls, useQueryState } from "@/components/attendance/shared";
import { AlertStatusBadge, LiveIndicator, MonitoringTabs, SeverityBadge, SourceBadge, eventWhere } from "@/components/monitoring/shared";
import { useResource } from "@/lib/useResource";
import { useMonitoringHub } from "@/lib/useMonitoringHub";
import { apiJson, errorMessage } from "@/lib/api";
import { P, type PagedResult } from "@/lib/access";
import { cn } from "@/lib/cn";
import {
  EVENT_TYPES, EVENT_TYPE_LABEL, MON, PRODUCER_LABEL, REVIEW_DECISION_LABEL, SIMULATED_NOTE, dhaka, dhakaInputToIso, payloadString, qs,
  type MonitoringCamera, type MonitoringEvent, type MonitoringEventDetail
} from "@/lib/monitoring";

export default function EventTimelinePage() {
  return (
    <RequirePermission perms={[P.MonitoringView, P.DevicesView]} what="Monitoring event timeline">
      <Timeline />
    </RequirePermission>
  );
}

const PAGE_SIZE = 25;

function Timeline() {
  const q = useQueryState(`${MON}/events`);
  const { factories } = useMyFactories();
  const factory = q.get("factory");
  const org = useFactoryOrg(factory || null);
  const cameras = useResource<MonitoringCamera[]>(factory ? `/api/monitoring/cameras?factoryId=${factory}` : null);
  const [showFilters, setShowFilters] = useState(false);
  const path = useMemo(() => `/api/monitoring/events${qs({
    factoryId: factory, lineId: q.get("line"), cameraId: q.get("camera"), eventType: q.get("type"), category: q.get("category"),
    sourceType: q.get("source"), trackId: q.get("track"), search: q.get("search"), from: dhakaInputToIso(q.get("from")), to: dhakaInputToIso(q.get("to")),
    page: q.page, pageSize: PAGE_SIZE
  })}`, [factory, q]);
  const events = useResource<PagedResult<MonitoringEvent>>(path);
  const [incoming, setIncoming] = useState(0);
  const open = q.get("event");

  const hub = useMonitoringHub(factory || null, {
    onEvent: (e) => {
      const fits = (!q.get("line") || e.line?.id === q.get("line")) && (!q.get("camera") || e.camera?.id === q.get("camera"))
        && (!q.get("type") || e.eventType === q.get("type")) && (!q.get("source") || e.sourceType === q.get("source"))
        && (!q.get("category") || e.category === q.get("category")) && !q.get("to");
      if (fits) setIncoming((n) => n + 1);
    },
    onResync: () => void events.reload()
  });

  function showNew() {
    setIncoming(0);
    if (q.page !== 1) q.set({}, true);
    else void events.reload();
  }

  const { th, td } = tableCls;
  const rows = events.data?.items ?? [];
  const filterCount = ["line", "camera", "type", "category", "source", "track", "from", "to"].filter((k) => q.get(k)).length;

  return (
    <div className="page" data-testid="event-timeline">
      <PageHeader eyebrow="Central monitoring" title="Event timeline"
        description="Every monitoring event in your scope as it was received. Track ids are temporary camera numbers, not people; a worker appears only when verified by RFID card."
        meta={<LiveIndicator state={hub.state} lastMessageAt={hub.lastMessageAt} />}
        actions={<Button size="sm" variant={showFilters || filterCount ? "secondary" : "ghost"} iconLeft={<Filter size={13} />} onClick={() => setShowFilters((s) => !s)}>Filters{filterCount ? ` (${filterCount})` : ""}</Button>} />
      <MonitoringTabs className="mb-4" />

      <div className="flex flex-wrap items-center gap-3 mb-3">
        {factories && factories.length > 1 && (
          <FilterSelect label="Factory" value={factory} onChange={(v) => q.set({ factory: v || null, line: null, camera: null })}>
            <option value="">All my factories</option>
            {factories.map((f) => <option key={f.id} value={f.id}>{f.code}</option>)}
          </FilterSelect>
        )}
        <FilterSelect label="Source" value={q.get("source")} onChange={(v) => q.set({ source: v || null })} testId="events-source">
          <option value="">Real and simulated</option>
          <option value="REAL">Real only</option>
          <option value="SIMULATED">Simulated only</option>
        </FilterSelect>
        <FilterSelect label="Type" value={q.get("type")} onChange={(v) => q.set({ type: v || null })} testId="events-type">
          <option value="">All</option>
          {EVENT_TYPES.map((t) => <option key={t} value={t}>{EVENT_TYPE_LABEL[t]}</option>)}
        </FilterSelect>
        <SearchBox value={q.get("search")} onChange={(v) => q.set({ search: v || null })} placeholder="Summary, track id or key" label="Search events" />
      </div>
      {showFilters && (
        <Panel className="mb-3">
          <div className="p-4 grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
            <FilterSelect label="Line" value={q.get("line")} onChange={(v) => q.set({ line: v || null })}>
              <option value="">{factory ? "All" : "Choose a factory"}</option>
              {(org.lines.data ?? []).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
            </FilterSelect>
            <FilterSelect label="Camera" value={q.get("camera")} onChange={(v) => q.set({ camera: v || null })}>
              <option value="">{factory ? "All" : "Choose a factory"}</option>
              {(cameras.data ?? []).map((c) => <option key={c.id} value={c.id}>{c.code}</option>)}
            </FilterSelect>
            <FilterSelect label="Category" value={q.get("category")} onChange={(v) => q.set({ category: v || null })}>
              <option value="">All</option>
              <option value="VISION">People, tracks and zones</option>
              <option value="SYSTEM">Camera, device and model status</option>
            </FilterSelect>
            <label className="flex items-center gap-2 text-caption text-fg-tertiary">Track
              <Input value={q.get("track")} onChange={(e) => q.set({ track: e.target.value || null })} placeholder="e.g. t-12" aria-label="Track id" />
            </label>
            <label className="flex items-center gap-2 text-caption text-fg-tertiary">From
              <Input type="datetime-local" value={q.get("from")} onChange={(e) => q.set({ from: e.target.value || null })} aria-label="From (Dhaka time)" />
            </label>
            <label className="flex items-center gap-2 text-caption text-fg-tertiary">To
              <Input type="datetime-local" value={q.get("to")} onChange={(e) => q.set({ to: e.target.value || null })} aria-label="To (Dhaka time)" />
            </label>
          </div>
        </Panel>
      )}

      {incoming > 0 && (
        <button type="button" onClick={showNew} className="mb-3 w-full rounded-[var(--radius-control)] border border-[var(--accent)] bg-[var(--accent-soft)] py-2 text-caption text-fg-primary inline-flex items-center justify-center gap-1.5" data-testid="events-new">
          <ArrowUp size={13} aria-hidden /> {incoming} new event{incoming === 1 ? "" : "s"}: show
        </button>
      )}

      {events.error && !events.data ? (
        <ErrorState retryLabel="Try again" title="Could not load events" body={events.error.message} onRetry={() => void events.reload()} />
      ) : !events.data ? (
        <SkeletonRows rows={8} />
      ) : (
        <Panel flush testId="events-table">
          <div className="overflow-x-auto">
            <table className="w-full min-w-[56rem]">
              <thead>
                <tr><th className={th}>Time (Dhaka)</th><th className={th}>Event</th><th className={th}>Where</th><th className={th}>Track / worker</th><th className={th}>Confidence</th><th className={th}>Source</th><th className={th}>Review</th></tr>
              </thead>
              <tbody>
                {rows.length === 0 ? (
                  <tr><td className={cn(td, "text-center text-fg-tertiary py-10")} colSpan={7}>No events match. Try a wider time range, or start the monitoring simulator.</td></tr>
                ) : rows.map((e) => (
                  <tr key={e.id} className="hover:bg-surface-2 cursor-pointer" onClick={() => q.set({ event: e.id }, false)} data-testid="timeline-row" data-source={e.sourceType}>
                    <td className={cn(td, "tabular-nums whitespace-nowrap")}>{dhaka(e.occurredAt)}</td>
                    <td className={td}>
                      <span className="font-medium">{EVENT_TYPE_LABEL[e.eventType] ?? e.eventType}</span>
                      {payloadString(e, "toStatus") && <span className="ml-1.5"><Badge tone={payloadString(e, "toStatus") === "OFFLINE" ? "danger" : "neutral"}>{payloadString(e, "toStatus")}</Badge></span>}
                      <p className="text-caption text-fg-tertiary truncate max-w-[22rem]">{e.summary}</p>
                    </td>
                    <td className={cn(td, "text-caption")}>{eventWhere(e) || "—"}</td>
                    <td className={cn(td, "text-caption")}>
                      {e.trackId ? <span className="font-mono">{e.trackId}</span> : "—"}
                      {e.verifiedWorker && <div><Badge tone="accent" title="Verified by RFID card">{e.verifiedWorker.code}</Badge></div>}
                    </td>
                    <td className={cn(td, "tabular-nums")}>{e.confidence != null ? e.confidence.toFixed(2) : "—"}</td>
                    <td className={td}><SourceBadge source={e.sourceType} /></td>
                    <td className={cn(td, "text-caption")}>{e.latestReview ? REVIEW_DECISION_LABEL[e.latestReview] ?? e.latestReview : "—"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="px-4 py-3 border-t border-border-subtle">
            <Pagination page={events.data.page} pageSize={events.data.pageSize} total={events.data.total} onPage={(p) => q.set({ page: String(p) }, false)} />
          </div>
        </Panel>
      )}

      <EventDrawer id={open || null} onClose={() => q.set({ event: null }, false)} onChanged={() => void events.reload()} />
    </div>
  );
}

function EventDrawer({ id, onClose, onChanged }: { id: string | null; onClose: () => void; onChanged: () => void }) {
  const detail = useResource<MonitoringEventDetail>(id ? `/api/monitoring/events/${id}` : null);
  const toast = useToast();
  const [decision, setDecision] = useState("CONFIRMED");
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const d = detail.data;
  const e = d?.event;

  async function review() {
    if (!id) return;
    setBusy(true);
    setError(null);
    try {
      const r = await apiJson<MonitoringEventDetail>(`/api/monitoring/events/${id}/reviews`, { method: "POST", body: { decision, note: note || null } });
      detail.setData(r);
      setNote("");
      toast.push({ tone: "success", title: "Review recorded" });
      onChanged();
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Drawer open={!!id} onClose={onClose} title={e ? EVENT_TYPE_LABEL[e.eventType] ?? e.eventType : "Event"} width="w-[34rem]">
      {detail.error && !d ? <ErrorState retryLabel="Try again" title="Event not available" body={detail.error.message} /> : !e || !d ? <SkeletonRows rows={6} /> : (
        <div className="space-y-4 text-body" data-testid="event-detail">
          <div className="flex flex-wrap items-center gap-2">
            <SourceBadge source={e.sourceType} />
            <Badge tone={e.severity === "CRITICAL" ? "danger" : e.severity === "WARNING" ? "warning" : "neutral"}>{e.severity}</Badge>
            <Badge tone="neutral">{e.category === "VISION" ? "People, tracks, zones" : "Equipment status"}</Badge>
          </div>
          <p>{e.summary}</p>
          {e.isSimulated && <Callout tone="warning">{SIMULATED_NOTE}</Callout>}
          <dl className="grid grid-cols-2 gap-x-4 gap-y-3 text-caption">
            <D label="Happened (Dhaka)">{dhaka(e.occurredAt)}</D>
            <D label="Received (Dhaka)">{dhaka(e.receivedAt)}</D>
            <D label="Factory">{e.factoryCode}</D>
            <D label="Line">{e.line?.name ?? "—"}</D>
            <D label="Department / floor">{[e.departmentName, e.floorName].filter(Boolean).join(" / ") || "—"}</D>
            <D label="Workstation">{e.workstation?.code ?? "—"}</D>
            <D label="Camera">{e.camera ? <Link className="text-[var(--accent)]" href={`${MON}/cameras/${e.camera.id}`}>{e.camera.code}</Link> : "—"}</D>
            <D label="Device">{e.device?.code ?? "—"}</D>
            <D label="Zone">{e.zone ? `${e.zone.name} (${e.zone.zoneType})` : "—"}</D>
            <D label="Track (temporary)">{e.trackId ?? "—"}</D>
            <D label="Verified worker">{e.verifiedWorker ? `${e.verifiedWorker.code} · ${e.verifiedWorker.name ?? ""} (RFID card)` : "None: not identified"}</D>
            <D label="Confidence">{e.confidence != null ? e.confidence.toFixed(2) : "—"}</D>
            <D label="Model">{e.modelName ? `${e.modelName}${e.modelVersion ? ` ${e.modelVersion}` : ""}` : "—"}</D>
            <D label="Producer">{PRODUCER_LABEL[e.producer] ?? e.producer}</D>
            <D label="Recorded by">{e.recordedByEmail}</D>
            <D label="Event id"><span className="font-mono break-all">{e.id}</span></D>
          </dl>
          <div>
            <p className="text-caption text-fg-tertiary mb-1">Payload</p>
            <pre className="text-[11px] bg-surface-2 rounded-[var(--radius-control)] p-3 overflow-x-auto max-h-56" data-testid="event-payload">{JSON.stringify(e.payload, null, 2)}</pre>
          </div>
          <p className="text-caption text-fg-tertiary inline-flex items-center gap-1.5"><Lock size={12} aria-hidden /> Raw events are never changed. Reviews are kept beside the event.</p>

          {d.alerts.length > 0 && (
            <div>
              <p className="text-caption text-fg-tertiary mb-1">Related alerts</p>
              <ul className="space-y-1.5">
                {d.alerts.map((a) => (
                  <li key={a.id}>
                    <Link href={`${MON}/alerts/${a.id}`} className="flex flex-wrap items-center gap-2 text-caption hover:underline" data-testid="event-alert-link">
                      <span className="font-mono">{a.code}</span><SeverityBadge severity={a.severity} /><AlertStatusBadge status={a.status} /><span>{a.title}</span>
                    </Link>
                  </li>
                ))}
              </ul>
            </div>
          )}

          <div>
            <p className="text-caption text-fg-tertiary mb-1 inline-flex items-center gap-1.5"><ClipboardCheck size={12} aria-hidden /> Reviews</p>
            {d.reviews.length === 0 ? <p className="text-caption text-fg-tertiary">Not reviewed.</p> : (
              <ul className="space-y-2" data-testid="event-reviews">
                {d.reviews.map((r) => (
                  <li key={r.id} className="text-caption border-l-2 border-border-subtle pl-2">
                    <span className="font-medium">{REVIEW_DECISION_LABEL[r.decision] ?? r.decision}</span> · {r.reviewedByEmail} · {dhaka(r.reviewedAt)}
                    {r.note && <p className="text-fg-secondary">{r.note}</p>}
                  </li>
                ))}
              </ul>
            )}
          </div>

          {d.canReview && (
            <div className="space-y-3 border-t border-border-subtle pt-3" data-testid="event-review-form">
              <Field label="Review decision">
                {(ids) => (
                  <Select id={ids.id} value={decision} onChange={(ev) => setDecision(ev.target.value)}>
                    {Object.entries(REVIEW_DECISION_LABEL).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
                  </Select>
                )}
              </Field>
              <Field label={decision === "FALSE_POSITIVE" || decision === "NEEDS_FOLLOW_UP" ? "Note (required)" : "Note (optional)"}>
                {(ids) => <TextArea id={ids.id} rows={2} maxLength={1000} value={note} onChange={(ev) => setNote(ev.target.value)} />}
              </Field>
              {error && <Callout tone="danger">{error}</Callout>}
              <Button size="sm" variant="primary" onClick={() => void review()} disabled={busy || ((decision === "FALSE_POSITIVE" || decision === "NEEDS_FOLLOW_UP") && !note.trim())} data-testid="event-review-submit">
                {busy ? "Saving…" : "Record review"}
              </Button>
            </div>
          )}
        </div>
      )}
    </Drawer>
  );
}

function D({ label, children }: { label: string; children: React.ReactNode }) {
  return <div><dt className="text-fg-tertiary">{label}</dt><dd className="text-fg-primary mt-0.5">{children}</dd></div>;
}
