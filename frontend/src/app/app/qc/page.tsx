"use client";

// /app/qc — QC defects & rework from GET /api/qc/defects. "Flag issue"
// POSTs /api/qc/flag (Operator), which stores an insight on the server.

import { useEffect, useMemo, useRef, useState } from "react";
import { Flag, RefreshCw } from "lucide-react";
import { Panel } from "@/components/ui/Panel";
import { PageHeader } from "@/components/ui/PageHeader";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { Modal } from "@/components/ui/Modal";
import { Field, TextArea } from "@/components/ui/Field";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { useT } from "@/lib/useT";
import { cn } from "@/lib/cn";
import { formatRelative } from "@/lib/format";
import { qcOperationLabel, qcRateColor, QC_RATE_HIGH_THRESHOLD, QC_RATE_MEDIUM_THRESHOLD } from "@/lib/qc";
import { useFactoryBrainLiveStore } from "@/store/factoryBrain.live.store";
import { useResource, announceDataChanged } from "@/lib/useResource";
import { apiJson, errorMessage } from "@/lib/api";
import { canOperate, useAuthStore } from "@/lib/auth";
import type { QcResponse, QcOperationStats } from "@/services/qc.defects.server";
import type { QcFlagRequest, QcFlagResponse } from "@/services/contracts";

type SortKey = "operation" | "line" | "defect" | "rework";
type FlagTarget = { operation: string; lineId: string; defectRatePct: number; reworkRatePct: number };
const NOTE_MAX = 280;

function rateTone(rate: number): "danger" | "warning" | "success" {
  if (rate >= QC_RATE_HIGH_THRESHOLD) return "danger";
  if (rate >= QC_RATE_MEDIUM_THRESHOLD) return "warning";
  return "success";
}
const defectRatePct = (c: QcOperationStats) => (c.totals.inspected > 0 ? (c.totals.defects / c.totals.inspected) * 100 : 0);
const reworkRatePct = (c: QcOperationStats) => (c.totals.inspected > 0 ? (c.totals.rework / c.totals.inspected) * 100 : 0);
const round2 = (n: number) => Math.round(n * 100) / 100;

