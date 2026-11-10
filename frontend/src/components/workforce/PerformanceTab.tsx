"use client";

import { useMemo, useState } from "react";
import { useRouter } from "next/navigation";
import { Award, BarChart3, Lock } from "lucide-react";
import { Panel } from "@/components/ui/Panel";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { FilterSelect } from "@/components/ui/Field";
import { Sparkline } from "@/components/ui/Sparkline";
import { useT } from "@/lib/useT";
import { formatBDT, formatNumber, bn } from "@/lib/format";
import { WORKFORCE_LINES, type WorkforceLineId } from "@/data/workforce/lines";
import { useWorkforceStore } from "@/store/workforce.store";
import {
  calcWorkerAvgDhuPct,
  calcWorkerAvgEfficiencyPct,
  calcWorkerEfficiencyPct,
  calcWorkerLatestEfficiencyPct,
  computeWorkerAttendanceRatePct,
  computeWorkerBonusPreview
} from "@/lib/workforce";
import { IllustrativePolicyNote, lineShortCode, roleLabel } from "./shared";

type PerfSort = "eff8w_desc" | "eff8w_asc" | "dhu_asc" | "att_desc" | "id";

type Props = {
  admin: boolean;
};

export function PerformanceTab({ admin }: Props) {
  const { t, locale } = useT();
  const router = useRouter();
  const state = useWorkforceStore();

  const [lineFilter, setLineFilter] = useState("");
  const [sortBy, setSortBy] = useState<PerfSort>("eff8w_desc");

  const activeWorkers = useMemo(
    () => state.workers.filter((w) => w.status !== "resigned"),
    [state.workers]
  );

  // Per-line summary
  const lineSummaries = useMemo(() => {
    return WORKFORCE_LINES.map((line) => {
      const lw = activeWorkers.filter((w) => w.lineId === line.id);
      const count = lw.length || 1;
      const avgEff =
        Math.round(
          (lw.reduce((acc, w) => acc + calcWorkerAvgEfficiencyPct(w), 0) / count) * 10
        ) / 10;
      const avgDhu =
        Math.round((lw.reduce((acc, w) => acc + calcWorkerAvgDhuPct(w), 0) / count) * 10) / 10;
      const avgAtt =
        Math.round(
          (lw.reduce(
            (acc, w) =>
              acc +
              computeWorkerAttendanceRatePct(w.id, state.attendanceByDate, state.anchorDate),
            0
          ) /
            count) *
            10
        ) / 10;
      return {
        line,
        workerCount: lw.length,
        avgEff,
        targetEff: line.targetEfficiencyPct,
        avgDhu,
        avgAtt
      };
    });
  }, [activeWorkers, state.attendanceByDate, state.anchorDate]);

  const workerRows = useMemo(() => {
    const filtered = activeWorkers.filter((w) => !lineFilter || w.lineId === lineFilter);
    const enriched = filtered.map((w) => {
      const latestEff = calcWorkerLatestEfficiencyPct(w);
      const avgEff = calcWorkerAvgEfficiencyPct(w);
      const avgDhu = calcWorkerAvgDhuPct(w);
      const att30d = computeWorkerAttendanceRatePct(
        w.id,
        state.attendanceByDate,
        state.anchorDate
      );
      const targetEff = w.performanceWeeks[w.performanceWeeks.length - 1]?.targetEfficiencyPct ?? 76;
      const bonus = computeWorkerBonusPreview(w, att30d);
      const sparkValues = w.performanceWeeks.map((pw) =>
        calcWorkerEfficiencyPct(pw.pieces, pw.smv, pw.minutesWorked)
      );
      return {
        worker: w,
        latestEff,
        avgEff,
        targetEff,
        avgDhu,
        att30d,
        bonus,
        sparkValues
      };
    });

    return enriched.sort((a, b) => {
      if (sortBy === "eff8w_desc") return b.avgEff - a.avgEff;
      if (sortBy === "eff8w_asc") return a.avgEff - b.avgEff;
      if (sortBy === "dhu_asc") return a.avgDhu - b.avgDhu;
      if (sortBy === "att_desc") return b.att30d - a.att30d;
      return a.worker.id.localeCompare(b.worker.id);
    });
  }, [activeWorkers, lineFilter, sortBy, state.attendanceByDate, state.anchorDate]);

  const fmtPctVal = (n: number) => (locale === "bn" ? `${bn(n.toFixed(1))}%` : `${n.toFixed(1)}%`);

  return (
    <div className="space-y-4" data-testid="workforce-performance">
      {/* Neutral team-framing callout */}
      <Callout
        tone="info"
        title={t("workforce.performance.calloutTitle")}
        testId="wf-performance-neutral-callout"
      >
        {t("workforce.performance.calloutBody")}
      </Callout>

      {/* Per-line summary cards (clickable to filter) */}
      <Panel
        title={t("workforce.performance.byLineTitle")}
        subtitle={t("workforce.performance.byLineSubtitle")}
        icon={<BarChart3 size={15} />}
        testId="wf-performance-by-line-panel"
      >
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-3">
          {lineSummaries.map((ls) => {
            const selected = lineFilter === ls.line.id;
            const ratio = Math.min(100, Math.round((ls.avgEff / Math.max(1, ls.targetEff)) * 100));
            return (
              <button
                key={ls.line.id}
                type="button"
                onClick={() => setLineFilter(selected ? "" : ls.line.id)}
                data-testid={`wf-perf-line-card-${ls.line.id}`}
                className={`text-left surface-2 p-3.5 rounded-md border transition-colors ${
                  selected
                    ? "border-[var(--accent)] ring-1 ring-[var(--accent)]"
                    : "border-border-subtle hover:border-[var(--border-strong)]"
                }`}
              >
                <div className="flex items-center justify-between gap-2">
                  <span className="font-semibold text-fg-primary">
                    {locale === "bn" ? ls.line.nameBn : ls.line.nameEn}
                  </span>
                  <Badge tone="neutral">{formatNumber(ls.workerCount, locale)}</Badge>
                </div>
                <div className="mt-2 grid grid-cols-3 gap-2 text-caption tabular-nums">
                  <div>
                    <span className="text-fg-tertiary block">8w Eff</span>
                    <span className="font-semibold text-fg-primary">{fmtPctVal(ls.avgEff)}</span>
                  </div>
                  <div>
                    <span className="text-fg-tertiary block">Target</span>
                    <span className="font-semibold text-fg-secondary">
                      {fmtPctVal(ls.targetEff)}
                    </span>
                  </div>
                  <div>
                    <span className="text-fg-tertiary block">Avg DHU</span>
                    <span className="font-semibold text-fg-primary">{fmtPctVal(ls.avgDhu)}</span>
                  </div>
                </div>
                {/* Target vs actual neutral bar */}
                <div className="mt-2.5 h-2 w-full rounded-full bg-surface-1 overflow-hidden">
                  <div
                    className="h-full bg-[var(--accent)] transition-all"
                    style={{ width: `${ratio}%` }}
                  />
                </div>
              </button>
            );
          })}
        </div>
      </Panel>

      {/* Neutral worker leaderboard (no shaming colours, identical styling top to bottom) */}
      <Panel
        title={`${t("workforce.performance.leaderboardTitle")} (${formatNumber(workerRows.length, locale)})`}
        subtitle={t("workforce.performance.leaderboardSubtitle")}
        right={
          <div className="flex flex-wrap items-center gap-2">
            <FilterSelect
              label={t("workforce.workers.filterLine") as string}
              value={lineFilter}
              onChange={setLineFilter}
              testId="wf-perf-line-select"
            >
              <option value="">{t("workforce.workers.allLines")}</option>
              {WORKFORCE_LINES.map((l) => (
                <option key={l.id} value={l.id}>
                  {locale === "bn" ? l.nameBn : l.nameEn}
                </option>
              ))}
            </FilterSelect>
            <FilterSelect
              label={t("workforce.workers.sortBy") as string}
              value={sortBy}
              onChange={(v) => setSortBy(v as PerfSort)}
              testId="wf-perf-sort-select"
            >
              <option value="eff8w_desc">8w Eff (High → Low)</option>
              <option value="eff8w_asc">8w Eff (Low → High)</option>
              <option value="dhu_asc">DHU % (Low → High)</option>
              <option value="att_desc">Attendance % (High → Low)</option>
              <option value="id">Worker ID</option>
            </FilterSelect>
          </div>
        }
        flush
        testId="wf-performance-leaderboard-panel"
      >
        <div className="overflow-x-auto max-h-[500px] overflow-y-auto">
          <table className="w-full text-left border-collapse" data-testid="wf-performance-table">
            <thead className="sticky top-0 bg-surface-1 z-10 border-b border-border-subtle text-caption text-fg-tertiary">
              <tr>
                <th className="py-2.5 px-3 font-medium">{t("workforce.workers.colId")}</th>
                <th className="py-2.5 px-3 font-medium">{t("workforce.workers.colName")}</th>
                <th className="py-2.5 px-3 font-medium">{t("workforce.workers.colLine")}</th>
                <th className="py-2.5 px-3 font-medium text-right">
                  {t("workforce.performance.colEffLatest")}
                </th>
                <th className="py-2.5 px-3 font-medium text-right">
                  {t("workforce.performance.colEff8w")}
                </th>
                <th className="py-2.5 px-3 font-medium text-right">
                  {t("workforce.performance.colTargetEff")}
                </th>
                <th className="py-2.5 px-3 font-medium">{t("workforce.performance.colGap")}</th>
                <th className="py-2.5 px-3 font-medium text-right">
                  {t("workforce.performance.colDhu")}
                </th>
                <th className="py-2.5 px-3 font-medium text-right">
                  {t("workforce.performance.colAttendance30d")}
                </th>
                <th className="py-2.5 px-3 font-medium">8w Trend</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-border-subtle text-body">
              {workerRows.slice(0, 40).map((r) => {
                const barPct = Math.min(100, Math.round((r.avgEff / Math.max(1, r.targetEff)) * 100));
                return (
                  <tr
                    key={r.worker.id}
                    onClick={() =>
                      router.push(
                        `/app/workforce/workers/${encodeURIComponent(r.worker.id)}?sub=performance`
                      )
                    }
                    data-testid="performance-row"
                    data-worker-id={r.worker.id}
                    className="cursor-pointer hover:bg-surface-2/60 transition-colors"
                  >
                    <td className="py-2 px-3 font-mono text-caption text-accent">{r.worker.id}</td>
                    <td className="py-2 px-3">
                      <span className="font-medium text-fg-primary">
                        {locale === "bn" ? r.worker.nameBn : r.worker.nameEn}
                      </span>
                      <span className="block text-caption text-fg-tertiary">
                        {roleLabel(t as (k: string) => string, r.worker.role)} · G{r.worker.grade}
                      </span>
                    </td>
                    <td className="py-2 px-3 text-caption text-fg-secondary">
                      {lineShortCode(r.worker.lineId)}
                    </td>
                    <td className="py-2 px-3 text-right font-mono text-caption text-fg-primary tabular-nums">
                      {fmtPctVal(r.latestEff)}
                    </td>
                    <td className="py-2 px-3 text-right font-mono text-body font-semibold text-fg-primary tabular-nums">
                      {fmtPctVal(r.avgEff)}
                    </td>
                    <td className="py-2 px-3 text-right font-mono text-caption text-fg-secondary tabular-nums">
                      {fmtPctVal(r.targetEff)}
                    </td>
                    <td className="py-2 px-3 w-32">
                      {/* Neutral single-colour progress bar for all rows */}
                      <div className="h-2 w-full rounded-full bg-surface-2 overflow-hidden">
                        <div
                          className="h-full bg-[var(--accent)]"
                          style={{ width: `${barPct}%` }}
                        />
                      </div>
                    </td>
                    <td className="py-2 px-3 text-right font-mono text-caption text-fg-primary tabular-nums">
                      {fmtPctVal(r.avgDhu)}
                    </td>
                    <td className="py-2 px-3 text-right font-mono text-caption text-fg-primary tabular-nums">
                      {fmtPctVal(r.att30d)}
                    </td>
                    <td className="py-2 px-3">
                      <Sparkline values={r.sparkValues} width={84} height={22} />
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </Panel>

      {/* Incentive & Bonus Preview (Admin only for BDT amounts) */}
      <Panel
        title={t("workforce.performance.bonusPanelTitle")}
        subtitle={t("workforce.performance.bonusPanelSubtitle")}
        icon={<Award size={15} />}
        right={<Badge tone={admin ? "accent" : "neutral"}>{admin ? "Admin" : "Restricted"}</Badge>}
        testId="wf-bonus-preview-panel"
      >
        {!admin ? (
          <Callout
            tone="info"
            icon-override={<Lock size={15} />}
            testId="wf-bonus-hidden-callout"
          >
            {t("workforce.performance.bonusHiddenNotice")}
          </Callout>
        ) : (
          <>
            <div className="overflow-x-auto max-h-[380px] overflow-y-auto">
              <table className="w-full text-left border-collapse" data-testid="wf-bonus-table">
                <thead className="sticky top-0 bg-surface-1 border-b border-border-subtle text-caption text-fg-tertiary">
                  <tr>
                    <th className="py-2 px-3 font-medium">{t("workforce.workers.colName")}</th>
                    <th className="py-2 px-3 font-medium">{t("workforce.workers.colGrade")}</th>
                    <th className="py-2 px-3 font-medium text-right">
                      {t("workforce.performance.colGrossWage")}
                    </th>
                    <th className="py-2 px-3 font-medium text-right">
                      {t("workforce.performance.colBasicWage")}
                    </th>
                    <th className="py-2 px-3 font-medium text-right">
                      {t("workforce.performance.colAttBonus")}
                    </th>
                    <th className="py-2 px-3 font-medium text-right">
                      {t("workforce.performance.colEffBonus")}
                    </th>
                    <th className="py-2 px-3 font-medium text-right">
                      {t("workforce.performance.colEidBonus")}
                    </th>
                    <th className="py-2 px-3 font-medium text-right">
                      {t("workforce.performance.colEstTotal")}
                    </th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-border-subtle text-caption tabular-nums">
                  {workerRows.slice(0, 20).map((r) => (
                    <tr key={r.worker.id} data-testid={`bonus-row-${r.worker.id}`}>
                      <td className="py-2 px-3 font-medium text-fg-primary">
                        {locale === "bn" ? r.worker.nameBn : r.worker.nameEn} ({r.worker.id})
                      </td>
                      <td className="py-2 px-3">G{r.worker.grade}</td>
                      <td className="py-2 px-3 text-right font-mono">
                        {formatBDT(r.bonus.grossMonthlyBdt, locale)}
                      </td>
                      <td className="py-2 px-3 text-right font-mono">
                        {formatBDT(r.bonus.basicMonthlyBdt, locale)}
                      </td>
                      <td className="py-2 px-3 text-right font-mono">
                        {formatBDT(r.bonus.attendanceBonusBdt, locale)}
                      </td>
                      <td className="py-2 px-3 text-right font-mono">
                        {formatBDT(r.bonus.efficiencyBonusBdt, locale)}
                      </td>
                      <td className="py-2 px-3 text-right font-mono">
                        {formatBDT(r.bonus.festivalBonusPreviewBdt, locale)}
                      </td>
                      <td className="py-2 px-3 text-right font-mono font-semibold text-accent">
                        {formatBDT(r.bonus.estimatedMonthlyTotalBdt, locale)}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <IllustrativePolicyNote
              text={t("workforce.illustrativeNote") as string}
              className="mt-3 text-caption text-fg-tertiary flex items-center gap-1.5"
            />
          </>
        )}
      </Panel>
    </div>
  );
}
