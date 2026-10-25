"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { CheckCircle2, FlaskConical, OctagonAlert, RefreshCw, StepForward, TriangleAlert } from "lucide-react";
import { useT } from "@/lib/useT";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Skeleton } from "@/components/ui/Skeleton";
import { useToast } from "@/components/ui/Toast";
import { KpiCell, KpiStrip, SegmentBar, StackBar, type KpiTone } from "@/components/overview/KpiStrip";
import { LineBoardPanel } from "@/components/overview/LineBoardPanel";
import { BriefCard } from "@/components/overview/BriefCard";
import { DecisionsPanel } from "@/components/overview/DecisionsPanel";
import { FloorAlertsCard } from "@/components/overview/FloorAlertsCard";
import { MachineHealthPanel } from "@/components/overview/MachineHealthPanel";
import { QcSummary } from "@/components/overview/QcSummary";
import { refreshPendingApprovals, usePendingApprovals } from "@/services/approvals.api";
import { isSeedAlert, setUnreadFrom } from "@/services/floorAlerts.api";
import { useBusinessStore } from "@/store/business.store";
import { useAppStore } from "@/store/app.store";
import { useFactoryBrainLiveStore } from "@/store/factoryBrain.live.store";
import { formatRelative } from "@/lib/format";
import { useMounted } from "@/lib/persist";
import { apiJson, errorMessage } from "@/lib/api";
import { useResource, announceDataChanged } from "@/lib/useResource";
import { canOperate, useAuthStore } from "@/lib/auth";
import { cn } from "@/lib/cn";
import type { FloorAlertList, LineBoard, LineBoardRow, SensorSnapshot } from "@/services/contracts";
import type { SensorReading } from "@/data/sensors";

/** How often the read-only sensor snapshot is polled. Polling never advances the simulation. */
const SNAPSHOT_POLL_MS = 6000;

const RISK_RANK = { high: 0, medium: 1, low: 2 } as const;

type FloorLevel = "action" | "watch" | "normal";

const FLOOR_LEVEL: Record<FloorLevel, { label: string; icon: typeof CheckCircle2; box: string; text: string }> = {
  action: { label: "Action needed", icon: OctagonAlert, box: "border-[var(--risk-high-border)] bg-[var(--risk-high-soft)]", text: "text-[var(--risk-high)]" },
  watch: { label: "Watch", icon: TriangleAlert, box: "border-[var(--risk-medium-border)] bg-[var(--risk-medium-soft)]", text: "text-[var(--risk-medium)]" },
  normal: { label: "Running normally", icon: CheckCircle2, box: "border-[var(--risk-low-border)] bg-[var(--risk-low-soft)]", text: "text-[var(--risk-low)]" }
};

/** Tone of one line in the KPI segment bar: the board flag, or green when at/above target. */
function lineTone(r: LineBoardRow): KpiTone {
  if (r.efficiencyPct >= r.sahTarget) return "good";
  return r.bottleneck === "red" ? "bad" : r.bottleneck === "amber" ? "warn" : "neutral";
}

