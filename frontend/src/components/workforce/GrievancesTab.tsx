"use client";

import { useMemo, useState } from "react";
import Link from "next/link";
import { Clock, MessageSquareWarning, PlusCircle, ShieldCheck } from "lucide-react";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { Modal } from "@/components/ui/Modal";
import { Field, FilterSelect, Select, TextArea } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { EmptyState } from "@/components/ui/EmptyState";
import { useToast } from "@/components/ui/Toast";
import { useT } from "@/lib/useT";
import { formatNumber } from "@/lib/format";
import { WORKFORCE_LINES, type WorkforceLineId } from "@/data/workforce/lines";
import {
  GRIEVANCE_CATEGORIES,
  GRIEVANCE_STATUSES,
  type GrievanceCategory,
  type GrievanceStatus
} from "@/data/workforce/types";
import { useWorkforceStore } from "@/store/workforce.store";
import { computeGrievanceSlaDays } from "@/lib/workforce";
import {
  LocalEditChip,
  grievanceCategoryLabel,
  grievanceStatusLabel,
  grievanceStatusTone,
  lineLabel,
  lineShortCode
} from "./shared";

type Props = {
  operator: boolean;
  actorEmail: string;
};

export function GrievancesTab({ operator, actorEmail }: Props) {
  const { t, locale } = useT();
  const toast = useToast();
  const state = useWorkforceStore();

  const [catFilter, setCatFilter] = useState("");
  const [statusFilter, setStatusFilter] = useState("");
  const [notesMap, setNotesMap] = useState<Record<string, string>>({});
  const [createOpen, setCreateOpen] = useState(false);

  const categoryCounts = useMemo(() => {
    return GRIEVANCE_CATEGORIES.map((cat) => {
      const items = state.grievances.filter((g) => g.category === cat);
      const openCount = items.filter((g) => g.status !== "resolved").length;
      return {
        category: cat,
        total: items.length,
        openCount
      };
    });
  }, [state.grievances]);

  const filteredGrievances = useMemo(() => {
    return state.grievances.filter((g) => {
      if (catFilter && g.category !== catFilter) return false;
      if (statusFilter === "open") return g.status !== "resolved";
      if (statusFilter && g.status !== statusFilter) return false;
      return true;
    });
  }, [state.grievances, catFilter, statusFilter]);

  const fmtNum = (n: number) => formatNumber(n, locale);

  return (
    <div className="space-y-4" data-testid="workforce-grievances">
      {/* Harassment Complaints Committee & confidential box demo process callout */}
      <Callout
        tone="info"
        title={t("workforce.grievances.committeeCalloutTitle")}
        testId="wf-grievance-committee-callout"
        action={
          <Button
            size="sm"
            variant="primary"
            iconLeft={<PlusCircle size={14} />}
            onClick={() => setCreateOpen(true)}
            data-testid="wf-new-grievance-btn"
          >
            {t("workforce.grievances.newGrievanceBtn")}
          </Button>
        }
      >
        {t("workforce.grievances.committeeCalloutBody")}
      </Callout>

      {/* Category summary tiles (clickable to filter) */}
      <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-6 gap-2.5" data-testid="wf-grievance-category-counts">
        {categoryCounts.map((c) => {
          const active = catFilter === c.category;
          return (
            <button
              key={c.category}
              type="button"
              onClick={() => setCatFilter(active ? "" : c.category)}
              data-testid={`wf-grievance-cat-tile-${c.category}`}
              className={`text-left surface p-3 rounded-md border transition-colors ${
                active
                  ? "border-[var(--accent)] ring-1 ring-[var(--accent)]"
                  : "border-border-subtle hover:border-[var(--border-strong)]"
              }`}
            >
              <span className="text-caption text-fg-tertiary block truncate">
                {grievanceCategoryLabel(t as (k: string) => string, c.category)}
              </span>
              <div className="mt-1 flex items-baseline justify-between gap-2">
                <span className="text-[20px] font-semibold text-fg-primary tabular-nums">
                  {fmtNum(c.total)}
                </span>
                <span className="text-[11px] text-fg-secondary tabular-nums">
                  {fmtNum(c.openCount)} open
                </span>
              </div>
            </button>
          );
        })}
      </div>

      {/* Viewer privacy guard: Viewer sees category counts only, never individual grievance details */}
      {!operator ? (
        <Callout tone="info" testId="wf-grievance-viewer-only">
          {t("workforce.grievances.viewerSummaryNote")}
        </Callout>
      ) : (
        <Panel
          title={`${t("workforce.tabs.grievances")} (${fmtNum(filteredGrievances.length)})`}
          right={
            <div className="flex flex-wrap items-center gap-2">
              <FilterSelect
                label={t("workforce.grievances.colCategory") as string}
                value={catFilter}
                onChange={setCatFilter}
                testId="wf-grievance-cat-select"
              >
                <option value="">All categories</option>
                {GRIEVANCE_CATEGORIES.map((c) => (
                  <option key={c} value={c}>
                    {grievanceCategoryLabel(t as (k: string) => string, c)}
                  </option>
                ))}
              </FilterSelect>
              <FilterSelect
                label={t("workforce.workers.filterStatus") as string}
                value={statusFilter}
                onChange={setStatusFilter}
                testId="wf-grievance-status-select"
              >
                <option value="">All statuses</option>
                <option value="open">Open (unresolved)</option>
                {GRIEVANCE_STATUSES.map((st) => (
                  <option key={st} value={st}>
                    {grievanceStatusLabel(t as (k: string) => string, st)}
                  </option>
                ))}
              </FilterSelect>
            </div>
          }
          flush
          testId="wf-grievances-table-panel"
        >
          {filteredGrievances.length === 0 ? (
            <EmptyState
              icon={<ShieldCheck size={20} />}
              title={t("workforce.overview.needsAttentionEmpty") as string}
            />
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full text-left border-collapse" data-testid="wf-grievances-table">
                <thead>
                  <tr className="border-b border-border-subtle text-caption text-fg-tertiary">
                    <th className="py-2.5 px-3 font-medium">{t("workforce.grievances.colTicket")}</th>
                    <th className="py-2.5 px-3 font-medium">{t("workforce.grievances.colCategory")}</th>
                    <th className="py-2.5 px-3 font-medium">{t("workforce.grievances.colLine")}</th>
                    <th className="py-2.5 px-3 font-medium">{t("workforce.grievances.colReporter")}</th>
                    <th className="py-2.5 px-3 font-medium">{t("workforce.grievances.colSubject")}</th>
                    <th className="py-2.5 px-3 font-medium">{t("workforce.grievances.colStatus")}</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-border-subtle text-body">
                  {filteredGrievances.map((grv) => {
                    const sla = computeGrievanceSlaDays(grv.createdAt, state.anchorDate);
                    const reporter =
                      !grv.anonymous && grv.workerId
                        ? state.workers.find((w) => w.id === grv.workerId)
                        : null;
                    return (
                      <tr
                        key={grv.id}
                        data-testid="grievance-row"
                        data-ticket-id={grv.id}
                        className="hover:bg-surface-2/50 align-top"
                      >
                        <td className="py-3 px-3">
                          <div className="flex flex-wrap items-center gap-1.5">
                            <span className="font-mono text-caption font-semibold text-accent">
                              {grv.ticketNo}
                            </span>
                            {grv.updatedByLocal && (
                              <LocalEditChip
                                label={t("workforce.localEditBadge") as string}
                                testId={`grv-local-${grv.id}`}
                              />
                            )}
                          </div>
                          <div className="mt-1">
                            {grv.status !== "resolved" ? (
                              <Badge
                                tone={sla.overdue ? "danger" : "neutral"}
                                testId={`grv-sla-${grv.id}`}
                              >
                                <Clock size={10} className="mr-1 inline" />
                                {sla.overdue
                                  ? t("workforce.grievances.slaOverdue", { n: sla.daysOpen })
                                  : t("workforce.grievances.slaDaysOpen", { n: sla.daysOpen })}
                              </Badge>
                            ) : (
                              <span className="text-[11px] text-fg-tertiary">{grv.updatedAt}</span>
                            )}
                          </div>
                        </td>
                        <td className="py-3 px-3 text-caption">
                          <Badge tone="neutral">
                            {grievanceCategoryLabel(t as (k: string) => string, grv.category)}
                          </Badge>
                        </td>
                        <td className="py-3 px-3 text-caption text-fg-secondary">
                          {lineShortCode(grv.lineId)}
                        </td>
                        <td className="py-3 px-3 text-caption">
                          {grv.anonymous ? (
                            <Badge tone="info" testId={`grv-anon-${grv.id}`}>
                              {t("workforce.grievances.anonymousLabel")}
                            </Badge>
                          ) : reporter ? (
                            <Link
                              href={`/app/workforce/workers/${encodeURIComponent(reporter.id)}`}
                              className="text-fg-primary font-medium hover:underline"
                            >
                              {locale === "bn" ? reporter.nameBn : reporter.nameEn} ({reporter.id})
                            </Link>
                          ) : (
                            grv.workerId ?? "—"
                          )}
                        </td>
                        <td className="py-3 px-3 max-w-md">
                          <p className="font-medium text-fg-primary">
                            {locale === "bn" ? grv.subjectBn : grv.subjectEn}
                          </p>
                          <p className="mt-0.5 text-caption text-fg-secondary">
                            {locale === "bn" ? grv.detailBn : grv.detailEn}
                          </p>
                          {grv.resolutionNote && (
                            <p className="mt-1.5 text-caption text-accent bg-surface-2 px-2.5 py-1 rounded">
                              Resolution note: {grv.resolutionNote}
                            </p>
                          )}
                        </td>
                        <td className="py-3 px-3 min-w-[240px]">
                          <div className="space-y-2">
                            <div className="flex flex-wrap items-center gap-1">
                              {GRIEVANCE_STATUSES.map((st) => {
                                const active = grv.status === st;
                                return (
                                  <button
                                    key={st}
                                    type="button"
                                    data-testid={`grievance-status-${grv.id}-${st}`}
                                    onClick={() => {
                                      const res = state.updateGrievanceStatus(
                                        grv.id,
                                        st,
                                        notesMap[grv.id],
                                        actorEmail
                                      );
                                      toast.push({
                                        title: t("workforce.grievances.grievanceUpdatedToast", {
                                          ticket: grv.ticketNo,
                                          status: grievanceStatusLabel(
                                            t as (k: string) => string,
                                            st
                                          )
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
                                    {grievanceStatusLabel(t as (k: string) => string, st)}
                                  </button>
                                );
                              })}
                            </div>
                            <div className="flex items-center gap-1.5">
                              <Input
                                value={notesMap[grv.id] ?? ""}
                                onChange={(e) =>
                                  setNotesMap((prev) => ({
                                    ...prev,
                                    [grv.id]: e.target.value
                                  }))
                                }
                                placeholder={
                                  t("workforce.grievances.resolutionPlaceholder") as string
                                }
                                className="!h-7 !text-caption"
                                data-testid={`grievance-note-input-${grv.id}`}
                              />
                              <Button
                                size="sm"
                                variant="secondary"
                                disabled={!notesMap[grv.id]?.trim()}
                                data-testid={`grievance-note-save-${grv.id}`}
                                onClick={() => {
                                  const res = state.updateGrievanceStatus(
                                    grv.id,
                                    grv.status,
                                    notesMap[grv.id],
                                    actorEmail
                                  );
                                  setNotesMap((prev) => ({ ...prev, [grv.id]: "" }));
                                  toast.push({
                                    title: t("workforce.grievances.grievanceUpdatedToast", {
                                      ticket: grv.ticketNo,
                                      status: grv.status
                                    }) as string,
                                    tone: "success",
                                    onUndo: res.undo
                                  });
                                }}
                              >
                                Save
                              </Button>
                            </div>
                          </div>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          )}
        </Panel>
      )}

      {/* Submit Grievance Modal */}
      <CreateGrievanceModal
        open={createOpen}
        onClose={() => setCreateOpen(false)}
        actorEmail={actorEmail}
        onCreated={(ticketNo, undo) => {
          setCreateOpen(false);
          toast.push({
            title: t("workforce.grievances.grievanceCreatedToast", {
              ticket: ticketNo
            }) as string,
            tone: "success",
            onUndo: undo
          });
        }}
      />
    </div>
  );
}

function CreateGrievanceModal({
  open,
  onClose,
  actorEmail,
  onCreated
}: {
  open: boolean;
  onClose: () => void;
  actorEmail: string;
  onCreated: (ticketNo: string, undo: () => void) => void;
}) {
  const { t, locale } = useT();
  const state = useWorkforceStore();

  const [category, setCategory] = useState<GrievanceCategory>("safety");
  const [anonymous, setAnonymous] = useState(true);
  const [workerId, setWorkerId] = useState("W-0045");
  const [lineId, setLineId] = useState<WorkforceLineId>("line-3");
  const [subjectEn, setSubjectEn] = useState("");
  const [subjectBn, setSubjectBn] = useState("");
  const [detailEn, setDetailEn] = useState("");

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!subjectEn.trim() || !detailEn.trim()) return;
    const res = state.createGrievance(
      {
        category,
        anonymous,
        workerId: anonymous ? undefined : workerId,
        lineId,
        subjectEn,
        subjectBn,
        detailEn,
        detailBn: detailEn
      },
      actorEmail
    );
    setSubjectEn("");
    setSubjectBn("");
    setDetailEn("");
    onCreated(res.ticketNo, res.undo);
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={t("workforce.grievances.modalCreateTitle")}
      testId="wf-create-grievance-modal"
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button
            variant="primary"
            onClick={handleSubmit}
            disabled={!subjectEn.trim() || !detailEn.trim()}
            data-testid="wf-create-grievance-submit"
          >
            {t("common.confirm")}
          </Button>
        </>
      }
    >
      <form onSubmit={handleSubmit} className="space-y-3">
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <Field label={t("workforce.grievances.colCategory")}>
            {({ id }) => (
              <Select
                id={id}
                value={category}
                onChange={(e) => setCategory(e.target.value as GrievanceCategory)}
                data-testid="wf-grievance-input-category"
              >
                {GRIEVANCE_CATEGORIES.map((c) => (
                  <option key={c} value={c}>
                    {grievanceCategoryLabel(t as (k: string) => string, c)}
                  </option>
                ))}
              </Select>
            )}
          </Field>
          <Field label={t("workforce.grievances.colLine")}>
            {({ id }) => (
              <Select
                id={id}
                value={lineId}
                onChange={(e) => setLineId(e.target.value as WorkforceLineId)}
                data-testid="wf-grievance-input-line"
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

        <label className="flex items-center gap-2 text-body text-fg-primary cursor-pointer surface-2 p-2.5 rounded-md">
          <input
            type="checkbox"
            checked={anonymous}
            onChange={(e) => setAnonymous(e.target.checked)}
            data-testid="wf-grievance-input-anonymous"
          />
          <span>{t("workforce.grievances.fieldAnonymous")}</span>
        </label>

        {!anonymous && (
          <Field label={t("workforce.leave.fieldWorker")}>
            {({ id }) => (
              <Select
                id={id}
                value={workerId}
                onChange={(e) => setWorkerId(e.target.value)}
                data-testid="wf-grievance-input-worker"
              >
                {state.workers
                  .filter((w) => w.status !== "resigned")
                  .map((w) => (
                    <option key={w.id} value={w.id}>
                      {w.id} — {locale === "bn" ? w.nameBn : w.nameEn}
                    </option>
                  ))}
              </Select>
            )}
          </Field>
        )}

        <Field label={t("workforce.grievances.fieldSubjectEn")}>
          {({ id }) => (
            <Input
              id={id}
              value={subjectEn}
              onChange={(e) => setSubjectEn(e.target.value)}
              data-testid="wf-grievance-input-subject"
            />
          )}
        </Field>

        <Field label={t("workforce.grievances.fieldDetailEn")}>
          {({ id }) => (
            <TextArea
              id={id}
              rows={3}
              value={detailEn}
              onChange={(e) => setDetailEn(e.target.value)}
              data-testid="wf-grievance-input-detail"
            />
          )}
        </Field>
      </form>
    </Modal>
  );
}
