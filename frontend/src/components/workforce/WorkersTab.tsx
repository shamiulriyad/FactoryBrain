"use client";

import { useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { z } from "zod";
import { Download, Edit3, ExternalLink, PlusCircle, Search, UserPlus, Users, X } from "lucide-react";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { Drawer } from "@/components/ui/Drawer";
import { Modal } from "@/components/ui/Modal";
import { Field, FilterSelect, Select } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { EmptyState } from "@/components/ui/EmptyState";
import { useToast } from "@/components/ui/Toast";
import { useT } from "@/lib/useT";
import { formatBDT, formatDate, formatNumber, bn } from "@/lib/format";
import { WORKFORCE_LINES, type WorkforceLineId } from "@/data/workforce/lines";
import { getGradeWage, type SkillGrade } from "@/data/workforce/policy";
import {
  MACHINE_SKILLS,
  PAYMENT_CHANNELS,
  WORKER_ROLES,
  WORKER_SHIFTS,
  WORKER_STATUSES,
  type MachineSkill,
  type PaymentChannel,
  type WorkerRecord,
  type WorkerRole,
  type WorkerShift,
  type WorkerStatus
} from "@/data/workforce/types";
import { useWorkforceStore } from "@/store/workforce.store";
import {
  buildViewerSafeCsv,
  calcWorkerLatestEfficiencyPct,
  computeWorkerAttendanceRatePct,
  hasConsecutiveWorkloadAlert
} from "@/lib/workforce";
import {
  IllustrativePolicyNote,
  LocalEditChip,
  WorkloadAlertChip,
  lineLabel,
  lineShortCode,
  roleLabel,
  skillLabel,
  workerStatusLabel,
  workerStatusTone
} from "./shared";

const WorkerFormSchema = z.object({
  nameEn: z.string().trim().min(2),
  nameBn: z.string().trim(),
  gender: z.enum(["female", "male"]),
  age: z.number().int().min(18).max(60),
  role: z.enum(WORKER_ROLES),
  lineId: z.enum(["line-1", "line-2", "line-3", "line-4", "line-5", "line-6"]),
  shift: z.enum(WORKER_SHIFTS),
  grade: z.union([
    z.literal(1),
    z.literal(2),
    z.literal(3),
    z.literal(4),
    z.literal(5),
    z.literal(6),
    z.literal(7)
  ]),
  status: z.enum(WORKER_STATUSES),
  joinDate: z.string().regex(/^\d{4}-\d{2}-\d{2}$/),
  homeDistrictEn: z.string().trim().min(2),
  residenceAreaEn: z.string().trim().min(2),
  paymentChannel: z.enum(PAYMENT_CHANNELS),
  primarySkill: z.enum(MACHINE_SKILLS),
  nidOnFile: z.boolean(),
  firstAidTrained: z.boolean(),
  dayCareEligible: z.boolean()
});

type SortKey = "id" | "name" | "grade" | "tenure" | "line";

type Props = {
  operator: boolean;
  admin: boolean;
  actorEmail: string;
  initialLine?: string;
  initialSkill?: string;
  initialStatus?: string;
  onSyncUrlParams?: (params: Record<string, string>) => void;
};

const PAGE_SIZE = 20;

export function WorkersTab({
  operator,
  admin,
  actorEmail,
  initialLine = "",
  initialSkill = "",
  initialStatus = "",
  onSyncUrlParams
}: Props) {
  const { t, locale } = useT();
  const toast = useToast();
  const state = useWorkforceStore();

  const [query, setQuery] = useState("");
  const [lineFilter, setLineFilter] = useState(initialLine);
  const [roleFilter, setRoleFilter] = useState("");
  const [gradeFilter, setGradeFilter] = useState("");
  const [statusFilter, setStatusFilter] = useState(initialStatus);
  const [skillFilter, setSkillFilter] = useState(initialSkill);
  const [shiftFilter, setShiftFilter] = useState("");
  const [sortBy, setSortBy] = useState<SortKey>("id");
  const [page, setPage] = useState(1);

  const [drawerWorkerId, setDrawerWorkerId] = useState<string | null>(null);
  const [modalOpen, setModalOpen] = useState(false);
  const [editingWorker, setEditingWorker] = useState<WorkerRecord | null>(null);

  useEffect(() => {
    setLineFilter(initialLine);
  }, [initialLine]);

  useEffect(() => {
    setSkillFilter(initialSkill);
  }, [initialSkill]);

  useEffect(() => {
    setStatusFilter(initialStatus);
  }, [initialStatus]);

  function updateLineFilter(v: string) {
    setLineFilter(v);
    setPage(1);
    onSyncUrlParams?.({ line: v, skill: skillFilter, status: statusFilter });
  }

  function updateSkillFilter(v: string) {
    setSkillFilter(v);
    setPage(1);
    onSyncUrlParams?.({ line: lineFilter, skill: v, status: statusFilter });
  }

  function updateStatusFilter(v: string) {
    setStatusFilter(v);
    setPage(1);
    onSyncUrlParams?.({ line: lineFilter, skill: skillFilter, status: v });
  }

  function clearFilters() {
    setQuery("");
    setLineFilter("");
    setRoleFilter("");
    setGradeFilter("");
    setStatusFilter("");
    setSkillFilter("");
    setShiftFilter("");
    setPage(1);
    onSyncUrlParams?.({ line: "", skill: "", status: "" });
  }

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    const list = state.workers.filter((w) => {
      if (lineFilter && w.lineId !== lineFilter) return false;
      if (roleFilter && w.role !== roleFilter) return false;
      if (gradeFilter && String(w.grade) !== gradeFilter) return false;
      if (statusFilter && w.status !== statusFilter) return false;
      if (shiftFilter && w.shift !== shiftFilter) return false;
      if (skillFilter && (w.skills[skillFilter as MachineSkill] ?? 0) < 2) return false;
      if (q) {
        const matchId = w.id.toLowerCase().includes(q);
        const matchEn = w.nameEn.toLowerCase().includes(q);
        const matchBn = w.nameBn.toLowerCase().includes(q);
        if (!matchId && !matchEn && !matchBn) return false;
      }
      return true;
    });

    return [...list].sort((a, b) => {
      if (sortBy === "name") return a.nameEn.localeCompare(b.nameEn);
      if (sortBy === "grade") return a.grade - b.grade;
      if (sortBy === "tenure") return a.joinDate.localeCompare(b.joinDate);
      if (sortBy === "line") return a.lineId.localeCompare(b.lineId) || a.id.localeCompare(b.id);
      return a.id.localeCompare(b.id);
    });
  }, [
    state.workers,
    query,
    lineFilter,
    roleFilter,
    gradeFilter,
    statusFilter,
    shiftFilter,
    skillFilter,
    sortBy
  ]);

  const totalPages = Math.max(1, Math.ceil(filtered.length / PAGE_SIZE));
  const safePage = Math.min(page, totalPages);
  const pageRows = filtered.slice((safePage - 1) * PAGE_SIZE, safePage * PAGE_SIZE);

  const drawerWorker = drawerWorkerId
    ? state.workers.find((w) => w.id === drawerWorkerId) ?? null
    : null;

  const backFilterQuery = useMemo(() => {
    const sp = new URLSearchParams();
    sp.set("tab", "workers");
    if (lineFilter) sp.set("line", lineFilter);
    if (skillFilter) sp.set("skill", skillFilter);
    if (statusFilter) sp.set("status", statusFilter);
    return sp.toString();
  }, [lineFilter, skillFilter, statusFilter]);

  function handleExportCsv() {
    const csv = buildViewerSafeCsv(filtered);
    const blob = new Blob([csv], { type: "text/csv;charset=utf-8;" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = `bunonbrain-workers-viewer-safe-${state.anchorDate}.csv`;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    URL.revokeObjectURL(url);
    toast.push({
      title: t("workforce.workers.exportCsvToast", {
        n: formatNumber(filtered.length, locale)
      }) as string,
      tone: "info"
    });
  }

  return (
    <div className="space-y-4" data-testid="workforce-workers">
      {/* Role & disbursement honesty callouts */}
      {!operator ? (
        <Callout tone="info" testId="wf-viewer-privacy-callout">
          {t("workforce.workers.viewerPrivacyNote")}
        </Callout>
      ) : !admin ? (
        <Callout tone="info" testId="wf-operator-privacy-callout">
          {t("workforce.workers.operatorPrivacyNote")} · {t("workforce.workers.paymentCallout")}
        </Callout>
      ) : (
        <Callout tone="info" testId="wf-admin-payment-callout">
          {t("workforce.workers.paymentCallout")}
        </Callout>
      )}

      {/* Search + filter bar */}
      <div className="surface p-3 space-y-3" role="search">
        <div className="flex flex-wrap items-center gap-2">
          <div className="relative flex-1 min-w-[220px]">
            <Search
              size={14}
              className="absolute left-3 top-1/2 -translate-y-1/2 text-fg-tertiary pointer-events-none"
            />
            <Input
              value={query}
              onChange={(e) => {
                setQuery(e.target.value);
                setPage(1);
              }}
              placeholder={t("workforce.workers.searchPlaceholder") as string}
              className="pl-8"
              data-testid="wf-workers-search"
            />
          </div>
          {operator && (
            <Button
              variant="primary"
              iconLeft={<UserPlus size={14} />}
              onClick={() => {
                setEditingWorker(null);
                setModalOpen(true);
              }}
              data-testid="wf-add-worker-btn"
            >
              {t("workforce.workers.addWorkerBtn")}
            </Button>
          )}
          <Button
            variant="secondary"
            iconLeft={<Download size={14} />}
            onClick={handleExportCsv}
            data-testid="wf-export-csv-btn"
          >
            {t("workforce.workers.exportCsvBtn")}
          </Button>
        </div>

        <div className="flex flex-wrap items-center gap-3">
          <FilterSelect
            label={t("workforce.workers.filterLine") as string}
            value={lineFilter}
            onChange={updateLineFilter}
            testId="wf-filter-line"
          >
            <option value="">{t("workforce.workers.allLines")}</option>
            {WORKFORCE_LINES.map((l) => (
              <option key={l.id} value={l.id}>
                {locale === "bn" ? l.nameBn : l.nameEn}
              </option>
            ))}
          </FilterSelect>

          <FilterSelect
            label={t("workforce.workers.filterRole") as string}
            value={roleFilter}
            onChange={(v) => {
              setRoleFilter(v);
              setPage(1);
            }}
            testId="wf-filter-role"
          >
            <option value="">{t("workforce.workers.allRoles")}</option>
            {WORKER_ROLES.map((r) => (
              <option key={r} value={r}>
                {roleLabel(t as (k: string) => string, r)}
              </option>
            ))}
          </FilterSelect>

          <FilterSelect
            label={t("workforce.workers.filterGrade") as string}
            value={gradeFilter}
            onChange={(v) => {
              setGradeFilter(v);
              setPage(1);
            }}
            testId="wf-filter-grade"
          >
            <option value="">{t("workforce.workers.allGrades")}</option>
            {[1, 2, 3, 4, 5, 6, 7].map((g) => (
              <option key={g} value={String(g)}>
                Grade {g}
              </option>
            ))}
          </FilterSelect>

          <FilterSelect
            label={t("workforce.workers.filterStatus") as string}
            value={statusFilter}
            onChange={updateStatusFilter}
            testId="wf-filter-status"
          >
            <option value="">{t("workforce.workers.allStatuses")}</option>
            {WORKER_STATUSES.map((s) => (
              <option key={s} value={s}>
                {workerStatusLabel(t as (k: string) => string, s)}
              </option>
            ))}
          </FilterSelect>

          <FilterSelect
            label={t("workforce.workers.filterSkill") as string}
            value={skillFilter}
            onChange={updateSkillFilter}
            testId="wf-filter-skill"
          >
            <option value="">{t("workforce.workers.allSkills")}</option>
            {MACHINE_SKILLS.map((sk) => (
              <option key={sk} value={sk}>
                {skillLabel(t as (k: string) => string, sk)}
              </option>
            ))}
          </FilterSelect>

          <FilterSelect
            label={t("workforce.workers.filterShift") as string}
            value={shiftFilter}
            onChange={(v) => {
              setShiftFilter(v);
              setPage(1);
            }}
            testId="wf-filter-shift"
          >
            <option value="">{t("workforce.workers.allShifts")}</option>
            {WORKER_SHIFTS.map((sh) => (
              <option key={sh} value={sh}>
                Shift {sh}
              </option>
            ))}
          </FilterSelect>

          <FilterSelect
            label={t("workforce.workers.sortBy") as string}
            value={sortBy}
            onChange={(v) => setSortBy(v as SortKey)}
            testId="wf-sort-by"
          >
            <option value="id">{t("workforce.workers.sortId")}</option>
            <option value="name">{t("workforce.workers.sortName")}</option>
            <option value="grade">{t("workforce.workers.sortGrade")}</option>
            <option value="tenure">{t("workforce.workers.sortTenure")}</option>
            <option value="line">{t("workforce.workers.sortLine")}</option>
          </FilterSelect>

          {(query || lineFilter || roleFilter || gradeFilter || statusFilter || skillFilter || shiftFilter) && (
            <Button
              size="sm"
              variant="ghost"
              iconLeft={<X size={13} />}
              onClick={clearFilters}
              data-testid="wf-clear-filters"
            >
              {t("workforce.workers.clearFilters")}
            </Button>
          )}
        </div>
      </div>

      {/* Directory table */}
      <Panel
        title={`${t("workforce.tabs.workers")} (${formatNumber(filtered.length, locale)})`}
        flush
        testId="wf-workers-table-panel"
      >
        {filtered.length === 0 ? (
          <EmptyState
            icon={<Users size={20} />}
            title={t("workforce.workers.emptyTitle") as string}
            body={t("workforce.workers.emptyBody") as string}
          />
        ) : (
          <>
            <div className="overflow-x-auto">
              <table className="w-full text-left border-collapse" data-testid="wf-workers-table">
                <thead>
                  <tr className="border-b border-border-subtle text-caption text-fg-tertiary">
                    <th className="py-2.5 px-3 font-medium">{t("workforce.workers.colId")}</th>
                    <th className="py-2.5 px-3 font-medium">{t("workforce.workers.colName")}</th>
                    <th className="py-2.5 px-3 font-medium">{t("workforce.workers.colRole")}</th>
                    <th className="py-2.5 px-3 font-medium">{t("workforce.workers.colLine")}</th>
                    <th className="py-2.5 px-3 font-medium">{t("workforce.workers.colShift")}</th>
                    <th className="py-2.5 px-3 font-medium">{t("workforce.workers.colGrade")}</th>
                    <th className="py-2.5 px-3 font-medium">{t("workforce.workers.colSkills")}</th>
                    <th className="py-2.5 px-3 font-medium">{t("workforce.workers.colStatus")}</th>
                    {operator && (
                      <>
                        <th className="py-2.5 px-3 font-medium">{t("workforce.workers.colPhone")}</th>
                        <th className="py-2.5 px-3 font-medium">{t("workforce.workers.colPayment")}</th>
                      </>
                    )}
                    {admin && (
                      <th className="py-2.5 px-3 font-medium text-right">
                        {t("workforce.workers.colWage")}
                      </th>
                    )}
                  </tr>
                </thead>
                <tbody className="divide-y divide-border-subtle text-body">
                  {pageRows.map((w) => {
                    const qualSkills = MACHINE_SKILLS.filter((sk) => (w.skills[sk] ?? 0) >= 2);
                    const wage = getGradeWage(w.grade);
                    return (
                      <tr
                        key={w.id}
                        onClick={() => setDrawerWorkerId(w.id)}
                        data-testid="worker-row"
                        data-worker-id={w.id}
                        tabIndex={0}
                        onKeyDown={(e) => {
                          if (e.key === "Enter" || e.key === " ") {
                            e.preventDefault();
                            setDrawerWorkerId(w.id);
                          }
                        }}
                        className="cursor-pointer hover:bg-surface-2/70 focus:bg-surface-2 outline-none transition-colors"
                      >
                        <td className="py-2.5 px-3 font-mono text-caption text-accent">{w.id}</td>
                        <td className="py-2.5 px-3">
                          <div className="font-medium text-fg-primary flex flex-wrap items-center gap-1.5">
                            <span>{locale === "bn" ? w.nameBn : w.nameEn}</span>
                            {w.updatedByLocal && (
                              <LocalEditChip
                                label={t("workforce.localEditBadge") as string}
                                testId={`worker-local-chip-${w.id}`}
                              />
                            )}
                            {hasConsecutiveWorkloadAlert(w.weeklyHoursHistory) && (
                              <span onClick={(e) => e.stopPropagation()}>
                                <WorkloadAlertChip
                                  workerId={w.id}
                                  label={t("workforce.workloadAlertBadge") as string}
                                />
                              </span>
                            )}
                          </div>
                          <span className="block text-caption text-fg-tertiary">
                            {locale === "bn" ? w.nameEn : w.nameBn}
                          </span>
                        </td>
                        <td className="py-2.5 px-3 text-caption text-fg-secondary">
                          {roleLabel(t as (k: string) => string, w.role)}
                        </td>
                        <td className="py-2.5 px-3 text-caption text-fg-secondary">
                          {lineShortCode(w.lineId)}
                        </td>
                        <td className="py-2.5 px-3 text-caption text-fg-secondary">{w.shift}</td>
                        <td className="py-2.5 px-3 text-caption tabular-nums">
                          <Badge tone="neutral">
                            G{locale === "bn" ? bn(w.grade) : w.grade}
                          </Badge>
                        </td>
                        <td className="py-2.5 px-3">
                          <div className="flex flex-wrap gap-1">
                            {qualSkills.length === 0 ? (
                              <span className="text-caption text-fg-tertiary">—</span>
                            ) : (
                              qualSkills.slice(0, 3).map((sk) => (
                                <Badge key={sk} tone="neutral">
                                  {sk.toUpperCase()} ({w.skills[sk]})
                                </Badge>
                              ))
                            )}
                          </div>
                        </td>
                        <td className="py-2.5 px-3">
                          <Badge tone={workerStatusTone(w.status)}>
                            {workerStatusLabel(t as (k: string) => string, w.status)}
                          </Badge>
                        </td>
                        {operator && (
                          <>
                            <td
                              className="py-2.5 px-3 font-mono text-caption text-fg-secondary"
                              data-testid={`worker-phone-${w.id}`}
                            >
                              {w.maskedPhone}
                            </td>
                            <td className="py-2.5 px-3 text-caption text-fg-secondary">
                              Paid via: {w.paymentChannel}
                            </td>
                          </>
                        )}
                        {admin && (
                          <td
                            className="py-2.5 px-3 text-right font-mono text-caption text-fg-primary tabular-nums"
                            data-testid={`worker-wage-${w.id}`}
                          >
                            {formatBDT(wage.grossMonthlyBdt, locale)}
                          </td>
                        )}
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>

            {/* Pagination footer */}
            <div className="px-4 py-3 border-t border-border-subtle flex flex-wrap items-center justify-between gap-2 text-caption text-fg-secondary">
              <span>
                {locale === "bn"
                  ? `পৃষ্ঠা ${bn(safePage)} / ${bn(totalPages)} · মোট ${bn(filtered.length)} জন`
                  : `Page ${safePage} of ${totalPages} · ${filtered.length} workers`}
              </span>
              <div className="flex items-center gap-1.5">
                <Button
                  size="sm"
                  variant="secondary"
                  disabled={safePage <= 1}
                  onClick={() => setPage((p) => Math.max(1, p - 1))}
                  data-testid="wf-workers-prev-page"
                >
                  Prev
                </Button>
                <Button
                  size="sm"
                  variant="secondary"
                  disabled={safePage >= totalPages}
                  onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
                  data-testid="wf-workers-next-page"
                >
                  Next
                </Button>
              </div>
            </div>
          </>
        )}
      </Panel>

      {/* Worker Quick Summary Drawer */}
      <Drawer
        open={Boolean(drawerWorker)}
        onClose={() => setDrawerWorkerId(null)}
        title={
          drawerWorker ? (
            <span className="flex items-center gap-2">
              <span className="font-mono text-caption text-accent">{drawerWorker.id}</span>
              <span>{locale === "bn" ? drawerWorker.nameBn : drawerWorker.nameEn}</span>
            </span>
          ) : (
            t("workforce.workers.drawerTitle")
          )
        }
      >
        {drawerWorker && (
          <div className="p-4 space-y-4" data-testid="wf-worker-drawer">
            <div className="flex items-center justify-between gap-2">
              <div>
                <p className="text-body font-semibold text-fg-primary">
                  {drawerWorker.nameEn} · {drawerWorker.nameBn}
                </p>
                <p className="text-caption text-fg-secondary">
                  {roleLabel(t as (k: string) => string, drawerWorker.role)} ·{" "}
                  {lineLabel(drawerWorker.lineId, locale)} · Shift {drawerWorker.shift}
                </p>
              </div>
              <Badge tone={workerStatusTone(drawerWorker.status)}>
                {workerStatusLabel(t as (k: string) => string, drawerWorker.status)}
              </Badge>
            </div>

            <div className="grid grid-cols-2 gap-2.5 surface-2 p-3 rounded-md text-caption">
              <div>
                <span className="text-fg-tertiary block">{t("workforce.workers.colGrade")}</span>
                <span className="font-semibold text-fg-primary">Grade {drawerWorker.grade}</span>
              </div>
              <div>
                <span className="text-fg-tertiary block">Joined</span>
                <span className="font-semibold text-fg-primary">
                  {formatDate(drawerWorker.joinDate, locale)}
                </span>
              </div>
              <div>
                <span className="text-fg-tertiary block">30d attendance</span>
                <span className="font-semibold text-fg-primary">
                  {computeWorkerAttendanceRatePct(
                    drawerWorker.id,
                    state.attendanceByDate,
                    state.anchorDate
                  )}
                  %
                </span>
              </div>
              <div>
                <span className="text-fg-tertiary block">Latest wk efficiency</span>
                <span className="font-semibold text-fg-primary">
                  {calcWorkerLatestEfficiencyPct(drawerWorker)}%
                </span>
              </div>
              {operator && (
                <>
                  <div>
                    <span className="text-fg-tertiary block">{t("workforce.workers.colPhone")}</span>
                    <span className="font-mono text-fg-primary">{drawerWorker.maskedPhone}</span>
                  </div>
                  <div>
                    <span className="text-fg-tertiary block">{t("workforce.workers.colPayment")}</span>
                    <span className="text-fg-primary">Paid via: {drawerWorker.paymentChannel}</span>
                  </div>
                </>
              )}
              {admin && (
                <div className="col-span-2 pt-1 border-t border-border-subtle">
                  <span className="text-fg-tertiary block">{t("workforce.workers.colWage")}</span>
                  <span className="font-semibold text-accent">
                    {formatBDT(getGradeWage(drawerWorker.grade).grossMonthlyBdt, locale)}
                  </span>
                </div>
              )}
            </div>

            <div>
              <p className="text-caption font-medium text-fg-secondary mb-1.5">
                {t("workforce.workers.colSkills")}
              </p>
              <div className="flex flex-wrap gap-1.5">
                {MACHINE_SKILLS.map((sk) => {
                  const lvl = drawerWorker.skills[sk] ?? 0;
                  return (
                    <Badge key={sk} tone={lvl >= 2 ? "info" : "neutral"}>
                      {sk.toUpperCase()}: {lvl}/4
                    </Badge>
                  );
                })}
              </div>
            </div>

            {operator && (
              <div className="space-y-2 pt-2 border-t border-border-subtle">
                <p className="text-caption font-medium text-fg-secondary">
                  {t("workforce.workers.quickStatusLabel")}
                </p>
                <div className="flex flex-wrap gap-2">
                  {WORKER_STATUSES.map((st) => (
                    <Button
                      key={st}
                      size="sm"
                      variant={drawerWorker.status === st ? "primary" : "secondary"}
                      data-testid={`wf-drawer-status-${st}`}
                      onClick={() => {
                        const res = state.updateWorker(drawerWorker.id, { status: st }, actorEmail);
                        toast.push({
                          title: t("workforce.workers.workerUpdatedToast", {
                            id: drawerWorker.id
                          }) as string,
                          tone: "success",
                          onUndo: res.undo
                        });
                      }}
                    >
                      {workerStatusLabel(t as (k: string) => string, st)}
                    </Button>
                  ))}
                </div>
              </div>
            )}

            <div className="flex flex-wrap items-center justify-between gap-2 pt-3 border-t border-border-subtle">
              {operator && (
                <Button
                  size="sm"
                  variant="secondary"
                  iconLeft={<Edit3 size={13} />}
                  data-testid="wf-drawer-edit-btn"
                  onClick={() => {
                    setEditingWorker(drawerWorker);
                    setModalOpen(true);
                  }}
                >
                  {t("workforce.workers.editWorkerBtn")}
                </Button>
              )}
              <Link
                href={`/app/workforce/workers/${encodeURIComponent(drawerWorker.id)}?${backFilterQuery}`}
                data-testid="wf-drawer-open-profile"
                className="inline-flex h-9 items-center gap-1.5 px-3.5 rounded-[var(--radius-control)] bg-[var(--btn-primary-bg)] text-[var(--btn-primary-fg)] text-body font-medium hover:bg-[var(--btn-primary-bg-hover)]"
              >
                <span>{t("workforce.workers.openFullProfile")}</span>
                <ExternalLink size={13} />
              </Link>
            </div>
          </div>
        )}
      </Drawer>

      {/* Add / Edit Worker Modal (Operator+) */}
      <WorkerFormModal
        open={modalOpen}
        onClose={() => setModalOpen(false)}
        worker={editingWorker}
        actorEmail={actorEmail}
        onSaved={(id, isEdit, undo) => {
          setModalOpen(false);
          toast.push({
            title: isEdit
              ? (t("workforce.workers.workerUpdatedToast", { id }) as string)
              : (t("workforce.workers.workerAddedToast", { id, line: "plant" }) as string),
            tone: "success",
            onUndo: undo
          });
        }}
      />
    </div>
  );
}

function WorkerFormModal({
  open,
  onClose,
  worker,
  actorEmail,
  onSaved
}: {
  open: boolean;
  onClose: () => void;
  worker: WorkerRecord | null;
  actorEmail: string;
  onSaved: (id: string, isEdit: boolean, undo: () => void) => void;
}) {
  const { t, locale } = useT();
  const state = useWorkforceStore();

  const [nameEn, setNameEn] = useState("");
  const [nameBn, setNameBn] = useState("");
  const [gender, setGender] = useState<"female" | "male">("female");
  const [age, setAge] = useState("24");
  const [role, setRole] = useState<WorkerRole>("sewing_operator");
  const [lineId, setLineId] = useState<WorkforceLineId>("line-3");
  const [shift, setShift] = useState<WorkerShift>("A");
  const [grade, setGrade] = useState<SkillGrade>(5);
  const [status, setStatus] = useState<WorkerStatus>("active");
  const [joinDate, setJoinDate] = useState(state.anchorDate);
  const [homeDistrictEn, setHomeDistrictEn] = useState("Gazipur");
  const [residenceAreaEn, setResidenceAreaEn] = useState("Ashulia");
  const [paymentChannel, setPaymentChannel] = useState<PaymentChannel>("bKash");
  const [primarySkill, setPrimarySkill] = useState<MachineSkill>("snls");
  const [nidOnFile, setNidOnFile] = useState(true);
  const [firstAidTrained, setFirstAidTrained] = useState(false);
  const [dayCareEligible, setDayCareEligible] = useState(false);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [submitting, setSubmitting] = useState(false);

  useEffect(() => {
    if (!open) return;
    setErrors({});
    setSubmitting(false);
    if (worker) {
      setNameEn(worker.nameEn);
      setNameBn(worker.nameBn);
      setGender(worker.gender);
      setAge(String(worker.age));
      setRole(worker.role);
      setLineId(worker.lineId);
      setShift(worker.shift);
      setGrade(worker.grade);
      setStatus(worker.status);
      setJoinDate(worker.joinDate);
      setHomeDistrictEn(worker.homeDistrictEn);
      setResidenceAreaEn(worker.residenceAreaEn);
      setPaymentChannel(worker.paymentChannel);
      setNidOnFile(worker.nidOnFile);
      setFirstAidTrained(worker.firstAidTrained);
      setDayCareEligible(worker.dayCareEligible);
    } else {
      setNameEn("");
      setNameBn("");
      setGender("female");
      setAge("24");
      setRole("sewing_operator");
      setLineId("line-3");
      setShift("A");
      setGrade(5);
      setStatus("active");
      setJoinDate(state.anchorDate);
      setHomeDistrictEn("Gazipur");
      setResidenceAreaEn("Ashulia");
      setPaymentChannel("bKash");
      setPrimarySkill("snls");
      setNidOnFile(true);
      setFirstAidTrained(false);
      setDayCareEligible(false);
    }
  }, [open, worker, state.anchorDate]);

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (submitting) return;
    const parsed = WorkerFormSchema.safeParse({
      nameEn,
      nameBn,
      gender,
      age: Number(age),
      role,
      lineId,
      shift,
      grade,
      status,
      joinDate,
      homeDistrictEn,
      residenceAreaEn,
      paymentChannel,
      primarySkill,
      nidOnFile,
      firstAidTrained,
      dayCareEligible
    });

    if (!parsed.success) {
      const nextErr: Record<string, string> = {};
      for (const issue of parsed.error.issues) {
        const key = String(issue.path[0] ?? "form");
        if (key === "nameEn") nextErr.nameEn = t("workforce.workers.errNameRequired") as string;
        else if (key === "age") nextErr.age = t("workforce.workers.errAgeRange") as string;
        else if (key === "joinDate") nextErr.joinDate = t("workforce.workers.errJoinDate") as string;
        else nextErr[key] = issue.message;
      }
      setErrors(nextErr);
      return;
    }

    setSubmitting(true);
    const v = parsed.data;
    if (worker) {
      const res = state.updateWorker(
        worker.id,
        {
          nameEn: v.nameEn,
          nameBn: v.nameBn || v.nameEn,
          gender: v.gender,
          age: v.age,
          role: v.role,
          lineId: v.lineId,
          shift: v.shift,
          grade: v.grade,
          status: v.status,
          homeDistrictEn: v.homeDistrictEn,
          residenceAreaEn: v.residenceAreaEn,
          paymentChannel: v.paymentChannel,
          nidOnFile: v.nidOnFile,
          firstAidTrained: v.firstAidTrained,
          dayCareEligible: v.dayCareEligible
        },
        actorEmail
      );
      onSaved(worker.id, true, res.undo);
    } else {
      const res = state.addWorker(
        {
          nameEn: v.nameEn,
          nameBn: v.nameBn || v.nameEn,
          gender: v.gender,
          age: v.age,
          role: v.role,
          lineId: v.lineId,
          shift: v.shift,
          grade: v.grade,
          status: v.status,
          joinDate: v.joinDate,
          homeDistrictEn: v.homeDistrictEn,
          homeDistrictBn: v.homeDistrictEn,
          residenceAreaEn: v.residenceAreaEn,
          residenceAreaBn: v.residenceAreaEn,
          maskedPhone: "017XX-XXXXXX",
          nidOnFile: v.nidOnFile,
          paymentChannel: v.paymentChannel,
          firstAidTrained: v.firstAidTrained,
          dayCareEligible: v.dayCareEligible,
          primarySkill: v.primarySkill
        },
        actorEmail
      );
      onSaved(res.id, false, res.undo);
    }
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={worker ? t("workforce.workers.modalEditTitle") : t("workforce.workers.modalAddTitle")}
      testId="wf-worker-modal"
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button
            variant="primary"
            onClick={handleSubmit}
            disabled={submitting}
            data-testid="wf-worker-modal-save"
          >
            {t("workforce.workers.saveWorkerBtn")}
          </Button>
        </>
      }
    >
      <form onSubmit={handleSubmit} className="space-y-3">
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <Field label={t("workforce.workers.fieldNameEn")} error={errors.nameEn}>
            {({ id }) => (
              <Input
                id={id}
                value={nameEn}
                onChange={(e) => setNameEn(e.target.value)}
                data-autofocus
                data-testid="wf-worker-input-name-en"
              />
            )}
          </Field>
          <Field label={t("workforce.workers.fieldNameBn")}>
            {({ id }) => (
              <Input
                id={id}
                value={nameBn}
                onChange={(e) => setNameBn(e.target.value)}
                data-testid="wf-worker-input-name-bn"
              />
            )}
          </Field>
        </div>

        <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
          <Field label={t("workforce.workers.fieldGender")}>
            {({ id }) => (
              <Select
                id={id}
                value={gender}
                onChange={(e) => setGender(e.target.value as "female" | "male")}
                data-testid="wf-worker-input-gender"
              >
                <option value="female">{t("workforce.workers.genderFemale")}</option>
                <option value="male">{t("workforce.workers.genderMale")}</option>
              </Select>
            )}
          </Field>
          <Field label={t("workforce.workers.fieldAge")} error={errors.age}>
            {({ id }) => (
              <Input
                id={id}
                type="number"
                value={age}
                onChange={(e) => setAge(e.target.value)}
                data-testid="wf-worker-input-age"
              />
            )}
          </Field>
          <Field label={t("workforce.workers.fieldJoinDate")} error={errors.joinDate}>
            {({ id }) => (
              <Input
                id={id}
                type="date"
                value={joinDate}
                onChange={(e) => setJoinDate(e.target.value)}
                data-testid="wf-worker-input-join-date"
              />
            )}
          </Field>
        </div>

        <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
          <Field label={t("workforce.workers.fieldRole")}>
            {({ id }) => (
              <Select
                id={id}
                value={role}
                onChange={(e) => setRole(e.target.value as WorkerRole)}
                data-testid="wf-worker-input-role"
              >
                {WORKER_ROLES.map((r) => (
                  <option key={r} value={r}>
                    {roleLabel(t as (k: string) => string, r)}
                  </option>
                ))}
              </Select>
            )}
          </Field>
          <Field label={t("workforce.workers.fieldLine")}>
            {({ id }) => (
              <Select
                id={id}
                value={lineId}
                onChange={(e) => setLineId(e.target.value as WorkforceLineId)}
                data-testid="wf-worker-input-line"
              >
                {WORKFORCE_LINES.map((l) => (
                  <option key={l.id} value={l.id}>
                    {locale === "bn" ? l.nameBn : l.nameEn}
                  </option>
                ))}
              </Select>
            )}
          </Field>
          <Field label={t("workforce.workers.fieldGrade")}>
            {({ id }) => (
              <Select
                id={id}
                value={String(grade)}
                onChange={(e) => setGrade(Number(e.target.value) as SkillGrade)}
                data-testid="wf-worker-input-grade"
              >
                {[7, 6, 5, 4, 3, 2, 1].map((g) => (
                  <option key={g} value={String(g)}>
                    Grade {g}
                  </option>
                ))}
              </Select>
            )}
          </Field>
        </div>

        <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
          <Field label={t("workforce.workers.fieldShift")}>
            {({ id }) => (
              <Select
                id={id}
                value={shift}
                onChange={(e) => setShift(e.target.value as WorkerShift)}
                data-testid="wf-worker-input-shift"
              >
                <option value="A">Shift A</option>
                <option value="B">Shift B</option>
              </Select>
            )}
          </Field>
          <Field label={t("workforce.workers.fieldStatus")}>
            {({ id }) => (
              <Select
                id={id}
                value={status}
                onChange={(e) => setStatus(e.target.value as WorkerStatus)}
                data-testid="wf-worker-input-status"
              >
                {WORKER_STATUSES.map((st) => (
                  <option key={st} value={st}>
                    {workerStatusLabel(t as (k: string) => string, st)}
                  </option>
                ))}
              </Select>
            )}
          </Field>
          <Field label={t("workforce.workers.fieldPayment")}>
            {({ id }) => (
              <Select
                id={id}
                value={paymentChannel}
                onChange={(e) => setPaymentChannel(e.target.value as PaymentChannel)}
                data-testid="wf-worker-input-payment"
              >
                {PAYMENT_CHANNELS.map((ch) => (
                  <option key={ch} value={ch}>
                    {ch}
                  </option>
                ))}
              </Select>
            )}
          </Field>
        </div>

        {!worker && (
          <Field label={t("workforce.workers.fieldPrimarySkill")}>
            {({ id }) => (
              <Select
                id={id}
                value={primarySkill}
                onChange={(e) => setPrimarySkill(e.target.value as MachineSkill)}
                data-testid="wf-worker-input-skill"
              >
                {MACHINE_SKILLS.map((sk) => (
                  <option key={sk} value={sk}>
                    {skillLabel(t as (k: string) => string, sk)}
                  </option>
                ))}
              </Select>
            )}
          </Field>
        )}

        <div className="flex flex-wrap gap-4 pt-2 text-caption text-fg-secondary">
          <label className="inline-flex items-center gap-2 cursor-pointer">
            <input
              type="checkbox"
              checked={nidOnFile}
              onChange={(e) => setNidOnFile(e.target.checked)}
              data-testid="wf-worker-input-nid"
            />
            <span>{t("workforce.workers.fieldNidOnFile")}</span>
          </label>
          <label className="inline-flex items-center gap-2 cursor-pointer">
            <input
              type="checkbox"
              checked={firstAidTrained}
              onChange={(e) => setFirstAidTrained(e.target.checked)}
              data-testid="wf-worker-input-first-aid"
            />
            <span>{t("workforce.workers.fieldFirstAid")}</span>
          </label>
          <label className="inline-flex items-center gap-2 cursor-pointer">
            <input
              type="checkbox"
              checked={dayCareEligible}
              onChange={(e) => setDayCareEligible(e.target.checked)}
              data-testid="wf-worker-input-daycare"
            />
            <span>{t("workforce.workers.fieldDayCare")}</span>
          </label>
        </div>
      </form>
    </Modal>
  );
}
