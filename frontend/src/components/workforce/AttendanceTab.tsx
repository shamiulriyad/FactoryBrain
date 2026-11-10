"use client";

import { useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { Calendar, CheckCheck, Clock, Moon, Sun, Users } from "lucide-react";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { ConfirmDialog } from "@/components/ui/Modal";
import { FilterSelect } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { EmptyState } from "@/components/ui/EmptyState";
import { useToast } from "@/components/ui/Toast";
import { useT } from "@/lib/useT";
import { formatNumber, bn } from "@/lib/format";
import { WORKFORCE_LINES, type WorkforceLineId } from "@/data/workforce/lines";
import { WORKFORCE_POLICY } from "@/data/workforce/policy";
import { addIsoDays, isFridayHoliday } from "@/data/workforce/seed";
import {
  ATTENDANCE_STATUSES,
  type AttendanceStatus
} from "@/data/workforce/types";
import { useWorkforceStore } from "@/store/workforce.store";
import { computeDayAttendanceSummary } from "@/lib/workforce";
import {
  LocalEditChip,
  attendanceStatusLabel,
  attendanceStatusTone,
  lineLabel,
  lineShortCode,
  roleLabel
} from "./shared";

const ROLL_CALL_BUTTONS: readonly AttendanceStatus[] = [
  "present",
  "absent",
  "late",
  "leave",
  "half_day"
];

type Props = {
  operator: boolean;
  actorEmail: string;
  initialDate?: string;
  initialLine?: string;
  initialAttStatus?: string;
  onSyncUrlParams?: (params: Record<string, string>) => void;
};

export function AttendanceTab({
  operator,
  actorEmail,
  initialDate,
  initialLine = "",
  initialAttStatus = "",
  onSyncUrlParams
}: Props) {
  const { t, locale } = useT();
  const toast = useToast();
  const state = useWorkforceStore();

  const [selectedDate, setSelectedDate] = useState(initialDate || state.anchorDate);
  const [lineFilter, setLineFilter] = useState(initialLine);
  const [shiftFilter, setShiftFilter] = useState("");
  const [statusFilter, setStatusFilter] = useState(initialAttStatus);
  const [confirmMarkLine, setConfirmMarkLine] = useState<WorkforceLineId | null>(null);

  useEffect(() => {
    if (initialDate) setSelectedDate(initialDate);
  }, [initialDate]);

  useEffect(() => {
    setLineFilter(initialLine);
  }, [initialLine]);

  useEffect(() => {
    setStatusFilter(initialAttStatus);
  }, [initialAttStatus]);

  function selectDateAndLine(date: string, line: string, attStatus = statusFilter) {
    setSelectedDate(date);
    setLineFilter(line);
    setStatusFilter(attStatus);
    onSyncUrlParams?.({ date, line, attStatus });
  }

  const dayMap = state.attendanceByDate[selectedDate];
  const summary = useMemo(
    () => computeDayAttendanceSummary(state.workers, dayMap, lineFilter, shiftFilter),
    [state.workers, dayMap, lineFilter, shiftFilter]
  );

  const rollCallWorkers = useMemo(() => {
    return state.workers.filter((w) => {
      if (w.status === "resigned") return false;
      if (lineFilter && w.lineId !== lineFilter) return false;
      if (shiftFilter && w.shift !== shiftFilter) return false;
      const st: AttendanceStatus = dayMap?.[w.id] ?? (w.status === "on_leave" ? "leave" : "present");
      if (statusFilter && st !== statusFilter) return false;
      return true;
    });
  }, [state.workers, lineFilter, shiftFilter, statusFilter, dayMap]);

  // 30-day dates for the monthly heatmap (oldest -> anchorDate)
  const last30Dates = useMemo(() => {
    const out: string[] = [];
    for (let d = 29; d >= 0; d--) {
      out.push(addIsoDays(state.anchorDate, -d));
    }
    return out;
  }, [state.anchorDate]);

  const targetBulkLine: WorkforceLineId = (lineFilter as WorkforceLineId) || "line-3";
  const shiftDisplay = state.ramadanHours
    ? WORKFORCE_POLICY.shifts.ramadan
    : WORKFORCE_POLICY.shifts.regular;

  const fmtNum = (n: number) => formatNumber(n, locale);

  return (
    <div className="space-y-4" data-testid="workforce-attendance">
      {/* Honesty callout: manual roll-call demo */}
      <Callout
        tone="info"
        title={t("workforce.attendance.calloutTitle")}
        testId="wf-attendance-manual-callout"
        action={
          <Button
            size="sm"
            variant="secondary"
            iconLeft={state.ramadanHours ? <Moon size={13} /> : <Sun size={13} />}
            data-testid="wf-ramadan-toggle"
            onClick={() => {
              const res = state.toggleRamadanHours(actorEmail);
              toast.push({
                title: (state.ramadanHours
                  ? t("workforce.ramadanOffToast")
                  : t("workforce.ramadanOnToast")) as string,
                tone: "info",
                onUndo: res.undo
              });
            }}
          >
            {t("workforce.ramadanToggle")}: {state.ramadanHours ? "ON" : "OFF"}
          </Button>
        }
      >
        <p>{t("workforce.attendance.calloutBody")}</p>
        <p className="mt-1 font-medium text-fg-primary" data-testid="wf-shift-hours-label">
          {locale === "bn" ? shiftDisplay.labelBn : shiftDisplay.labelEn} ·{" "}
          {locale === "bn"
            ? `সাপ্তাহিক ছুটি: ${WORKFORCE_POLICY.shifts.weeklyHolidayNameBn}`
            : `Weekly holiday: ${WORKFORCE_POLICY.shifts.weeklyHolidayNameEn}`}
        </p>
      </Callout>

      {/* Date, Line, Shift, Status filter bar + Mark Line Present button */}
      <div className="surface p-3 flex flex-wrap items-center gap-3">
        <div className="flex items-center gap-2">
          <label
            htmlFor="wf-att-date"
            className="text-caption text-fg-tertiary whitespace-nowrap"
          >
            {t("workforce.attendance.dateLabel")}
          </label>
          <Input
            id="wf-att-date"
            type="date"
            value={selectedDate}
            onChange={(e) => selectDateAndLine(e.target.value, lineFilter)}
            className="!h-8 !w-auto !text-caption"
            data-testid="wf-attendance-date"
          />
        </div>

        <FilterSelect
          label={t("workforce.workers.filterLine") as string}
          value={lineFilter}
          onChange={(v) => selectDateAndLine(selectedDate, v)}
          testId="wf-attendance-line"
        >
          <option value="">{t("workforce.workers.allLines")}</option>
          {WORKFORCE_LINES.map((l) => (
            <option key={l.id} value={l.id}>
              {locale === "bn" ? l.nameBn : l.nameEn}
            </option>
          ))}
        </FilterSelect>

        <FilterSelect
          label={t("workforce.workers.filterShift") as string}
          value={shiftFilter}
          onChange={setShiftFilter}
          testId="wf-attendance-shift"
        >
          <option value="">{t("workforce.workers.allShifts")}</option>
          <option value="A">Shift A</option>
          <option value="B">Shift B</option>
        </FilterSelect>

        <FilterSelect
          label={t("workforce.workers.filterStatus") as string}
          value={statusFilter}
          onChange={(v) => selectDateAndLine(selectedDate, lineFilter, v)}
          testId="wf-attendance-status-filter"
        >
          <option value="">{t("workforce.workers.allStatuses")}</option>
          {ATTENDANCE_STATUSES.map((st) => (
            <option key={st} value={st}>
              {attendanceStatusLabel(t as (k: string) => string, st)}
            </option>
          ))}
        </FilterSelect>

        {operator && (
          <Button
            size="sm"
            variant="primary"
            className="ml-auto"
            iconLeft={<CheckCheck size={14} />}
            onClick={() => setConfirmMarkLine(targetBulkLine)}
            data-testid="wf-mark-line-present-btn"
          >
            {t("workforce.attendance.markLinePresentBtn", {
              line: lineShortCode(targetBulkLine)
            })}
          </Button>
        )}
      </div>

      {/* Live summary bar */}
      <div
        className="surface-2 px-4 py-2.5 rounded-md flex flex-wrap items-center justify-between gap-2 text-caption"
        data-testid="wf-attendance-summary-bar"
      >
        <span className="font-medium text-fg-primary">
          {t("workforce.attendance.summaryBar", {
            present: fmtNum(summary.present),
            late: fmtNum(summary.late),
            absent: fmtNum(summary.absent),
            leave: fmtNum(summary.leave),
            halfDay: fmtNum(summary.halfDay),
            pct: locale === "bn" ? bn(summary.presentPct.toFixed(1)) : summary.presentPct.toFixed(1)
          })}
        </span>
        {isFridayHoliday(selectedDate) && (
          <Badge tone="info" testId="wf-friday-holiday-badge">
            {t("workforce.attendanceStatuses.holiday")}
          </Badge>
        )}
      </div>

      {/* Roll-call grid */}
      <Panel
        title={`${t("workforce.tabs.attendance")} — ${selectedDate} (${fmtNum(rollCallWorkers.length)})`}
        flush
        testId="wf-rollcall-panel"
      >
        {rollCallWorkers.length === 0 ? (
          <EmptyState
            icon={<Users size={20} />}
            title={t("workforce.workers.emptyTitle") as string}
            body={t("workforce.workers.emptyBody") as string}
          />
        ) : (
          <div className="overflow-x-auto max-h-[520px] overflow-y-auto">
            <table className="w-full text-left border-collapse" data-testid="wf-rollcall-table">
              <thead className="sticky top-0 bg-surface-1 z-10 border-b border-border-subtle text-caption text-fg-tertiary">
                <tr>
                  <th className="py-2.5 px-3 font-medium">{t("workforce.workers.colId")}</th>
                  <th className="py-2.5 px-3 font-medium">{t("workforce.workers.colName")}</th>
                  <th className="py-2.5 px-3 font-medium">{t("workforce.workers.colLine")}</th>
                  <th className="py-2.5 px-3 font-medium">{t("workforce.workers.colRole")}</th>
                  <th className="py-2.5 px-3 font-medium">{t("workforce.workers.colStatus")}</th>
                  {operator && (
                    <th className="py-2.5 px-3 font-medium">One-click roll-call</th>
                  )}
                </tr>
              </thead>
              <tbody className="divide-y divide-border-subtle text-body">
                {rollCallWorkers.map((w) => {
                  const curStatus: AttendanceStatus =
                    dayMap?.[w.id] ?? (w.status === "on_leave" ? "leave" : "present");
                  const edited = Boolean(state.attendanceEditedKeys[`${selectedDate}::${w.id}`]);
                  return (
                    <tr
                      key={w.id}
                      data-testid="attendance-row"
                      data-worker-id={w.id}
                      className="hover:bg-surface-2/50"
                    >
                      <td className="py-2 px-3 font-mono text-caption">
                        <Link
                          href={`/app/workforce/workers/${encodeURIComponent(w.id)}?sub=attendance`}
                          className="text-accent hover:underline"
                        >
                          {w.id}
                        </Link>
                      </td>
                      <td className="py-2 px-3">
                        <div className="flex flex-wrap items-center gap-1.5">
                          <Link
                            href={`/app/workforce/workers/${encodeURIComponent(w.id)}?sub=attendance`}
                            className="font-medium text-fg-primary hover:underline"
                          >
                            {locale === "bn" ? w.nameBn : w.nameEn}
                          </Link>
                          {edited && (
                            <LocalEditChip
                              label={t("workforce.localEditBadge") as string}
                              testId={`att-local-${w.id}`}
                            />
                          )}
                        </div>
                      </td>
                      <td className="py-2 px-3 text-caption text-fg-secondary">
                        {lineShortCode(w.lineId)} · {w.shift}
                      </td>
                      <td className="py-2 px-3 text-caption text-fg-secondary">
                        {roleLabel(t as (k: string) => string, w.role)}
                      </td>
                      <td className="py-2 px-3">
                        <Badge
                          tone={attendanceStatusTone(curStatus)}
                          testId={`attendance-current-${w.id}`}
                        >
                          {attendanceStatusLabel(t as (k: string) => string, curStatus)}
                        </Badge>
                      </td>
                      {operator && (
                        <td className="py-2 px-3">
                          <div className="flex flex-wrap gap-1">
                            {ROLL_CALL_BUTTONS.map((st) => {
                              const active = curStatus === st;
                              return (
                                <button
                                  key={st}
                                  type="button"
                                  data-testid={`attendance-status-${w.id}-${st}`}
                                  onClick={() => {
                                    if (active) return;
                                    const res = state.setWorkerAttendance(
                                      selectedDate,
                                      w.id,
                                      st,
                                      actorEmail
                                    );
                                    toast.push({
                                      title: t("workforce.attendance.statusUpdatedToast", {
                                        id: w.id,
                                        status: attendanceStatusLabel(
                                          t as (k: string) => string,
                                          st
                                        ),
                                        date: selectedDate
                                      }) as string,
                                      tone: "success",
                                      onUndo: res.undo
                                    });
                                  }}
                                  className={
                                    active
                                      ? "h-7 px-2 rounded text-[11px] font-semibold bg-[var(--btn-primary-bg)] text-[var(--btn-primary-fg)]"
                                      : "h-7 px-2 rounded text-[11px] border border-border-subtle bg-surface-2 text-fg-secondary hover:text-fg-primary"
                                  }
                                >
                                  {attendanceStatusLabel(t as (k: string) => string, st)}
                                </button>
                              );
                            })}
                          </div>
                        </td>
                      )}
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </Panel>

      {/* Monthly heatmap per line (30 days x 6 lines, every cell clickable) */}
      <Panel
        title={t("workforce.attendance.heatmapTitle")}
        subtitle={t("workforce.attendance.heatmapSubtitle")}
        icon={<Calendar size={15} />}
        testId="wf-attendance-heatmap-panel"
      >
        <div className="overflow-x-auto">
          <table className="w-full border-collapse text-caption" data-testid="wf-attendance-heatmap">
            <thead>
              <tr>
                <th className="py-1.5 pr-3 text-left font-medium text-fg-tertiary">Line</th>
                {last30Dates.map((d) => (
                  <th
                    key={d}
                    className="px-0.5 py-1 text-[10px] font-mono text-fg-tertiary text-center"
                    title={d}
                  >
                    {d.slice(8)}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {WORKFORCE_LINES.map((line) => (
                <tr key={line.id} className="border-t border-border-subtle/50">
                  <td className="py-1.5 pr-3 font-medium text-fg-primary whitespace-nowrap">
                    {line.code}
                  </td>
                  {last30Dates.map((d) => {
                    const holiday = isFridayHoliday(d);
                    const s = computeDayAttendanceSummary(
                      state.workers,
                      state.attendanceByDate[d],
                      line.id
                    );
                    const isSelected = d === selectedDate && lineFilter === line.id;
                    const bgClass = holiday
                      ? "bg-surface-2 text-fg-tertiary"
                      : s.presentPct >= 92
                      ? "bg-[var(--risk-low-soft)] text-[var(--risk-low)] border-[var(--risk-low-border)]"
                      : s.presentPct >= 84
                      ? "bg-[var(--risk-medium-soft)] text-[var(--risk-medium)] border-[var(--risk-medium-border)]"
                      : "bg-[var(--risk-high-soft)] text-[var(--risk-high)] border-[var(--risk-high-border)]";
                    return (
                      <td key={d} className="p-0.5 text-center">
                        <button
                          type="button"
                          onClick={() => selectDateAndLine(d, line.id, "")}
                          data-testid={`wf-heatmap-cell-${line.id}-${d}`}
                          title={`${line.code} · ${d}: ${holiday ? "Weekly holiday (Fri)" : `${s.presentPct}% present (${s.absent} absent)`}`}
                          className={`size-6 rounded border text-[10px] font-mono tabular-nums transition-transform hover:scale-110 ${bgClass} ${
                            isSelected ? "ring-2 ring-accent" : ""
                          }`}
                        >
                          {holiday ? "F" : Math.round(s.presentPct)}
                        </button>
                      </td>
                    );
                  })}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Panel>

      {/* Confirm modal for "Mark all on line present" */}
      <ConfirmDialog
        open={Boolean(confirmMarkLine)}
        title={
          confirmMarkLine
            ? t("workforce.attendance.markLineConfirmTitle", {
                line: lineLabel(confirmMarkLine, locale)
              })
            : ""
        }
        description={
          confirmMarkLine
            ? t("workforce.attendance.markLineConfirmDesc", {
                line: lineLabel(confirmMarkLine, locale),
                date: selectedDate
              })
            : ""
        }
        confirmLabel={t("common.confirm")}
        cancelLabel={t("common.cancel")}
        tone="primary"
        testId="wf-mark-line-confirm-modal"
        onCancel={() => setConfirmMarkLine(null)}
        onConfirm={() => {
          if (!confirmMarkLine) return;
          const target = confirmMarkLine;
          setConfirmMarkLine(null);
          const res = state.markLinePresent(selectedDate, target, shiftFilter, actorEmail);
          toast.push({
            title: t("workforce.attendance.markLineToast", {
              n: fmtNum(res.count),
              line: lineShortCode(target),
              date: selectedDate
            }) as string,
            tone: "success",
            onUndo: res.undo
          });
        }}
      />
    </div>
  );
}