export default function QcPage() {
  const { t, locale } = useT();
  const toast = useToast();
  const operator = canOperate(useAuthStore((s) => s.user));
  const qc = useResource<QcResponse>("/api/qc/defects");
  const [sortKey, setSortKey] = useState<SortKey>("defect");
  const [target, setTarget] = useState<FlagTarget | null>(null);
  const [note, setNote] = useState("");
  const [flagging, setFlagging] = useState(false);
  const [flagError, setFlagError] = useState<string | null>(null);

  // Refresh when the overview's sensor snapshot moves the simulation.
  const simTick = useFactoryBrainLiveStore((s) => s.snapshot?.tick);
  const lastTick = useRef(simTick);
  useEffect(() => {
    if (simTick === undefined || simTick === lastTick.current) return;
    lastTick.current = simTick;
    void qc.reload();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [simTick]);

  const data = qc.data;
  const cells = useMemo(() => {
    if (!data) return [];
    return [...data.operations].sort((a, b) => {
      if (sortKey === "operation") return a.operation.localeCompare(b.operation);
      if (sortKey === "line") return a.lineId.localeCompare(b.lineId);
      if (sortKey === "defect") return defectRatePct(b) - defectRatePct(a);
      return reworkRatePct(b) - reworkRatePct(a);
    });
  }, [data, sortKey]);

  function openFlag(row: FlagTarget) {
    setTarget(row);
    setNote("");
    setFlagError(null);
  }

  async function submitFlag() {
    if (!target || flagging) return;
    setFlagging(true);
    setFlagError(null);
    const body: QcFlagRequest = {
      operation: target.operation,
      lineId: target.lineId,
      defectRatePct: Math.min(100, round2(target.defectRatePct)),
      reworkRatePct: Math.min(100, round2(target.reworkRatePct)),
      ...(note.trim() ? { note: note.trim() } : {})
    };
    try {
      const res = await apiJson<QcFlagResponse>("/api/qc/flag", { method: "POST", body });
      toast.push({ title: t("qc.flagToastTitle"), description: `${qcOperationLabel(target.operation)} · ${target.lineId} · insight ${res.insightId}` });
      announceDataChanged();
      setTarget(null);
    } catch (e) {
      setFlagError(errorMessage(e));
    } finally {
      setFlagging(false);
    }
  }

  const FlagButton = ({ row, testId, iconOnly }: { row: FlagTarget; testId: string; iconOnly?: boolean }) =>
    operator ? (
      <Button variant="secondary" size="sm" onClick={() => openFlag(row)} data-testid={testId} aria-label={t("qc.flagAria", { op: row.operation, line: row.lineId })}>
        <Flag size={12} /> {!iconOnly && t("qc.flagIssue")}
      </Button>
    ) : null;

  return (
    <div className="page" data-tour="qc">
      <PageHeader
        eyebrow="Operations"
        title={t("qc.cardTitle")}
        description={t("qc.pageSubtitle")}
        meta={data?.meta.simulated ? <span title={data.meta.source} data-testid="qc-page-simulated-pill"><Badge tone="accent" dot>{t("qc.simulatedChip")}</Badge></span> : null}
        actions={
          <Button variant="secondary" size="sm" onClick={() => void qc.reload()} disabled={qc.refreshing} aria-label={t("qc.refreshAria")}>
            <RefreshCw size={13} className={cn(qc.refreshing && "animate-spin")} /> Refresh
          </Button>
        }
      />

      {!operator && <Callout tone="info" className="mt-5">Flagging an issue needs the Operator or Admin role.</Callout>}

      <div className="mt-5">
        {qc.error && !data ? (
          <div className="surface">
            <ErrorState title={t("qc.loadFailed")} body={t("qc.loadFailedBody")} detail={errorMessage(qc.error)} retryLabel={t("qc.retry")} onRetry={() => void qc.reload()} />
          </div>
        ) : qc.loading ? (
          <SkeletonRows rows={8} />
        ) : data ? (
          <div data-testid="qc-page-table" data-simulated={data.meta.simulated ? "true" : "false"} className="space-y-4">
            <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
              {(
                [
                  { key: "defect", title: t("qc.topDefect"), subtitle: t("qc.topDefectSubtitle"), rows: data.topByDefectRate, rate: (r: (typeof data.topByDefectRate)[number]) => r.defectRatePct, label: (p: string) => t("qc.defectPct", { pct: p }) },
                  { key: "rework", title: t("qc.topRework"), subtitle: t("qc.topReworkSubtitle"), rows: data.topByReworkRate, rate: (r: (typeof data.topByReworkRate)[number]) => r.reworkRatePct, label: (p: string) => t("qc.reworkPct", { pct: p }) }
                ] as const
              ).map((list) => (
                <Panel key={list.key} title={list.title} subtitle={list.subtitle}>
                  <ul className="space-y-2">
                    {list.rows.map((row) => (
                      <li key={`${list.key}-${row.operation}-${row.lineId}`} className="surface-2 p-3 flex items-center justify-between gap-2">
                        <div className="min-w-0">
                          <p className="text-body text-fg-primary truncate">{qcOperationLabel(row.operation)} <span className="font-mono text-[12px] text-fg-tertiary">· {row.lineId}</span></p>
                          <p className={cn("text-caption tabular-nums font-medium", qcRateColor(list.rate(row)))}>{list.label(list.rate(row).toFixed(2))}</p>
                        </div>
                        <FlagButton row={row} testId={`qc-flag-top-${list.key}-${row.operation}-${row.lineId}`} />
                      </li>
                    ))}
                  </ul>
                </Panel>
              ))}
            </div>

            <Panel
              title={t("qc.fullGrid")}
              subtitle={t("qc.fullGridSubtitle")}
              flush
              right={
                <div className="inline-flex rounded-[var(--radius-control)] border border-border-subtle p-0.5 bg-surface-2" role="group" aria-label={t("qc.sortBy")}>
                  {(["operation", "line", "defect", "rework"] as SortKey[]).map((k) => (
                    <button
                      key={k}
                      type="button"
                      onClick={() => setSortKey(k)}
                      aria-pressed={sortKey === k}
                      className={cn("h-7 px-2.5 rounded-md text-caption", sortKey === k ? "bg-[var(--bg-surface)] text-fg-primary shadow-sm" : "text-fg-secondary hover:text-fg-primary")}
                    >
                      {k === "operation" ? t("qc.colOp") : k === "line" ? t("qc.colLine") : k === "defect" ? t("qc.colDefect") : t("qc.colRework")}
                    </button>
                  ))}
                </div>
              }
            >
              <div className="overflow-x-auto">
                <table className="data-table" data-testid="qc-full-grid">
                  <thead>
                    <tr>
                      <th>{t("qc.colOp")}</th>
                      <th>{t("qc.colLine")}</th>
                      <th className="num">{t("qc.colInspected")}</th>
                      <th className="num">{t("qc.colDefects")}</th>
                      <th className="num">{t("qc.colMajor")}</th>
                      <th className="num">{t("qc.colRework")}</th>
                      <th className="num">{t("qc.colDefectRate")}</th>
                      <th className="num">{t("qc.colReworkRate")}</th>
                      {operator && <th><span className="sr-only">Actions</span></th>}
                    </tr>
                  </thead>
                  <tbody>
                    {cells.map((c) => {
                      const d = defectRatePct(c);
                      const r = reworkRatePct(c);
                      return (
                        <tr key={`${c.operation}|${c.lineId}`} data-testid={`qc-grid-row-${c.operation}-${c.lineId}`}>
                          <td className="text-fg-primary font-medium">{qcOperationLabel(c.operation)}</td>
                          <td className="font-mono text-[12px]">{c.lineId}</td>
                          <td className="num">{c.totals.inspected.toLocaleString(locale)}</td>
                          <td className="num">{c.totals.defects.toLocaleString(locale)}</td>
                          <td className="num">{c.totals.major.toLocaleString(locale)}</td>
                          <td className="num">{c.totals.rework.toLocaleString(locale)}</td>
                          <td className="num"><Badge tone={rateTone(d)}>{d.toFixed(2)}%</Badge></td>
                          <td className="num"><Badge tone={rateTone(r)}>{r.toFixed(2)}%</Badge></td>
                          {operator && (
                            <td className="num">
                              <FlagButton row={{ operation: c.operation, lineId: c.lineId, defectRatePct: d, reworkRatePct: r }} testId={`qc-flag-grid-${c.operation}-${c.lineId}`} iconOnly />
                            </td>
                          )}
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
              <p className="px-4 py-3 border-t border-border-subtle text-caption text-fg-tertiary">
                {data.meta.notes}
                {data.meta.generatedAt && ` · ${t("qc.generated", { rel: formatRelative(data.meta.generatedAt, locale) })}`}
              </p>
            </Panel>
          </div>
        ) : null}
      </div>

      <Modal
        open={!!target}
        onClose={() => !flagging && setTarget(null)}
        locked={flagging}
        title={t("qc.flagIssue")}
        description={target ? `${qcOperationLabel(target.operation)} · ${target.lineId} · defect ${target.defectRatePct.toFixed(2)}% · rework ${target.reworkRatePct.toFixed(2)}%` : undefined}
        testId="qc-flag-dialog"
        footer={
          <>
            <Button variant="ghost" onClick={() => setTarget(null)} disabled={flagging}>Cancel</Button>
            <Button variant="primary" onClick={() => void submitFlag()} disabled={flagging || note.length > NOTE_MAX} data-testid="qc-flag-submit">
              <Flag size={13} /> {flagging ? "Flagging…" : "Flag for review"}
            </Button>
          </>
        }
      >
        <p className="text-caption text-fg-secondary mb-3">Creates an insight on the server for the QA team to review.</p>
        <Field label="Note (optional)" hint={`${note.length}/${NOTE_MAX}`} error={note.length > NOTE_MAX ? `At most ${NOTE_MAX} characters.` : null}>
          {(p) => <TextArea id={p.id} aria-describedby={p.describedBy} rows={3} value={note} onChange={(e) => setNote(e.target.value)} placeholder="What should QA look at?" />}
        </Field>
        {flagError && <Callout tone="danger" className="mt-3" title={t("qc.flagFailedTitle")}>{flagError}</Callout>}
      </Modal>
    </div>
  );
}
