"use client";

// Store & inventory: items, balances and the stock ledger. Stock only moves
// through approved GRN and material issue documents, so every balance traces
// back to an approved document.

import Link from "next/link";
import { useState } from "react";
import { Plus } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { FilterSelect, Field, Select } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Modal } from "@/components/ui/Modal";
import { SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { DataTable } from "@/components/analytics/shared";
import { SearchBox, useQueryState } from "@/components/attendance/shared";
import { useMyFactories } from "@/components/workers/shared";
import { DOCS, Loaded, fmtDate } from "@/components/documents/shared";
import { useResource } from "@/lib/useResource";
import { useCan } from "@/lib/auth";
import { P } from "@/lib/access";
import { errorMessage } from "@/lib/api";
import { ITEM_CATEGORIES, ROLL_STATUS_TEXT, inventoryApi, type InventoryItem, type InventoryRoll, type Paged, type StockMovement } from "@/lib/documents";

export default function InventoryPage() {
  return (
    <RequirePermission perms={[P.InventoryView]} what="Store & inventory">
      <Inventory />
    </RequirePermission>
  );
}

function Inventory() {
  const q = useQueryState("/app/inventory");
  const { factories } = useMyFactories();
  const factoryId = q.get("factory") || (factories && factories.length > 0 ? factories[0].id : "");
  const search = q.get("q"), category = q.get("category"), itemId = q.get("item");
  const items = useResource<InventoryItem[]>(factoryId ? inventoryApi.items({ factoryId, q: search, category }) : null);
  const moves = useResource<Paged<StockMovement>>(factoryId ? inventoryApi.movements({ factoryId, itemId, page: q.page, pageSize: 50 }) : null);
  const rollStatus = q.get("rolls") || "AVAILABLE";
  const rolls = useResource<Paged<InventoryRoll>>(factoryId ? inventoryApi.rolls({ factoryId, itemId, status: rollStatus === "ALL" ? undefined : rollStatus, pageSize: 200 }) : null);
  const canManage = useCan(P.InventoryManage);
  const canPrepare = useCan(P.DocumentsCreate);
  const [editing, setEditing] = useState<InventoryItem | "new" | null>(null);
  const f = factoryId ? `?factory=${factoryId}` : "";
  return (
    <div className="page" data-testid="inventory-page">
      <PageHeader eyebrow="Store" title="Store & inventory" description="Item balances and every receipt and issue. Stock changes only when a GRN or material issue note is approved."
        actions={<span className="flex gap-2">
          {canPrepare && <Link href={`${DOCS}/new${f}&type=store.grn`}><Button size="sm" variant="secondary">New GRN</Button></Link>}
          {canPrepare && <Link href={`${DOCS}/new${f}&type=store.issue`}><Button size="sm" variant="secondary">New issue note</Button></Link>}
          {canManage && <Button size="sm" iconLeft={<Plus size={14} />} onClick={() => setEditing("new")} data-testid="item-new">New item</Button>}
        </span>} />
      {factories && factories.length > 1 && (
        <div className="mb-4"><FilterSelect label="Factory" value={factoryId} onChange={(id) => q.set({ factory: id || null })}>{factories.map((x) => <option key={x.id} value={x.id}>{x.code} · {x.name}</option>)}</FilterSelect></div>
      )}
      {!factoryId ? <SkeletonRows rows={4} /> : (
        <div className="space-y-4">
          <div className="surface p-3 flex flex-wrap items-end gap-3" role="search">
            <SearchBox value={search} onChange={(v) => q.set({ q: v || null })} placeholder="Code or name" label="Search items" />
            <FilterSelect label="Category" value={category} onChange={(v) => q.set({ category: v || null })}>
              <option value="">All</option>
              {ITEM_CATEGORIES.map((c) => <option key={c} value={c}>{c}</option>)}
            </FilterSelect>
          </div>
          <Panel title="Items and balances" flush ariaLabel="Items">
            <Loaded res={items}>
              {(list) => list.length === 0
                ? <p className="px-4 py-4 text-caption text-fg-secondary">No items yet. Items are created when the first GRN that names them is approved{canManage ? ", or with New item" : ""}.</p>
                : <DataTable testId="inventory-items" columns={["Item", "Category", "Unit", "Received", "Issued", "Balance", "Rolls", "Reorder level", ""]}
                    rows={list.map((i) => [
                      <button key="i" type="button" className="text-left text-accent hover:underline" onClick={() => q.set({ item: i.id })}><span className="font-mono text-[12px]">{i.code}</span><span className="block text-fg-secondary">{i.name}</span></button>,
                      i.category, i.unit, i.received.toLocaleString(), i.issued.toLocaleString(),
                      <span key="b" className="font-semibold">{i.balance.toLocaleString()} {i.belowReorder && <Badge tone="warning">Reorder</Badge>}</span>,
                      i.rollsInStock ? `${i.rollsInStock} (${(i.onRolls ?? 0).toLocaleString()} ${i.unit})` : "—",
                      i.reorderLevel ?? "—",
                      canManage ? <Button key="e" size="sm" variant="ghost" onClick={() => setEditing(i)}>Edit</Button> : ""
                    ])} />}
            </Loaded>
          </Panel>
          <Panel title={itemId ? "Fabric rolls (one item)" : "Fabric rolls"} subtitle="Rolls received roll by roll on approved GRNs; what is left on each comes from its issues."
            right={<FilterSelect label="Show" value={rollStatus} onChange={(v) => q.set({ rolls: v || null }, false)}>
              <option value="AVAILABLE">With stock left</option><option value="IN_STOCK">Untouched</option><option value="PARTLY_ISSUED">Partly issued</option>
              <option value="ISSUED">Fully issued</option><option value="ALL">All rolls</option>
            </FilterSelect>} flush ariaLabel="Fabric rolls">
            <Loaded res={rolls}>
              {(r) => (
                <DataTable testId="inventory-rolls" empty="No rolls. Fabric listed roll by roll on a GRN appears here when the GRN is approved."
                  columns={["Roll", "Item", "Lot / colour", "Supplier", "Warehouse", "Received", "Left", "Status", "GRN"]}
                  rows={r.items.map((x) => [
                    <span key="n" className="font-mono text-[12px]">{x.rollNumber}</span>,
                    `${x.itemCode} · ${x.itemName}`,
                    [x.lotNumber, x.color, x.shadeGroup ? `shade ${x.shadeGroup}` : null].filter(Boolean).join(" · ") || "—",
                    x.supplier ?? "—",
                    [x.warehouse, x.location].filter(Boolean).join(" / ") || "—",
                    `${x.received.toLocaleString()} ${x.unit}`,
                    <span key="l" className="font-semibold">{x.remaining.toLocaleString()} {x.unit}</span>,
                    <Badge key="s" tone={x.status === "IN_STOCK" ? "success" : x.status === "PARTLY_ISSUED" ? "info" : "neutral"}>{ROLL_STATUS_TEXT[x.status]}</Badge>,
                    <Link key="g" href={`${DOCS}/${x.grnDocumentId}${f}`} className="font-mono text-[12px] text-accent hover:underline">{x.grnReference}</Link>
                  ])} />
              )}
            </Loaded>
          </Panel>
          <Panel title={itemId ? "Stock ledger (one item)" : "Stock ledger"} right={itemId ? <Button size="sm" variant="ghost" onClick={() => q.set({ item: null })}>Show all items</Button> : undefined} flush ariaLabel="Stock ledger">
            <Loaded res={moves}>
              {(m) => (
                <>
                  <DataTable testId="inventory-movements" empty="No receipts or issues yet." columns={["Date", "Item", "Kind", "Quantity", "Unit cost", "Document", "From / to", "Recorded"]}
                    rows={m.items.map((x) => [x.movementDate, `${x.itemCode} · ${x.itemName}`,
                      <Badge key="k" tone={x.kind === "RECEIPT" ? "success" : "info"}>{x.kind === "RECEIPT" ? "Received" : "Issued"}</Badge>,
                      x.quantity.toLocaleString(), x.unitCost ?? "—",
                      <Link key="d" href={`${DOCS}/${x.documentId}${f}`} className="font-mono text-[12px] text-accent hover:underline">{x.documentReference}</Link>,
                      x.counterparty ?? "—", `${x.recordedByEmail} · ${fmtDate(x.recordedAt)}`])} />
                  {m.total > m.pageSize && (
                    <div className="flex items-center justify-between px-4 py-2 text-caption">
                      <span>Page {m.page} of {Math.ceil(m.total / m.pageSize)}</span>
                      <span className="flex gap-2">
                        <Button size="sm" variant="ghost" disabled={m.page <= 1} onClick={() => q.set({ page: String(m.page - 1) }, false)}>Previous</Button>
                        <Button size="sm" variant="ghost" disabled={m.page >= Math.ceil(m.total / m.pageSize)} onClick={() => q.set({ page: String(m.page + 1) }, false)}>Next</Button>
                      </span>
                    </div>
                  )}
                </>
              )}
            </Loaded>
          </Panel>
        </div>
      )}
      {editing && <ItemModal item={editing === "new" ? null : editing} factoryId={factoryId} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); void items.reload(); }} />}
    </div>
  );
}

