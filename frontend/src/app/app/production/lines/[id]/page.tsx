"use client";

// /app/production/lines/[id] — one line on one production date (optionally
// one shift): hourly planned vs actual, the plans covering the day, the
// bottleneck and recorded layout per style, and every output record behind
// the figures.

import { useEffect, useState } from "react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { Bar, CartesianGrid, ComposedChart, Legend, Line, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { ArrowLeft, ClipboardPen, LayoutGrid, Timer } from "lucide-react";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { FilterSelect } from "@/components/ui/Field";
import { StatCard } from "@/components/ui/StatCard";
import { SkeletonRows } from "@/components/ui/Status";
import { useOnDataChanged, useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { canOperate, useAuthStore } from "@/lib/auth";
import { fmt, fmtPct, fmtSigned, plantDateTime, plantToday } from "@/lib/production";
import { productionPaths } from "@/services/production.api";
import type { LineDetail, ProductionStyle, TimelinePoint } from "@/services/contracts";
import { OutputFormModal } from "@/components/production/forms";
import { HistoryPanel, PlanRows } from "@/components/production/panels";
import { BottleneckCard, LayoutModal, MetricsRow, TimeStudyModal, WorkloadBars } from "@/components/production/analysis";
import { Definitions, WipList, useMasterData, useProductionConfig } from "@/components/production/shared";

export default function LinePage() {
  const id = decodeURIComponent(useParams<{ id: string }>()?.id ?? "");
  const operator = canOperate(useAuthStore((s) => s.user));
  const config = useProductionConfig();
  const md = useMasterData();
  const [date, setDate] = useState("");
  const [shift, setShift] = useState("");
  useEffect(() => { if (config.data && !date) setDate(plantToday(config.offset)); }, [config.data, config.offset, date]);
  const res = useResource<LineDetail>(id && date ? productionPaths.lineDetail(id, { date, shift }) : null);
  useOnDataChanged(() => void res.reload());
  const [recording, setRecording] = useState(false);
  const [study, setStudy] = useState<ProductionStyle | null>(null);
  const [layoutFor, setLayoutFor] = useState<ProductionStyle | null>(null);
  const d = res.data;
  const styleById = (sid: string) => (md.styles.data ?? []).find((s) => s.id === sid) ?? null;

  return (
    <div className="page" data-testid="line-page">
      <Link href="/app/production" className="inline-flex items-center gap-1 text-caption text-fg-tertiary hover:text-fg-primary"><ArrowLeft size={13} /> Production</Link>
      <PageHeader className="mt-2" eyebrow="Production line" title={d?.line.name ?? id}
        description={d ? `${d.line.workstations} declared workstations · plant time ${d.timeZone}` : undefined}
        actions={operator && <Button variant="primary" iconLeft={<ClipboardPen size={14} />} onClick={() => setRecording(true)} data-testid="line-record">Record output</Button>}
      />
      <div className="mt-4 surface p-3 flex flex-wrap items-center gap-3" role="search">
        <label className="flex items-center gap-2 text-caption text-fg-tertiary">Production date
          <input type="date" className="control !h-8 !w-auto !text-caption" value={date} onChange={(e) => setDate(e.target.value)} data-testid="line-date" />
        </label>
        <FilterSelect label="Shift" value={shift} onChange={setShift} testId="line-shift">
          <option value="">Whole day</option>
          {(md.shifts.data ?? []).map((s) => <option key={s.code} value={s.code}>{s.name}</option>)}
          <option value="overtime">Outside shifts</option>
        </FilterSelect>
        {d && <span className="text-caption text-fg-tertiary">Planned output is compared through {plantDateTime(d.comparedThrough, config.offset)}.</span>}
      </div>

      {res.loading || !date ? <div className="mt-4"><SkeletonRows rows={5} rowHeight="h-20" /></div> : !d ? (
        <Callout tone="danger" className="mt-4" title="Could not load the line" action={<Button size="sm" onClick={() => void res.reload()}>Retry</Button>}>{errorMessage(res.error)}</Callout>
      ) : (
        <div className="mt-4 space-y-4">
          <div className="grid grid-cols-2 lg:grid-cols-5 gap-3">
            <StatCard label="Good output" value={fmt(d.totals.actual)} hint={`planned ${fmt(d.totals.planned)} · ${fmtSigned(d.totals.variance)} pcs`} testId="line-actual" />
            <StatCard label="Achievement" value={fmtPct(d.totals.achievementPct)} hint="actual ÷ planned" />
            <StatCard label="Line efficiency" value={fmtPct(d.totals.efficiencyPct)} hint={`${fmtPct(d.totals.efficiencyExcludingDowntimePct)} excl. downtime`} testId="line-efficiency" />
            <StatCard label="Downtime" value={`${fmt(d.totals.downtimeMinutes)} min`} hint={`${d.totals.records} records`} />
            <StatCard label="Rework · reject" value={`${fmt(d.totals.rework)} · ${fmt(d.totals.reject)}`} />
          </div>

          <Panel title="Hourly planned vs actual" subtitle={`Pieces per plant-local clock hour on ${d.date}${d.shift ? `, shift ${d.shift}` : ""}. An output record counts in the hour it starts. Efficiency (%) is per hour where operators were recorded. Source: recorded output and live plans.`}>
            <Timeline points={d.timeline} />
          </Panel>

          <div className="grid grid-cols-1 xl:grid-cols-[minmax(0,1.4fr)_minmax(0,1fr)] gap-4">
            <div>
              <h2 className="text-[15px] font-semibold text-fg-primary mb-2">Plans covering {d.date}</h2>
              {d.plans.length === 0 ? <Callout tone="info">No plan covers this line on this date, so nothing is planned.</Callout> : <PlanRows plans={d.plans} />}
            </div>
            <Panel title="Work in process" subtitle="Open orders on this line, as of now.">
              <WipList buffers={d.wip[0]?.buffers ?? []} />
            </Panel>
          </div>

          {d.styles.length === 0 ? (
            <Callout tone="info">No style runs on this line on this date (no live plan or output), so there is nothing to analyse.</Callout>
          ) : d.styles.map((s) => {
            const style = styleById(s.styleId);
            return (
              <Panel key={s.styleId} title={`${s.styleCode} · ${s.styleName}`} subtitle="Bottleneck from time studies on this line; workload from the recorded layout."
                right={operator && style && (
                  <>
                    <Button size="sm" variant="secondary" iconLeft={<Timer size={13} />} onClick={() => setStudy(style)} disabled={style.operations.length === 0} data-testid="line-add-study">Time study</Button>
                    <Button size="sm" variant="secondary" iconLeft={<LayoutGrid size={13} />} onClick={() => setLayoutFor(style)} disabled={style.operations.length === 0} data-testid="line-record-layout">Record layout</Button>
                  </>
                )}>
                <BottleneckCard b={s.bottleneck} />
                <div className="mt-4">
                  <h3 className="text-body font-semibold text-fg-primary mb-2">Current layout</h3>
                  {s.layout ? (
                    <div className="space-y-3">
                      <MetricsRow m={s.layout.metrics} />
                      <WorkloadBars stations={s.layout.stations} metrics={s.layout.metrics} testId="line-layout-bars" />
                      {s.layout.issues.length > 0 && <Callout tone="warning" title="Constraint issues in the recorded layout"><ul className="list-disc pl-4">{s.layout.issues.map((i, k) => <li key={k}>{i.message}</li>)}</ul></Callout>}
                      <p className="text-caption text-fg-tertiary">{s.layout.origin === "proposal" ? "Applied from an approved balance proposal" : "Recorded as found"} by {s.layout.createdByEmail} · {plantDateTime(s.layout.createdAt, config.offset)}.</p>
                    </div>
                  ) : <p className="text-caption text-fg-tertiary">No layout recorded for this style on this line.</p>}
                </div>
              </Panel>
            );
          })}

          <div>
            <h2 className="text-[15px] font-semibold text-fg-primary mb-2">Records behind these figures</h2>
            <HistoryPanel operator={operator} lines={md.lines.data ?? []} shifts={md.shifts.data ?? []} offset={config.offset} fixed={{ lineId: id, from: d.date, to: d.date }} />
          </div>
          <Definitions definitions={d.definitions} />
        </div>
      )}

      <OutputFormModal open={recording} onClose={() => setRecording(false)} lines={md.lines.data ?? []} offset={config.offset} defaults={{ lineId: id }} />
      <TimeStudyModal open={!!study} onClose={() => setStudy(null)} lineId={id} style={study} onSaved={() => void res.reload()} />
      <LayoutModal open={!!layoutFor} onClose={() => setLayoutFor(null)} lineId={id} workstations={d?.line.workstations ?? 1} style={layoutFor}
        current={d?.styles.find((s) => s.styleId === layoutFor?.id)?.layout} onSaved={() => void res.reload()} />
    </div>
  );
}

function Timeline({ points }: { points: TimelinePoint[] }) {
  if (!points.some((p) => p.actual > 0 || p.planned > 0)) return <p className="text-caption text-fg-tertiary">Nothing planned or recorded on this date.</p>;
  const data = points.map((p) => ({ ...p, planned: Math.round(p.planned) }));
  return (
    <div className="h-64" data-testid="line-timeline">
      <ResponsiveContainer width="100%" height="100%">
        <ComposedChart data={data} margin={{ top: 8, right: 8, bottom: 0, left: -8 }}>
          <CartesianGrid stroke="var(--border-subtle)" strokeDasharray="3 3" vertical={false} />
          <XAxis dataKey="label" tick={{ fill: "var(--fg-tertiary)", fontSize: 11 }} tickLine={false} axisLine={{ stroke: "var(--border-subtle)" }} />
          <YAxis yAxisId="pcs" tick={{ fill: "var(--fg-tertiary)", fontSize: 11 }} tickLine={false} axisLine={false} width={44} unit=" pcs" />
          <YAxis yAxisId="pct" orientation="right" tick={{ fill: "var(--fg-tertiary)", fontSize: 11 }} tickLine={false} axisLine={false} width={40} unit="%" />
          <Legend wrapperStyle={{ fontSize: 12 }} />
          <Tooltip cursor={{ fill: "var(--surface-2)" }} content={({ active, payload }) => {
            const p = active && (payload?.[0]?.payload as (typeof data)[number] | undefined);
            if (!p) return null;
            return (
              <div className="surface px-3 py-2 text-caption shadow-md">
                <p className="font-medium text-fg-primary">{p.label}{p.shiftCode ? ` · shift ${p.shiftCode}` : ""}</p>
                <p className="text-fg-secondary">Actual {fmt(p.actual)} · planned {fmt(p.planned)} pcs</p>
                <p className="text-fg-tertiary">Efficiency {fmtPct(p.efficiencyPct)} · downtime {fmt(p.downtimeMinutes)} min · {p.records} records</p>
              </div>
            );
          }} />
          <Bar yAxisId="pcs" dataKey="planned" name="Planned (pcs)" fill="var(--border-strong)" isAnimationActive={false} radius={[3, 3, 0, 0]} />
          <Bar yAxisId="pcs" dataKey="actual" name="Actual (pcs)" fill="var(--accent)" isAnimationActive={false} radius={[3, 3, 0, 0]} />
          <Line yAxisId="pct" dataKey="efficiencyPct" name="Efficiency (%)" stroke="var(--info)" dot={{ r: 3 }} connectNulls={false} isAnimationActive={false} />
        </ComposedChart>
      </ResponsiveContainer>
    </div>
  );
}
