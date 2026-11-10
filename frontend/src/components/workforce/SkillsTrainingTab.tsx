"use client";

import { useState } from "react";
import Link from "next/link";
import { Award, CheckCircle2, GraduationCap, PlusCircle, Sparkles, UserPlus } from "lucide-react";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Modal } from "@/components/ui/Modal";
import { Field, Select } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { EmptyState } from "@/components/ui/EmptyState";
import { useToast } from "@/components/ui/Toast";
import { useT } from "@/lib/useT";
import { formatNumber } from "@/lib/format";
import { WORKFORCE_LINES, type WorkforceLineId } from "@/data/workforce/lines";
import { addIsoDays } from "@/data/workforce/seed";
import { MACHINE_SKILLS, type MachineSkill } from "@/data/workforce/types";
import { useWorkforceStore } from "@/store/workforce.store";
import {
  computeCrossTrainingCandidates,
  computeSkillCoverageMatrix
} from "@/lib/workforce";
import {
  LocalEditChip,
  RuleBasedChip,
  lineLabel,
  lineShortCode,
  roleLabel,
  skillLabel,
  type WorkforceTab
} from "./shared";

type Props = {
  operator: boolean;
  actorEmail: string;
  onNavigate: (tab: WorkforceTab, extraParams?: Record<string, string>) => void;
};

