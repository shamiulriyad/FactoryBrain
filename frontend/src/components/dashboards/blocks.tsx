"use client";

// Dashboard blocks that read the planning module: today's plan against verified
// output, delivery deadlines and open exceptions. They respect the viewer's
// scope (a supervisor gets their own lines) and say "no data" when there is none.

import Link from "next/link";
import { AlertTriangle } from "lucide-react";
import { Badge } from "@/components/ui/Badge";
import { Skeleton } from "@/components/ui/Skeleton";
import { StatCard } from "@/components/ui/StatCard";
import { Card, Empty, Section } from "@/components/dashboards/kit";
import { BasisBadge, ForecastBadge, SeverityBadge, StateBadge } from "@/components/planning/shared";
import { useCan } from "@/lib/auth";
import { P } from "@/lib/access";
import { useResource } from "@/lib/useResource";
import { num, pct, planningApi, type Monitor, type Paged, type ProductionException } from "@/lib/planning";
import { dhaka } from "@/lib/monitoring";

const PL = "/app/planning";

/** Today's published plan against verified output, line by line. */
export function PlanToday({ factoryId, compact = false }: { factoryId: string; compact?: boolean }) {
  const allowed = useCan(P.PlanningView);
  const r = useResource<Monitor>(allowed && factoryId ? planningApi.monitor({ factoryId }) : null, { pollMs: 60_000 });
  if (!allowed) return null;
  const m = r.data;
  const s = m?.summary;
  return (
    <Section title="Today's production" hint={m ? `${m.date} · ${m.planStatus ? `plan ${m.planReference} (${m.planStatus.toLowerCase()})` : "no plan published yet"}` : undefined}
      href={`${PL}?factory=${factoryId}`} linkLabel="Open monitoring" testId="home-plan-today">
      {r.error && !m ? <Card><Empty>Could not load today&apos;s production: {r.error.message}</Empty></Card>
        : !m || !s ? <div className="grid gap-3 grid-cols-2 xl:grid-cols-4">{[0, 1, 2, 3].map((i) => <Skeleton key={i} className="h-24" />)}</div>
        : (
          <>
            <div className="grid gap-3 grid-cols-2 xl:grid-cols-4 mb-4">
              <StatCard label="Target today" value={num(s.targetToday)} hint="published plan" testId="home-target" />
              <StatCard label="Counted today" value={s.actualToday == null ? "No data" : num(s.actualToday)} hint={s.actualToday == null ? "no counter output yet" : "verified at output counters"} testId="home-actual" />
              <StatCard label="Achievement" value={pct(s.achievementPct)} tone={s.achievementPct == null ? "neutral" : s.achievementPct >= 90 ? "good" : s.achievementPct >= 70 ? "warn" : "bad"} testId="home-achievement" />
              <StatCard label="Delayed orders" value={s.ordersDelayed} tone={s.ordersDelayed > 0 ? "bad" : "good"} hint={`${s.activeOrders} active`} href={`${PL}/orders?factory=${factoryId}`} testId="home-delayed" />
            </div>
            {!compact && (
              m.lines.length === 0
                ? <Card><Empty>{m.planStatus ? "The published plan has no items for you." : "Nothing is planned for today."}</Empty></Card>
                : (
                  <Card>
                    <table className="w-full text-caption" data-testid="home-lines">
                      <thead><tr className="text-left text-fg-tertiary"><th className="px-4 py-2 font-medium">Line</th><th className="px-3 py-2 font-medium text-right">Target</th><th className="px-3 py-2 font-medium text-right">Counted</th><th className="px-3 py-2 font-medium text-right">Achievement</th><th className="px-3 py-2 font-medium">Measured by</th><th className="px-4 py-2 font-medium">State</th></tr></thead>
                      <tbody>
                        {m.lines.map((l) => (
                          <tr key={l.lineId} className="border-t border-border-subtle">
                            <td className="px-4 py-2 font-medium text-fg-primary">{l.lineName}</td>
                            <td className="px-3 py-2 text-right tabular-nums">{num(l.target)}</td>
                            <td className="px-3 py-2 text-right tabular-nums">{l.actual == null ? <span className="text-fg-tertiary">No data</span> : num(l.actual)}</td>
                            <td className="px-3 py-2 text-right tabular-nums">{pct(l.achievementPct)}</td>
                            <td className="px-3 py-2"><BasisBadge basis={l.basis} /></td>
                            <td className="px-4 py-2"><StateBadge state={l.state} /></td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </Card>
                )
            )}
            {!compact && m.upcomingDeadlines.length > 0 && (
              <Card className="mt-4">
                <p className="px-4 pt-3 text-caption font-medium text-fg-secondary">Delivery deadlines within 14 days</p>
                <ul className="divide-y divide-border-subtle" data-testid="home-deadlines">
                  {m.upcomingDeadlines.slice(0, 5).map((o) => (
                    <li key={o.orderId} className="flex flex-wrap items-center justify-between gap-2 px-4 py-2 text-caption">
                      <Link href={`${PL}/orders/${o.orderId}?factory=${factoryId}`} className="font-mono text-[12px] text-fg-primary underline-offset-4 hover:underline">{o.orderNumber}</Link>
                      <span className="text-fg-tertiary">{o.buyer ?? ""}</span>
                      <span>{o.deliveryDeadline} · {o.daysToDeadline == null ? "" : o.daysToDeadline < 0 ? `${-o.daysToDeadline} days late` : `${o.daysToDeadline} days left`}</span>
                      <ForecastBadge status={o.forecastStatus} />
                    </li>
                  ))}
                </ul>
              </Card>
            )}
          </>
        )}
    </Section>
  );
}

/** Open and acknowledged production exceptions, most severe first. */
export function OpenExceptions({ factoryId, limit = 4 }: { factoryId: string; limit?: number }) {
  const allowed = useCan(P.PlanningView);
  const r = useResource<Paged<ProductionException>>(allowed && factoryId ? planningApi.exceptions({ factoryId, pageSize: 20 }) : null, { pollMs: 60_000 });
  if (!allowed) return null;
  const open = (r.data?.items ?? []).filter((e) => e.status !== "RESOLVED").slice(0, limit);
  return (
    <Section title="Needs attention" hint="Delays and data gaps found from counters, machines and capacity" href={`${PL}/exceptions?factory=${factoryId}`} linkLabel="All exceptions" testId="home-exceptions">
      <Card>
        {r.error && !r.data ? <Empty>Could not load exceptions: {r.error.message}</Empty>
          : !r.data ? <div className="p-4"><Skeleton className="h-16" /></div>
          : open.length === 0 ? <Empty>Nothing open. The check runs every ten minutes while a plan is published.</Empty>
          : (
            <ul className="divide-y divide-border-subtle">
              {open.map((e) => (
                <li key={e.id} className="flex items-start gap-3 px-4 py-3 text-caption">
                  <AlertTriangle size={15} className="mt-0.5 shrink-0 text-fg-tertiary" aria-hidden />
                  <div className="min-w-0 flex-1">
                    <p className="flex flex-wrap items-center gap-2 font-medium text-fg-primary"><SeverityBadge severity={e.severity} />{e.title}</p>
                    <p className="text-fg-secondary">{e.reason}</p>
                    <p className="text-fg-tertiary">Detected {dhaka(e.detectedAt)}{e.status === "ACKNOWLEDGED" && <Badge className="ml-2">Acknowledged</Badge>}</p>
                  </div>
                </li>
              ))}
            </ul>
          )}
      </Card>
    </Section>
  );
}
