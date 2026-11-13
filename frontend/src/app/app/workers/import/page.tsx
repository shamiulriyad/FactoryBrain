"use client";

// Worker import from CSV, in three steps: choose a file → the API validates
// every row and returns a preview (nothing saved) → confirm, and the valid
// rows are registered in one transaction with new Worker IDs. Invalid rows
// are skipped and explained; fix them in the file and import it again.

import Link from "next/link";
import { useMemo, useState } from "react";
import { useRouter } from "next/navigation";
import { AlertTriangle, CheckCircle2, Download, FileUp, RotateCcw, XCircle } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Field, FilterSelect, Select } from "@/components/ui/Field";
import { Callout } from "@/components/ui/Callout";
import { ConfirmDialog } from "@/components/ui/Modal";
import { StatCard } from "@/components/ui/StatCard";
import { SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { useMyFactories, useFactoryOrg } from "@/components/workers/shared";
import { errorMessage } from "@/lib/api";
import { useT } from "@/lib/useT";
import { P } from "@/lib/access";
import { downloadCsv, formatDay, importWorkers, type ImportResult } from "@/lib/workers";

const MAX_BYTES = 1024 * 1024;

export default function ImportWorkersPage() {
  return (
    <RequirePermission perms={[P.WorkersManage]} what="Importing workers">
      <ImportWorkers />
    </RequirePermission>
  );
}

function ImportWorkers() {
  const { locale } = useT();
  const router = useRouter();
  const toast = useToast();
  const { factories, error: factoriesError } = useMyFactories();
  const [picked, setPicked] = useState("");
  const factoryId = picked || (factories?.length === 1 ? factories[0].id : "");
  const { departments } = useFactoryOrg(factoryId || null);
  const [file, setFile] = useState<File | null>(null);
  const [preview, setPreview] = useState<ImportResult | null>(null);
  const [done, setDone] = useState<ImportResult | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [show, setShow] = useState<"all" | "invalid" | "valid">("all");

  function reset() {
    setFile(null);
    setPreview(null);
    setDone(null);
    setError(null);
    setShow("all");
  }

  async function validate(f: File) {
    setError(null);
    setPreview(null);
    setDone(null);
    if (!factoryId) {
      setError("Choose the factory first.");
      return;
    }
    if (f.size > MAX_BYTES) {
      setError("The file is larger than 1 MB. Split it into smaller files.");
      return;
    }
    setBusy(true);
    try {
      setPreview(await importWorkers(f, factoryId, true));
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  async function commit() {
    if (!file || !preview) return;
    setBusy(true);
    setError(null);
    try {
      const result = await importWorkers(file, factoryId, false);
      setDone(result);
      setPreview(null);
      toast.push({ tone: "success", title: `${result.imported} worker${result.imported === 1 ? "" : "s"} imported` });
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
      setConfirming(false);
    }
  }

  async function template() {
    try {
      await downloadCsv("/api/workers/import/template", "worker-import-template.csv");
    } catch (e) {
      toast.push({ tone: "error", title: "Download failed", description: errorMessage(e) });
    }
  }

  const result = done ?? preview;
  const rows = useMemo(
    () => (result?.rows ?? []).filter((r) => show === "all" || (show === "valid" ? r.valid : !r.valid)),
    [result, show]
  );

  return (
    <div className="page" data-testid="worker-import-page">
      <PageHeader
        eyebrow={<Link href="/app/workers" className="hover:underline">← Worker directory</Link>}
        title="Import workers"
        description="Register many workers from a CSV file. Every row is checked first; nothing is saved until you confirm."
        actions={<Button variant="secondary" size="sm" iconLeft={<Download size={13} />} onClick={() => void template()} data-testid="import-template">Download template</Button>}
      />

      <div className="mt-6 grid gap-4 lg:grid-cols-[minmax(0,1fr)_minmax(0,1.4fr)]">
        <Panel title="1. Choose the file" icon={<FileUp size={14} />}>
          {factories === null && !factoriesError ? (
            <SkeletonRows rows={2} rowHeight="h-10" />
          ) : factoriesError ? (
            <Callout tone="danger">{factoriesError}</Callout>
          ) : (
            <div className="space-y-3">
              {(factories?.length ?? 0) > 1 && (
                <Field label="Import into factory">
                  {({ id }) => (
                    <Select id={id} value={factoryId} onChange={(e) => { setPicked(e.target.value); reset(); }} disabled={busy} data-testid="import-factory">
                      <option value="">Choose a factory…</option>
                      {factories!.map((f) => <option key={f.id} value={f.id}>{f.code} · {f.name}</option>)}
                    </Select>
                  )}
                </Field>
              )}
              <Field label="CSV file" hint="UTF-8 CSV, up to 2,000 rows and 1 MB. Comma, semicolon or tab separated.">
                {({ id, describedBy }) => (
                  <input
                    id={id}
                    type="file"
                    accept=".csv,text/csv"
                    aria-describedby={describedBy}
                    disabled={busy || !factoryId}
                    className="block w-full text-caption text-fg-secondary file:mr-3 file:h-8 file:px-3 file:rounded-[var(--radius-control)] file:border file:border-border-subtle file:bg-surface-2 file:text-fg-primary"
                    onChange={(e) => {
                      const f = e.target.files?.[0] ?? null;
                      setFile(f);
                      if (f) void validate(f);
                    }}
                    data-testid="import-file"
                  />
                )}
              </Field>
              {busy && <SkeletonRows rows={1} rowHeight="h-8" />}
              {error && <Callout tone="danger" role="alert" testId="import-error">{error}</Callout>}
              {(file || done) && !busy && (
                <Button size="sm" variant="ghost" iconLeft={<RotateCcw size={12} />} onClick={reset}>Start over</Button>
              )}
            </div>
          )}
        </Panel>

        <Panel title="The columns" subtitle="Header names are matched loosely (e.g. “Mobile” for phone). Extra columns are ignored.">
          <ul className="grid gap-x-6 gap-y-1.5 sm:grid-cols-2 text-caption text-fg-secondary">
            <li><code className="text-fg-primary">full_name</code> <Badge tone="warning">required</Badge></li>
            <li><code className="text-fg-primary">phone</code> <Badge tone="warning">required</Badge></li>
            <li><code className="text-fg-primary">joining_date</code> <Badge tone="warning">required</Badge> YYYY-MM-DD or DD/MM/YYYY</li>
            <li><code className="text-fg-primary">department</code> <Badge tone="warning">required</Badge> name or code</li>
            <li><code className="text-fg-primary">designation</code> <Badge tone="warning">required</Badge></li>
            <li><code className="text-fg-primary">employment_type</code> Permanent, Probation, Contract, Temporary, Apprentice</li>
            <li><code className="text-fg-primary">employee_number</code> unique in the factory</li>
            <li><code className="text-fg-primary">gender</code>, <code className="text-fg-primary">date_of_birth</code>, <code className="text-fg-primary">email</code></li>
            <li><code className="text-fg-primary">floor</code>, <code className="text-fg-primary">line</code> name, code or id</li>
            <li><code className="text-fg-primary">grade</code>, <code className="text-fg-primary">present_address</code>, emergency contact</li>
          </ul>
          {factoryId && departments.data && (
            <p className="mt-3 text-caption text-fg-tertiary">
              Departments in this factory: {departments.data.filter((d) => d.isActive).map((d) => d.name).join(", ") || "none yet. Create them under Factories & lines first."}
            </p>
          )}
          <p className="mt-2 text-caption text-fg-tertiary">Worker IDs are generated on import; a Worker ID column in the file is ignored.</p>
        </Panel>
      </div>

      {result && (
        <div className="mt-4 space-y-4" data-testid={done ? "import-done" : "import-preview"}>
          <div className="grid gap-3 grid-cols-2 lg:grid-cols-4">
            <StatCard label="Rows in file" value={result.totalRows} />
            <StatCard label="Valid" value={result.validRows} tone="good" testId="import-valid-count" />
            <StatCard label="With errors" value={result.invalidRows} tone={result.invalidRows > 0 ? "bad" : "neutral"} testId="import-invalid-count" />
            <StatCard label={done ? "Imported" : "Will be imported"} value={done ? result.imported : result.validRows} tone="good" />
          </div>

          {done ? (
            <Callout tone="success" title={`${done.imported} worker${done.imported === 1 ? "" : "s"} registered`}
              action={<Button size="sm" variant="secondary" onClick={() => router.push(`/app/workers?factory=${done.factoryId}&sort=-workerId`)}>Open directory</Button>}>
              {done.invalidRows > 0 ? `${done.invalidRows} row(s) were skipped. Fix them in the file and import it again.` : "Every row was imported."}
            </Callout>
          ) : (
            <Callout tone={result.validRows > 0 ? "info" : "warning"} title={result.validRows > 0 ? "Preview: nothing has been saved yet" : "No row can be imported"}
              action={result.validRows > 0 && (
                <Button variant="primary" size="sm" onClick={() => setConfirming(true)} disabled={busy} data-testid="import-confirm">
                  Import {result.validRows} worker{result.validRows === 1 ? "" : "s"}
                </Button>
              )}>
              {result.invalidRows > 0 ? `${result.invalidRows} row(s) have errors and will be skipped.` : "Every row is valid."}
              {result.ignoredColumns.length > 0 && <> Ignored columns: {result.ignoredColumns.join(", ")}.</>}
            </Callout>
          )}

          <Panel flush title="Rows" right={
            <FilterSelect label="Show" value={show} onChange={(v) => setShow(v as typeof show)}>
              <option value="all">All rows</option>
              <option value="invalid">Rows with errors</option>
              <option value="valid">Valid rows</option>
            </FilterSelect>
          }>
            <div className="overflow-x-auto">
              <table className="data-table" data-testid="import-rows">
                <thead>
                  <tr><th>Line</th><th>Result</th><th>Name</th><th>Phone</th><th>Joined</th><th>Department</th><th>Designation</th><th>Line / floor</th><th>Problems</th></tr>
                </thead>
                <tbody>
                  {rows.map((r) => (
                    <tr key={r.rowNumber} data-testid="import-row" data-valid={r.valid}>
                      <td className="tabular-nums text-caption text-fg-tertiary">{r.rowNumber}</td>
                      <td className="whitespace-nowrap">
                        {r.workerCode ? <Badge tone="success">{r.workerCode}</Badge>
                          : r.valid ? <span className="inline-flex items-center gap-1 text-caption text-[var(--risk-low)]"><CheckCircle2 size={13} aria-hidden /> Valid</span>
                          : <span className="inline-flex items-center gap-1 text-caption text-[var(--risk-high)]"><XCircle size={13} aria-hidden /> {done ? "Skipped" : "Error"}</span>}
                      </td>
                      <td className="text-fg-primary">{r.fullName ?? "—"}{r.employeeCode && <span className="block font-mono text-caption text-fg-tertiary">{r.employeeCode}</span>}</td>
                      <td className="text-caption whitespace-nowrap">{r.phone ?? "—"}</td>
                      <td className="text-caption whitespace-nowrap">{formatDay(r.joinedOn, locale)}</td>
                      <td className="text-caption">{r.department ?? "—"}</td>
                      <td className="text-caption">{r.designation ?? "—"}</td>
                      <td className="text-caption">{[r.line, r.floor].filter(Boolean).join(" · ") || "—"}</td>
                      <td className="text-caption min-w-[220px]">
                        {r.errors.map((e, i) => <span key={`e${i}`} className="block text-[var(--risk-high)]">{e.message}</span>)}
                        {r.warnings.map((e, i) => (
                          <span key={`w${i}`} className="flex items-start gap-1 text-[var(--risk-medium)]"><AlertTriangle size={12} className="mt-0.5 shrink-0" aria-hidden />{e.message}</span>
                        ))}
                      </td>
                    </tr>
                  ))}
                  {rows.length === 0 && <tr><td colSpan={9} className="text-center text-caption text-fg-tertiary py-6">No rows to show.</td></tr>}
                </tbody>
              </table>
            </div>
          </Panel>
        </div>
      )}

      <ConfirmDialog
        open={confirming}
        title={`Import ${preview?.validRows ?? 0} workers?`}
        description="Each valid row is registered with a new permanent Worker ID. Rows with errors are skipped. If anything conflicts while saving, nothing is imported."
        confirmLabel={busy ? "Importing…" : "Import"}
        busy={busy}
        onConfirm={() => void commit()}
        onCancel={() => setConfirming(false)}
        confirmTestId="import-confirm-final"
      />
    </div>
  );
}
