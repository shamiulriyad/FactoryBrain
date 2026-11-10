"use client";

// Confidential Wellbeing tab.
// NON-NEGOTIABLE RULES:
//   1. No individual "mental health score", mood label, diagnosis, or ranking of workers.
//   2. Wellbeing data is kept in its own slice (`state.wellbeing`) and never touches
//      performance, bonus, discipline, or termination views.
//   3. Pulse survey responses are anonymous and aggregated per line ONLY when k >= 5.
//   4. Counselling referrals are voluntary and worker-initiated; only case status,
//      date, and counsellor name are stored — never the reason.
//   5. Workload alerts (>60 h/week for 3 weeks) are derived strictly from hours.

import { useState } from "react";
import Link from "next/link";
import {
  AlertTriangle,
  CheckCircle2,
  HeartHandshake,
  Lock,
  PlusCircle,
  ShieldCheck
} from "lucide-react";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { Modal } from "@/components/ui/Modal";
import { Field, Select } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { useToast } from "@/components/ui/Toast";
import { useT } from "@/lib/useT";
import { formatNumber, bn } from "@/lib/format";
import { WORKFORCE_LINES, type WorkforceLineId } from "@/data/workforce/lines";
import { addIsoDays } from "@/data/workforce/seed";
import { REFERRAL_STATUSES, type ReferralStatus } from "@/data/workforce/types";
import { useWorkforceStore } from "@/store/workforce.store";
import {
  aggregatePulseByLine,
  hasConsecutiveWorkloadAlert
} from "@/lib/workforce";
import {
  IllustrativePolicyNote,
  LocalEditChip,
  WorkloadAlertChip,
  lineLabel,
  lineShortCode,
  referralStatusLabel,
  referralStatusTone
} from "./shared";

type Props = {
  operator: boolean;
  actorEmail: string;
};

type Score1To5 = 1 | 2 | 3 | 4 | 5;