export function SkillsTrainingTab({ operator, actorEmail, onNavigate }: Props) {
  const { t, locale } = useT();
  const toast = useToast();
  const state = useWorkforceStore();

  const [batchModalOpen, setBatchModalOpen] = useState(false);
  const [enrolWorkerSelect, setEnrolWorkerSelect] = useState<Record<string, string>>({});

  const matrix = computeSkillCoverageMatrix(state.workers);
  const candidates = computeCrossTrainingCandidates(state.workers);
  const activeWorkers = state.workers.filter((w) => w.status !== "resigned");

  const fmtNum = (n: number) => formatNumber(n, locale);

  return (
    <div className="space-y-4" data-testid="workforce-skills">
      {/* Line x Machine-skill coverage matrix (every cell clickable -> Workers filtered by line & skill) */}
      <Panel
        title={t("workforce.skillsTab.matrixTitle")}
        subtitle={t("workforce.skillsTab.matrixSubtitle")}
        icon={<Award size={15} />}
        testId="wf-skill-matrix-panel"
      >
        <div className="overflow-x-auto">
          <table className="w-full text-left border-collapse" data-testid="wf-skill-matrix-table">
            <thead>
              <tr className="border-b border-border-subtle text-caption text-fg-tertiary">
                <th className="py-2 px-3 font-medium">Line</th>
                {MACHINE_SKILLS.map((sk) => (
                  <th key={sk} className="py-2 px-2.5 font-medium text-center">
                    {skillLabel(t as (k: string) => string, sk)}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-border-subtle text-body">
              {WORKFORCE_LINES.map((line) => (
                <tr key={line.id}>
                  <td className="py-2.5 px-3 font-medium text-fg-primary whitespace-nowrap">
                    {locale === "bn" ? line.nameBn : line.nameEn}
                  </td>
                  {MACHINE_SKILLS.map((sk) => {
                    const cell = matrix[line.id][sk];
                    const low = cell.qualifiedCount < 2;
                    return (
                      <td key={sk} className="p-1.5 text-center">
                        <button
                          type="button"
                          onClick={() =>
                            onNavigate("workers", { line: line.id, skill: sk })
                          }
                          data-testid={`wf-skill-cell-${line.id}-${sk}`}
                          className={`w-full py-2 px-2 rounded-md border text-caption font-semibold tabular-nums transition-colors ${
                            low
                              ? "bg-[var(--risk-medium-soft)] border-[var(--risk-medium-border)] text-[var(--risk-medium)] hover:border-[var(--accent)]"
                              : "bg-surface-2 border-border-subtle text-fg-primary hover:border-[var(--accent)]"
                          }`}
                          title={`Click to view ${cell.qualifiedCount} qualified workers on ${line.code} for ${sk.toUpperCase()}`}
                        >
                          {fmtNum(cell.qualifiedCount)}
                          {cell.trainerCount > 0 && (
                            <span className="ml-1 text-[10px] text-fg-tertiary">
                              ({fmtNum(cell.trainerCount)}T)
                            </span>
                          )}
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

      {/* Rule-based cross-training candidates */}
      <Panel
        title={t("workforce.skillsTab.candidatesTitle")}
        subtitle={t("workforce.skillsTab.candidatesSubtitle")}
        icon={<Sparkles size={15} />}
        right={<RuleBasedChip label={t("workforce.ruleBasedBadge") as string} />}
        testId="wf-cross-training-candidates-panel"
      >
        <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
          {candidates.map((cand) => {
            const matchingBatch = state.trainingBatches.find((b) => b.skill === cand.targetSkill);
            const alreadyEnrolled = matchingBatch?.enrolledWorkerIds.includes(cand.workerId);
            return (
              <div
                key={`${cand.workerId}-${cand.targetSkill}`}
                className="surface-2 p-3 rounded-md flex flex-wrap items-center justify-between gap-3"
                data-testid={`wf-candidate-${cand.workerId}`}
              >
                <div className="min-w-0 flex-1">
                  <div className="flex flex-wrap items-center gap-2">
                    <Link
                      href={`/app/workforce/workers/${encodeURIComponent(cand.workerId)}?sub=skills`}
                      className="text-body font-semibold text-fg-primary hover:underline"
                    >
                      {locale === "bn" ? cand.nameBn : cand.nameEn} ({cand.workerId})
                    </Link>
                    <Badge tone="info">{cand.targetSkill.toUpperCase()}</Badge>
                    <Badge tone="neutral">{lineShortCode(cand.lineId)}</Badge>
                  </div>
                  <p className="mt-1 text-caption text-fg-secondary">
                    {locale === "bn" ? cand.reasonBn : cand.reasonEn}
                  </p>
                </div>
                {operator && matchingBatch && (
                  <Button
                    size="sm"
                    variant={alreadyEnrolled ? "secondary" : "primary"}
                    disabled={alreadyEnrolled}
                    data-testid={`wf-enrol-candidate-${cand.workerId}`}
                    onClick={() => {
                      const res = state.enrollInTrainingBatch(
                        matchingBatch.id,
                        cand.workerId,
                        actorEmail
                      );
                      toast.push({
                        title: `Enrolled ${cand.workerId} in ${matchingBatch.id}`,
                        tone: "success",
                        onUndo: res.undo
                      });
                    }}
                  >
                    {alreadyEnrolled
                      ? "Enrolled"
                      : t("workforce.skillsTab.enrolCandidateBtn")}
                  </Button>
                )}
              </div>
            );
          })}
        </div>
      </Panel>

      {/* Training batches */}
      <Panel
        title={t("workforce.skillsTab.batchesTitle")}
        subtitle={t("workforce.skillsTab.batchesSubtitle")}
        icon={<GraduationCap size={15} />}
        right={
          operator && (
            <Button
              size="sm"
              variant="primary"
              iconLeft={<PlusCircle size={14} />}
              onClick={() => setBatchModalOpen(true)}
              data-testid="wf-new-batch-btn"
            >
              {t("workforce.skillsTab.newBatchBtn")}
            </Button>
          )
        }
        testId="wf-training-batches-panel"
      >
        <div className="space-y-3">
          {state.trainingBatches.map((batch) => (
            <div
              key={batch.id}
              className="surface-2 p-4 rounded-md space-y-3"
              data-testid={`wf-training-batch-${batch.id}`}
            >
              <div className="flex flex-wrap items-start justify-between gap-2">
                <div>
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="font-mono text-caption text-accent">{batch.id}</span>
                    <h3 className="text-body font-semibold text-fg-primary">
                      {locale === "bn" ? batch.titleBn : batch.titleEn}
                    </h3>
                    <Badge tone="info">
                      {skillLabel(t as (k: string) => string, batch.skill)}
                    </Badge>
                    <Badge tone="neutral">{lineLabel(batch.lineId, locale)}</Badge>
                    {batch.updatedByLocal && (
                      <LocalEditChip
                        label={t("workforce.localEditBadge") as string}
                        testId={`batch-local-${batch.id}`}
                      />
                    )}
                  </div>
                  <p className="mt-1 text-caption text-fg-secondary">
                    {locale === "bn" ? batch.instructorBn : batch.instructorEn} ·{" "}
                    {batch.scheduledDate}
                  </p>
                </div>

                {operator && (
                  <div className="flex items-center gap-2">
                    <Select
                      value={enrolWorkerSelect[batch.id] ?? ""}
                      onChange={(e) =>
                        setEnrolWorkerSelect((prev) => ({
                          ...prev,
                          [batch.id]: e.target.value
                        }))
                      }
                      className="!h-8 !w-44 !text-caption"
                      data-testid={`wf-batch-enrol-select-${batch.id}`}
                    >
                      <option value="">Select worker to enrol…</option>
                      {activeWorkers
                        .filter((w) => !batch.enrolledWorkerIds.includes(w.id))
                        .slice(0, 30)
                        .map((w) => (
                          <option key={w.id} value={w.id}>
                            {w.id} — {locale === "bn" ? w.nameBn : w.nameEn}
                          </option>
                        ))}
                    </Select>
                    <Button
                      size="sm"
                      variant="secondary"
                      iconLeft={<UserPlus size={13} />}
                      disabled={!enrolWorkerSelect[batch.id]}
                      data-testid={`wf-batch-enrol-btn-${batch.id}`}
                      onClick={() => {
                        const wid = enrolWorkerSelect[batch.id];
                        if (!wid) return;
                        const res = state.enrollInTrainingBatch(batch.id, wid, actorEmail);
                        setEnrolWorkerSelect((prev) => ({ ...prev, [batch.id]: "" }));
                        toast.push({
                          title: `Enrolled ${wid} in ${batch.id}`,
                          tone: "success",
                          onUndo: res.undo
                        });
                      }}
                    >
                      Enrol
                    </Button>
                  </div>
                )}
              </div>

              {/* Enrolled workers list with Attendance and Completion controls */}
              <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-2">
                {batch.enrolledWorkerIds.map((wid) => {
                  const w = state.workers.find((x) => x.id === wid);
                  const attended = batch.attendedWorkerIds.includes(wid);
                  const completed = batch.completedWorkerIds.includes(wid);
                  return (
                    <div
                      key={wid}
                      className="surface p-2.5 rounded border border-border-subtle flex items-center justify-between gap-2 text-caption"
                      data-testid={`wf-batch-worker-${batch.id}-${wid}`}
                    >
                      <div className="min-w-0">
                        <Link
                          href={`/app/workforce/workers/${encodeURIComponent(wid)}?sub=training`}
                          className="font-medium text-fg-primary hover:underline block truncate"
                        >
                          {w ? `${locale === "bn" ? w.nameBn : w.nameEn} (${wid})` : wid}
                        </Link>
                        <span className="text-[11px] text-fg-tertiary">
                          {completed
                            ? t("workforce.skillsTab.colCompleted")
                            : attended
                            ? t("workforce.skillsTab.colAttended")
                            : "Enrolled"}
                        </span>
                      </div>
                      {operator && (
                        <div className="flex items-center gap-1 shrink-0">
                          <Button
                            size="sm"
                            variant={attended ? "primary" : "ghost"}
                            data-testid={`wf-batch-attend-${batch.id}-${wid}`}
                            onClick={() => {
                              const res = state.toggleTrainingAttendance(batch.id, wid, actorEmail);
                              toast.push({
                                title: `Updated attendance for ${wid}`,
                                tone: "info",
                                onUndo: res.undo
                              });
                            }}
                          >
                            {t("workforce.skillsTab.colAttended")}
                          </Button>
                          {!completed && (
                            <Button
                              size="sm"
                              variant="success"
                              data-testid={`wf-batch-complete-${batch.id}-${wid}`}
                              onClick={() => {
                                const res = state.markTrainingCompleted(batch.id, wid, actorEmail);
                                toast.push({
                                  title: `${wid} completed ${batch.id} (${batch.skill.toUpperCase()} upgraded)`,
                                  tone: "success",
                                  onUndo: res.undo
                                });
                              }}
                            >
                              <CheckCircle2 size={12} />
                            </Button>
                          )}
                        </div>
                      )}
                    </div>
                  );
                })}
              </div>
            </div>
          ))}
        </div>
      </Panel>

      {/* Create Training Batch Modal */}
      <CreateBatchModal
        open={batchModalOpen}
        onClose={() => setBatchModalOpen(false)}
        actorEmail={actorEmail}
        onCreated={(id, undo) => {
          setBatchModalOpen(false);
          toast.push({
            title: t("workforce.skillsTab.batchCreatedToast", { id }) as string,
            tone: "success",
            onUndo: undo
          });
        }}
      />
    </div>
  );
}

function CreateBatchModal({
  open,
  onClose,
  actorEmail,
  onCreated
}: {
  open: boolean;
  onClose: () => void;
  actorEmail: string;
  onCreated: (id: string, undo: () => void) => void;
}) {
  const { t, locale } = useT();
  const state = useWorkforceStore();

  const [titleEn, setTitleEn] = useState("");
  const [titleBn, setTitleBn] = useState("");
  const [skill, setSkill] = useState<MachineSkill>("buttonhole");
  const [lineId, setLineId] = useState<WorkforceLineId>("line-3");
  const [instructorEn, setInstructorEn] = useState("Master Mechanic Faruk Ahmed");
  const [scheduledDate, setScheduledDate] = useState(addIsoDays(state.anchorDate, 3));

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!titleEn.trim()) return;
    const defaultWorkers = state.workers
      .filter((w) => w.status === "active" && w.lineId === lineId)
      .slice(0, 2)
      .map((w) => w.id);
    const res = state.createTrainingBatch(
      {
        titleEn,
        titleBn,
        skill,
        lineId,
        instructorEn,
        instructorBn: instructorEn,
        scheduledDate,
        enrolledWorkerIds: defaultWorkers
      },
      actorEmail
    );
    setTitleEn("");
    setTitleBn("");
    onCreated(res.id, res.undo);
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={t("workforce.skillsTab.modalBatchTitle")}
      testId="wf-create-batch-modal"
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button
            variant="primary"
            onClick={handleSubmit}
            disabled={!titleEn.trim()}
            data-testid="wf-create-batch-submit"
          >
            {t("common.confirm")}
          </Button>
        </>
      }
    >
      <form onSubmit={handleSubmit} className="space-y-3">
        <Field label={t("workforce.skillsTab.fieldBatchTitleEn")}>
          {({ id }) => (
            <Input
              id={id}
              value={titleEn}
              onChange={(e) => setTitleEn(e.target.value)}
              placeholder="e.g. Line 3 Buttonhole Qualification Batch"
              data-testid="wf-batch-input-title-en"
            />
          )}
        </Field>
        <Field label={t("workforce.skillsTab.fieldBatchTitleBn")}>
          {({ id }) => (
            <Input
              id={id}
              value={titleBn}
              onChange={(e) => setTitleBn(e.target.value)}
              data-testid="wf-batch-input-title-bn"
            />
          )}
        </Field>
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <Field label={t("workforce.skillsTab.fieldBatchSkill")}>
            {({ id }) => (
              <Select
                id={id}
                value={skill}
                onChange={(e) => setSkill(e.target.value as MachineSkill)}
                data-testid="wf-batch-input-skill"
              >
                {MACHINE_SKILLS.map((sk) => (
                  <option key={sk} value={sk}>
                    {skillLabel(t as (k: string) => string, sk)}
                  </option>
                ))}
              </Select>
            )}
          </Field>
          <Field label={t("workforce.skillsTab.fieldBatchLine")}>
            {({ id }) => (
              <Select
                id={id}
                value={lineId}
                onChange={(e) => setLineId(e.target.value as WorkforceLineId)}
                data-testid="wf-batch-input-line"
              >
                {WORKFORCE_LINES.map((l) => (
                  <option key={l.id} value={l.id}>
                    {locale === "bn" ? l.nameBn : l.nameEn}
                  </option>
                ))}
              </Select>
            )}
          </Field>
        </div>
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <Field label={t("workforce.skillsTab.fieldInstructorEn")}>
            {({ id }) => (
              <Input
                id={id}
                value={instructorEn}
                onChange={(e) => setInstructorEn(e.target.value)}
                data-testid="wf-batch-input-instructor"
              />
            )}
          </Field>
          <Field label={t("workforce.skillsTab.fieldScheduledDate")}>
            {({ id }) => (
              <Input
                id={id}
                type="date"
                value={scheduledDate}
                onChange={(e) => setScheduledDate(e.target.value)}
                data-testid="wf-batch-input-date"
              />
            )}
          </Field>
        </div>
      </form>
    </Modal>
  );
}