const plural = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`;

export default function OverviewPage() {
  const { t, locale } = useT();
  const toast = useToast();
  const mounted = useMounted();
  const user = useAuthStore((s) => s.user);
  const operator = canOperate(user);
  const profile = useBusinessStore((s) => s.profile);
  const plantName = mounted ? profile.businessName || profile.factoryName || "" : "";

  const snapshot = useResource<SensorSnapshot>("/api/sensors/snapshot", { pollMs: SNAPSHOT_POLL_MS });
  const board = useResource<LineBoard>("/api/line-board");
  const alerts = useResource<FloorAlertList>("/api/floor-alerts", { pollMs: 15_000 });
  const pending = usePendingApprovals((s) => s.pending);
  const pendingCount = usePendingApprovals((s) => s.pendingCount);
  const pendingLoaded = usePendingApprovals((s) => s.loaded);
  const pendingError = usePendingApprovals((s) => s.error);
  const [advancing, setAdvancing] = useState<null | "sensors" | "board">(null);

  // Publish the snapshot for other views (QC page refreshes on tick changes).
  const setLive = useFactoryBrainLiveStore((s) => s.setSnapshot);
  const tick = snapshot.data?.tick;
  useEffect(() => {
    const d = snapshot.data;
    if (!d) return;
    setLive({
      tick: d.tick,
      lines: d.lines,
      machines: d.machines as never,
      readings: d.readings as unknown as SensorReading[],
      appliedAt: new Date().toISOString()
    });
  }, [snapshot.data, setLive]);

  // When the simulation moves, the board moves with it.
  const lastTick = useRef<number | undefined>(undefined);
  useEffect(() => {
    if (tick === undefined) return;
    if (lastTick.current !== undefined && lastTick.current !== tick) void board.reload();
    lastTick.current = tick;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tick]);

  useEffect(() => {
    if (alerts.data) setUnreadFrom(alerts.data);
  }, [alerts.data]);

  useEffect(() => {
    void refreshPendingApprovals();
  }, []);

  // First arrival after onboarding: lets the guided tour start.
  useEffect(() => {
    if (!mounted) return;
    if (!useAppStore.getState().onboardedAt && useBusinessStore.getState().onboardingComplete) {
      useAppStore.getState().markOnboarded();
    }
  }, [mounted]);

  // ── Figures, all counted from API responses ────────────────────────────
  const rows = board.data?.rows ?? [];
  const below = rows.filter((r) => r.efficiencyPct < r.sahTarget);
  const red = rows.filter((r) => r.bottleneck === "red");
  const amber = rows.filter((r) => r.bottleneck === "amber");
  const effs = rows.map((r) => r.efficiencyPct);
  const meanEff = rows.length ? Math.round(effs.reduce((a, b) => a + b, 0) / rows.length) : null;

  const machines = snapshot.data?.machines ?? [];
  const down = machines.filter((m) => m.status === "down").length;
  const atRisk = machines.filter((m) => m.status === "at_risk").length;
  const healthy = machines.filter((m) => m.status === "healthy").length;

  const alertList = alerts.data?.alerts ?? [];
  const unreadAlerts = alertList.filter((a) => !a.read);
  const unreadCritical = unreadAlerts.filter((a) => a.severity === "critical").length;
  const unreadSeed = unreadAlerts.filter(isSeedAlert).length;

  const topRisk = pending.length ? pending.slice().sort((a, b) => RISK_RANK[a.riskTier] - RISK_RANK[b.riskTier])[0].riskTier : null;
  const simulated = snapshot.data?.simulated ?? board.data?.meta.simulated ?? null;

  // Floor status: worst signal across the board flags and machine states;
  // only decided once both sources have answered.
  const floor = (() => {
    if (!board.data || !snapshot.data) return null;
    const reasons: { text: string; tone: KpiTone }[] = [];
    if (red.length) reasons.push({ text: `${plural(red.length, "line")} flagged critical (${red.map((r) => r.lineId).join(", ")})`, tone: "bad" });
    if (down) reasons.push({ text: `${plural(down, "machine")} down`, tone: "bad" });
    if (amber.length) reasons.push({ text: `${plural(amber.length, "line")} on watch`, tone: "warn" });
    if (atRisk) reasons.push({ text: `${plural(atRisk, "machine")} at risk`, tone: "warn" });
    if (pendingLoaded && pendingCount) reasons.push({ text: `${plural(pendingCount, "decision")} waiting`, tone: "warn" });
    const level: FloorLevel = red.length || down ? "action" : amber.length || atRisk ? "watch" : "normal";
    return { level, reasons };
  })();

  async function advanceSensors() {
    if (advancing) return;
    setAdvancing("sensors");
    try {
      const next = await apiJson<SensorSnapshot>("/api/sensors/ingest", { method: "POST", body: {} });
      snapshot.setData(next);
      toast.push({ title: "Simulation advanced", description: `Now at tick #${next.tick}.` });
      announceDataChanged();
    } catch (e) {
      toast.push({ tone: "error", title: "Could not advance the simulation", description: errorMessage(e) });
    } finally {
      setAdvancing(null);
    }
  }

  async function advanceBoard() {
    if (advancing) return;
    setAdvancing("board");
    try {
      const next = await apiJson<LineBoard>("/api/line-board/refresh", { method: "POST", body: {} });
      board.setData(next);
      toast.push({ title: "Line board advanced", description: `Tick #${next.meta.tick}.` });
    } catch (e) {
      toast.push({ tone: "error", title: "Could not advance the line board", description: errorMessage(e) });
    } finally {
      setAdvancing(null);
    }
  }

  function refreshAll() {
    void snapshot.reload();
    void board.reload();
    void alerts.reload();
    void refreshPendingApprovals();
  }

  const greeting = useMemo(() => {
    const h = new Date().getHours();
    const key = h < 12 ? "overview.greetingMorning" : h < 18 ? "overview.greetingAfternoon" : "overview.greetingEvening";
    return t(key, { name: plantName || "" }).replace(/,\s*$/, "").replace(/\s+—\s*$/, "");
  }, [t, plantName]);

  const today = new Date().toLocaleDateString(locale === "bn" ? "bn-BD" : "en-GB", { weekday: "long", day: "numeric", month: "long", year: "numeric" });
  const level = floor ? FLOOR_LEVEL[floor.level] : null;
  const LevelIcon = level?.icon;

  return (
    <div className="page" data-tour="overview">
      {/* ── Shift status header ─────────────────────────────────────────── */}
      <header className="surface overflow-hidden">
        {simulated && (
          <div className="flex items-start gap-2 px-4 md:px-6 py-2 border-b border-[var(--accent-border)] bg-[var(--accent-soft)] text-caption text-fg-primary" data-testid="overview-sim-banner">
            <FlaskConical size={14} className="mt-px shrink-0 text-accent" aria-hidden />
            <p>
              <span className="font-semibold">Simulated plant data.</span>{" "}
              <span className="text-fg-secondary">Lines, machines and QC come from the server&apos;s simulator and demo seed — {snapshot.data?.source ?? board.data?.meta.source}.</span>
            </p>
          </div>
        )}
        <div className="grid gap-5 p-4 md:p-6 lg:grid-cols-[minmax(0,1fr)_minmax(300px,380px)] lg:items-center">
          <div className="min-w-0">
            <p className="eyebrow">{today}</p>
            <h1 className="mt-1 text-[26px] leading-[32px] md:text-[30px] md:leading-[36px] font-semibold tracking-tight text-fg-primary">
              {plantName || "Floor overview"}
            </h1>
            <p className="mt-1 text-body text-fg-secondary">{plantName ? greeting : "Shift status from the line board, sensor snapshot, approval queue and floor alerts."}</p>
            <div className="mt-3 flex flex-wrap items-center gap-2">
              {simulated !== null && (
                <span title={snapshot.data?.source ?? board.data?.meta.source} data-testid="overview-data-source">
                  <Badge tone={simulated ? "accent" : "success"} dot>{simulated ? "Simulated floor data" : "Live gateway data"}</Badge>
                </span>
              )}
              {snapshot.data && (
                <span className="text-caption text-fg-tertiary inline-flex items-center gap-1.5">
                  <span className="size-1.5 rounded-full bg-[var(--risk-low)] live-dot" aria-hidden />
                  Sensor tick #{snapshot.data.tick}{snapshot.updatedAt ? ` · polled ${formatRelative(snapshot.updatedAt.toISOString(), locale)}` : ""}
                </span>
              )}
              <span className="flex-1" />
              {operator && (
                <Button variant="secondary" size="sm" onClick={() => void advanceSensors()} disabled={advancing !== null} data-testid="simulate-tick" title="Advance the sensor simulation one tick (Operator)">
                  <StepForward size={13} /> {advancing === "sensors" ? "Advancing…" : t("overview.demoPathSimulate")}
                </Button>
              )}
              <Button variant="secondary" size="sm" onClick={refreshAll} disabled={snapshot.refreshing} aria-label="Refresh overview">
                <RefreshCw size={13} className={cn(snapshot.refreshing && "animate-spin")} /> Refresh
              </Button>
            </div>
          </div>

          <div className={cn("rounded-[var(--radius-card)] border p-4", level ? level.box : "border-border-subtle bg-surface-2")} data-testid="floor-status" data-level={floor?.level ?? "loading"}>
            <p className="eyebrow !text-[10.5px]">Floor status</p>
            {!floor || !level || !LevelIcon ? (
              board.error || snapshot.error ? (
                <p className="mt-1.5 text-body text-[var(--risk-high)]">Unavailable — line board or sensor data did not load.</p>
              ) : (
                <div className="mt-2 space-y-2"><Skeleton className="h-7 w-40" /><Skeleton className="h-4 w-56" /></div>
              )
            ) : (
              <>
                <p className={cn("mt-1 flex items-center gap-2 text-[22px] leading-[28px] font-semibold tracking-tight", level.text)}>
                  <LevelIcon size={22} aria-hidden /> {level.label}
                </p>
                {floor.reasons.length ? (
                  <ul className="mt-2 space-y-1">
                    {floor.reasons.map((r) => (
                      <li key={r.text} className="flex items-center gap-2 text-caption text-fg-primary">
                        <span className={cn("size-1.5 rounded-full shrink-0", r.tone === "bad" ? "bg-[var(--risk-high)]" : "bg-[var(--risk-medium)]")} aria-hidden />
                        {r.text}
                      </li>
                    ))}
                  </ul>
                ) : (
                  <p className="mt-1.5 text-caption text-fg-secondary">No line flags, no machines down or at risk.</p>
                )}
              </>
            )}
          </div>
        </div>
      </header>

      {snapshot.error && !snapshot.data && (
        <Callout tone="danger" className="mt-4" title="Sensor data unavailable">
          {errorMessage(snapshot.error)}
        </Callout>
      )}

      {/* ── KPI strip ───────────────────────────────────────────────────── */}
      <KpiStrip className="mt-4">
        <KpiCell
          label="Lines below target"
          value={rows.length ? below.length : "—"}
          unit={rows.length ? `of ${rows.length}` : undefined}
          tone={red.length ? "bad" : below.length ? "warn" : "good"}
          visual={rows.length ? <SegmentBar label="One segment per line" segments={rows.map((r) => ({ key: r.lineId, tone: lineTone(r), title: `${r.name}: ${r.efficiencyPct}% vs ${r.sahTarget}%` }))} /> : undefined}
          detail={rows.length ? `Board flags: ${red.length} critical · ${amber.length} watch` : undefined}
          loading={board.loading}
          error={board.error && !board.data ? "Unavailable" : null}
          testId="kpi-lines-below"
        />
        <KpiCell
          label="Mean line efficiency"
          value={meanEff === null ? "—" : `${meanEff}%`}
          detail={rows.length ? `Unweighted mean of ${rows.length} lines · range ${Math.min(...effs)}–${Math.max(...effs)}%` : undefined}
          loading={board.loading}
          error={board.error && !board.data ? "Unavailable" : null}
          testId="kpi-efficiency"
        />
        <KpiCell
          label="Machines down"
          value={machines.length ? down : "—"}
          unit={machines.length ? `of ${machines.length}` : undefined}
          tone={down ? "bad" : atRisk ? "warn" : "good"}
          visual={machines.length ? <StackBar label={`${down} down, ${atRisk} at risk, ${healthy} healthy`} parts={[{ key: "down", value: down, tone: "bad" }, { key: "risk", value: atRisk, tone: "warn" }, { key: "ok", value: healthy, tone: "good" }]} /> : undefined}
          detail={machines.length ? `${atRisk} at risk · ${healthy} healthy` : undefined}
          href="/app/sensors"
          loading={snapshot.loading}
          error={snapshot.error && !snapshot.data ? "Unavailable" : null}
          testId="kpi-machines"
        />
        <KpiCell
          label="Pending decisions"
          value={pendingCount}
          tone={topRisk === "high" ? "bad" : pendingCount ? "warn" : "good"}
          detail={topRisk ? `Highest risk: ${t(`risk.${topRisk}`)}` : "Approval queue is clear"}
          href="/app/approvals"
          loading={!pendingLoaded}
          error={pendingError && pending.length === 0 ? "Unavailable" : null}
          testId="kpi-approvals"
        />
        <KpiCell
          label="Unread floor alerts"
          value={alerts.data ? unreadAlerts.length : "—"}
          unit={alerts.data ? `of ${alerts.data.count}` : undefined}
          tone={unreadCritical ? "bad" : unreadAlerts.length ? "warn" : "good"}
          detail={alerts.data ? `${unreadCritical} critical${unreadSeed ? ` · ${unreadSeed} from demo seed` : ""}` : undefined}
          href="/app/activity"
          loading={alerts.loading}
          error={alerts.error && !alerts.data ? "Unavailable" : null}
          testId="kpi-alerts"
          className="col-span-2 md:col-span-1"
        />
      </KpiStrip>

      {/* ── Lines + brief / decisions ───────────────────────────────────── */}
      <div className="mt-4 grid grid-cols-1 xl:grid-cols-12 gap-4">
        <div className="xl:col-span-8 min-w-0">
          <LineBoardPanel board={board} canAdvance={operator} advancing={advancing === "board"} onAdvance={() => void advanceBoard()} />
        </div>
        <div className="xl:col-span-4 min-w-0 grid grid-cols-1 lg:grid-cols-2 xl:grid-cols-1 gap-4 content-start">
          <BriefCard tick={tick} />
          <DecisionsPanel />
        </div>
      </div>

      {/* ── Alerts, machines, quality ───────────────────────────────────── */}
      <div className="mt-4 grid grid-cols-1 lg:grid-cols-2 xl:grid-cols-12 gap-4">
        <div className="xl:col-span-4 min-w-0"><FloorAlertsCard alerts={alerts} /></div>
        <div className="xl:col-span-5 min-w-0"><MachineHealthPanel snapshot={snapshot} /></div>
        <div className="lg:col-span-2 xl:col-span-3 min-w-0"><QcSummary /></div>
      </div>
    </div>
  );
}
