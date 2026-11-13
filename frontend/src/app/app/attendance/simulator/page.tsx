"use client";

// RFID attendance simulator: taps a worker's card on a simulated reader.
// Events go through exactly the same pipeline as physical readers
// (duplicate window, idempotency, session rules) but are stored with
// source = Simulator and always shown as SIMULATED — never as real presence.
// "Send again" re-sends the last tap with the same idempotency key to show
// that a retried reader message is recorded only once.

import { useEffect, useMemo, useState } from "react";
import { Coffee, FlaskConical, LogIn, LogOut, RefreshCw, Repeat } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { Field, FilterSelect, Select } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { Pagination } from "@/components/access/shared";
import { useMyFactories } from "@/components/workers/shared";
import { AnyWorkerPicker, AttendanceTabs, OutcomeBadge, SessionFlags, SessionStatusBadge, SimulatedBadge, tableCls, useQueryState } from "@/components/attendance/shared";
import { useResource } from "@/lib/useResource";
import { apiJson, errorMessage, isApiError } from "@/lib/api";
import { cn } from "@/lib/cn";
import { formatDateTime } from "@/lib/format";
import { P, type PagedResult } from "@/lib/access";
import type { Worker } from "@/lib/workers";
import { BREAK_TYPES, EVENT_TYPE_LABEL, SIMULATED_NOTE, clock, localToIso, minutes, newKey, type AttendanceEvent, type EventResult, type Reader } from "@/lib/attendance";

type Tap = "check-in" | "check-out" | "break-start" | "break-end";
type Sent = { tap: Tap; body: Record<string, unknown>; result?: EventResult; error?: string; code?: string | null };

export default function SimulatorPage() {
  return (
    <RequirePermission perms={[P.AttendanceManage]} what="RFID simulator">
      <Simulator />
    </RequirePermission>
  );
}

