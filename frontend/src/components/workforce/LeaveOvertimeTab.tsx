"use client";

import { useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { AlertTriangle, CalendarPlus, Check, Clock, PlusCircle, X } from "lucide-react";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { Modal } from "@/components/ui/Modal";
import { Field, FilterSelect, Select } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { EmptyState } from "@/components/ui/EmptyState";
import { useToast } from "@/components/ui/Toast";
import { useT } from "@/lib/useT";
import { formatBDT, formatNumber, bn } from "@/lib/format";
import { WORKFORCE_LINES } from "@/data/workforce/lines";
import { WORKFORCE_POLICY, getHourlyBasicBdt, getOvertimeHourlyRateBdt } from "@/data/workforce/policy";
import { addIsoDays } from "@/data/workforce/seed";
import {
  LEAVE_TYPES,
  type LeaveRequestStatus,
  type LeaveType
} from "@/data/workforce/types";
import { useWorkforceStore } from "@/store/workforce.store";
import {
  computeLeaveBalances,
  getWorkerCurrentOvertimeHours,
  getWorkerCurrentWeeklyHours,
  hasConsecutiveWorkloadAlert,
  isOverWeeklyCap,
  validateLeaveRequest
} from "@/lib/workforce";
import {
  IllustrativePolicyNote,
  LocalEditChip,
  WorkloadAlertChip,
  leaveStatusLabel,
  leaveStatusTone,
  leaveTypeLabel,
  lineShortCode
} from "./shared";

type Props = {
  operator: boolean;
  admin: boolean;
  actorEmail: string;
  initialStatusFilter?: string;
};

export function LeaveOvertimeTab({
  operator,
  admin,
  actorEmail,
  initialStatusFilter = "pending"
}: Props) {
  const { t, locale } = useT();
  const toast = useToast();
  const state = useWorkforceStore();

  const [statusTab, setStatusTab] = useState<"pending" | "approved" | "rejected" | "all">(
    initialStatusFilter === "approved" || initialStatusFilter === "rejected" || initialStatusFilter === "all"
      ? initialStatusFilter
      : "pending"
  );
  const [otLineFilter, setOtLineFilter] = useState("");
  const [otOnlyFlagged, setOtOnlyFlagged] = useState(false);
  const [decisionNotes, setDecisionNotes] = useState<Record<string, string>>({});
  const [createOpen, setCreateOpen] = useState(false);

  useEffect(() => {
    if (
      initialStatusFilter === "pending" ||
      initialStatusFilter === "approved" ||
      initialStatusFilter === "rejected" ||
      initialStatusFilter === "all"
    ) {
      setStatusTab(initialStatusFilter);
    }
  }, [initialStatusFilter]);

  const filteredRequests = useMemo(() => {
    return state.leaveRequests.filter((r) => (statusTab === "all" ? true : r.status === statusTab));
  }, [state.leaveRequests, statusTab]);

  const activeWorkers = useMemo(() => {
    return state.workers
      .filter((w) => {
        if (w.status === "resigned") return false;
        if (otLineFilter && w.lineId !== otLineFilter) return false;
        if (otOnlyFlagged) {
          const cur = getWorkerCurrentWeeklyHours(w);
          if (!isOverWeeklyCap(cur) && !hasConsecutiveWorkloadAlert(w.weeklyHoursHistory)) return false;
        }
        return true;
      })
      .sort((a, b) => getWorkerCurrentWeeklyHours(b) - getWorkerCurrentWeeklyHours(a));
  }, [state.workers, otLineFilter, otOnlyFlagged]);

  const fmtNum = (n: number) => formatNumber(n, locale);

  return (
    <div className="space-y-4" data-testid="workforce-leave">
      {/* Leave request queue */}
      <Panel
        title={t("workforce.leave.queueTitle")}
        subtitle={t("workforce.leave.queueSubtitle")}
        right={
          <div className="flex flex-wrap items-center gap-2">
            {(["pending", "approved", "rejected", "all"] as const).map((st) => (
              <Button
                key={st}
                size="sm"
                variant={statusTab === st ? "primary" : "secondary"}
                onClick={() => setStatusTab(st)}
                data-testid={`wf-leave-filter-${st}`}
              >
                {st === "pending"
                  ? t("workforce.leave.tabPending")
                  : st === "approved"
                  ? t("workforce.leave.tabApproved")
                  : st === "rejected"
                  ? t("workforce.leave.tabRejected")
                  : t("workforce.leave.tabAll")}
              </Button>
            ))}
            {operator && (
              <Button
                size="sm"
                variant="primary"
                iconLeft={<CalendarPlus size={14} />}
                onClick={() => setCreateOpen(true)}
                data-testid="wf-new-leave-btn"
              >
                {t("workforce.leave.newRequestBtn")}
              </Button>
            )}
          </div>
        }
        flush
        testId="wf-leave-queue-panel"
      >
        {filteredRequests.length === 0 ? (
          <EmptyState
            icon={<Clock size={20} />}
            title={t("workforce.overview.needsAttentionEmpty") as string}
          />
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-left border-collapse" data-testid="wf-leave-table">
              <thead>
                <tr className="border-b border-border-subtle text-caption text-fg-tertiary">
                  <th className="py-2.5 px-3 font-medium">ID</th>
                  <th className="py-2.5 px-3 font-medium">{t("workforce.leave.colWorker")}</th>
                  <th className="py-2.5 px-3 font-medium">{t("workforce.leave.colType")}</th>
                  <th className="py-2.5 px-3 font-medium">{t("workforce.leave.colDates")}</th>
                  <th className="py-2.5 px-3 font-medium">{t("workforce.leave.colBalance")}</th>
                  <th className="py-2.5 px-3 font-medium">{t("workforce.leave.colReason")}</th>
                  <th className="py-2.5 px-3 font-medium">{t("workforce.leave.colStatus")}</th>
                  {operator && (
                    <th className="py-2.5 px-3 font-medium">{t("workforce.leave.colActions")}</th>
                  )}
                </tr>
              </thead>
              <tbody className="divide-y divide-border-subtle text-body">
                {filteredRequests.map((req) => {
                  const worker = state.workers.find((w) => w.id === req.workerId);
                  const balances = worker ? computeLeaveBalances(worker) : null;
                  const bal = balances?.[req.type];
                  return (
                    <tr
                      key={req.id}
                      data-testid="leave-request-row"
                      data-request-id={req.id}
                      className="hover:bg-surface-2/50"
                    >
                      <td className="py-2.5 px-3 font-mono text-caption text-fg-secondary">
                        <div className="flex items-center gap-1.5">
                          <span>{req.id}</span>
                          {req.updatedByLocal && (
                            <LocalEditChip
                              label={t("workforce.localEditBadge") as string}
                              testId={`leave-local-${req.id}`}
                            />
                          )}
                        </div>
                      </td>
                      <td className="py-2.5 px-3">
                        <Link
                          href={`/app/workforce/workers/${encodeURIComponent(req.workerId)}?sub=leave`}
                          className="font-medium text-fg-primary hover:underline"
                        >
                          {worker
                            ? `${locale === "bn" ? worker.nameBn : worker.nameEn} (${worker.id})`
                            : req.workerId}
                        </Link>
                        {worker && (
                          <span className="block text-caption text-fg-tertiary">
                            {lineShortCode(worker.lineId)} · Shift {worker.shift}
                          </span>
                        )}
                      </td>
                      <td className="py-2.5 px-3 text-caption">
                        {leaveTypeLabel(t as (k: string) => string, req.type)}
                      </td>
                      <td className="py-2.5 px-3 text-caption tabular-nums">
                        {req.startDate} → {req.endDate} ({fmtNum(req.days)}d)
                      </td>
                      <td className="py-2.5 px-3 text-caption tabular-nums">
                        {bal ? (
                          <span>
                            {fmtNum(bal.remaining)} / {fmtNum(bal.entitled)}d
                          </span>
                        ) : (
                          "—"
                        )}
                      </td>
                      <td className="py-2.5 px-3 text-caption text-fg-secondary">
                        {operator ? (
                          <div>
                            <p>{locale === "bn" ? req.reasonBn : req.reasonEn}</p>
                            {req.decisionNote && (
                              <p className="mt-0.5 text-[11px] text-fg-tertiary">
                                Note: {req.decisionNote}
                              </p>
                            )}
                          </div>
                        ) : (
                          <span
                            className="italic text-fg-tertiary"
                            data-testid={`leave-reason-hidden-${req.id}`}
                          >
                            {t("workforce.leave.reasonHiddenViewer")}
                          </span>
                        )}
                      </td>
                      <td className="py-2.5 px-3">
                        <Badge tone={leaveStatusTone(req.status)} testId={`leave-status-${req.id}`}>
                          {leaveStatusLabel(t as (k: string) => string, req.status)}
                        </Badge>
                      </td>
                      {operator && (
                        <td className="py-2.5 px-3">
                          {req.status === "pending" ? (
                            <div className="flex flex-wrap items-center gap-1.5">
                              <Input
                                value={decisionNotes[req.id] ?? ""}
                                onChange={(e) =>
                                  setDecisionNotes((prev) => ({
                                    ...prev,
                                    [req.id]: e.target.value
                                  }))
                                }
                                placeholder={
                                  t("workforce.leave.decisionNotePlaceholder") as string
                                }
                                className="!h-7 !w-36 !text-caption"
                                data-testid={`leave-note-${req.id}`}
                              />
                              <Button
                                size="sm"
                                variant="success"
                                data-testid={`leave-approve-${req.id}`}
                                onClick={() => {
                                  const res = state.decideLeaveRequest(
                                    req.id,
                                    "approved",
                                    decisionNotes[req.id],
                                    actorEmail
                                  );
                                  toast.push({
                                    title: t("workforce.leave.approvedToast", {
                                      id: req.id
                                    }) as string,
                                    tone: "success",
                                    onUndo: res.undo
                                  });
                                }}
                              >
                                {t("workforce.leave.approveBtn")}
                              </Button>
                              <Button
                                size="sm"
                                variant="danger"
                                data-testid={`leave-reject-${req.id}`}
                                onClick={() => {
                                  const res = state.decideLeaveRequest(
                                    req.id,
                                    "rejected",
                                    decisionNotes[req.id],
                                    actorEmail
                                  );
                                  toast.push({
                                    title: t("workforce.leave.rejectedToast", {
                                      id: req.id
                                    }) as string,
                                    tone: "info",
                                    onUndo: res.undo
                                  });
                                }}
                              >
                                {t("workforce.leave.rejectBtn")}
                              </Button>
                            </div>
                          ) : (
                            <span className="text-caption text-fg-tertiary">
                              {req.decidedBy ?? "—"}
                            </span>
                          )}
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

      {/* Festival holidays reference strip */}
      <Panel
        title={locale === "bn" ? "জাতীয় ও উৎসব ছুটির তালিকা (ডেমো)" : "Festival & National Holidays (demo policy)"}
        subtitle={t("workforce.illustrativeNote")}
        testId="wf-festival-holidays-panel"
      >
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-2.5">
          {WORKFORCE_POLICY.leave.festivalHolidays.map((fh) => (
            <div key={fh.id} className="surface-2 p-2.5 rounded-md text-caption">
              <div className="flex items-center justify-between gap-2">
                <span className="font-semibold text-fg-primary">
                  {locale === "bn" ? fh.nameBn : fh.nameEn}
                </span>
                <Badge tone="info">{fmtNum(fh.days)}d</Badge>
              </div>
              <p className="mt-1 text-fg-tertiary">{locale === "bn" ? fh.noteBn : fh.noteEn}</p>
            </div>
          ))}
        </div>
      </Panel>

      {/* Overtime table */}
      <Panel
        title={t("workforce.leave.overtimeTitle")}
        subtitle={t("workforce.leave.overtimeSubtitle")}
        right={
          <div className="flex flex-wrap items-center gap-2">
            <FilterSelect
              label={t("workforce.workers.filterLine") as string}
              value={otLineFilter}
              onChange={setOtLineFilter}
              testId="wf-ot-line-filter"
            >
              <option value="">{t("workforce.workers.allLines")}</option>
              {WORKFORCE_LINES.map((l) => (
                <option key={l.id} value={l.id}>
                  {locale === "bn" ? l.nameBn : l.nameEn}
                </option>
              ))}
            </FilterSelect>
            <Button
              size="sm"
              variant={otOnlyFlagged ? "primary" : "secondary"}
              onClick={() => setOtOnlyFlagged((v) => !v)}
              data-testid="wf-ot-flagged-toggle"
            >
              {locale === "bn" ? "শুধু >৬০ঘ. সতর্কতা" : "Only >60 h flagged"}
            </Button>
          </div>
        }
        flush
        testId="wf-overtime-panel"
      >
        <div className="px-4 py-2.5 border-b border-border-subtle bg-surface-2/40 text-caption text-fg-secondary">
          {t("workforce.leave.overtimeRateNote")}
        </div>
        <div className="overflow-x-auto max-h-[480px] overflow-y-auto">
          <table className="w-full text-left border-collapse" data-testid="wf-overtime-table">
            <thead className="sticky top-0 bg-surface-1 z-10 border-b border-border-subtle text-caption text-fg-tertiary">
              <tr>
                <th className="py-2.5 px-3 font-medium">{t("workforce.leave.colWorker")}</th>
                <th className="py-2.5 px-3 font-medium">{t("workforce.workers.colLine")}</th>
                <th className="py-2.5 px-3 font-medium text-right">{t("workforce.leave.colW1")}</th>
                <th className="py-2.5 px-3 font-medium text-right">{t("workforce.leave.colW2")}</th>
                <th className="py-2.5 px-3 font-medium text-right">{t("workforce.leave.colW3")}</th>
                <th className="py-2.5 px-3 font-medium text-right">
                  {t("workforce.leave.colCurrentWeek")}
                </th>
                <th className="py-2.5 px-3 font-medium text-right">
                  {t("workforce.leave.colOtHours")}
                </th>
                {admin && (
                  <th className="py-2.5 px-3 font-medium text-right">
                    {t("workforce.leave.colOtRate")}
                  </th>
                )}
                <th className="py-2.5 px-3 font-medium">{t("workforce.leave.colAlerts")}</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-border-subtle text-body">
              {activeWorkers.slice(0, 35).map((w) => {
                const [w1, w2, w3, cur] = w.weeklyHoursHistory;
                const otHrs = getWorkerCurrentOvertimeHours(w);
                const overCap = isOverWeeklyCap(cur);
                const workloadAlert = hasConsecutiveWorkloadAlert(w.weeklyHoursHistory);
                const hourlyBasic = getHourlyBasicBdt(w.grade);
                const otRate = getOvertimeHourlyRateBdt(w.grade);
                const otPay = Math.round(otHrs * otRate);

                return (
                  <tr
                    key={w.id}
                    data-testid="overtime-row"
                    data-worker-id={w.id}
                    className="hover:bg-surface-2/50"
                  >
                    <td className="py-2 px-3">
                      <Link
                        href={`/app/workforce/workers/${encodeURIComponent(w.id)}`}
                        className="font-medium text-fg-primary hover:underline"
                      >
                        {locale === "bn" ? w.nameBn : w.nameEn}{" "}
                        <span className="font-mono text-caption text-accent">({w.id})</span>
                      </Link>
                    </td>
                    <td className="py-2 px-3 text-caption text-fg-secondary">
                      {lineShortCode(w.lineId)} · G{w.grade}
                    </td>
                    <td className="py-2 px-3 text-right font-mono text-caption tabular-nums">
                      {fmtNum(w1)}h
                    </td>
                    <td className="py-2 px-3 text-right font-mono text-caption tabular-nums">
                      {fmtNum(w2)}h
                    </td>
                    <td className="py-2 px-3 text-right font-mono text-caption tabular-nums">
                      {fmtNum(w3)}h
                    </td>
                    <td className="py-2 px-3 text-right font-mono text-body font-semibold tabular-nums">
                      <span className={overCap ? "text-[var(--risk-high)]" : "text-fg-primary"}>
                        {fmtNum(cur)}h
                      </span>
                    </td>
                    <td className="py-2 px-3 text-right font-mono text-caption tabular-nums">
                      {fmtNum(otHrs)}h
                    </td>
                    {admin && (
                      <td
                        className="py-2 px-3 text-right font-mono text-caption text-fg-secondary tabular-nums"
                        data-testid={`ot-admin-pay-${w.id}`}
                      >
                        2×{formatBDT(hourlyBasic, locale)} = {formatBDT(otPay, locale)}
                      </td>
                    )}
                    <td className="py-2 px-3">
                      <div className="flex flex-wrap items-center gap-1.5">
                        {overCap && (
                          <Badge tone="danger" testId={`ot-cap-warning-${w.id}`}>
                            {t("workforce.leave.capWarningChip")}
                          </Badge>
                        )}
                        {workloadAlert && (
                          <WorkloadAlertChip
                            workerId={w.id}
                            label={t("workforce.workloadAlertBadge") as string}
                            testId={`ot-workload-chip-${w.id}`}
                          />
                        )}
                        {operator && overCap && (
                          <Button
                            size="sm"
                            variant="secondary"
                            data-testid={`ot-cap-btn-${w.id}`}
                            onClick={() => {
                              const res = state.adjustWorkerWeeklyHours(w.id, 54, actorEmail);
                              toast.push({
                                title:
                                  locale === "bn"
                                    ? `${w.id}-এর চলতি সপ্তাহের কর্মঘণ্টা ৫৪ ঘণ্টায় সীমিত করা হয়েছে`
                                    : `Capped ${w.id} current week hours at 54 h`,
                                tone: "success",
                                onUndo: res.undo
                              });
                            }}
                          >
                            {t("workforce.leave.capHoursBtn")}
                          </Button>
                        )}
                        {!overCap && !workloadAlert && (
                          <span className="text-caption text-fg-tertiary">OK (≤60h)</span>
                        )}
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </Panel>

      {/* Create Leave Request Modal */}
      <CreateLeaveModal
        open={createOpen}
        onClose={() => setCreateOpen(false)}
        actorEmail={actorEmail}
        onCreated={(id, undo) => {
          setCreateOpen(false);
          toast.push({
            title: t("workforce.leave.createdToast", { id }) as string,
            tone: "success",
            onUndo: undo
          });
        }}
      />
    </div>
  );
}

export function CreateLeaveModal({
  open,
  onClose,
  actorEmail,
  defaultWorkerId,
  onCreated
}: {
  open: boolean;
  onClose: () => void;
  actorEmail: string;
  defaultWorkerId?: string;
  onCreated: (id: string, undo: () => void) => void;
}) {
  const { t, locale } = useT();
  const state = useWorkforceStore();

  const activeWorkers = useMemo(
    () => state.workers.filter((w) => w.status !== "resigned"),
    [state.workers]
  );

  const [workerId, setWorkerId] = useState(defaultWorkerId || activeWorkers[0]?.id || "W-0001");
  const [leaveType, setLeaveType] = useState<LeaveType>("casual");
  const [startDate, setStartDate] = useState(addIsoDays(state.anchorDate, 1));
  const [endDate, setEndDate] = useState(addIsoDays(state.anchorDate, 2));
  const [days, setDays] = useState("2");
  const [reasonEn, setReasonEn] = useState("");
  const [reasonBn, setReasonBn] = useState("");
  const [errorMsg, setErrorMsg] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    if (defaultWorkerId) setWorkerId(defaultWorkerId);
    setErrorMsg(null);
    setReasonEn("");
    setReasonBn("");
    setDays("2");
  }, [open, defaultWorkerId]);

  const selectedWorker = state.workers.find((w) => w.id === workerId) ?? activeWorkers[0];
  const balances = selectedWorker ? computeLeaveBalances(selectedWorker) : null;
  const curBalance = balances?.[leaveType];

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!selectedWorker) return;
    const numDays = Number(days);
    if (!Number.isFinite(numDays) || numDays <= 0 || !reasonEn.trim()) {
      setErrorMsg(t("workforce.leave.errInvalidDays") as string);
      return;
    }

    const check = validateLeaveRequest(selectedWorker, leaveType, numDays);
    if (!check.valid) {
      if (check.reasonCode === "ineligible_maternity") {
        setErrorMsg(t("workforce.leave.errIneligibleMaternity") as string);
      } else {
        setErrorMsg(
          t("workforce.leave.errOverdrawn", {
            days: numDays,
            remaining: check.remaining
          }) as string
        );
      }
      return;
    }

    const res = state.createLeaveRequest(
      {
        workerId: selectedWorker.id,
        type: leaveType,
        startDate,
        endDate,
        days: numDays,
        reasonEn,
        reasonBn
      },
      actorEmail
    );
    if (!res.ok) {
      setErrorMsg(
        t("workforce.leave.errOverdrawn", {
          days: numDays,
          remaining: curBalance?.remaining ?? 0
        }) as string
      );
      return;
    }
    onCreated(res.id, res.undo);
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={t("workforce.leave.createModalTitle")}
      testId="wf-create-leave-modal"
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button
            variant="primary"
            onClick={handleSubmit}
            data-testid="wf-create-leave-submit"
          >
            {t("common.confirm")}
          </Button>
        </>
      }
    >
      <form onSubmit={handleSubmit} className="space-y-3">
        <Field label={t("workforce.leave.fieldWorker")}>
          {({ id }) => (
            <Select
              id={id}
              value={workerId}
              onChange={(e) => {
                setWorkerId(e.target.value);
                setErrorMsg(null);
              }}
              data-testid="wf-leave-input-worker"
            >
              {activeWorkers.map((w) => (
                <option key={w.id} value={w.id}>
                  {w.id} — {locale === "bn" ? w.nameBn : w.nameEn} ({lineShortCode(w.lineId)})
                </option>
              ))}
            </Select>
          )}
        </Field>

        <Field
          label={t("workforce.leave.fieldLeaveType")}
          hint={
            curBalance
              ? t("workforce.leave.balancePreview", {
                  entitled: curBalance.entitled,
                  used: curBalance.used,
                  remaining: curBalance.remaining
                })
              : undefined
          }
        >
          {({ id }) => (
            <Select
              id={id}
              value={leaveType}
              onChange={(e) => {
                setLeaveType(e.target.value as LeaveType);
                setErrorMsg(null);
              }}
              data-testid="wf-leave-input-type"
            >
              {LEAVE_TYPES.map((lt) => (
                <option key={lt} value={lt}>
                  {leaveTypeLabel(t as (k: string) => string, lt)}
                </option>
              ))}
            </Select>
          )}
        </Field>

        <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
          <Field label={t("workforce.leave.fieldStartDate")}>
            {({ id }) => (
              <Input
                id={id}
                type="date"
                value={startDate}
                onChange={(e) => setStartDate(e.target.value)}
                data-testid="wf-leave-input-start"
              />
            )}
          </Field>
          <Field label={t("workforce.leave.fieldEndDate")}>
            {({ id }) => (
              <Input
                id={id}
                type="date"
                value={endDate}
                onChange={(e) => setEndDate(e.target.value)}
                data-testid="wf-leave-input-end"
              />
            )}
          </Field>
          <Field label={t("workforce.leave.fieldDays")}>
            {({ id }) => (
              <Input
                id={id}
                type="number"
                value={days}
                onChange={(e) => {
                  setDays(e.target.value);
                  setErrorMsg(null);
                }}
                data-testid="wf-leave-input-days"
              />
            )}
          </Field>
        </div>

        <Field label={t("workforce.leave.fieldReasonEn")}>
          {({ id }) => (
            <Input
              id={id}
              value={reasonEn}
              onChange={(e) => {
                setReasonEn(e.target.value);
                setErrorMsg(null);
              }}
              placeholder="e.g. Clinic appointment in Savar"
              data-testid="wf-leave-input-reason-en"
            />
          )}
        </Field>

        <Field label={t("workforce.leave.fieldReasonBn")}>
          {({ id }) => (
            <Input
              id={id}
              value={reasonBn}
              onChange={(e) => setReasonBn(e.target.value)}
              placeholder="ঐচ্ছিক বাংলা কারণ"
              data-testid="wf-leave-input-reason-bn"
            />
          )}
        </Field>

        {errorMsg && (
          <Callout tone="danger" role="alert" testId="wf-leave-balance-error">
            {errorMsg}
          </Callout>
        )}
      </form>
    </Modal>
  );
}
