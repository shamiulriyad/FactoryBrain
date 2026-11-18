"use client";

// Factory overview — every figure on this page is read from the factory's
// own records in the database (production output, line targets, attendance,
// machines, monitoring alerts and the approval queues), never from a demo
// simulator. Records that come from a simulator are labelled SIMULATED where
// they appear, and nothing is invented to fill an empty period.

import Link from "next/link";
import { useEffect, useMemo, useState } from "react";
import { AlertTriangle, CheckCircle2, ClipboardCheck, Cpu, Factory, OctagonAlert, RefreshCw, TriangleAlert, Users } from "lucide-react";
import { Badge, type BadgeTone } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Skeleton } from "@/components/ui/Skeleton";
import { KpiCell, KpiStrip, SegmentBar, StackBar, type KpiTone } from "@/components/overview/KpiStrip";
import { useMyFactories } from "@/components/workers/shared";
import { useBusinessStore } from "@/store/business.store";
import { useAppStore } from "@/store/app.store";
import { useMounted } from "@/lib/persist";
import { useResource } from "@/lib/useResource";
import { useAuthStore, can } from "@/lib/auth";
import { P } from "@/lib/access";
import { useT } from "@/lib/useT";
import { cn } from "@/lib/cn";
import { errorMessage } from "@/lib/api";

// ─── API shapes (only the fields this page reads) ──────────────────────────

type PlanningContext = { factoryId: string; factoryCode: string; factoryName: string; timeZone: string; today: string; lines: { id: string; name: string; isActive: boolean }[] };
type SummaryRow = { key: string; label: string; actual: number; planned: number; efficiencyPct?: number | null; downtimeMinutes: number; rework: number; reject: number; records: number };
type ProductionSummary = {
  fromDate: string; toDate: string;
  totals: SummaryRow & { efficiencyExcludingDowntimePct?: number | null };
  byLine: SummaryRow[];
  daily: { date: string; actual: number; efficiencyPct?: number | null; downtimeMinutes: number }[];
};
type Target = { lineId?: string | null; level: string; period: string; quantity: number; inForceToday: boolean; isSuperseded: boolean };
type Capacity = { efficiencyPct?: number | null };
type Paged<T> = { items: T[]; total: number };
type Kpi = { metricId: string; label: string; value: number | null; unit?: string | null; availability: string };
type AnalyticsOverview = { groups: { id: string; title: string; kpis: Kpi[] }[] };
type Machine = { id: string; code: string; line?: { id: string; name: string } | null; monitored: boolean; state: string; reason?: string | null; isSimulated: boolean; since?: string | null };
type Alert = { id: string; code: string; alertType: string; severity: string; title: string; status: string; sourceType: string; lastOccurredAt?: string; createdAt?: string };

type Period = "today" | "yesterday" | "week";
const PERIOD_LABEL: Record<Period, string> = { today: "Today", yesterday: "Yesterday", week: "Last 7 days" };

