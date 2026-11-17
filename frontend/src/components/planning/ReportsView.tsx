"use client";

// Plan-versus-actual reports: order-wise production, line target
// achievement, daily and shift plan versus actual, delivery progress,
// capacity utilisation, delays and target revisions. Preview here, export
// CSV or PDF; reports respect factory and line scope and carry their
// definitions, data source and caveats.

import { useState } from "react";
import { Download, FileText } from "lucide-react";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { FilterSelect } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Panel } from "@/components/ui/Panel";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { DataTable } from "@/components/analytics/shared";
import type { PlanningScope } from "@/components/planning/shared";
import { useResource } from "@/lib/useResource";
import { downloadCsv } from "@/lib/workers";
import { errorMessage } from "@/lib/api";
import { dhaka } from "@/lib/monitoring";
import { addDays, planningApi, type PlanningReportType, type ReportDocument } from "@/lib/planning";

export function ReportsView({ scope }: { scope: PlanningScope }) {
  const { q, ctx, factoryId } = scope;
  const toast = useToast();
  const types = useResource<PlanningReportType[]>(planningApi.reportTypes);
  const type = q.get("type");
  const from = q.get("from") || addDays(ctx.today, -6);
  const to = q.get("to") || ctx.today;
  const source = q.get("source") === "SIMULATED" ? "SIMULATED" : "REAL";
  const line = q.get("line");
  const params = { factoryId, from, to, source, lineId: line || undefined };
  const doc = useResource<ReportDocument>(type ? planningApi.report(type, params) : null);
  const [busy, setBusy] = useState<string | null>(null);
  const d = doc.data;

  async function exportAs(format: "csv" | "pdf") {
    if (!type) return;
    setBusy(format);
    try {
      await downloadCsv(planningApi.reportExport(type, format, params), `bunonbrain-planning-${type}.${format}`);
      toast.push({ tone: "success", title: `${format.toUpperCase()} downloaded` });
    } catch (e) {
      toast.push({ tone: "error", title: "Export failed", description: errorMessage(e) });
    } finally { setBusy(null); }
  }

  return (
    <>
      <div className="mb-4 flex flex-wrap items-end gap-2">
        <label className="text-caption text-fg-secondary flex flex-col gap-1">From<Input type="date" className="h-9" value={from} onChange={(e) => q.set({ from: e.target.value || null })} data-testid="report-from" /></label>
        <label className="text-caption text-fg-secondary flex flex-col gap-1">To<Input type="date" className="h-9" value={to} onChange={(e) => q.set({ to: e.target.value || null })} data-testid="report-to" /></label>
        <FilterSelect label="Line" value={line} onChange={(v) => q.set({ line: v || null })}><option value="">All lines</option>{ctx.lines.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}</FilterSelect>
        <FilterSelect label="Data" value={source} onChange={(v) => q.set({ source: v === "REAL" ? null : v })}><option value="REAL">Real data</option><option value="SIMULATED">Simulated data</option></FilterSelect>
      </div>
      <div className="grid gap-4 xl:grid-cols-[18rem_1fr]">
        <Panel title="Report" icon={<FileText size={15} />} testId="planning-report-types">
          {!types.data ? <SkeletonRows rows={5} /> : (
            <ul className="p-2 space-y-1">
              {types.data.map((t) => (
                <li key={t.type}>
                  <button type="button" onClick={() => q.set({ type: t.type })} data-testid={`report-${t.type}`} title={t.description}
                    className={`w-full text-left rounded px-3 py-2 text-caption ${type === t.type ? "bg-[var(--accent-soft)] text-fg-primary font-medium" : "hover:bg-surface-2"}`}>{t.title}
                    <span className="block text-fg-tertiary font-normal">{t.description}</span></button>
                </li>
              ))}
            </ul>
          )}
        </Panel>
        <div className="min-w-0">
          {!type ? <Callout tone="info">Choose a report. Figures use the factory&apos;s published plans and counters; where there is no evidence the cell is empty, not zero.</Callout>
            : doc.error && !d ? <ErrorState title="Could not generate the report" body={doc.error.message} retryLabel="Try again" onRetry={() => void doc.reload()} />
            : !d ? <SkeletonRows rows={8} />
            : (
              <Panel title={d.title} subtitle={`${d.scope} · ${d.from} – ${d.to} (${d.timeZone}) · generated ${dhaka(d.generatedAt)} by ${d.generatedBy}`} testId="planning-report-preview"
                right={ctx.capabilities.exportReports ? (
                  <span className="flex gap-2">
                    <Button size="sm" iconLeft={<Download size={13} />} disabled={busy !== null} onClick={() => void exportAs("csv")} data-testid="export-csv">CSV</Button>
                    <Button size="sm" iconLeft={<Download size={13} />} disabled={busy !== null} onClick={() => void exportAs("pdf")} data-testid="export-pdf">PDF</Button>
                  </span>) : <span className="text-caption text-fg-tertiary">Export needs the export permission</span>}>
                <div className="p-4 space-y-4">
                  <p className="flex flex-wrap gap-2"><Badge tone={d.source === "SIMULATED" ? "accent" : "success"}>{d.source === "SIMULATED" ? "SIMULATED data" : "REAL data"}</Badge>{d.filters.map((x) => <Badge key={x.name}>{x.name}: {x.value}</Badge>)}</p>
                  {d.warnings.length > 0 && <Callout tone="warning"><ul className="list-disc pl-4">{d.warnings.map((w) => <li key={w}>{w}</li>)}</ul></Callout>}
                  {d.sections.map((s) => (
                    <section key={s.title} className="space-y-2">
                      <h3 className="font-medium">{s.title}</h3>
                      {s.note && <p className="text-caption text-fg-tertiary">{s.note}</p>}
                      {s.tables.map((t) => (
                        <div key={t.title} className="rounded border border-border-subtle">
                          {t.note && <p className="px-3 pt-2 text-caption text-fg-tertiary">{t.note}</p>}
                          <DataTable columns={t.columns} empty="No rows in this period." rows={t.rows.slice(0, 100).map((row) => row.map((c) => (c === null ? "—" : typeof c === "boolean" ? (c ? "yes" : "no") : typeof c === "string" && /^\d{4}-\d\d-\d\dT/.test(c) ? dhaka(c) : c)))} />
                          {t.rows.length > 100 && <p className="px-3 pb-2 text-caption text-fg-tertiary">{t.rows.length - 100} more rows in the export.</p>}
                        </div>
                      ))}
                    </section>
                  ))}
                  <details className="text-caption"><summary className="cursor-pointer font-medium">Metric definitions ({d.definitions.length})</summary>
                    <ul className="mt-2 space-y-1">{d.definitions.map((m) => <li key={m.id}><b>{m.name}</b>: {m.formula}. {m.missingData}</li>)}</ul></details>
                </div>
              </Panel>
            )}
        </div>
      </div>
    </>
  );
}