function Simulator() {
  const q = useQueryState("/app/attendance/simulator");
  const { factories } = useMyFactories();
  const factoryId = q.get("factory") || (factories?.length ? factories[0].id : "");
  const readers = useResource<Reader[]>(factoryId ? `/api/simulator/rfid/readers?factoryId=${factoryId}` : null);
  const events = useResource<PagedResult<AttendanceEvent>>(factoryId ? `/api/simulator/rfid/events?factoryId=${factoryId}&page=${q.page}&pageSize=25` : null);
  const [worker, setWorker] = useState<Worker | null>(null);
  const [readerId, setReaderId] = useState("");
  const [when, setWhen] = useState("");
  const [breakType, setBreakType] = useState("Tea");
  const [busy, setBusy] = useState(false);
  const [last, setLast] = useState<Sent | null>(null);

  const entry = useMemo(() => readers.data?.find((r) => r.direction !== "Exit"), [readers.data]);
  useEffect(() => { setReaderId(""); setWorker(null); setLast(null); }, [factoryId]);

  async function send(tap: Tap, body: Record<string, unknown>) {
    setBusy(true);
    const sent: Sent = { tap, body };
    try {
      sent.result = await apiJson<EventResult>(`/api/simulator/rfid/${tap}`, { method: "POST", body });
    } catch (e) {
      sent.error = errorMessage(e);
      sent.code = isApiError(e) ? ((e.body as { code?: string } | null)?.code ?? null) : null;
    } finally {
      setLast(sent);
      setBusy(false);
      void events.reload();
    }
  }

  function tap(t: Tap) {
    if (!worker) return;
    const reader = readerId || (t === "check-out" ? readers.data?.find((r) => r.direction === "Exit")?.id : entry?.id) || undefined;
    void send(t, { workerId: worker.id, readerId: reader, occurredAt: localToIso(when), idempotencyKey: newKey("sim"), breakType: t === "break-start" ? breakType : undefined });
  }

  return (
    <div className="page" data-testid="simulator-page">
      <PageHeader title={<span className="inline-flex items-center gap-2">RFID attendance simulator <SimulatedBadge /></span>}
        description="For demonstrations and testing without hardware. Each tap carries the worker's active card UID when one is registered (Workers → Cards); otherwise no card." />
      <AttendanceTabs className="mt-4" />
      <Callout className="mt-4" tone="warning" title="Simulated, not real">{SIMULATED_NOTE} Real readers send to POST /api/attendance/events with their own reader id and card UID.</Callout>

      <div className="mt-4 flex flex-wrap items-center gap-3">
        {(factories?.length ?? 0) > 1 && (
          <FilterSelect label="Factory" value={factoryId} onChange={(v) => q.set({ factory: v || null })}>
            {factories!.map((f) => <option key={f.id} value={f.id}>{f.code} · {f.name}</option>)}
          </FilterSelect>
        )}
      </div>

      <div className="mt-4 grid gap-6 xl:grid-cols-[1.1fr_1fr]">
        <Panel title="Tap a card" icon={<FlaskConical size={14} />} testId="simulator-panel">
          {!factoryId ? <EmptyState title="No factory" body="You need a factory assignment to use the simulator." /> : (
            <div className="space-y-3">
              <Field label="Worker">{() => <AnyWorkerPicker factoryId={factoryId} value={worker?.id ?? ""} onChange={setWorker} />}</Field>
              <div className="grid gap-3 sm:grid-cols-3">
                <Field label="Simulated reader" hint="Default: entry for check-in, exit for check-out">
                  {({ id, describedBy }) => (
                    <Select id={id} aria-describedby={describedBy} value={readerId} onChange={(e) => setReaderId(e.target.value)} data-testid="sim-reader">
                      <option value="">Automatic</option>
                      {(readers.data ?? []).map((r) => <option key={r.id} value={r.id}>{r.code} · {r.name}</option>)}
                    </Select>
                  )}
                </Field>
                <Field label="Time" hint="Empty: now">{({ id, describedBy }) => <Input id={id} aria-describedby={describedBy} type="datetime-local" value={when} onChange={(e) => setWhen(e.target.value)} data-testid="sim-time" />}</Field>
                <Field label="Break type">
                  {({ id }) => (
                    <Select id={id} value={breakType} onChange={(e) => setBreakType(e.target.value)}>
                      {BREAK_TYPES.map((b) => <option key={b} value={b}>{b}</option>)}
                    </Select>
                  )}
                </Field>
              </div>
              <div className="flex flex-wrap gap-2">
                <Button variant="primary" iconLeft={<LogIn size={14} />} disabled={busy || !worker} onClick={() => tap("check-in")} data-testid="sim-check-in">Check in</Button>
                <Button variant="secondary" iconLeft={<LogOut size={14} />} disabled={busy || !worker} onClick={() => tap("check-out")} data-testid="sim-check-out">Check out</Button>
                <Button variant="secondary" iconLeft={<Coffee size={14} />} disabled={busy || !worker} onClick={() => tap("break-start")} data-testid="sim-break-start">Break start</Button>
                <Button variant="secondary" disabled={busy || !worker} onClick={() => tap("break-end")} data-testid="sim-break-end">Break end</Button>
                <Button variant="ghost" iconLeft={<Repeat size={14} />} disabled={busy || !last} onClick={() => last && void send(last.tap, last.body)} data-testid="sim-resend"
                  title="Send the last tap again with the same idempotency key">Send again (same key)</Button>
              </div>
            </div>
          )}
        </Panel>

        <Panel title="Last result" testId="simulator-result">
          {!last ? <p className="text-caption text-fg-tertiary">Tap a card to see what the pipeline did with it.</p>
            : last.error ? (
              <div className="space-y-2">
                <Callout tone="danger" title={`Rejected${last.code ? ` (${last.code})` : ""}`}>{last.error}</Callout>
                <p className="text-caption text-fg-tertiary">Rejected taps are still stored as events with their reason, for review.</p>
              </div>
            ) : last.result && (
              <div className="space-y-2 text-caption">
                <div className="flex flex-wrap items-center gap-2">
                  <SimulatedBadge />
                  <Badge tone="info">{EVENT_TYPE_LABEL[last.result.event.eventType]}</Badge>
                  <OutcomeBadge outcome={last.result.event.outcome} />
                  {last.result.replayed && <Badge tone="accent" testId="sim-replayed">Replayed: same key, recorded once</Badge>}
                </div>
                <p>{last.result.event.workerCode} · {last.result.event.workerName} · {formatDateTime(last.result.event.occurredAt)} · reader {last.result.event.readerCode ?? "—"} · card {last.result.event.cardUid ?? "—"}</p>
                {last.result.event.outcomeMessage && <p className="text-fg-secondary">{last.result.event.outcomeMessage}</p>}
                {last.result.session && (
                  <div className="rounded-[var(--radius-control)] border border-border-subtle p-3 space-y-1">
                    <div className="flex flex-wrap items-center gap-2"><SessionStatusBadge status={last.result.session.status} /><SessionFlags s={last.result.session} /></div>
                    <p>Session {last.result.session.workDate}: {clock(last.result.session.checkInAt)} – {clock(last.result.session.checkOutAt)}{last.result.session.shift ? ` · shift ${last.result.session.shift.code}` : ""}</p>
                    <p className="text-fg-tertiary">Worked {minutes(last.result.session.figures.netWorkingMinutes)} · breaks {minutes(last.result.session.figures.breakMinutes)} · late {minutes(last.result.session.figures.lateMinutes)}</p>
                  </div>
                )}
                {last.result.break && <p>Break {last.result.break.breakType}: allowed {minutes(last.result.break.allowedMinutes)}{last.result.break.actualMinutes !== null && last.result.break.actualMinutes !== undefined ? `, taken ${minutes(last.result.break.actualMinutes)}` : ""}</p>}
              </div>
            )}
        </Panel>
      </div>

      <Panel className="mt-6" flush title="Simulated events" subtitle={events.data ? `${events.data.total} event${events.data.total === 1 ? "" : "s"}` : undefined}
        right={<Button size="sm" variant="secondary" aria-label="Refresh" onClick={() => void events.reload()}><RefreshCw size={13} className={cn(events.refreshing && "animate-spin")} /></Button>}>
        {events.loading ? <div className="p-4"><SkeletonRows rows={5} /></div>
          : events.error && !events.data ? <ErrorState className="m-4" title="Couldn't load events" body={errorMessage(events.error)} retryLabel="Try again" onRetry={() => void events.reload()} />
          : (events.data?.items.length ?? 0) === 0 ? <EmptyState className="m-4" icon={<FlaskConical size={18} />} title="No simulated events yet" body="Taps from the simulator appear here." />
          : (
            <>
              <div className="overflow-x-auto">
                <table className="w-full min-w-[820px]" data-testid="sim-events">
                  <thead className="border-b border-border-subtle">
                    <tr>{["Time", "Worker", "Event", "Reader", "Card", "Outcome", "Source"].map((h) => <th key={h} className={tableCls.th}>{h}</th>)}</tr>
                  </thead>
                  <tbody className="divide-y divide-border-subtle">
                    {events.data!.items.map((e) => (
                      <tr key={e.id}>
                        <td className={cn(tableCls.td, "whitespace-nowrap")}>{formatDateTime(e.occurredAt)}</td>
                        <td className={tableCls.td}><span className="font-mono">{e.workerCode}</span> {e.workerName}</td>
                        <td className={tableCls.td}>{EVENT_TYPE_LABEL[e.eventType] ?? e.eventType}</td>
                        <td className={cn(tableCls.td, "font-mono")}>{e.readerCode ?? "—"}</td>
                        <td className={cn(tableCls.td, "font-mono")}>{e.cardUid ?? "—"}</td>
                        <td className={tableCls.td} title={e.outcomeMessage ?? undefined}><OutcomeBadge outcome={e.outcome} />{e.outcomeCode && <span className="ml-1 text-fg-tertiary">{e.outcomeCode}</span>}</td>
                        <td className={tableCls.td}><SimulatedBadge /></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <Pagination page={events.data!.page} pageSize={events.data!.pageSize} total={events.data!.total} onPage={(p) => q.set({ page: String(p) }, false)} />
            </>
          )}
      </Panel>
    </div>
  );
}
