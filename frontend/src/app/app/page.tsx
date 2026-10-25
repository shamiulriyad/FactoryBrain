"use client";

import Link from "next/link";
import { useEffect, useMemo, useRef, useState } from "react";
import { Activity, ArrowUpRight, Bell, Cpu, Gauge, RefreshCw, ShieldCheck, StepForward } from "lucide-react";
import { useT } from "@/lib/useT";
import { Panel } from "@/components/ui/Panel";
import { PageHeader } from "@/components/ui/PageHeader";
import { StatCard } from "@/components/ui/StatCard";
import { Badge, RISK_TONE } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Skeleton } from "@/components/ui/Skeleton";
import { useToast } from "@/components/ui/Toast";
import { LineBoardPanel } from "@/components/overview/LineBoardPanel";
import { QcSummary } from "@/components/overview/QcSummary";
import { BriefCard } from "@/components/overview/BriefCard";
import { approvalInsight, refreshPendingApprovals, usePendingApprovals } from "@/services/approvals.api";
import { setUnreadFrom } from "@/services/floorAlerts.api";
import { useBusinessStore } from "@/store/business.store";
import { useAppStore } from "@/store/app.store";
import { useFactoryBrainLiveStore } from "@/store/factoryBrain.live.store";
import { formatRelative } from "@/lib/format";
import { useMounted } from "@/lib/persist";
import { apiJson, errorMessage } from "@/lib/api";
import { useResource, announceDataChanged, type Resource } from "@/lib/useResource";
import { canOperate, useAuthStore } from "@/lib/auth";
import { cn } from "@/lib/cn";
import type { FloorAlertList, LineBoard, SensorSnapshot } from "@/services/contracts";
import type { SensorReading } from "@/data/sensors";

