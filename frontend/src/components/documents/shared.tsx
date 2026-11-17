"use client";

// Factory documents building blocks: the section tabs and factory picker, the
// catalog (document types the caller works with), status / risk badges, and the
// one generic form that renders every document type from its field schema.
// Values marked "computed" come from the server (records, totals, AQL); they are
// shown read-only and never typed here.

import Link from "next/link";
import { usePathname, useSearchParams } from "next/navigation";
import { useState, type ReactNode } from "react";
import { Plus, Search, Trash2 } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { Badge, type BadgeTone } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { FilterSelect, Select, TextArea } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { useQueryState } from "@/components/attendance/shared";
import { useMyFactories } from "@/components/workers/shared";
import { useResource, type Resource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { P } from "@/lib/access";
import { cn } from "@/lib/cn";
import { DataTable } from "@/components/analytics/shared";
import { docsApi, sourceText, STATUS_TEXT, type DocCatalog, type DocChecks, type DocField, type DocListItem, type DocStatus, type Risk } from "@/lib/documents";

export const DOCS = "/app/documents";

const SECTIONS: { href: string; label: string; exact?: boolean; perm?: "monitor" | "control" }[] = [
  { href: DOCS, label: "My work", exact: true },
  { href: `${DOCS}/list`, label: "All documents" },
  { href: `${DOCS}/new`, label: "New document" },
  { href: `${DOCS}/monitor`, label: "Status board", perm: "monitor" },
  { href: `${DOCS}/settings`, label: "Settings & AI usage", perm: "control" }
];

export type DocScope = { factoryId: string; catalog: DocCatalog; q: ReturnType<typeof useQueryState>; reloadCatalog: () => void };

function Tabs({ catalog }: { catalog: DocCatalog | null }) {
  const path = usePathname() ?? "";
  const params = useSearchParams();
  const factory = params?.get("factory");
  const search = factory ? `?factory=${factory}` : "";
  return (
    <nav aria-label="Document sections" className="mb-4 flex gap-1 overflow-x-auto border-b border-border-subtle" data-testid="documents-tabs">
      {SECTIONS.filter((s) => !s.perm || (s.perm === "monitor" ? catalog?.can.monitor : catalog?.can.control)).map((s) => {
        const active = s.exact ? path === s.href : path.startsWith(s.href);
        return (
          <Link key={s.href} href={`${s.href}${search}`}
            className={cn("px-3 h-9 inline-flex items-center text-caption border-b-2 -mb-px whitespace-nowrap",
              active ? "border-[var(--accent)] text-fg-primary font-medium" : "border-transparent text-fg-secondary hover:text-fg-primary")}>{s.label}</Link>
        );
      })}
    </nav>
  );
}

/** Page frame: permission gate, header, tabs, factory picker and the catalog. */
export function DocsPage({ title, description, actions, testId, children }: {
  title: string; description: string; actions?: ReactNode; testId: string; children: (scope: DocScope) => ReactNode;
}) {
  return (
    <RequirePermission perms={[P.DocumentsView, P.DocumentsMonitor]} what="Documents">
      <Frame title={title} description={description} actions={actions} testId={testId}>{children}</Frame>
    </RequirePermission>
  );
}

function Frame({ title, description, actions, testId, children }: {
  title: string; description: string; actions?: ReactNode; testId: string; children: (scope: DocScope) => ReactNode;
}) {
  const q = useQueryState(usePathname() ?? DOCS);
  const { factories, error } = useMyFactories();
  const factoryId = q.get("factory") || (factories && factories.length > 0 ? factories[0].id : "");
  const catalog = useResource<DocCatalog>(factoryId ? docsApi.catalog(factoryId) : null);
  return (
    <div className="page" data-testid={testId}>
      <PageHeader eyebrow="Factory documents" title={title} description={description} actions={actions} />
      <Tabs catalog={catalog.data} />
      {factories && factories.length > 1 && (
        <div className="mb-4 flex flex-wrap items-end gap-2">
          <FilterSelect label="Factory" value={factoryId} onChange={(id) => q.set({ factory: id || null })} testId="documents-factory">
            {factories.map((f) => <option key={f.id} value={f.id}>{f.code} · {f.name}</option>)}
          </FilterSelect>
        </div>
      )}
      {error ? <Callout tone="danger" title="Could not load your factories">{error}</Callout>
        : factories && factories.length === 0 ? <Callout tone="warning" title="No factory">Your account has no factory assigned. Ask an administrator.</Callout>
        : !factoryId ? <SkeletonRows rows={4} />
        : catalog.error && !catalog.data ? <ErrorState title="Could not load document types" body={errorMessage(catalog.error)} retryLabel="Try again" onRetry={() => void catalog.reload()} />
        : !catalog.data ? <SkeletonRows rows={6} />
        : children({ factoryId, catalog: catalog.data, q, reloadCatalog: () => void catalog.reload() })}
    </div>
  );
}

const STATUS_TONE: Record<DocStatus, BadgeTone> = { DRAFT: "neutral", SUBMITTED: "info", UNDER_REVIEW: "warning", APPROVED: "success", REJECTED: "danger", ARCHIVED: "neutral" };
export function DocStatusBadge({ status }: { status: DocStatus }) {
  return <Badge tone={STATUS_TONE[status]} testId="doc-status">{STATUS_TEXT[status]}</Badge>;
}
export function DocRiskBadge({ risk }: { risk: Risk }) {
  if (risk === "LOW") return null;
  return <Badge tone={risk === "HIGH" ? "danger" : "neutral"}>{risk === "HIGH" ? "High risk" : "Normal risk"}</Badge>;
}

export function CheckCounts({ missing, errors, warnings }: { missing: number; errors: number; warnings: number }) {
  if (missing + errors + warnings === 0) return <span className="text-fg-tertiary text-[12px]">OK</span>;
  return (
    <span className="inline-flex flex-wrap gap-1">
      {missing > 0 && <Badge tone="warning">{missing} missing</Badge>}
      {errors > 0 && <Badge tone="danger">{errors} error{errors > 1 ? "s" : ""}</Badge>}
      {warnings > 0 && <Badge tone="neutral">{warnings} note{warnings > 1 ? "s" : ""}</Badge>}
    </span>
  );
}

export function fmtDate(iso?: string | null) {
  if (!iso) return "—";
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString(undefined, { year: "numeric", month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
}

export function period(from?: string | null, to?: string | null) {
  if (!from) return "—";
  return !to || to === from ? from : `${from} → ${to}`;
}

/** Results of a resource, or its loading / error state. */
export function Loaded<T>({ res, children, rows = 5 }: { res: Resource<T>; children: (data: T) => ReactNode; rows?: number }) {
  if (res.error && !res.data) return <ErrorState title="Could not load" body={errorMessage(res.error)} retryLabel="Try again" onRetry={() => void res.reload()} />;
  if (!res.data) return <SkeletonRows rows={rows} />;
  return <>{children(res.data)}</>;
}

// ── The generic document form ──────────────────────────────────────────────

type Values = Record<string, unknown>;

function issuesFor(checks: DocChecks | null, path: string) {
  if (!checks) return { missing: [], errors: [] };
  return {
    missing: checks.missing.filter((m) => m.field === path).map((m) => m.message),
    errors: checks.errors.filter((m) => m.field === path).map((m) => m.message)
  };
}

function display(v: unknown): string {
  if (v === null || v === undefined || v === "") return "";
  if (typeof v === "boolean") return v ? "Yes" : "No";
  if (typeof v === "number") return Number.isInteger(v) ? v.toLocaleString() : v.toLocaleString(undefined, { maximumFractionDigits: 4 });
  return String(v);
}

function Control({ field, value, onChange, disabled, invalid, testId }: { field: DocField; value: unknown; onChange: (v: unknown) => void; disabled: boolean; invalid: boolean; testId?: string }) {
  const str = value === null || value === undefined ? "" : String(value);
  const common = { disabled, "aria-invalid": invalid || undefined, "data-testid": testId } as const;
  switch (field.kind) {
    case "textarea":
      return <TextArea rows={3} value={str} onChange={(e) => onChange(e.target.value)} {...common} />;
    case "number":
    case "integer":
      return <Input inputMode="decimal" value={str} onChange={(e) => onChange(e.target.value)} {...common} />;
    case "date":
      return <Input type="date" value={str.slice(0, 10)} onChange={(e) => onChange(e.target.value)} {...common} />;
    case "select":
      return (
        <Select value={str} onChange={(e) => onChange(e.target.value)} {...common}>
          <option value="">—</option>
          {(field.options ?? []).map((o) => <option key={o} value={o}>{o.replace(/_/g, " ")}</option>)}
        </Select>
      );
    case "boolean":
      return <input type="checkbox" className="size-4" checked={value === true || value === "true"} onChange={(e) => onChange(e.target.checked)} disabled={disabled} data-testid={testId} />;
    default:
      return <Input value={str} onChange={(e) => onChange(e.target.value)} {...common} />;
  }
}

function FieldMessages({ missing, errors }: { missing: string[]; errors: string[] }) {
  if (missing.length + errors.length === 0) return null;
  return (
    <>
      {missing.map((m, i) => <p key={`m${i}`} className="field-error">{m}</p>)}
      {errors.map((m, i) => <p key={`e${i}`} className="field-error">{m}</p>)}
    </>
  );
}

/**
 * Renders a document's fields and tables. Editable fields change `values`; computed
 * fields show the server's value. A field with a lookup (item, worker, machine, buyer
 * order, document) offers "Fill from records", which copies the record's values in.
 */
export function DocForm({ fields, values, onChange, editable, checks, sources, factoryId, aiKeys }: {
  fields: DocField[]; values: Values; onChange: (next: Values) => void; editable: boolean; checks: DocChecks | null; sources?: Record<string, string>;
  factoryId: string; aiKeys?: Set<string>;
}) {
  const set = (k: string, v: unknown) => onChange({ ...values, [k]: v === "" ? null : v });
  const plain = fields.filter((f) => f.kind !== "table");
  const tables = fields.filter((f) => f.kind === "table");
  return (
    <div className="space-y-5" data-testid="doc-form">
      <div className="grid gap-x-4 gap-y-3 sm:grid-cols-2 lg:grid-cols-3">
        {plain.map((f) => {
          const { missing, errors } = issuesFor(checks, f.key);
          const src = sourceText(sources?.[f.key]);
          const wide = f.kind === "textarea";
          return (
            <div key={f.key} className={cn(wide && "sm:col-span-2 lg:col-span-3")}>
              <label className="field-label">
                {f.label}{f.unit ? ` (${f.unit})` : ""}{f.required && !f.computed ? <span className="text-[var(--risk-high)]"> *</span> : null}
                {aiKeys?.has(f.key) && <Badge tone="accent" className="ml-2">AI suggestion</Badge>}
              </label>
              {f.computed || !editable ? (
                <div className={cn("min-h-9 px-2.5 py-2 rounded-[var(--radius-control)] border border-border-subtle bg-surface-2 text-body whitespace-pre-wrap", f.computed && "font-mono text-[13px]")}
                  data-testid={`field-${f.key}`}>
                  {display(values[f.key]) || <span className="text-fg-tertiary">{f.required ? "Missing" : "—"}</span>}
                </div>
              ) : (
                <div className="flex gap-2">
                  <div className="flex-1 min-w-0">
                    <Control field={f} value={values[f.key]} onChange={(v) => set(f.key, v)} disabled={!editable} invalid={missing.length + errors.length > 0} testId={`field-${f.key}`} />
                  </div>
                  {f.lookup && f.lookup !== "document" && <LookupButton factoryId={factoryId} lookup={f.lookup} value={String(values[f.key] ?? "")} onFound={(found) => onChange(mergeLookup(fields, values, found))} />}
                </div>
              )}
              {src && <p className="text-[11px] text-fg-tertiary mt-0.5">{src}</p>}
              <FieldMessages missing={missing} errors={errors} />
            </div>
          );
        })}
      </div>
      {tables.map((t) => (
        <TableField key={t.key} field={t} rows={(values[t.key] as Values[] | undefined) ?? []} editable={editable && !t.computed} checks={checks}
          onChange={(rows) => onChange({ ...values, [t.key]: rows })} factoryId={factoryId} aiSuggested={aiKeys?.has(t.key)} source={sourceText(sources?.[t.key])} />
      ))}
    </div>
  );
}

/** Copy looked-up values into fields of the same key (top-level, editable or computed display fields). */
function mergeLookup(fields: DocField[], values: Values, found: Record<string, unknown>): Values {
  const next = { ...values };
  for (const f of fields) if (f.kind !== "table" && found[f.key] !== undefined && found[f.key] !== null) next[f.key] = found[f.key];
  const table = fields.find((f) => f.kind === "table" && Array.isArray(found[f.key]));
  if (table && (!Array.isArray(values[table.key]) || (values[table.key] as unknown[]).length === 0)) next[table.key] = found[table.key];
  return next;
}

function LookupButton({ factoryId, lookup, value, onFound }: { factoryId: string; lookup: string; value: string; onFound: (v: Record<string, unknown>) => void }) {
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);
  async function run() {
    if (!value.trim()) { setMsg("Type the code first."); return; }
    setBusy(true); setMsg(null);
    try {
      const r = await docsApi.lookup(factoryId, lookup, value.trim());
      if (r.found && r.values) onFound(r.values);
      else setMsg(r.message ?? "Not found.");
    } catch (e) { setMsg(errorMessage(e)); } finally { setBusy(false); }
  }
  return (
    <span className="shrink-0">
      <Button type="button" size="sm" variant="secondary" onClick={() => void run()} disabled={busy} iconLeft={<Search size={13} />} title="Fill from records">Fill</Button>
      {msg && <span className="block text-[11px] text-[var(--risk-high)] max-w-[140px]">{msg}</span>}
    </span>
  );
}

function TableField({ field, rows, editable, checks, onChange, factoryId, aiSuggested, source }: {
  field: DocField; rows: Values[]; editable: boolean; checks: DocChecks | null; onChange: (rows: Values[]) => void; factoryId: string; aiSuggested?: boolean; source?: string | null;
}) {
  const cols = field.columns ?? [];
  const tableIssues = issuesFor(checks, field.key);
  const setCell = (i: number, k: string, v: unknown) => onChange(rows.map((r, j) => (j === i ? { ...r, [k]: v === "" ? null : v } : r)));
  return (
    <section className="surface overflow-hidden" aria-label={field.label}>
      <header className="flex items-center justify-between gap-2 px-3 py-2 border-b border-border-subtle">
        <span className="text-caption font-semibold">{field.label} <span className="text-fg-tertiary font-normal">({rows.length})</span>
          {aiSuggested && <Badge tone="accent" className="ml-2">AI suggestion</Badge>}
          {source && <span className="ml-2 text-[11px] text-fg-tertiary font-normal">{source}</span>}</span>
        {editable && <Button size="sm" variant="ghost" iconLeft={<Plus size={13} />} onClick={() => onChange([...rows, {}])} data-testid={`add-row-${field.key}`}>Add row</Button>}
      </header>
      <FieldMessages missing={tableIssues.missing} errors={tableIssues.errors} />
      {rows.length === 0 ? <p className="px-3 py-3 text-caption text-fg-tertiary">No rows.</p> : (
        <div className="overflow-x-auto">
          <table className="data-table">
            <thead><tr><th>#</th>{cols.map((c) => <th key={c.key}>{c.label}{c.required && !c.computed && editable ? " *" : ""}</th>)}{editable && <th />}</tr></thead>
            <tbody>
              {rows.map((r, i) => (
                <tr key={i}>
                  <td className="text-fg-tertiary">{i + 1}</td>
                  {cols.map((c) => {
                    const path = `${field.key}[${i}].${c.key}`;
                    const { missing, errors } = issuesFor(checks, path);
                    const bad = missing.length + errors.length > 0;
                    return (
                      <td key={c.key} className={cn(c.kind === "number" || c.kind === "integer" ? "num" : "", "min-w-[90px]")} title={[...missing, ...errors].join(" ") || undefined}>
                        {editable && !c.computed ? (
                          <div className="flex gap-1 items-start">
                            <Control field={c} value={r[c.key]} onChange={(v) => setCell(i, c.key, v)} disabled={false} invalid={bad} />
                            {c.lookup === "item" || c.lookup === "worker" ? (
                              <LookupButton factoryId={factoryId} lookup={c.lookup} value={String(r[c.key] ?? "")}
                                onFound={(found) => onChange(rows.map((row, j) => (j === i ? { ...row, ...pick(cols, found) } : row)))} />
                            ) : null}
                          </div>
                        ) : <span className={cn(bad && "text-[var(--risk-high)]")}>{display(r[c.key]) || (bad ? "!" : "")}</span>}
                      </td>
                    );
                  })}
                  {editable && (
                    <td><Button size="sm" variant="ghost" aria-label={`Remove row ${i + 1}`} onClick={() => onChange(rows.filter((_, j) => j !== i))}><Trash2 size={13} /></Button></td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

function pick(cols: DocField[], found: Record<string, unknown>) {
  const out: Values = {};
  for (const c of cols) if (found[c.key] !== undefined && found[c.key] !== null) out[c.key] = found[c.key];
  return out;
}

/** The checks panel: what blocks submission, and notes. */
export function ChecksPanel({ checks }: { checks: DocChecks }) {
  if (checks.missing.length + checks.errors.length + checks.warnings.length === 0)
    return <Callout tone="success" title="All checks pass">Required values are present and the business rules hold.</Callout>;
  return (
    <div className="space-y-2" data-testid="doc-checks">
      {checks.missing.length > 0 && (
        <Callout tone="warning" title={`${checks.missing.length} required value(s) missing — needs human input`}>
          <ul className="list-disc pl-5">{checks.missing.slice(0, 30).map((m, i) => <li key={i}>{m.message}</li>)}</ul>
        </Callout>
      )}
      {checks.errors.length > 0 && (
        <Callout tone="danger" title={`${checks.errors.length} error(s) to fix`}>
          <ul className="list-disc pl-5">{checks.errors.slice(0, 30).map((m, i) => <li key={i}>{m.message}</li>)}</ul>
        </Callout>
      )}
      {checks.warnings.length > 0 && (
        <Callout tone="info" title={`${checks.warnings.length} note(s)`}>
          <ul className="list-disc pl-5">{checks.warnings.slice(0, 30).map((m, i) => <li key={i}>{m.message}</li>)}</ul>
        </Callout>
      )}
    </div>
  );
}

/** The standard document table (reference, title, period, status, checks, updated). */
export function DocTable({ items, factoryId, empty }: { items: DocListItem[]; factoryId: string; empty: string }) {
  return (
    <DataTable testId="documents-table" empty={empty} columns={["Reference", "Document", "Period", "Status", "Checks", "Updated"]}
      rows={items.map((d) => [
        <Link key="r" href={`${DOCS}/${d.id}?factory=${factoryId}`} className="font-mono text-[12px] text-accent hover:underline" data-testid="document-row">{d.reference}</Link>,
        <span key="t">{d.title}<span className="block text-[11px] text-fg-tertiary">{d.typeName}{d.source === "GENERATED" ? " · from records" : ""}</span></span>,
        period(d.periodFrom, d.periodTo),
        <span key="s" className="inline-flex flex-wrap gap-1"><DocStatusBadge status={d.status} /><DocRiskBadge risk={d.risk} /></span>,
        <CheckCounts key="c" missing={d.missingCount} errors={d.errorCount} warnings={d.warningCount} />,
        fmtDate(d.updatedAt)
      ])} />
  );
}