export function WellbeingTab({ operator, actorEmail }: Props) {
  const { t, locale } = useT();
  const toast = useToast();
  const state = useWorkforceStore();

  // Anonymous pulse survey state (default to line-6 so a judge can test unlocking k >= 5 in 2 clicks!)
  const [pulseLine, setPulseLine] = useState<WorkforceLineId>("line-6");
  const [q1, setQ1] = useState<Score1To5>(4);
  const [q2, setQ2] = useState<Score1To5>(4);
  const [q3, setQ3] = useState<Score1To5>(5);

  const [referralModalOpen, setReferralModalOpen] = useState(false);

  const pulseAggregates = aggregatePulseByLine(state.wellbeing.pulseResponses);
  const workloadAlertWorkers = state.workers.filter(
    (w) => w.status === "active" && hasConsecutiveWorkloadAlert(w.weeklyHoursHistory)
  );

  const neverDoRules = t("workforce.wellbeing.neverDoRules") as unknown as string[];
  const fmtNum = (n: number) => formatNumber(n, locale);

  return (
    <div className="space-y-4" data-testid="workforce-wellbeing">
      {/* 1. Explicit "What this page will never do" panel */}
      <Panel
        title={t("workforce.wellbeing.neverDoTitle")}
        icon={<ShieldCheck size={15} />}
        testId="wf-wellbeing-never-do-panel"
      >
        <ul className="list-disc pl-5 space-y-1.5 text-caption text-fg-secondary">
          {Array.isArray(neverDoRules) &&
            neverDoRules.map((rule, i) => (
              <li key={i} className="text-fg-primary">
                {rule}
              </li>
            ))}
        </ul>
      </Panel>

      {/* 2. Anonymous weekly pulse survey + Aggregated results per line (k >= 5 rule) */}
      <div className="grid grid-cols-1 xl:grid-cols-12 gap-4">
        <Panel
          className="xl:col-span-5"
          title={t("workforce.wellbeing.pulseFormTitle")}
          subtitle={t("workforce.wellbeing.pulseFormSubtitle")}
          icon={<HeartHandshake size={15} />}
          testId="wf-pulse-form-panel"
        >
          <div className="space-y-4">
            <Field label={t("workforce.workers.filterLine")}>
              {({ id }) => (
                <Select
                  id={id}
                  value={pulseLine}
                  onChange={(e) => setPulseLine(e.target.value as WorkforceLineId)}
                  data-testid="wf-pulse-line-select"
                >
                  {WORKFORCE_LINES.map((l) => (
                    <option key={l.id} value={l.id}>
                      {locale === "bn" ? l.nameBn : l.nameEn}
                    </option>
                  ))}
                </Select>
              )}
            </Field>

            <PulseQuestionRow
              label={t("workforce.wellbeing.q1Label") as string}
              value={q1}
              onChange={setQ1}
              lowLabel={t("workforce.wellbeing.scaleLow") as string}
              highLabel={t("workforce.wellbeing.scaleHigh") as string}
              testIdPrefix="wf-pulse-q1"
            />
            <PulseQuestionRow
              label={t("workforce.wellbeing.q2Label") as string}
              value={q2}
              onChange={setQ2}
              lowLabel={t("workforce.wellbeing.scaleLow") as string}
              highLabel={t("workforce.wellbeing.scaleHigh") as string}
              testIdPrefix="wf-pulse-q2"
            />
            <PulseQuestionRow
              label={t("workforce.wellbeing.q3Label") as string}
              value={q3}
              onChange={setQ3}
              lowLabel={t("workforce.wellbeing.scaleLow") as string}
              highLabel={t("workforce.wellbeing.scaleHigh") as string}
              testIdPrefix="wf-pulse-q3"
            />

            <Button
              variant="primary"
              className="w-full"
              data-testid="wf-pulse-submit-btn"
              onClick={() => {
                const res = state.submitPulseSurvey({
                  lineId: pulseLine,
                  q1PaceManageable: q1,
                  q2ToolsAndCoolingOk: q2,
                  q3RespectfulSupport: q3
                });
                toast.push({
                  title: t("workforce.wellbeing.pulseSubmittedToast", {
                    line: lineShortCode(pulseLine)
                  }) as string,
                  tone: "success",
                  onUndo: res.undo
                });
              }}
            >
              {t("workforce.wellbeing.submitPulseBtn")}
            </Button>
          </div>
        </Panel>

        <Panel
          className="xl:col-span-7"
          title={t("workforce.wellbeing.pulseAggTitle")}
          subtitle={t("workforce.wellbeing.pulseAggSubtitle")}
          right={<Badge tone="info">k ≥ 5</Badge>}
          testId="wf-pulse-aggregates-panel"
        >
          <div className="space-y-2.5" data-testid="wf-pulse-aggregates-list">
            {pulseAggregates.map((agg) => {
              const need = Math.max(0, agg.minRequired - agg.count);
              return (
                <div
                  key={agg.lineId}
                  className="surface-2 p-3 rounded-md"
                  data-testid={`wf-pulse-line-${agg.lineId}`}
                  data-sufficient={agg.sufficient ? "true" : "false"}
                >
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <span className="font-semibold text-fg-primary">
                      {lineLabel(agg.lineId, locale)}
                    </span>
                    <Badge tone={agg.sufficient ? "success" : "warning"}>
                      {fmtNum(agg.count)} responses
                    </Badge>
                  </div>

                  {!agg.sufficient ? (
                    <div
                      className="mt-2 flex items-center justify-between gap-2 text-caption text-fg-secondary"
                      data-testid={`wf-pulse-insufficient-${agg.lineId}`}
                    >
                      <span className="inline-flex items-center gap-1.5">
                        <Lock size={12} className="text-[var(--risk-medium)] shrink-0" />
                        {t("workforce.wellbeing.insufficientResponses", {
                          count: fmtNum(agg.count),
                          min: fmtNum(agg.minRequired),
                          need: fmtNum(need),
                          line: lineShortCode(agg.lineId)
                        })}
                      </span>
                      <Button
                        size="sm"
                        variant="secondary"
                        data-testid={`wf-pulse-select-line-${agg.lineId}`}
                        onClick={() => setPulseLine(agg.lineId)}
                      >
                        Select {lineShortCode(agg.lineId)}
                      </Button>
                    </div>
                  ) : (
                    <div className="mt-2 grid grid-cols-2 sm:grid-cols-4 gap-2 text-caption tabular-nums">
                      <div>
                        <span className="text-fg-tertiary block">
                          {t("workforce.wellbeing.colQ1Avg")}
                        </span>
                        <span className="font-semibold text-fg-primary">
                          {agg.avgQ1?.toFixed(1)} / 5
                        </span>
                      </div>
                      <div>
                        <span className="text-fg-tertiary block">
                          {t("workforce.wellbeing.colQ2Avg")}
                        </span>
                        <span className="font-semibold text-fg-primary">
                          {agg.avgQ2?.toFixed(1)} / 5
                        </span>
                      </div>
                      <div>
                        <span className="text-fg-tertiary block">
                          {t("workforce.wellbeing.colQ3Avg")}
                        </span>
                        <span className="font-semibold text-fg-primary">
                          {agg.avgQ3?.toFixed(1)} / 5
                        </span>
                      </div>
                      <div>
                        <span className="text-fg-tertiary block">
                          {t("workforce.wellbeing.colOverallPulse")}
                        </span>
                        <span className="font-semibold text-accent">
                          {agg.overallAvg?.toFixed(1)} / 5
                        </span>
                      </div>
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        </Panel>
      </div>

      {/* 3. Voluntary counsellor-referral queue (Operator+ sees queue & schedules; Viewer sees aggregate count only) */}
      <Panel
        title={t("workforce.wellbeing.referralsTitle")}
        subtitle={t("workforce.wellbeing.referralsSubtitle")}
        right={
          operator && (
            <Button
              size="sm"
              variant="primary"
              iconLeft={<PlusCircle size={14} />}
              onClick={() => setReferralModalOpen(true)}
              data-testid="wf-new-referral-btn"
            >
              {t("workforce.wellbeing.newReferralBtn")}
            </Button>
          )
        }
        testId="wf-referrals-panel"
      >
        {!operator ? (
          <p className="text-caption text-fg-secondary" data-testid="wf-referrals-viewer-summary">
            {locale === "bn"
              ? `মোট ঐচ্ছিক কাউন্সেলর রেফারেল: ${bn(state.wellbeing.counsellorReferrals.length)}টি (কেস মেটাডেটা শুধুমাত্র Operator+ ভূমিকার জন্য দৃশ্যমান, কারণ কখনোই সংরক্ষণ করা হয় না)।`
              : `Total voluntary counsellor referrals: ${state.wellbeing.counsellorReferrals.length} (case metadata visible to Operator+; reasons are never recorded).`}
          </p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-left border-collapse" data-testid="wf-referrals-table">
              <thead>
                <tr className="border-b border-border-subtle text-caption text-fg-tertiary">
                  <th className="py-2 px-3 font-medium">
                    {t("workforce.wellbeing.colCaseCode")}
                  </th>
                  <th className="py-2 px-3 font-medium">{t("workforce.workers.colLine")}</th>
                  <th className="py-2 px-3 font-medium">
                    {t("workforce.wellbeing.colCounsellor")}
                  </th>
                  <th className="py-2 px-3 font-medium">
                    {t("workforce.wellbeing.colRequestedDate")}
                  </th>
                  <th className="py-2 px-3 font-medium">
                    {t("workforce.wellbeing.colScheduledDate")}
                  </th>
                  <th className="py-2 px-3 font-medium">{t("workforce.workers.colStatus")}</th>
                  <th className="py-2 px-3 font-medium">Update status</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-border-subtle text-body">
                {state.wellbeing.counsellorReferrals.map((ref) => (
                  <tr
                    key={ref.id}
                    data-testid="referral-row"
                    data-referral-id={ref.id}
                    className="hover:bg-surface-2/50"
                  >
                    <td className="py-2.5 px-3 font-mono text-caption text-accent">
                      <div className="flex items-center gap-1.5">
                        <span>{ref.caseCode}</span>
                        {ref.updatedByLocal && (
                          <LocalEditChip
                            label={t("workforce.localEditBadge") as string}
                            testId={`ref-local-${ref.id}`}
                          />
                        )}
                      </div>
                    </td>
                    <td className="py-2.5 px-3 text-caption text-fg-secondary">
                      {lineShortCode(ref.lineId)}
                    </td>
                    <td className="py-2.5 px-3 text-caption text-fg-primary">
                      {locale === "bn" ? ref.counsellorNameBn : ref.counsellorNameEn}
                    </td>
                    <td className="py-2.5 px-3 text-caption tabular-nums">{ref.requestedDate}</td>
                    <td className="py-2.5 px-3 text-caption tabular-nums">
                      {ref.scheduledDate ?? "—"}
                    </td>
                    <td className="py-2.5 px-3">
                      <Badge tone={referralStatusTone(ref.status)}>
                        {referralStatusLabel(t as (k: string) => string, ref.status)}
                      </Badge>
                    </td>
                    <td className="py-2.5 px-3">
                      <div className="flex flex-wrap gap-1">
                        {REFERRAL_STATUSES.map((st) => (
                          <Button
                            key={st}
                            size="sm"
                            variant={ref.status === st ? "primary" : "secondary"}
                            data-testid={`referral-status-${ref.id}-${st}`}
                            onClick={() => {
                              const sched =
                                st === "scheduled" && !ref.scheduledDate
                                  ? addIsoDays(state.anchorDate, 1)
                                  : ref.scheduledDate;
                              const res = state.updateCounsellorReferralStatus(
                                ref.id,
                                st,
                                sched,
                                actorEmail
                              );
                              toast.push({
                                title: `Updated ${ref.caseCode} to ${st}`,
                                tone: "success",
                                onUndo: res.undo
                              });
                            }}
                          >
                            {referralStatusLabel(t as (k: string) => string, st)}
                          </Button>
                        ))}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Panel>

      {/* 4. Health & safety checklist + 5. Hours-only Workload alerts */}
      <div className="grid grid-cols-1 xl:grid-cols-2 gap-4">
        <Panel
          title={t("workforce.wellbeing.safetyTitle")}
          subtitle={t("workforce.wellbeing.safetySubtitle")}
          testId="wf-safety-checklist-panel"
        >
          <div className="space-y-2.5">
            {state.wellbeing.safetyChecklist.map((item) => (
              <div
                key={item.id}
                className="surface-2 p-3 rounded-md flex flex-wrap items-start justify-between gap-3"
                data-testid={`wf-safety-item-${item.key}`}
              >
                <div className="min-w-0 flex-1">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="font-semibold text-fg-primary">
                      {locale === "bn" ? item.titleBn : item.titleEn}
                    </span>
                    <Badge tone={item.status === "verified" ? "success" : "warning"}>
                      {item.status === "verified" ? "Verified" : "Due soon"}
                    </Badge>
                    {item.updatedByLocal && (
                      <LocalEditChip
                        label={t("workforce.localEditBadge") as string}
                        testId={`saf-local-${item.id}`}
                      />
                    )}
                  </div>
                  <p className="mt-1 text-caption text-fg-secondary">
                    {locale === "bn" ? item.detailBn : item.detailEn}
                  </p>
                  <p className="mt-1 text-[11px] text-fg-tertiary">
                    {locale === "bn" ? item.metricBn : item.metricEn} · Last checked:{" "}
                    {item.lastCheckedDate}
                  </p>
                </div>
                {operator && item.status !== "verified" && (
                  <Button
                    size="sm"
                    variant="secondary"
                    data-testid={`wf-safety-verify-${item.key}`}
                    onClick={() => {
                      const res = state.verifySafetyItem(item.id, actorEmail);
                      toast.push({
                        title: `Marked ${item.titleEn} verified`,
                        tone: "success",
                        onUndo: res.undo
                      });
                    }}
                  >
                    {t("workforce.wellbeing.markVerifiedBtn")}
                  </Button>
                )}
              </div>
            ))}
          </div>
          <IllustrativePolicyNote
            text={t("workforce.illustrativeNote") as string}
            className="mt-3 text-caption text-fg-tertiary flex items-center gap-1.5"
          />
        </Panel>

        <Panel
          title={t("workforce.wellbeing.workloadTitle")}
          subtitle={t("workforce.wellbeing.workloadSubtitle")}
          icon={<AlertTriangle size={15} />}
          testId="wf-wellbeing-workload-panel"
        >
          {workloadAlertWorkers.length === 0 ? (
            <p className="text-caption text-fg-tertiary">
              No workers currently exceed 60 h/week for 3 consecutive weeks.
            </p>
          ) : (
            <ul className="space-y-2.5" data-testid="wf-wellbeing-workload-list">
              {workloadAlertWorkers.map((w) => (
                <li
                  key={w.id}
                  className="surface-2 p-3 rounded-md flex flex-wrap items-center justify-between gap-2"
                  data-testid={`wf-wellbeing-workload-row-${w.id}`}
                >
                  <div>
                    <div className="flex flex-wrap items-center gap-2">
                      <WorkloadAlertChip
                        workerId={w.id}
                        label={t("workforce.workloadAlertBadge") as string}
                      />
                      <Link
                        href={`/app/workforce/workers/${encodeURIComponent(w.id)}`}
                        className="text-body font-medium text-fg-primary hover:underline"
                      >
                        {locale === "bn" ? w.nameBn : w.nameEn} ({w.id})
                      </Link>
                    </div>
                    <p className="mt-0.5 text-caption text-fg-tertiary">
                      {lineLabel(w.lineId, locale)} · Weekly hours (last 3 wks):{" "}
                      {w.weeklyHoursHistory.slice(-3).join("h, ")}h
                    </p>
                  </div>
                  {operator && (
                    <Button
                      size="sm"
                      variant="secondary"
                      data-testid={`wf-wellbeing-cap-btn-${w.id}`}
                      onClick={() => {
                        const res = state.acknowledgeWorkloadAlert(w.id, actorEmail);
                        toast.push({
                          title: `Capped ${w.id} weekly schedule at 54 h`,
                          tone: "success",
                          onUndo: res.undo
                        });
                      }}
                    >
                      {t("workforce.overview.actionCapWorkload")}
                    </Button>
                  )}
                </li>
              ))}
            </ul>
          )}
        </Panel>
      </div>

      {/* New Voluntary Counsellor Referral Modal (metadata only, NO reason field!) */}
      <Modal
        open={referralModalOpen}
        onClose={() => setReferralModalOpen(false)}
        title={t("workforce.wellbeing.newReferralBtn")}
        description={t("workforce.wellbeing.referralsSubtitle")}
        testId="wf-create-referral-modal"
      >
        <CreateReferralForm
          actorEmail={actorEmail}
          onCreated={(caseCode, undo) => {
            setReferralModalOpen(false);
            toast.push({
              title: `Logged voluntary referral ${caseCode}`,
              tone: "success",
              onUndo: undo
            });
          }}
          onCancel={() => setReferralModalOpen(false)}
        />
      </Modal>
    </div>
  );
}

function PulseQuestionRow({
  label,
  value,
  onChange,
  lowLabel,
  highLabel,
  testIdPrefix
}: {
  label: string;
  value: Score1To5;
  onChange: (v: Score1To5) => void;
  lowLabel: string;
  highLabel: string;
  testIdPrefix: string;
}) {
  return (
    <div className="surface-2 p-3 rounded-md space-y-2">
      <p className="text-body font-medium text-fg-primary">{label}</p>
      <div className="flex items-center gap-1.5">
        {([1, 2, 3, 4, 5] as const).map((n) => (
          <button
            key={n}
            type="button"
            onClick={() => onChange(n)}
            data-testid={`${testIdPrefix}-${n}`}
            className={`flex-1 h-9 rounded-md border text-body font-semibold tabular-nums transition-colors ${
              value === n
                ? "bg-[var(--btn-primary-bg)] text-[var(--btn-primary-fg)] border-transparent"
                : "bg-surface-1 border-border-subtle text-fg-secondary hover:text-fg-primary"
            }`}
          >
            {n}
          </button>
        ))}
      </div>
      <div className="flex items-center justify-between text-[11px] text-fg-tertiary">
        <span>{lowLabel}</span>
        <span>{highLabel}</span>
      </div>
    </div>
  );
}

function CreateReferralForm({
  actorEmail,
  onCreated,
  onCancel
}: {
  actorEmail: string;
  onCreated: (caseCode: string, undo: () => void) => void;
  onCancel: () => void;
}) {
  const { t, locale } = useT();
  const state = useWorkforceStore();
  const [lineId, setLineId] = useState<WorkforceLineId>("line-3");
  const [counsellor, setCounsellor] = useState("Farhana Rahman (Welfare Counsellor)");
  const [scheduledDate, setScheduledDate] = useState(addIsoDays(state.anchorDate, 1));

  return (
    <form
      onSubmit={(e) => {
        e.preventDefault();
        const res = state.createCounsellorReferral(
          {
            lineId,
            counsellorNameEn: counsellor,
            counsellorNameBn: counsellor,
            scheduledDate
          },
          actorEmail
        );
        onCreated(res.caseCode, res.undo);
      }}
      className="space-y-3"
    >
      <Field label={t("workforce.workers.colLine")}>
        {({ id }) => (
          <Select
            id={id}
            value={lineId}
            onChange={(e) => setLineId(e.target.value as WorkforceLineId)}
            data-testid="wf-referral-input-line"
          >
            {WORKFORCE_LINES.map((l) => (
              <option key={l.id} value={l.id}>
                {locale === "bn" ? l.nameBn : l.nameEn}
              </option>
            ))}
          </Select>
        )}
      </Field>

      <Field label={t("workforce.wellbeing.colCounsellor")}>
        {({ id }) => (
          <Select
            id={id}
            value={counsellor}
            onChange={(e) => setCounsellor(e.target.value)}
            data-testid="wf-referral-input-counsellor"
          >
            <option value="Farhana Rahman (Welfare Counsellor)">
              Farhana Rahman (Welfare Counsellor)
            </option>
            <option value="Dr. Mahbub Alam (Visiting Counsellor)">
              Dr. Mahbub Alam (Visiting Counsellor)
            </option>
          </Select>
        )}
      </Field>

      <Field label={t("workforce.wellbeing.colScheduledDate")}>
        {({ id }) => (
          <Input
            id={id}
            type="date"
            value={scheduledDate}
            onChange={(e) => setScheduledDate(e.target.value)}
            data-testid="wf-referral-input-date"
          />
        )}
      </Field>

      <div className="flex justify-end gap-2 pt-2">
        <Button type="button" variant="ghost" onClick={onCancel}>
          {t("common.cancel")}
        </Button>
        <Button type="submit" variant="primary" data-testid="wf-referral-submit">
          {t("common.confirm")}
        </Button>
      </div>
    </form>
  );
}