/** How often the read-only sensor snapshot is polled. Polling never advances the simulation. */
const SNAPSHOT_POLL_MS = 6000;

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

  const rows = board.data?.rows ?? [];
  const avgEff = rows.length ? Math.round(rows.reduce((a, r) => a + r.efficiencyPct, 0) / rows.length) : null;
  const onTarget = rows.filter((r) => r.efficiencyPct >= r.sahTarget).length;
  const behind = rows.filter((r) => r.bottleneck !== "green");
  const machines = snapshot.data?.machines ?? [];
  const down = machines.filter((m) => m.status === "down").length;
  const atRisk = machines.filter((m) => m.status === "at_risk").length;
  const unread = alerts.data?.alerts.filter((a) => !a.read).length ?? null;
  const simulated = snapshot.data?.simulated ?? board.data?.meta.simulated ?? null;

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

  return (
    <div className="page" data-tour="overview">
      <PageHeader
        eyebrow={new Date().toLocaleDateString(locale === "bn" ? "bn-BD" : "en-GB", { weekday: "long", day: "numeric", month: "long" })}
        title={plantName || "Floor overview"}
        description={plantName ? greeting : "Lines, decisions and alerts, straight from the API."}
        meta={
          simulated === null ? null : (
            <>
              <span title={snapshot.data?.source ?? board.data?.meta.source} data-testid="overview-data-source">
                <Badge tone={simulated ? "accent" : "success"} dot>{simulated ? "Simulated floor data" : "Live gateway data"}</Badge>
              </span>
              {snapshot.data && <span className="text-caption text-fg-tertiary">Tick #{snapshot.data.tick}{snapshot.updatedAt ? ` · polled ${formatRelative(snapshot.updatedAt.toISOString(), locale)}` : ""}</span>}
            </>
          )
        }
        actions={
          <>
            {operator && (
              <Button variant="secondary" size="sm" onClick={() => void advanceSensors()} disabled={advancing !== null} data-testid="simulate-tick" title="Advance the sensor simulation one tick (Operator)">
                <StepForward size={13} /> {advancing === "sensors" ? "Advancing…" : t("overview.demoPathSimulate")}
              </Button>
            )}
            <Button variant="secondary" size="sm" onClick={refreshAll} disabled={snapshot.refreshing} aria-label="Refresh overview">
              <RefreshCw size={13} className={cn(snapshot.refreshing && "animate-spin")} /> Refresh
            </Button>
          </>
        }
      />

      {snapshot.error && !snapshot.data && (
        <Callout tone="danger" className="mt-5" title="Sensor data unavailable">
          {errorMessage(snapshot.error)}
        </Callout>
      )}

      <section className="mt-6 grid grid-cols-2 lg:grid-cols-5 gap-3" aria-label="Key figures" data-tour="overview-kpis">
        <StatCard
          label="Avg line efficiency"
          value={avgEff === null ? "—" : `${avgEff}%`}
          hint={rows.length ? `${onTarget} of ${rows.length} lines at or above target` : undefined}
          tone={avgEff === null ? "neutral" : onTarget === rows.length ? "good" : "warn"}
          loading={board.loading}
          error={board.error && !board.data ? "Unavailable" : null}
          icon={<Gauge size={15} />}
          testId="kpi-efficiency"
        />
        <StatCard
          label="Lines needing attention"
          value={rows.length ? `${behind.length}` : "—"}
          hint={behind.length ? behind.slice(0, 3).map((r) => r.name).join(", ") : rows.length ? "All lines green" : undefined}
          tone={behind.some((r) => r.bottleneck === "red") ? "bad" : behind.length ? "warn" : "good"}
          loading={board.loading}
          error={board.error && !board.data ? "Unavailable" : null}
          icon={<Activity size={15} />}
        />
        <StatCard
          label="Pending approvals"
          value={`${pendingCount}`}
          hint={pendingCount ? "Waiting for a decision" : "Nothing waiting"}
          tone={pendingCount ? "warn" : "good"}
          loading={!pendingLoaded}
          href="/app/approvals"
          icon={<ShieldCheck size={15} />}
          testId="kpi-approvals"
        />
        <StatCard
          label="Unread floor alerts"
          value={unread === null ? "—" : `${unread}`}
          hint={alerts.data ? `${alerts.data.count} in the feed` : undefined}
          tone={unread ? "warn" : "good"}
          loading={alerts.loading}
          error={alerts.error && !alerts.data ? "Unavailable" : null}
          href="/app/activity"
          icon={<Bell size={15} />}
        />
        <StatCard
          label="Machines down / at risk"
          value={machines.length ? `${down} / ${atRisk}` : "—"}
          hint={machines.length ? `${machines.length} machines reporting` : undefined}
          tone={down ? "bad" : atRisk ? "warn" : "good"}
          loading={snapshot.loading}
          error={snapshot.error && !snapshot.data ? "Unavailable" : null}
          href="/app/sensors"
          icon={<Cpu size={15} />}
        />
      </section>

      <div className="mt-4 grid grid-cols-1 xl:grid-cols-3 gap-4">
        <div className="xl:col-span-2 min-w-0">
          <LineBoardPanel board={board} canAdvance={operator} advancing={advancing === "board"} onAdvance={() => void advanceBoard()} />
        </div>
        <BriefCard tick={tick} />
      </div>

      <div className="mt-4 grid grid-cols-1 lg:grid-cols-2 gap-4">
        <Panel
          title="Decisions waiting"
          subtitle="Newest pending approvals from the server queue"
          icon={<ShieldCheck size={14} />}
          right={<Link href="/app/approvals" className="text-caption text-accent hover:underline inline-flex items-center gap-1">All approvals <ArrowUpRight size={12} /></Link>}
        >
          <div data-tour="overview-actions">
            {!pendingLoaded ? (
              <div className="space-y-2">{[0, 1, 2].map((i) => <Skeleton key={i} className="h-12" />)}</div>
            ) : pending.length === 0 ? (
              <p className="text-caption text-fg-tertiary">{t("approvals.empty")}</p>
            ) : (
              <ul className="divide-y divide-border-subtle -my-2">
                {pending.slice(0, 4).map((a) => {
                  const ins = approvalInsight(a);
                  return (
                    <li key={a.id}>
                      <Link href={`/app/approvals?focus=${a.id}`} className="flex items-center gap-3 py-2.5 group">
                        <div className="min-w-0 flex-1">
                          <p className="text-body text-fg-primary truncate group-hover:text-accent">{locale === "bn" ? a.titleBn || a.title : a.title}</p>
                          <p className="text-caption text-fg-tertiary truncate">{ins.agentLabel} · {formatRelative(a.createdAt, locale)}</p>
                        </div>
                        <Badge tone={RISK_TONE[a.riskTier]}>{t(`risk.${a.riskTier}`)}</Badge>
                      </Link>
                    </li>
                  );
                })}
              </ul>
            )}
          </div>
        </Panel>

        <Panel
          title={t("nav.floorAlerts")}
          subtitle={alerts.data?.simulated ? "Simulated WhatsApp channel — no message is actually sent" : undefined}
          icon={<Bell size={14} />}
          right={<Link href="/app/activity" className="text-caption text-accent hover:underline inline-flex items-center gap-1">Open feed <ArrowUpRight size={12} /></Link>}
        >
          {alerts.loading ? (
            <div className="space-y-2">{[0, 1, 2].map((i) => <Skeleton key={i} className="h-12" />)}</div>
          ) : alerts.error && !alerts.data ? (
            <p className="text-caption text-[var(--risk-high)]" role="alert">{errorMessage(alerts.error)}</p>
          ) : (alerts.data?.alerts.length ?? 0) === 0 ? (
            <p className="text-caption text-fg-tertiary">No alerts.</p>
          ) : (
            <ul className="divide-y divide-border-subtle -my-2">
              {alerts.data!.alerts.slice(0, 4).map((a) => (
                <li key={a.id} className="py-2.5 flex items-start gap-3">
                  <span
                    className={cn("mt-1.5 size-2 rounded-full shrink-0", a.read ? "bg-fg-tertiary/40" : a.severity === "critical" ? "bg-[var(--risk-high)]" : a.severity === "warn" ? "bg-[var(--risk-medium)]" : "bg-[var(--info)]")}
                    aria-label={a.read ? "read" : "unread"}
                  />
                  <div className="min-w-0 flex-1">
                    <p className={cn("text-body line-clamp-2", a.read ? "text-fg-tertiary" : "text-fg-primary")}>{locale === "bn" ? a.bodyBn : a.bodyEn}</p>
                    <p className="text-caption text-fg-tertiary">{a.severity} · {formatRelative(a.createdAt, locale)}</p>
                  </div>
                </li>
              ))}
            </ul>
          )}
        </Panel>
      </div>

      <div className="mt-4 grid grid-cols-1 lg:grid-cols-2 gap-4">
        <MachinesPanel snapshot={snapshot} />
        <QcSummary />
      </div>
    </div>
  );
}

