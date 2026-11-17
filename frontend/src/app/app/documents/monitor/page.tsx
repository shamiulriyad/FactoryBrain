"use client";

import Link from "next/link";
import { Panel } from "@/components/ui/Panel";
import { StatCard } from "@/components/ui/StatCard";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { DataTable } from "@/components/analytics/shared";
import { DOCS, DocsPage, Loaded, fmtDate, type DocScope } from "@/components/documents/shared";
import { useResource } from "@/lib/useResource";
import { docsApi, type DocMonitor } from "@/lib/documents";

const KIND: Record<string, { label: string; tone: "danger" | "warning" | "info" | "neutral" }> = {
  MISSING: { label: "Missing", tone: "danger" },
  OVERDUE_DECISION: { label: "Decision overdue", tone: "warning" },
  REJECTED_NOT_CORRECTED: { label: "Rejected, not corrected", tone: "warning" },
  GENERATED_NOT_SUBMITTED: { label: "Generated, not submitted", tone: "info" }
};

export default function Page() {
  return (
    <DocsPage title="Document status board" testId="documents-monitor"
      description="Missing daily documents, decisions waiting too long, rejected documents not corrected and generated drafts nobody submitted — for every department, without opening the documents' content.">
      {(scope) => <Board scope={scope} />}
    </DocsPage>
  );
}

function Board({ scope }: { scope: DocScope }) {
  const { factoryId, catalog } = scope;
  const res = useResource<DocMonitor>(catalog.can.monitor ? docsApi.monitor(factoryId) : null, { pollMs: 120_000 });
  if (!catalog.can.monitor) return <Callout tone="info">The status board is for document controllers, managers and monitoring operators.</Callout>;
  const cat = (c: string) => catalog.categoryList.find((x) => x.code === c)?.name ?? c;
  return (
    <Loaded res={res}>
      {(m) => (
        <div className="space-y-4">
          <div className="grid gap-3 sm:grid-cols-3 lg:grid-cols-6">
            <StatCard label="Missing (last working day)" value={m.missingToday} tone={m.missingToday ? "bad" : "neutral"} testId="mon-missing" />
            <StatCard label="Waiting for a decision" value={m.pending} />
            <StatCard label="Decision overdue" value={m.overdue} tone={m.overdue ? "warn" : "neutral"} />
            <StatCard label="Rejected" value={m.rejected} />
            <StatCard label="Drafts" value={m.drafts} />
            <StatCard label="Approved in 7 days" value={m.approvedThisWeek} tone="good" />
          </div>
          <Panel title="Needs attention" flush ariaLabel="Needs attention">
            <DataTable testId="monitor-table" empty="Nothing is missing or overdue." columns={["Kind", "Document", "Department", "Period", "Since", "What"]}
              rows={m.items.map((i) => [
                <Badge key="k" tone={KIND[i.kind]?.tone ?? "neutral"}>{KIND[i.kind]?.label ?? i.kind}</Badge>,
                i.documentId && catalog.can.categories.includes(i.category)
                  ? <Link key="d" href={`${DOCS}/${i.documentId}?factory=${factoryId}`} className="text-accent hover:underline">{i.reference} · {i.typeName}</Link>
                  : <span key="d">{i.reference ? `${i.reference} · ` : ""}{i.typeName}</span>,
                cat(i.category), i.period ?? "—", fmtDate(i.since), i.message
              ])} />
          </Panel>
          <Panel title="By department" flush ariaLabel="By department">
            <DataTable columns={["Department", "Waiting for a decision", "Drafts", "Missing"]}
              rows={m.byCategory.filter((c) => c.pending + c.drafts + c.missing > 0).map((c) => [cat(c.category), c.pending, c.drafts, c.missing])} empty="All departments are up to date." />
          </Panel>
        </div>
      )}
    </Loaded>
  );
}
