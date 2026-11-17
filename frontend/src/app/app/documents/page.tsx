"use client";

import Link from "next/link";
import { FilePlus2 } from "lucide-react";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Panel } from "@/components/ui/Panel";
import { StatCard } from "@/components/ui/StatCard";
import { DOCS, DocTable, DocsPage, Loaded, type DocScope } from "@/components/documents/shared";
import { useResource } from "@/lib/useResource";
import { docsApi, type Paged, type DocListItem } from "@/lib/documents";

export default function Page() {
  return (
    <DocsPage title="My document work" testId="documents-home"
      description="Documents waiting for you, drafts generated from records, and quick starts for the document types you prepare. Figures come from the factory's records; you check, complete and approve.">
      {(scope) => <Home scope={scope} />}
    </DocsPage>
  );
}

function Home({ scope }: { scope: DocScope }) {
  const { factoryId, catalog } = scope;
  const action = useResource<Paged<DocListItem>>(catalog.can.categories.length ? docsApi.list({ factoryId, view: "action", pageSize: 50 }) : null);
  const mine = useResource<Paged<DocListItem>>(catalog.can.categories.length ? docsApi.list({ factoryId, view: "mine", pageSize: 10 }) : null);
  const f = `?factory=${factoryId}`;
  const creatable = catalog.types.filter((t) => t.canCreate);

  if (catalog.can.categories.length === 0)
    return (
      <Callout tone="info" title="Status board only">
        Your role watches for missing and overdue documents across departments without opening their content. <Link className="text-accent underline" href={`${DOCS}/monitor${f}`}>Open the status board</Link>.
      </Callout>
    );

  return (
    <div className="space-y-5">
      {!catalog.ai.configured && (
        <Callout tone="info" title="AI assistance is off">
          {catalog.ai.reason} Generation from records, calculations, validation and approvals work without it; uploaded files are read with rules only.
        </Callout>
      )}
      <div className="grid gap-3 sm:grid-cols-3">
        <StatCard label="Waiting for you" value={action.data?.total ?? "…"} hint="Reviews, approvals, your drafts and corrections" testId="stat-action" href={`${DOCS}/list${f}&view=action`} />
        <StatCard label="Generated drafts to check" value={action.data ? action.data.items.filter((d) => d.source === "GENERATED" && d.status === "DRAFT").length : "…"} hint="Made automatically from records" />
        <StatCard label="Need input" value={action.data ? action.data.items.filter((d) => d.missingCount + d.errorCount > 0).length : "…"} hint="Missing values or errors" tone="warn" />
      </div>

      <Panel title="Waiting for you" flush ariaLabel="Waiting for you">
        <Loaded res={action}>
          {(d) => <DocTable items={d.items} factoryId={factoryId} empty="Nothing is waiting for you." />}
        </Loaded>
      </Panel>

      {creatable.length > 0 && (
        <Panel title="Start a document" subtitle="Generated types are built from records; forms can start from the last approved one." right={<Link href={`${DOCS}/new${f}`}><Button size="sm" iconLeft={<FilePlus2 size={14} />}>New document</Button></Link>}>
          <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
            {catalog.categoryList.filter((c) => creatable.some((t) => t.category === c.code)).map((c) => (
              <div key={c.code}>
                <p className="text-caption font-semibold mb-1">{c.name}</p>
                <ul className="space-y-0.5">
                  {creatable.filter((t) => t.category === c.code).map((t) => (
                    <li key={t.code}>
                      <Link href={`${DOCS}/new${f}&type=${t.code}`} className="text-caption text-accent hover:underline" data-testid={`quick-${t.code}`}>{t.name}</Link>
                      {t.generated && <span className="ml-1 text-[11px] text-fg-tertiary">· from records</span>}
                    </li>
                  ))}
                </ul>
              </div>
            ))}
          </div>
        </Panel>
      )}

      <Panel title="Recently mine" flush ariaLabel="My recent documents">
        <Loaded res={mine}>{(d) => <DocTable items={d.items} factoryId={factoryId} empty="You haven't prepared or submitted a document yet." />}</Loaded>
      </Panel>
    </div>
  );
}