const MACHINE_TONE: Record<string, "success" | "warning" | "danger" | "neutral"> = { healthy: "success", at_risk: "warning", down: "danger" };

function MachinesPanel({ snapshot }: { snapshot: Resource<SensorSnapshot> }) {
  const machines = (snapshot.data?.machines ?? []).slice().sort((a, b) => {
    const rank = (s: string) => (s === "down" ? 0 : s === "at_risk" ? 1 : 2);
    return rank(a.status) - rank(b.status);
  });
  return (
    <Panel
      title="Machines"
      subtitle={snapshot.data ? `From the sensor snapshot · ${snapshot.data.readings.length} readings this tick` : undefined}
      icon={<Cpu size={14} />}
      right={<Link href="/app/sensors" className="text-caption text-accent hover:underline inline-flex items-center gap-1">Sensors <ArrowUpRight size={12} /></Link>}
      flush
    >
      {snapshot.loading ? (
        <div className="p-4 space-y-2">{[0, 1, 2].map((i) => <Skeleton key={i} className="h-9" />)}</div>
      ) : machines.length === 0 ? (
        <p className="p-5 text-caption text-fg-tertiary">{snapshot.error ? errorMessage(snapshot.error) : "No machines are reporting yet."}</p>
      ) : (
        <div className="overflow-x-auto max-h-72">
          <table className="data-table">
            <thead>
              <tr><th>Machine</th><th>Status</th><th className="num">Vibration</th><th className="num">Temp</th><th className="num">Duty</th></tr>
            </thead>
            <tbody>
              {machines.map((m) => (
                <tr key={m.id}>
                  <td className="font-mono text-fg-primary">{m.id}</td>
                  <td><Badge tone={MACHINE_TONE[m.status] ?? "neutral"} dot>{m.status.replace("_", " ")}</Badge></td>
                  <td className="num">{m.vibration.toFixed(1)} mm/s</td>
                  <td className="num">{m.temperature.toFixed(1)} °C</td>
                  <td className="num">{Math.round(m.dutyCycle * 100)}%</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  );
}