function ItemModal({ item, factoryId, onClose, onSaved }: { item: InventoryItem | null; factoryId: string; onClose: () => void; onSaved: () => void }) {
  const toast = useToast();
  const [v, setV] = useState({ code: item?.code ?? "", name: item?.name ?? "", category: item?.category ?? "FABRIC", unit: item?.unit ?? "", reorderLevel: item?.reorderLevel?.toString() ?? "", isActive: item?.isActive ?? true });
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  async function save() {
    setBusy(true); setError(null);
    try {
      const body = { code: v.code, name: v.name, category: v.category, unit: v.unit, reorderLevel: v.reorderLevel ? Number(v.reorderLevel) : null, isActive: v.isActive };
      if (item) await inventoryApi.update(item.id, body); else await inventoryApi.create({ ...body, factoryId });
      toast.push({ title: item ? "Item updated" : "Item created" });
      onSaved();
    } catch (e) { setError(errorMessage(e)); } finally { setBusy(false); }
  }
  return (
    <Modal open onClose={onClose} title={item ? `Edit ${item.code}` : "New store item"}
      footer={<><Button variant="ghost" onClick={onClose}>Cancel</Button><Button onClick={() => void save()} disabled={busy || !v.code || !v.name || !v.unit} data-testid="item-save">Save</Button></>}>
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label="Code">{(ids) => <Input id={ids.id} value={v.code} onChange={(e) => setV({ ...v, code: e.target.value })} />}</Field>
        <Field label="Name">{(ids) => <Input id={ids.id} value={v.name} onChange={(e) => setV({ ...v, name: e.target.value })} />}</Field>
        <Field label="Category">{(ids) => <Select id={ids.id} value={v.category} onChange={(e) => setV({ ...v, category: e.target.value })}>{ITEM_CATEGORIES.map((c) => <option key={c} value={c}>{c}</option>)}</Select>}</Field>
        <Field label="Unit" hint={item && item.received + item.issued > 0 ? "Fixed once stock has moved." : "e.g. kg, m, pcs, cone"}>{(ids) => <Input id={ids.id} value={v.unit} onChange={(e) => setV({ ...v, unit: e.target.value })} />}</Field>
        <Field label="Reorder level">{(ids) => <Input id={ids.id} inputMode="decimal" value={v.reorderLevel} onChange={(e) => setV({ ...v, reorderLevel: e.target.value })} />}</Field>
        {item && <label className="inline-flex items-center gap-2 text-caption mt-6"><input type="checkbox" checked={v.isActive} onChange={(e) => setV({ ...v, isActive: e.target.checked })} /> Active</label>}
      </div>
      {error && <Callout tone="danger" className="mt-3">{error}</Callout>}
    </Modal>
  );
}
