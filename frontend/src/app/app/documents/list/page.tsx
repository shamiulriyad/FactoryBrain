"use client";

import { Button } from "@/components/ui/Button";
import { FilterSelect } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Panel } from "@/components/ui/Panel";
import { SearchBox } from "@/components/attendance/shared";
import { DocTable, DocsPage, Loaded, type DocScope } from "@/components/documents/shared";
import { useResource } from "@/lib/useResource";
import { docsApi, STATUS_TEXT, type DocListItem, type DocStatus, type Paged } from "@/lib/documents";

export default function Page() {
  return (
    <DocsPage title="All documents" testId="documents-list" description="Search, filter and open every document of your departments. Archived documents are hidden unless you ask for them.">
      {(scope) => <List scope={scope} />}
    </DocsPage>
  );
}

function List({ scope }: { scope: DocScope }) {
  const { factoryId, catalog, q } = scope;
  const category = q.get("category"), type = q.get("type"), status = q.get("status"), view = q.get("view"), from = q.get("from"), to = q.get("to"), text = q.get("q");
  const archived = q.get("archived") === "1";
  const res = useResource<Paged<DocListItem>>(docsApi.list({ factoryId, q: text, category, type, status, from, to, view, includeArchived: archived, page: q.page, pageSize: 25 }));
  const types = catalog.types.filter((t) => !category || t.category === category);
  const pages = res.data ? Math.max(1, Math.ceil(res.data.total / res.data.pageSize)) : 1;
  return (
    <div className="space-y-4">
      <div className="surface p-3 flex flex-wrap items-end gap-3" role="search">
        <SearchBox value={text} onChange={(v) => q.set({ q: v || null })} placeholder="Reference, title or person" label="Search" testId="documents-search" />
        <FilterSelect label="Department" value={category} onChange={(v) => q.set({ category: v || null, type: null })}>
          <option value="">All</option>
          {catalog.categoryList.map((c) => <option key={c.code} value={c.code}>{c.name}</option>)}
        </FilterSelect>
        <FilterSelect label="Type" value={type} onChange={(v) => q.set({ type: v || null })}>
          <option value="">All</option>
          {types.map((t) => <option key={t.code} value={t.code}>{t.name}</option>)}
        </FilterSelect>
        <FilterSelect label="Status" value={status} onChange={(v) => q.set({ status: v || null })} testId="documents-status">
          <option value="">Any (not archived)</option>
          {(Object.keys(STATUS_TEXT) as DocStatus[]).map((s) => <option key={s} value={s}>{STATUS_TEXT[s]}</option>)}
        </FilterSelect>
        <FilterSelect label="Show" value={view} onChange={(v) => q.set({ view: v || null })}>
          <option value="">Everything I can see</option>
          <option value="action">Waiting for me</option>
          <option value="mine">Mine</option>
          <option value="missing">With missing data or errors</option>
        </FilterSelect>
        <label className="text-caption">From<Input type="date" value={from} onChange={(e) => q.set({ from: e.target.value || null })} /></label>
        <label className="text-caption">To<Input type="date" value={to} onChange={(e) => q.set({ to: e.target.value || null })} /></label>
        <label className="text-caption inline-flex items-center gap-1.5 h-9"><input type="checkbox" checked={archived} onChange={(e) => q.set({ archived: e.target.checked ? "1" : null })} /> Include archived</label>
      </div>
      <Panel flush ariaLabel="Documents">
        <Loaded res={res}>
          {(d) => (
            <>
              <DocTable items={d.items} factoryId={factoryId} empty="No documents match." />
              {pages > 1 && (
                <div className="flex items-center justify-between px-4 py-2 text-caption">
                  <span>Page {d.page} of {pages} · {d.total} documents</span>
                  <span className="flex gap-2">
                    <Button size="sm" variant="ghost" disabled={d.page <= 1} onClick={() => q.set({ page: String(d.page - 1) }, false)}>Previous</Button>
                    <Button size="sm" variant="ghost" disabled={d.page >= pages} onClick={() => q.set({ page: String(d.page + 1) }, false)}>Next</Button>
                  </span>
                </div>
              )}
            </>
          )}
        </Loaded>
      </Panel>
    </div>
  );
}
