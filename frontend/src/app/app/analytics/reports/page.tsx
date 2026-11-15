"use client";

// Reports & exports: generate a report for the current filters (preview
// here), then export it as CSV or PDF. Reports carry their scope, period,
// generation time, filters, metric definitions, REAL / SIMULATED and
// coverage warnings, and respect your role and factory scope. Exports are audited.

import { useState } from "react";
import { Download, FileText } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { AN, ANALYTICS_PERMS, AnalyticsFilterBar, AnalyticsTabs, DataTable, KpiCard, useAnalyticsFilters, useDefinitions } from "@/components/analytics/shared";
import { useResource } from "@/lib/useResource";
import { downloadCsv } from "@/lib/workers";
import { errorMessage } from "@/lib/api";
import { dhaka } from "@/lib/monitoring";
import type { ReportDocument, ReportType } from "@/lib/analytics";

export default function ReportsPage() {
  return (
    <RequirePermission perms={ANALYTICS_PERMS} what="Reports">
      <Reports />
    </RequirePermission>
  );
}

function Reports() {
  const { f, set, query, factories } = useAnalyticsFilters(`${AN}/reports`);
  const defs = useDefinitions();
  const toast = useToast();
  const types = useResource<ReportType[]>("/api/analytics/reports");
  const [type, setType] = useState<string | null>(null);
  const doc = useResource<ReportDocument>(type ? `/api/analytics/reports/${type}${query()}` : null);
  const [busy, setBusy] = useState<string | null>(null);

  async function exportAs(format: "csv" | "pdf") {
    if (!type) return;
    setBusy(format);
    try {
      await downloadCsv(`/api/analytics/reports/${type}/export${query({ format })}`, `bunonbrain-${type}.${format}`);
      toast.push({ tone: "success", title: `${format.toUpperCase()} downloaded` });
    } catch (e) {
      toast.push({ tone: "error", title: "Export failed", description: errorMessage(e) });
    } finally {
      setBusy(null);
    }
  }

  const d = doc.data;
  return (
    <div className="page" data-testid="analytics-reports">
      <PageHeader eyebrow="Factory analytics" title="Reports & exports"
        description="Daily, weekly and monthly operations reports and topic reports, for the filters below — as a preview, CSV or PDF." />
      <AnalyticsTabs />
      <AnalyticsFilterBar f={f} set={set} factories={factories} show={{ machine: true, shift: true, family: true }} />
      <div className="grid gap-4 xl:grid-cols-[18rem_1fr]">
        <Panel title="Report" icon={<FileText size={15} />} testId="report-types">
          {!types.data ? <SkeletonRows rows={5} /> : (
            <ul className="p-2 space-y-1">
              {types.data.map((t) => (
                <li key={t.type}>
                  <button type="button" disabled={!t.available} onClick={() => setType(t.type)} title={t.reason ?? undefined} data-testid={`report-${t.type}`}
                    className={`w-full text-left rounded px-3 py-2 text-caption ${type === t.type ? "bg-[var(--accent-soft)] text-fg-primary font-medium" : "hover:bg-surface-2"} disabled:opacity-50 disabled:cursor-not-allowed`}>
                    {t.title}{!t.available && <span className="block text-fg-tertiary">{t.reason}</span>}
                  </button>
                </li>
              ))}
            </ul>
          )}
        </Panel>
        <div className="min-w-0">
          {!type ? <Callout tone="info">Choose a report. Operations reports cover 1, 7 or 30 days ending on the “To” date (today by default); the others use the date range.</Callout>
            : doc.error && !d ? <ErrorState title="Could not generate the report" body={doc.error.message} retryLabel="Try again" onRetry={() => void doc.reload()} />
            : !d ? <SkeletonRows rows={8} />
            : (
              <Panel title={d.title} subtitle={`${d.scope} · ${d.from === d.to ? d.from : `${d.from} – ${d.to}`} (${d.timeZone}) · generated ${dhaka(d.generatedAt)} by ${d.generatedBy}`}
                right={<span className="flex gap-2">
                  <Button size="sm" iconLeft={<Download size={13} />} disabled={busy !== null} onClick={() => void exportAs("csv")} data-testid="export-csv">CSV</Button>
                  <Button size="sm" iconLeft={<Download size={13} />} disabled={busy !== null} onClick={() => void exportAs("pdf")} data-testid="export-pdf">PDF</Button>
                </span>} testId="report-preview">
                <div className="p-4 space-y-4">
                  <p className="flex flex-wrap gap-2 text-caption">
                    <Badge tone={d.source === "SIMULATED" ? "accent" : "success"}>{d.source === "SIMULATED" ? "SIMULATED data" : "REAL data"}</Badge>
                    {d.filters.map((x) => <Badge key={x.name}>{x.name}: {x.value}</Badge>)}
                  </p>
                  {d.warnings.length > 0 && <Callout tone="warning"><ul className="list-disc pl-4">{d.warnings.map((w) => <li key={w}>{w}</li>)}</ul></Callout>}
                  {d.sections.map((s) => (
                    <section key={s.title} className="space-y-2">
                      <h3 className="font-medium">{s.title}</h3>
                      {s.note && <p className="text-caption text-fg-tertiary">{s.note}</p>}
                      {s.kpis.length > 0 && <div className="grid gap-3 grid-cols-2 md:grid-cols-3 xl:grid-cols-4">{s.kpis.map((k) => <KpiCard key={k.metricId} k={k} defs={defs} />)}</div>}
                      {s.tables.map((t) => (
                        <div key={t.title} className="rounded border border-border-subtle">
                          <p className="px-3 pt-2 text-caption font-medium">{t.title}</p>
                          {t.note && <p className="px-3 text-caption text-fg-tertiary">{t.note}</p>}
                          <DataTable columns={t.columns} rows={t.rows.slice(0, 50).map((row) => row.map((c) => (c === null ? "—" : typeof c === "boolean" ? (c ? "yes" : "no") : c)))} />
                          {t.rows.length > 50 && <p className="px-3 pb-2 text-caption text-fg-tertiary">{t.rows.length - 50} more rows in the export.</p>}
                        </div>
                      ))}
                    </section>
                  ))}
                  <details className="text-caption">
                    <summary className="cursor-pointer font-medium">Metric definitions ({d.definitions.length})</summary>
                    <ul className="mt-2 space-y-1">{d.definitions.map((m) => <li key={m.id}><b>{m.name}</b> ({m.kind.toLowerCase().replace("_", " ")}): {m.formula}. {m.missingData}</li>)}</ul>
                  </details>
                </div>
              </Panel>
            )}
        </div>
      </div>
    </div>
  );
}
