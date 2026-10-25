"use client";

// Quality metrics from GET /api/qc/summary — every number is calculated by
// the API from persisted, inspector-reviewed records. Sample sizes are shown
// next to every rate, and rates with no reviewed sample render as "—", never 0.

import { useState } from "react";
import { CartesianGrid, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { Panel } from "@/components/ui/Panel";
import { StatCard } from "@/components/ui/StatCard";
import { FilterSelect } from "@/components/ui/Field";
import { Callout } from "@/components/ui/Callout";
import { Button } from "@/components/ui/Button";
import { SkeletonRows } from "@/components/ui/Status";
import { useOnDataChanged, useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { STAGE_LABEL, formatPct } from "@/lib/qcInspection";
import { summaryPath } from "@/services/qc.api";
import { QC_STAGES, type QcSummary } from "@/services/contracts";

const RANGES = [7, 30, 90, 365] as const;

export function QualitySummary() {
  const [days, setDays] = useState<number>(30);
  const [stage, setStage] = useState("");
  const [lineId, setLineId] = useState("");
  const res = useResource<QcSummary>(summaryPath({ days, stage, lineId }));
  useOnDataChanged(() => void res.reload());
  const s = res.data;

  return (
    <div className="space-y-4" data-testid="qc-summary-view">
      <div className="flex flex-wrap items-center gap-3">
        <FilterSelect label="Range" value={String(days)} onChange={(v) => setDays(Number(v))} testId="qc-summary-range">
          {RANGES.map((d) => <option key={d} value={d}>Last {d} days</option>)}
        </FilterSelect>
        <FilterSelect label="Stage" value={stage} onChange={setStage}>
          <option value="">All stages</option>
          {QC_STAGES.map((st) => <option key={st} value={st}>{STAGE_LABEL[st]}</option>)}
        </FilterSelect>
        <FilterSelect label="Line" value={lineId} onChange={setLineId}>
          <option value="">All lines</option>
          {(s?.byLine ?? []).map((l) => <option key={l.lineId} value={l.lineId}>{l.lineId}</option>)}
          {lineId && !(s?.byLine ?? []).some((l) => l.lineId === lineId) && <option value={lineId}>{lineId}</option>}
        </FilterSelect>
        {s && <span className="text-caption text-fg-tertiary">{s.from.slice(0, 10)} → {s.to.slice(0, 10)} (UTC days)</span>}
      </div>

      {res.loading ? (
        <SkeletonRows rows={4} rowHeight="h-24" />
      ) : res.error && !s ? (
        <Callout tone="danger" title="Could not load the quality summary" action={<Button size="sm" onClick={() => void res.reload()}>Retry</Button>}>
          {errorMessage(res.error)}
        </Callout>
      ) : s && s.totals.inspections === 0 ? (
        <Callout tone="info" title="No inspections in this range" testId="qc-summary-empty">
          Metrics appear once inspections are submitted and reviewed. Nothing is estimated or filled in.
        </Callout>
      ) : s ? (
        <>
          <div className="grid grid-cols-2 lg:grid-cols-4 gap-3">
            <StatCard label="Inspections" value={s.totals.inspections} testId="qc-stat-inspections"
              hint={`${s.totals.reviewed} reviewed · ${s.totals.awaitingReview} awaiting review · ${s.totals.inProgress} analysing · ${s.totals.failed} failed`} />
            <StatCard label="Defective rate" value={formatPct(s.totals.defectiveRatePct)} testId="qc-stat-defective"
              tone={s.totals.defectiveRatePct == null ? "neutral" : s.totals.defectiveRatePct >= 10 ? "bad" : s.totals.defectiveRatePct >= 5 ? "warn" : "good"}
              hint={`of ${s.totals.reviewed} reviewed`} />
            <StatCard label="Defects per hundred units" value={s.totals.dhu == null ? "—" : s.totals.dhu.toFixed(1)} testId="qc-stat-dhu"
              hint={`${s.totals.verifiedDefects} verified defects`} />
            <StatCard label="Rework or reject" value={formatPct(s.totals.rejectOrReworkRatePct)}
              hint={`${s.totals.rework} rework · ${s.totals.rejected} reject · ${s.totals.passed} pass`} />
          </div>

          <div className="grid grid-cols-1 xl:grid-cols-[minmax(0,1.4fr)_minmax(0,1fr)] gap-4">
            <Panel title="Defective rate by day" subtitle="Share of that day's reviewed inspections with a verified defect. Days without reviews have no point.">
              <DailyChart daily={s.daily} />
            </Panel>
            <Panel title="Verified defects by category" subtitle={`${s.totals.verifiedDefects} defects on ${s.totals.reviewed} reviewed inspections`}>
              {s.byCategory.length === 0 ? (
                <p className="text-caption text-fg-tertiary">No verified defects in this range.</p>
              ) : (
                <ul className="space-y-2.5" data-testid="qc-by-category">
                  {s.byCategory.map((c) => (
                    <li key={c.code ?? "unmapped"}>
                      <div className="flex items-baseline justify-between gap-2 text-caption">
                        <span className="text-fg-primary truncate">{c.name}</span>
                        <span className="tabular-nums text-fg-secondary">{c.count} · {c.sharePct.toFixed(0)}%</span>
                      </div>
                      <div className="mt-1 h-1.5 rounded-full bg-surface-3 overflow-hidden" role="presentation">
                        <div className="h-full rounded-full bg-[var(--accent)]" style={{ width: `${Math.max(2, c.sharePct)}%` }} />
                      </div>
                    </li>
                  ))}
                </ul>
              )}
              <p className="mt-3 text-caption text-fg-tertiary">
                Severity: {s.bySeverity.critical} critical · {s.bySeverity.major} major · {s.bySeverity.minor} minor
              </p>
            </Panel>
          </div>

          <div className="grid grid-cols-1 xl:grid-cols-2 gap-4">
            <Panel title="By line" flush>
              {s.byLine.length === 0 ? (
                <p className="p-4 text-caption text-fg-tertiary">No reviewed inspections yet.</p>
              ) : (
                <div className="overflow-x-auto">
                  <table className="data-table" data-testid="qc-by-line">
                    <thead><tr><th>Line</th><th className="num">Reviewed</th><th className="num">Defective</th><th className="num">Rate</th><th className="num">Defects</th></tr></thead>
                    <tbody>
                      {s.byLine.map((l) => (
                        <tr key={l.lineId}>
                          <td className="font-mono text-[12px]">{l.lineId}</td>
                          <td className="num">{l.reviewed}</td>
                          <td className="num">{l.defective}</td>
                          <td className="num">{formatPct(l.defectiveRatePct)}</td>
                          <td className="num">{l.verifiedDefects}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </Panel>
            <div className="space-y-4">
              <Panel title="Recurring issues" subtitle={`A line and category with at least ${s.recurringThreshold} verified defects in the range.`}>
                {s.recurring.length === 0 ? (
                  <p className="text-caption text-fg-tertiary">None in this range.</p>
                ) : (
                  <ul className="space-y-1.5 text-body" data-testid="qc-recurring">
                    {s.recurring.map((r) => (
                      <li key={`${r.lineId}-${r.categoryCode}`} className="flex justify-between gap-2">
                        <span><span className="font-mono text-[12px]">{r.lineId}</span> · {r.categoryName}</span>
                        <span className="tabular-nums text-fg-secondary">{r.defects} defects on {r.inspections} pieces</span>
                      </li>
                    ))}
                  </ul>
                )}
              </Panel>
              <Panel title="Model agreement with inspectors">
                <p className="text-body text-fg-primary">
                  {s.model.predictionsReviewed === 0 ? "No predictions reviewed yet." :
                    `${formatPct(s.model.acceptedPct, 0)} of ${s.model.predictionsReviewed} reviewed predictions accepted unchanged.`}
                </p>
                <p className="mt-1 text-caption text-fg-tertiary">
                  {s.model.corrected} corrected · {s.model.rejected} rejected · {s.model.inspectorAdded} missed defects added by inspectors · {s.model.awaitingReview} predictions awaiting review.
                </p>
              </Panel>
            </div>
          </div>

          <details className="surface p-4 text-caption text-fg-secondary">
            <summary className="cursor-pointer text-fg-primary font-medium">How these numbers are calculated</summary>
            <dl className="mt-2 space-y-1.5">
              {Object.entries(s.definitions).map(([k, v]) => (
                <div key={k}><dt className="inline font-mono text-fg-primary">{k}</dt>: <dd className="inline">{v}</dd></div>
              ))}
            </dl>
          </details>
        </>
      ) : null}
    </div>
  );
}

function DailyChart({ daily }: { daily: QcSummary["daily"] }) {
  const data = daily.map((d) => ({ ...d, label: d.date.slice(5), rate: d.defectiveRatePct ?? null }));
  if (!data.some((d) => d.rate !== null)) return <p className="text-caption text-fg-tertiary">No reviewed inspections in this range.</p>;
  return (
    <div className="h-56" data-testid="qc-daily-chart">
      <ResponsiveContainer width="100%" height="100%">
        <LineChart data={data} margin={{ top: 8, right: 8, bottom: 0, left: -12 }}>
          <CartesianGrid stroke="var(--border-subtle)" strokeDasharray="3 3" vertical={false} />
          <XAxis dataKey="label" tick={{ fill: "var(--fg-tertiary)", fontSize: 11 }} tickLine={false} axisLine={{ stroke: "var(--border-subtle)" }} minTickGap={16} />
          <YAxis domain={[0, 100]} unit="%" tick={{ fill: "var(--fg-tertiary)", fontSize: 11 }} tickLine={false} axisLine={false} width={44} />
          <Tooltip
            cursor={{ stroke: "var(--border-strong)" }}
            content={({ active, payload }) => {
              const p = active && payload?.[0]?.payload as (typeof data)[number] | undefined;
              if (!p) return null;
              return (
                <div className="surface px-3 py-2 text-caption shadow-md">
                  <p className="font-medium text-fg-primary">{p.date}</p>
                  <p className="text-fg-secondary">Defective rate: {formatPct(p.rate)}</p>
                  <p className="text-fg-tertiary">{p.defective} of {p.reviewed} reviewed · {p.inspections} submitted</p>
                </div>
              );
            }}
          />
          <Line type="monotone" dataKey="rate" stroke="var(--accent)" strokeWidth={2} dot={{ r: 4, strokeWidth: 0, fill: "var(--accent)" }}
            activeDot={{ r: 5 }} connectNulls={false} isAnimationActive={false} />
        </LineChart>
      </ResponsiveContainer>
    </div>
  );
}