const addDays = (iso: string, n: number) => {
  const d = new Date(`${iso}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + n);
  return d.toISOString().slice(0, 10);
};
const isFriday = (iso: string) => new Date(`${iso}T00:00:00Z`).getUTCDay() === 5;
/** Working days (Friday is the weekly holiday) in [from, to]. */
const workingDays = (from: string, to: string) => {
  let n = 0;
  for (let d = from; d <= to; d = addDays(d, 1)) if (!isFriday(d)) n++;
  return n;
};
const pct = (a: number, b: number) => (b > 0 ? Math.round((a / b) * 1000) / 10 : null);
const fmt = (n: number | null | undefined) => (n == null ? "—" : n.toLocaleString("en-US"));
const kpi = (o: AnalyticsOverview | null, group: string, id: string) => o?.groups.find((g) => g.id === group)?.kpis.find((k) => k.metricId === id) ?? null;

const SEVERITY_TONE: Record<string, BadgeTone> = { CRITICAL: "danger", HIGH: "danger", MEDIUM: "warning", LOW: "neutral" };
const STATE_TONE: Record<string, KpiTone> = { RUNNING: "good", IDLE: "warn", STOPPED: "warn", ERROR: "bad", OFFLINE: "bad", MAINTENANCE: "neutral", UNKNOWN: "neutral" };

type FloorLevel = "action" | "watch" | "normal";
const FLOOR_LEVEL: Record<FloorLevel, { label: string; icon: typeof CheckCircle2; box: string; text: string }> = {
  action: { label: "Action needed", icon: OctagonAlert, box: "border-[var(--risk-high-border)] bg-[var(--risk-high-soft)]", text: "text-[var(--risk-high)]" },
  watch: { label: "Watch", icon: TriangleAlert, box: "border-[var(--risk-medium-border)] bg-[var(--risk-medium-soft)]", text: "text-[var(--risk-medium)]" },
  normal: { label: "Running normally", icon: CheckCircle2, box: "border-[var(--risk-low-border)] bg-[var(--risk-low-soft)]", text: "text-[var(--risk-low)]" }
};

export default function OverviewPage() {
  const { locale } = useT();
  const user = useAuthStore((s) => s.user);
  const { factories } = useMyFactories();
  const [factoryId, setFactoryId] = useState<string | null>(null);
  const [period, setPeriod] = useState<Period>("today");

  useEffect(() => {
    if (!factoryId && factories?.length) setFactoryId(factories[0].id);
  }, [factories, factoryId]);

  // First arrival after onboarding: lets the guided tour start.
  const mounted = useMounted();
  useEffect(() => {
    if (!mounted) return;
    if (!useAppStore.getState().onboardedAt && useBusinessStore.getState().onboardingComplete) useAppStore.getState().markOnboarded();
  }, [mounted]);

  const canProduction = can(user, P.ProductionView);
  const canPlanning = can(user, P.PlanningView);
  const canMachines = can(user, P.MonitoringView, P.DevicesView, P.ProductionView);
  const canAlerts = can(user, P.MonitoringView, P.DevicesView);
  const canAnalytics = can(user, P.ProductionView, P.MonitoringView, P.DevicesView, P.AttendanceView, P.OvertimeView);
  const canOvertime = can(user, P.OvertimeView, P.OvertimeApprove);
  const canDocuments = can(user, P.DocumentsView);

  const f = factoryId;
  const ctx = useResource<PlanningContext>(f && canPlanning ? `/api/planning/context?factoryId=${f}` : null);
  const today = ctx.data?.today ?? new Date().toISOString().slice(0, 10);
  const range = period === "today" ? { from: today, to: today } : period === "yesterday" ? { from: addDays(today, -1), to: addDays(today, -1) } : { from: addDays(today, -6), to: today };
  const days = Math.max(1, workingDays(range.from, range.to));

  const summary = useResource<ProductionSummary>(f && canProduction ? `/api/production/summary?from=${range.from}&to=${range.to}` : null, { pollMs: 60_000 });
  const week = useResource<ProductionSummary>(f && canProduction ? `/api/production/summary?from=${addDays(today, -6)}&to=${today}` : null);
  const targets = useResource<Paged<Target>>(f && canPlanning ? `/api/planning/targets?factoryId=${f}&pageSize=100` : null);
  const analytics = useResource<AnalyticsOverview>(f && canAnalytics ? `/api/analytics/overview?factoryId=${f}&from=${range.from}&to=${range.to}` : null, { pollMs: 60_000 });
  const machines = useResource<Paged<Machine> | Machine[]>(f && canMachines ? `/api/iot/machines?factoryId=${f}` : null, { pollMs: 30_000 });
  const alerts = useResource<Paged<Alert>>(f && canAlerts ? `/api/monitoring/alerts?factoryId=${f}&status=ACTIVE&pageSize=6` : null, { pollMs: 30_000 });
  const overtime = useResource<Paged<unknown>>(f && canOvertime ? `/api/overtime?factoryId=${f}&status=PendingApproval&pageSize=1` : null);
  const plans = useResource<Paged<unknown>>(f && canProduction ? `/api/production/plans?status=pending_approval&pageSize=1` : null);
  const docs = useResource<Paged<unknown>>(f && canDocuments ? `/api/documents?factoryId=${f}&status=SUBMITTED&pageSize=1` : null);
  const dailyPlans = useResource<Paged<{ status: string }>>(f && canPlanning ? `/api/planning/daily-plans?factoryId=${f}` : null);

  // Capacity (target efficiency) per line of this factory.
  const lines = useMemo(() => (ctx.data?.lines ?? []).filter((l) => l.isActive), [ctx.data]);
  const [capacity, setCapacity] = useState<Record<string, number | null>>({});
  useEffect(() => {
    if (!lines.length || !canPlanning) return;
    let cancelled = false;
    void (async () => {
      const { apiJson } = await import("@/lib/api");
      const entries = await Promise.all(lines.map(async (l) => {
        try { return [l.id, (await apiJson<Capacity>(`/api/planning/lines/${l.id}/capacity-settings`)).efficiencyPct ?? null] as const; }
        catch { return [l.id, null] as const; }
      }));
      if (!cancelled) setCapacity(Object.fromEntries(entries));
    })();
    return () => { cancelled = true; };
  }, [lines, canPlanning]);

  // ── Lines of this factory with output vs target ──
  const lineIds = new Set(lines.map((l) => l.id));
  const dailyTarget: Record<string, number> = {};
  for (const t of targets.data?.items ?? [])
    if (t.level === "LINE" && t.period === "DAILY" && t.inForceToday && !t.isSuperseded && t.lineId) dailyTarget[t.lineId] = t.quantity;
  const rows = (lines.length ? lines : (summary.data?.byLine ?? []).map((r) => ({ id: r.key, name: r.label, isActive: true }))).map((l) => {
    const s = summary.data?.byLine.find((r) => r.key === l.id);
    const target = dailyTarget[l.id] != null ? dailyTarget[l.id] * days : null;
    const eff = s?.efficiencyPct ?? null;
    const effTarget = capacity[l.id] ?? null;
    const achievement = target ? pct(s?.actual ?? 0, target) : null;
    const tone: KpiTone = !s || !s.records ? "neutral" : effTarget != null && eff != null
      ? (eff < effTarget - 5 ? "bad" : eff < effTarget ? "warn" : "good")
      : achievement != null ? (achievement < 85 ? "bad" : achievement < 95 ? "warn" : "good") : "neutral";
    return { id: l.id, name: l.name, actual: s?.actual ?? 0, records: s?.records ?? 0, target, achievement, eff, effTarget, downtime: s?.downtimeMinutes ?? 0,
             rework: s?.rework ?? 0, reject: s?.reject ?? 0, tone };
  }).filter((r) => !lineIds.size || lineIds.has(r.id));

  const actual = rows.reduce((a, r) => a + r.actual, 0);
  const targetTotal = rows.every((r) => r.target == null) ? null : rows.reduce((a, r) => a + (r.target ?? 0), 0);
  const rework = rows.reduce((a, r) => a + r.rework, 0);
  const reject = rows.reduce((a, r) => a + r.reject, 0);
  const downtime = rows.reduce((a, r) => a + r.downtime, 0);
  const effs = rows.filter((r) => r.eff != null && r.records > 0).map((r) => r.eff!);
  const meanEff = effs.length ? Math.round((effs.reduce((a, b) => a + b, 0) / effs.length) * 10) / 10 : null;
  const hasOutput = rows.some((r) => r.records > 0);
  const red = rows.filter((r) => r.tone === "bad");
  const amber = rows.filter((r) => r.tone === "warn");

  const machineList: Machine[] = Array.isArray(machines.data) ? machines.data : machines.data?.items ?? [];
  const monitored = machineList.filter((m) => m.monitored);
  const byState = (s: string, sim?: boolean) => monitored.filter((m) => m.state === s && (sim === undefined || m.isSimulated === sim)).length;
  const realProblem = monitored.filter((m) => !m.isSimulated && (m.state === "ERROR" || m.state === "OFFLINE"));

  const alertList = alerts.data?.items ?? [];
  const realAlerts = alertList.filter((a) => a.sourceType !== "SIMULATED");
  const criticalReal = realAlerts.filter((a) => a.severity === "CRITICAL" || a.severity === "HIGH").length;

  const queue = [
    { key: "overtime", label: "Overtime requests", count: overtime.data?.total, href: "/app/attendance/overtime", show: canOvertime },
    { key: "plans", label: "Production plans", count: plans.data?.total, href: "/app/production/plans", show: canProduction },
    { key: "docs", label: "Documents to review", count: docs.data?.total, href: "/app/documents/list", show: canDocuments },
    { key: "daily", label: "Daily plans submitted", count: dailyPlans.data?.items?.filter((p) => p.status === "SUBMITTED").length, href: "/app/planning/plans", show: canPlanning }
  ].filter((q) => q.show);
  const pendingTotal = queue.reduce((a, q) => a + (q.count ?? 0), 0);

  const present = kpi(analytics.data, "workforce", "attendance.present");
  const scheduled = kpi(analytics.data, "workforce", "workers.scheduled");
  const attRate = kpi(analytics.data, "workforce", "attendance.rate");
  const late = kpi(analytics.data, "workforce", "attendance.late");

  // ── Floor status from real signals only ──
  const floor = (() => {
    if (!f) return null;
    const reasons: { text: string; tone: KpiTone }[] = [];
    if (red.length) reasons.push({ text: `${red.length} line${red.length > 1 ? "s" : ""} well below target efficiency (${red.map((r) => r.name).join(", ")})`, tone: "bad" });
    if (realProblem.length) reasons.push({ text: `${realProblem.length} machine${realProblem.length > 1 ? "s" : ""} in error or offline`, tone: "bad" });
    if (criticalReal) reasons.push({ text: `${criticalReal} high-severity alert${criticalReal > 1 ? "s" : ""} open`, tone: "bad" });
    if (amber.length) reasons.push({ text: `${amber.length} line${amber.length > 1 ? "s" : ""} slightly below target`, tone: "warn" });
    if (pendingTotal) reasons.push({ text: `${pendingTotal} item${pendingTotal > 1 ? "s" : ""} waiting for approval`, tone: "warn" });
    const level: FloorLevel = red.length || realProblem.length || criticalReal ? "action" : amber.length || pendingTotal ? "watch" : "normal";
    return { level, reasons };
  })();
  const level = floor ? FLOOR_LEVEL[floor.level] : null;
  const LevelIcon = level?.icon;

  function refreshAll() {
    for (const r of [ctx, summary, week, targets, analytics, machines, alerts, overtime, plans, docs, dailyPlans]) void r.reload();
  }

  const factoryName = ctx.data ? `${ctx.data.factoryName}` : factories?.find((x) => x.id === f)?.name ?? "Factory overview";
  const dateLabel = new Date(`${today}T00:00:00`).toLocaleDateString(locale === "bn" ? "bn-BD" : "en-GB", { weekday: "long", day: "numeric", month: "long", year: "numeric" });
  const periodText = period === "week" ? `${range.from} – ${range.to}` : range.from;

  return (
    <div className="page" data-tour="overview">
      {/* ── Header ── */}
      <header className="surface overflow-hidden">
        <div className="grid gap-5 p-4 md:p-6 lg:grid-cols-[minmax(0,1fr)_minmax(300px,380px)] lg:items-center">
          <div className="min-w-0">
            <p className="eyebrow">{dateLabel}</p>
            <h1 className="mt-1 text-[26px] leading-[32px] md:text-[30px] md:leading-[36px] font-semibold tracking-tight text-fg-primary" data-testid="overview-factory">
              {factoryName}
            </h1>
            <p className="mt-1 text-body text-fg-secondary">Production, people, machines, alerts and approvals — from this factory&apos;s records.</p>
            <div className="mt-3 flex flex-wrap items-center gap-2">
              <Badge tone="success" dot testId="overview-data-source">Live database records</Badge>
              {(factories?.length ?? 0) > 1 && (
                <select className="control !h-8 !py-0 text-caption" value={f ?? ""} onChange={(e) => setFactoryId(e.target.value)} aria-label="Factory" data-testid="overview-factory-select">
                  {factories!.map((x) => <option key={x.id} value={x.id}>{x.code} · {x.name}</option>)}
                </select>
              )}
              <div className="inline-flex rounded-[var(--radius-control)] border border-border-subtle p-0.5" role="group" aria-label="Period">
                {(Object.keys(PERIOD_LABEL) as Period[]).map((p) => (
                  <button key={p} type="button" onClick={() => setPeriod(p)} data-testid={`period-${p}`}
                    className={cn("px-2.5 py-1 text-caption rounded-[calc(var(--radius-control)-2px)]", period === p ? "bg-[var(--accent-soft)] text-fg-primary font-medium" : "text-fg-secondary")}>
                    {PERIOD_LABEL[p]}
                  </button>
                ))}
              </div>
              <span className="flex-1" />
              <Button variant="secondary" size="sm" onClick={refreshAll} aria-label="Refresh overview"><RefreshCw size={13} /> Refresh</Button>
            </div>
          </div>
          <div className={cn("rounded-[var(--radius-card)] border p-4", level ? level.box : "border-border-subtle bg-surface-2")} data-testid="floor-status" data-level={floor?.level ?? "loading"}>
            <p className="eyebrow !text-[10.5px]">Floor status · {PERIOD_LABEL[period].toLowerCase()}</p>
            {!floor || !level || !LevelIcon ? (
              <div className="mt-2 space-y-2"><Skeleton className="h-7 w-40" /><Skeleton className="h-4 w-56" /></div>
            ) : (
              <>
                <p className={cn("mt-1 flex items-center gap-2 text-[22px] leading-[28px] font-semibold tracking-tight", level.text)}><LevelIcon size={22} aria-hidden /> {level.label}</p>
                {floor.reasons.length ? (
                  <ul className="mt-2 space-y-1">
                    {floor.reasons.map((r) => (
                      <li key={r.text} className="flex items-center gap-2 text-caption text-fg-primary">
                        <span className={cn("size-1.5 rounded-full shrink-0", r.tone === "bad" ? "bg-[var(--risk-high)]" : "bg-[var(--risk-medium)]")} aria-hidden />{r.text}
                      </li>
                    ))}
                  </ul>
                ) : <p className="mt-1.5 text-caption text-fg-secondary">No line below target, no machine fault, no open high-severity alert.</p>}
              </>
            )}
          </div>
        </div>
      </header>

      {!canPlanning && (
        <Callout tone="info" className="mt-4" title="Limited view">Your role doesn&apos;t include planning, so line targets and the factory&apos;s line list are not shown.</Callout>
      )}

      {/* ── KPI strip ── */}
      <KpiStrip className="mt-4">
        <KpiCell label={`Output · ${PERIOD_LABEL[period].toLowerCase()}`} value={canProduction ? fmt(actual) : "—"} unit={targetTotal ? `of ${fmt(targetTotal)} target` : "pieces"}
          tone={!hasOutput ? "neutral" : red.length ? "bad" : amber.length ? "warn" : "good"}
          visual={rows.length ? <SegmentBar label="One segment per line" segments={rows.map((r) => ({ key: r.id, tone: r.tone, title: `${r.name}: ${fmt(r.actual)} pcs${r.eff != null ? ` · ${r.eff}% eff.` : ""}` }))} /> : undefined}
          detail={!canProduction ? "No production access" : hasOutput ? `Mean efficiency ${meanEff ?? "—"}% · downtime ${fmt(downtime)} min` : `No output recorded for ${periodText} yet`}
          href="/app/production" loading={summary.loading} error={summary.error && !summary.data ? "Unavailable" : null} testId="kpi-output" />
        <KpiCell label="Attendance" value={present?.value != null ? fmt(present.value) : "—"} unit={scheduled?.value != null ? `of ${fmt(scheduled.value)} scheduled` : undefined}
          tone={attRate?.value == null ? "neutral" : attRate.value >= 95 ? "good" : attRate.value >= 90 ? "warn" : "bad"}
          detail={attRate?.value != null ? `${attRate.value}% attendance · ${fmt(late?.value)} late` : present?.availability === "NO_DATA" ? `No punches for ${periodText} yet` : undefined}
          href="/app/attendance" loading={analytics.loading} error={analytics.error && !analytics.data ? "Unavailable" : null} testId="kpi-attendance" />
        <KpiCell label="Machines monitored" value={canMachines ? fmt(monitored.length) : "—"} unit={machineList.length ? `of ${machineList.length}` : undefined}
          tone={realProblem.length ? "bad" : monitored.length ? "good" : "neutral"}
          visual={monitored.length ? <StackBar label="Machine states" parts={[{ key: "run", value: byState("RUNNING"), tone: "good" }, { key: "idle", value: byState("IDLE") + byState("STOPPED"), tone: "warn" },
            { key: "bad", value: byState("ERROR") + byState("OFFLINE"), tone: "bad" }]} /> : undefined}
          detail={monitored.length ? `${byState("RUNNING")} running · ${byState("ERROR") + byState("OFFLINE")} error/offline${monitored.some((m) => m.isSimulated) ? " · includes simulated sensors" : ""}` : "No machine has a sensor yet"}
          href="/app/monitoring/machines" loading={machines.loading} error={machines.error && !machines.data ? "Unavailable" : null} testId="kpi-machines" />
        <KpiCell label="Waiting for approval" value={fmt(pendingTotal)} tone={pendingTotal ? "warn" : "good"}
          detail={queue.filter((q) => q.count).map((q) => `${q.count} ${q.label.toLowerCase()}`).join(" · ") || "Nothing waiting"} testId="kpi-approvals" />
        <KpiCell label="Open alerts" value={canAlerts ? fmt(alerts.data?.total ?? 0) : "—"} tone={criticalReal ? "bad" : (alerts.data?.total ?? 0) ? "warn" : "good"}
          detail={alerts.data ? `${realAlerts.length} real · ${alertList.length - realAlerts.length} simulated (latest ${alertList.length})` : undefined}
          href="/app/monitoring/alerts" loading={alerts.loading} error={alerts.error && !alerts.data ? "Unavailable" : null} testId="kpi-alerts" className="col-span-2 md:col-span-1" />
      </KpiStrip>

      {/* ── Lines + approvals ── */}
      <div className="mt-4 grid grid-cols-1 xl:grid-cols-12 gap-4">
        <section className="surface xl:col-span-8 min-w-0 overflow-hidden" data-testid="overview-lines">
          <header className="flex flex-wrap items-center justify-between gap-2 px-4 py-3 border-b border-border-subtle">
            <h2 className="text-body font-semibold flex items-center gap-2"><Factory size={14} aria-hidden /> Lines · {PERIOD_LABEL[period].toLowerCase()}</h2>
            <Link href="/app/production" className="text-caption text-[var(--accent)] hover:underline">Production →</Link>
          </header>
          {summary.loading || ctx.loading ? <div className="p-4 space-y-2"><Skeleton className="h-8" /><Skeleton className="h-8" /><Skeleton className="h-8" /></div>
            : !canProduction ? <p className="p-4 text-caption text-fg-tertiary">Your role doesn&apos;t include production data.</p>
            : rows.length === 0 ? <p className="p-4 text-caption text-fg-tertiary">No production lines in this factory yet.</p>
            : (
              <div className="overflow-x-auto">
                <table className="w-full text-caption">
                  <thead className="text-fg-tertiary text-left">
                    <tr className="border-b border-border-subtle">
                      <th className="px-4 py-2 font-medium">Line</th><th className="px-2 py-2 font-medium text-right">Output</th><th className="px-2 py-2 font-medium text-right">Target</th>
                      <th className="px-2 py-2 font-medium text-right">Achieved</th><th className="px-2 py-2 font-medium text-right">Efficiency</th>
                      <th className="px-2 py-2 font-medium text-right">Downtime</th><th className="px-2 py-2 font-medium text-right">Rework / reject</th>
                    </tr>
                  </thead>
                  <tbody>
                    {rows.map((r) => (
                      <tr key={r.id} className="border-b border-border-subtle last:border-0" data-testid="overview-line-row">
                        <td className="px-4 py-2">
                          <span className="inline-flex items-center gap-2">
                            <span className={cn("size-2 rounded-full", r.tone === "bad" ? "bg-[var(--risk-high)]" : r.tone === "warn" ? "bg-[var(--risk-medium)]" : r.tone === "good" ? "bg-[var(--risk-low)]" : "bg-border-strong")} aria-hidden />
                            <Link href={`/app/production/lines/${r.id}`} className="font-medium text-fg-primary hover:underline">{r.name}</Link>
                          </span>
                        </td>
                        <td className="px-2 py-2 text-right tabular-nums">{r.records ? fmt(r.actual) : <span className="text-fg-tertiary">no output</span>}</td>
                        <td className="px-2 py-2 text-right tabular-nums">{fmt(r.target)}</td>
                        <td className="px-2 py-2 text-right tabular-nums">{r.achievement != null && r.records ? `${r.achievement}%` : "—"}</td>
                        <td className="px-2 py-2 text-right tabular-nums">{r.eff != null && r.records ? `${r.eff}%` : "—"}{r.effTarget != null && <span className="text-fg-tertiary"> / {r.effTarget}%</span>}</td>
                        <td className="px-2 py-2 text-right tabular-nums">{r.downtime ? `${fmt(r.downtime)} min` : "—"}</td>
                        <td className="px-2 py-2 text-right tabular-nums">{r.records ? `${fmt(r.rework)} / ${fmt(r.reject)}` : "—"}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
                {!hasOutput && <p className="px-4 py-2 text-caption text-fg-tertiary">No output has been recorded for {periodText} yet{period === "today" ? " — the shift may not have started. Try Yesterday." : "."}</p>}
              </div>
            )}
          <WeekTrend week={week.data} today={today} />
        </section>

        <section className="surface xl:col-span-4 min-w-0" data-testid="overview-approvals">
          <header className="flex items-center justify-between gap-2 px-4 py-3 border-b border-border-subtle">
            <h2 className="text-body font-semibold flex items-center gap-2"><ClipboardCheck size={14} aria-hidden /> Waiting for approval</h2>
          </header>
          <ul className="divide-y divide-border-subtle">
            {queue.map((q) => (
              <li key={q.key}>
                <Link href={q.href} className="flex items-center justify-between gap-3 px-4 py-3 hover:bg-bg-subtle">
                  <span className="text-body text-fg-primary">{q.label}</span>
                  <Badge tone={q.count ? "warning" : "neutral"}>{q.count == null ? "…" : q.count}</Badge>
                </Link>
              </li>
            ))}
            {queue.length === 0 && <li className="px-4 py-3 text-caption text-fg-tertiary">No approval queues in your role.</li>}
          </ul>
          <p className="px-4 pb-3 text-caption text-fg-tertiary">The person who created a request can&apos;t approve it; another manager decides.</p>
        </section>
      </div>

      {/* ── Alerts, machines, quality ── */}
      <div className="mt-4 grid grid-cols-1 lg:grid-cols-2 xl:grid-cols-12 gap-4">
        <section className="surface xl:col-span-5 min-w-0" data-testid="overview-alerts">
          <header className="flex items-center justify-between gap-2 px-4 py-3 border-b border-border-subtle">
            <h2 className="text-body font-semibold flex items-center gap-2"><AlertTriangle size={14} aria-hidden /> Open alerts</h2>
            <Link href="/app/monitoring/alerts" className="text-caption text-[var(--accent)] hover:underline">All alerts →</Link>
          </header>
          {!canAlerts ? <p className="p-4 text-caption text-fg-tertiary">Your role doesn&apos;t include monitoring.</p>
            : alerts.loading ? <div className="p-4 space-y-2"><Skeleton className="h-8" /><Skeleton className="h-8" /></div>
            : alertList.length === 0 ? <p className="p-4 text-caption text-fg-tertiary">No open alerts.</p>
            : (
              <ul className="divide-y divide-border-subtle">
                {alertList.map((a) => (
                  <li key={a.id}>
                    <Link href={`/app/monitoring/alerts/${a.id}`} className="flex items-start gap-2 px-4 py-2.5 hover:bg-bg-subtle">
                      <Badge tone={SEVERITY_TONE[a.severity] ?? "neutral"}>{a.severity}</Badge>
                      <span className="min-w-0 flex-1 text-caption text-fg-primary">{a.title}</span>
                      {a.sourceType === "SIMULATED" && <Badge tone="accent">Simulated</Badge>}
                    </Link>
                  </li>
                ))}
              </ul>
            )}
        </section>

        <section className="surface xl:col-span-4 min-w-0" data-testid="overview-machines">
          <header className="flex items-center justify-between gap-2 px-4 py-3 border-b border-border-subtle">
            <h2 className="text-body font-semibold flex items-center gap-2"><Cpu size={14} aria-hidden /> Machines</h2>
            <Link href="/app/monitoring/machines" className="text-caption text-[var(--accent)] hover:underline">Machine monitoring →</Link>
          </header>
          {!canMachines ? <p className="p-4 text-caption text-fg-tertiary">Your role doesn&apos;t include machines.</p>
            : machines.loading ? <div className="p-4"><Skeleton className="h-16" /></div>
            : (
              <div className="p-4 space-y-3">
                <dl className="grid grid-cols-3 gap-2 text-center">
                  {["RUNNING", "IDLE", "STOPPED", "ERROR", "OFFLINE", "MAINTENANCE"].map((s) => (
                    <div key={s} className="rounded-[var(--radius-control)] border border-border-subtle py-2">
                      <dt className="text-[10.5px] uppercase tracking-wide text-fg-tertiary">{s.toLowerCase()}</dt>
                      <dd className={cn("text-body font-semibold", byState(s) ? (STATE_TONE[s] === "bad" ? "text-[var(--risk-high)]" : STATE_TONE[s] === "warn" ? "text-[var(--risk-medium)]" : "text-fg-primary") : "text-fg-tertiary")}>{byState(s)}</dd>
                    </div>
                  ))}
                </dl>
                <p className="text-caption text-fg-tertiary">
                  {monitored.length} of {machineList.length} machines have sensors{monitored.some((m) => m.isSimulated) ? `; ${monitored.filter((m) => m.isSimulated).length} use simulated sensors` : ""}.
                  {machineList.length - monitored.length > 0 ? ` ${machineList.length - monitored.length} have no sensor, so their state is unknown.` : ""}
                </p>
              </div>
            )}
        </section>

        <section className="surface lg:col-span-2 xl:col-span-3 min-w-0" data-testid="overview-quality">
          <header className="flex items-center justify-between gap-2 px-4 py-3 border-b border-border-subtle">
            <h2 className="text-body font-semibold flex items-center gap-2"><Users size={14} aria-hidden /> Quality · {PERIOD_LABEL[period].toLowerCase()}</h2>
          </header>
          {!canProduction ? <p className="p-4 text-caption text-fg-tertiary">Your role doesn&apos;t include production data.</p> : (
            <dl className="p-4 grid grid-cols-2 gap-3 text-caption">
              <div><dt className="text-fg-tertiary">Rework</dt><dd className="text-body font-semibold">{hasOutput ? `${fmt(rework)} · ${pct(rework, actual)}%` : "—"}</dd></div>
              <div><dt className="text-fg-tertiary">Reject</dt><dd className="text-body font-semibold">{hasOutput ? `${fmt(reject)} · ${pct(reject, actual)}%` : "—"}</dd></div>
              <div className="col-span-2 text-fg-tertiary">From the line output records. Inspections, AQL and CAPA are in Quality and Documents.</div>
              <Link href="/app/qc" className="col-span-2 text-[var(--accent)] hover:underline">Quality control →</Link>
            </dl>
          )}
        </section>
      </div>

      {[summary, analytics, machines, alerts].some((r) => r.error && !r.data) && (
        <Callout tone="warning" className="mt-4" title="Some figures could not be loaded">
          {[summary, analytics, machines, alerts].filter((r) => r.error && !r.data).map((r) => errorMessage(r.error)).join(" · ")}
        </Callout>
      )}
    </div>
  );
}

/** Daily output for the last 7 days (Fridays shown as the weekly holiday). */
function WeekTrend({ week, today }: { week: ProductionSummary | null; today: string }) {
  if (!week) return null;
  const days = Array.from({ length: 7 }, (_, i) => addDays(today, i - 6));
  const map = new Map(week.daily.map((d) => [d.date, d.actual]));
  const max = Math.max(1, ...days.map((d) => map.get(d) ?? 0));
  return (
    <div className="px-4 py-3 border-t border-border-subtle" data-testid="overview-week">
      <p className="text-caption text-fg-tertiary mb-2">Daily output, last 7 days (all lines)</p>
      <div className="flex items-end gap-2 h-20">
        {days.map((d) => {
          const v = map.get(d) ?? 0;
          return (
            <div key={d} className="flex-1 flex flex-col items-center gap-1 min-w-0" title={`${d}: ${v.toLocaleString("en-US")} pcs`}>
              <span className="text-[10px] tabular-nums text-fg-tertiary">{v ? v.toLocaleString("en-US") : isFriday(d) ? "off" : "0"}</span>
              <div className={cn("w-full rounded-sm", v ? "bg-[var(--accent)]" : "bg-border-subtle")} style={{ height: `${Math.max(2, (v / max) * 48)}px` }} />
              <span className="text-[10px] text-fg-tertiary">{new Date(`${d}T00:00:00`).toLocaleDateString("en-GB", { weekday: "short" })}</span>
            </div>
          );
        })}
      </div>
    </div>
  );
}
