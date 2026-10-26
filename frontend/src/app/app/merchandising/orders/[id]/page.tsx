"use client";

// /app/merchandising/orders/[id] — one buyer order: its confirmed values and
// where each came from, the prioritised missing-information list, milestones,
// documents and their versions, conflicts between documents, and the linked
// production order. Everything comes from GET /api/merchandising/orders/{id}.

import { useEffect, useState } from "react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { ArrowLeft, CalendarPlus, FilePlus2, Link2, Pencil, RefreshCw, Unlink, Upload } from "lucide-react";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Select } from "@/components/ui/Field";
import { SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { AttentionList, BreakdownTable, DueText, ExtractionNotice, OrderStatusBadge, RequiredStateBadge, VersionStatusBadge, useMerchConfig } from "@/components/merchandising/shared";
import { MilestoneModal, OrderFormModal, ResolveConflictModal, UploadModal } from "@/components/merchandising/forms";
import { announceDataChanged, useOnDataChanged, useResource } from "@/lib/useResource";
import { errorMessage, isApiError } from "@/lib/api";
import { canOperate, isAdmin, useAuthStore } from "@/lib/auth";
import { formatDateTime } from "@/lib/format";
import { SOURCE_LABEL, describeCode, fmtQty, isInProgress } from "@/lib/merchandising";
import { merchPaths, updateMerchOrder } from "@/services/merchandising.api";
import { productionPaths } from "@/services/production.api";
import type { AuditEntry, MerchConflict, MerchMilestone, MerchOrderDetail, Paged, ProductionOrderListItem } from "@/services/contracts";

export default function MerchOrderPage() {
  const params = useParams<{ id: string }>();
  const id = params?.id ?? "";
  const user = useAuthStore((s) => s.user);
  const operator = canOperate(user);
  const admin = isAdmin(user);
  const config = useMerchConfig();
  const [poll, setPoll] = useState(false);
  const res = useResource<MerchOrderDetail>(id ? merchPaths.order(id) : null, { pollMs: poll ? 3000 : undefined });
  useOnDataChanged(() => void res.reload());
  const audit = useResource<AuditEntry[]>(admin && id ? `/api/audit?entityType=merch_order&entityId=${encodeURIComponent(id)}&limit=100` : null);
  const d = res.data;
  useEffect(() => setPoll(!!d?.documents.some((x) => x.currentVersion && isInProgress(x.currentVersion.status))), [d]);

  const [editing, setEditing] = useState(false);
  const [uploading, setUploading] = useState<{ documentId?: string; title?: string } | null>(null);
  const [milestone, setMilestone] = useState<MerchMilestone | null | undefined>(undefined);
  const [resolving, setResolving] = useState<MerchConflict | null>(null);

  if (!id) return null;
  if (res.loading) return <div className="page"><SkeletonRows rows={6} rowHeight="h-16" /></div>;
  if (res.error && !d) {
    const notFound = isApiError(res.error) && res.error.kind === "not_found";
    return (
      <div className="page page-narrow">
        <Callout tone="danger" title={notFound ? "Order not found" : "Could not load the order"} action={!notFound && <Button size="sm" onClick={() => void res.reload()}>Retry</Button>}>
          {notFound ? "It may have been mistyped." : errorMessage(res.error)}
        </Callout>
        <Link href="/app/merchandising?tab=orders" className="mt-4 inline-flex text-caption text-accent hover:underline">← Orders</Link>
      </div>
    );
  }
  if (!d) return null;
  const o = d.order;
  const open = d.conflicts.filter((c) => c.status === "open");
  const closed = d.conflicts.filter((c) => c.status !== "open");
  const reload = () => { void res.reload(); void audit.reload(); };

  return (
    <div className="page" data-testid="merch-order" data-reference={o.reference}>
      <Link href="/app/merchandising?tab=orders" className="inline-flex items-center gap-1 text-caption text-fg-secondary hover:text-fg-primary"><ArrowLeft size={12} /> Orders</Link>
      <PageHeader
        className="mt-2"
        eyebrow="Buyer order"
        title={<span data-testid="merch-order-title">{o.buyer} · PO {o.buyerOrderNumber}</span>}
        description={<span className="font-mono">{o.reference}</span>}
        meta={<><OrderStatusBadge status={o.status} /><span className="text-caption text-fg-tertiary">Registered {formatDateTime(o.createdAt)} by {o.createdByEmail} · plant date {d.today}</span></>}
        actions={operator && (
          <>
            <Button variant="secondary" size="sm" iconLeft={<Pencil size={13} />} onClick={() => setEditing(true)} data-testid="merch-edit-order">Edit</Button>
            <Button variant="primary" size="sm" iconLeft={<Upload size={13} />} onClick={() => setUploading({})} data-testid="merch-upload">Upload document</Button>
          </>
        )}
      />
      {res.error && <Callout tone="warning" className="mt-3">Showing the last loaded state: {errorMessage(res.error)}</Callout>}

      <div className="mt-5 grid grid-cols-1 xl:grid-cols-[minmax(0,1.15fr)_minmax(0,1fr)] gap-4 items-start">
        <div className="space-y-4 min-w-0">
          <Panel title="Summary" subtitle={d.summary.basis} testId="merch-summary">
            <p className="text-body font-medium text-fg-primary" data-testid="merch-summary-headline">{d.summary.headline}</p>
            <ul className="mt-2 space-y-1 text-body text-fg-secondary list-disc pl-5">
              {d.summary.lines.map((l, i) => <li key={i}>{l}</li>)}
            </ul>
          </Panel>

          <Panel title="Order values" subtitle="The authoritative values, and where each one came from." testId="merch-values">
            <dl className="divide-y divide-border-subtle">
              {d.fields.map((f) => (
                <div key={f.key} className="py-2 grid grid-cols-1 sm:grid-cols-[160px_1fr] gap-1" data-testid="merch-value" data-key={f.key}>
                  <dt className="text-caption text-fg-tertiary">{f.label}</dt>
                  <dd className="min-w-0">
                    <span className={f.value ? "text-body text-fg-primary" : "text-body text-fg-tertiary"} data-testid="merch-value-display">{f.value ? f.display : "not confirmed"}</span>
                    {f.source && (
                      <span className="block text-caption text-fg-tertiary">
                        {SOURCE_LABEL[f.source.kind]}
                        {f.source.versionId && <> · <Link className="text-accent hover:underline" href={`/app/merchandising/versions/${f.source.versionId}`}>{f.source.documentTitle} v{f.source.versionNumber}</Link></>}
                        {" · "}{f.source.byEmail} · {formatDateTime(f.source.at)}
                        {f.source.stale && <Badge tone="warning" className="ml-1.5">source replaced</Badge>}
                      </span>
                    )}
                  </dd>
                </div>
              ))}
            </dl>
            {o.breakdown && o.breakdown.length > 0 && (
              <details className="mt-3">
                <summary className="cursor-pointer text-caption text-fg-primary">Colour × size breakdown ({o.breakdown.length} rows, total {fmtQty(o.breakdownTotal)})</summary>
                <BreakdownTable rows={o.breakdown} />
              </details>
            )}
          </Panel>

          <Panel title={`Documents (${d.documents.length})`} testId="merch-documents"
            right={operator && <Button size="sm" variant="secondary" iconLeft={<FilePlus2 size={12} />} onClick={() => setUploading({})}>Add</Button>}>
            <div className="mb-3 flex flex-wrap gap-2" data-testid="merch-required-docs">
              {d.requiredDocuments.map((r) => (
                <span key={r.type} className="inline-flex items-center gap-1.5 text-caption text-fg-secondary" data-testid="merch-required-doc" data-state={r.state}>
                  {r.label}: <RequiredStateBadge state={r.state} />
                </span>
              ))}
              {d.requiredDocuments.length === 0 && <span className="text-caption text-fg-tertiary">No documents are marked as required.</span>}
            </div>
            {config.data && <ExtractionNotice extraction={config.data.extraction} className="mb-3" />}
            {d.documents.length === 0 ? <p className="text-caption text-fg-tertiary" data-testid="merch-no-documents">No documents uploaded yet.</p> : (
              <ul className="space-y-2">
                {d.documents.map((doc) => {
                  const v = doc.currentVersion;
                  return (
                    <li key={doc.id} className="surface-2 p-3" data-testid="merch-document" data-type={doc.type} data-status={v?.status}>
                      <div className="flex flex-wrap items-start justify-between gap-2">
                        <div className="min-w-0">
                          <p className="text-body font-medium text-fg-primary">
                            {v ? <Link className="hover:underline" href={`/app/merchandising/versions/${v.id}`} data-testid="merch-document-link">{doc.title}</Link> : doc.title}
                            <span className="ml-2 text-caption text-fg-tertiary">{doc.typeLabel}</span>
                          </p>
                          {v && (
                            <p className="text-caption text-fg-tertiary">
                              v{v.versionNumber} of {doc.versionCount} · {v.originalFileName ?? v.format} · {formatDateTime(v.createdAt)} by {v.uploadedByEmail}
                              {v.confirmedAt && <> · confirmed by {v.confirmedByEmail}</>}
                            </p>
                          )}
                          {v?.failureCode && <p className="text-caption text-[var(--risk-high)]">{describeCode(v.failureCode)}</p>}
                          {v?.extractionCode && <p className="text-caption text-[var(--risk-medium)]">{describeCode(v.extractionCode)}</p>}
                        </div>
                        <div className="flex items-center gap-2 shrink-0">
                          {v && <VersionStatusBadge status={v.status} />}
                          {operator && (
                            <Button size="sm" variant="ghost" iconLeft={<RefreshCw size={12} />} onClick={() => setUploading({ documentId: doc.id, title: doc.title })} data-testid="merch-new-version">New version</Button>
                          )}
                        </div>
                      </div>
                    </li>
                  );
                })}
              </ul>
            )}
          </Panel>

          <div id="conflicts">
            <Panel title={`Conflicts${open.length ? ` (${open.length} open)` : ""}`} subtitle="Values that differ between the order's current documents (and values typed on the order), found by fixed comparison rules." testId="merch-conflicts">
              {open.length === 0 && closed.length === 0 && <p className="text-caption text-fg-tertiary">No conflicting values found.</p>}
              <ul className="space-y-3">
                {open.map((c) => (
                  <li key={c.id} className="surface-2 p-3" data-testid="merch-conflict" data-field={c.fieldKey}>
                    <div className="flex flex-wrap items-start justify-between gap-2">
                      <p className="text-body font-medium text-fg-primary">{c.label} <Badge tone="danger" className="ml-1">open</Badge></p>
                      {operator && <Button size="sm" variant="primary" onClick={() => setResolving(c)} data-testid="merch-conflict-resolve">Resolve</Button>}
                    </div>
                    <ul className="mt-2 space-y-1.5">
                      {c.values.map((v, i) => (
                        <li key={i} className="text-caption">
                          <span className="font-medium text-fg-primary tabular-nums" data-testid="merch-conflict-value">{v.display}</span>{" "}
                          <span className="text-fg-tertiary">— {v.sourceKind === "order" ? "typed on the order" : <Link className="text-accent hover:underline" href={`/app/merchandising/versions/${v.versionId}`}>{v.documentTitle} v{v.versionNumber}{v.page ? `, page ${v.page}` : ""}</Link>}</span>
                          {v.snippet && <span className="block font-mono text-[11px] text-fg-secondary">“{v.snippet}”</span>}
                        </li>
                      ))}
                    </ul>
                  </li>
                ))}
              </ul>
              {closed.length > 0 && (
                <details className="mt-3">
                  <summary className="cursor-pointer text-caption text-fg-secondary">History ({closed.length})</summary>
                  <ul className="mt-2 space-y-1.5 text-caption text-fg-secondary">
                    {closed.map((c) => (
                      <li key={c.id} data-testid="merch-conflict-history">
                        <span className="text-fg-primary">{c.label}</span>: {c.status === "resolved"
                          ? <>resolved as <span className="font-medium">{c.resolvedValue}</span> by {c.resolvedByEmail} · {c.resolvedAt && formatDateTime(c.resolvedAt)} · “{c.resolutionNote}”</>
                          : "cleared (the sources now agree)"}
                      </li>
                    ))}
                  </ul>
                </details>
              )}
            </Panel>
          </div>
        </div>

        <div className="space-y-4 min-w-0">
          <Panel title="Missing information and deadlines" subtitle="Prioritised by fixed rules from the stored records." testId="merch-attention-panel">
            <AttentionList items={d.attention} />
          </Panel>

          <Panel title={`Milestones (${d.summary.milestonesDone}/${d.summary.milestonesTotal} done)`} testId="merch-milestones"
            right={operator && <Button size="sm" variant="secondary" iconLeft={<CalendarPlus size={12} />} onClick={() => setMilestone(null)} data-testid="merch-add-milestone">Add</Button>}>
            {d.milestones.length === 0 ? <p className="text-caption text-fg-tertiary">No milestones recorded.</p> : (
              <ul className="divide-y divide-border-subtle">
                {d.milestones.map((m) => (
                  <li key={m.id} className="py-2 flex flex-wrap items-center justify-between gap-2" data-testid="merch-milestone" data-status={m.status} data-overdue={m.overdue}>
                    <span className="min-w-0">
                      <span className="text-body text-fg-primary">{m.name}</span>
                      {m.notes && <span className="block text-caption text-fg-tertiary">{m.notes}</span>}
                    </span>
                    <span className="flex items-center gap-2">
                      {m.status === "pending" ? <DueText date={m.dueDate} daysUntil={m.daysUntilDue} overdue={m.overdue} />
                        : <Badge tone={m.status === "done" ? "success" : "neutral"}>{m.status === "done" ? `Done ${m.completedOn ?? ""}` : "Waived"}</Badge>}
                      {m.overdue && <Badge tone="danger">overdue</Badge>}
                      {operator && <Button size="sm" variant="ghost" onClick={() => setMilestone(m)} aria-label={`Update ${m.name}`} data-testid="merch-edit-milestone"><Pencil size={12} /></Button>}
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </Panel>

          <ProductionLink detail={d} operator={operator} onChanged={reload} />

          {admin && (
            <Panel title="Audit trail" testId="merch-audit">
              {audit.loading ? <SkeletonRows rows={2} rowHeight="h-6" /> : audit.error ? <p className="text-caption text-[var(--risk-high)]">{errorMessage(audit.error)}</p> : (
                <ol className="space-y-1.5 text-caption">
                  {(audit.data ?? []).map((e) => (
                    <li key={e.id} className="flex flex-wrap gap-x-2">
                      <span className="text-fg-tertiary">{formatDateTime(e.occurredAt)}</span>
                      <span className="font-mono text-fg-primary">{e.action}</span>
                      <span className="text-fg-secondary">{e.actorEmail}</span>
                      {e.reason && <span className="text-fg-tertiary basis-full break-words">{e.reason}</span>}
                    </li>
                  ))}
                </ol>
              )}
            </Panel>
          )}
        </div>
      </div>

      <OrderFormModal open={editing} onClose={() => setEditing(false)} order={o} config={config.data} onSaved={(x) => res.setData(x)} />
      {uploading && (
        <UploadModal open onClose={() => setUploading(null)} orderId={o.id} documentId={uploading.documentId} documentTitle={uploading.title}
          config={config.data} onUploaded={() => { setPoll(true); reload(); }} />
      )}
      <MilestoneModal open={milestone !== undefined} onClose={() => setMilestone(undefined)} orderId={o.id} milestone={milestone ?? null} onSaved={reload} />
      <ResolveConflictModal conflict={resolving} onClose={() => setResolving(null)} onResolved={reload} />
    </div>
  );
}

/** The verified link to a Sector 2 production order: shown as stored, chosen from existing orders only. */
function ProductionLink({ detail, operator, onChanged }: { detail: MerchOrderDetail; operator: boolean; onChanged: () => void }) {
  const toast = useToast();
  const [choosing, setChoosing] = useState(false);
  const [choice, setChoice] = useState("");
  const [busy, setBusy] = useState(false);
  const orders = useResource<Paged<ProductionOrderListItem>>(choosing ? productionPaths.orders({ pageSize: 100, includeRisk: false, status: "open" }) : null);
  const p = detail.productionOrder;

  async function save(body: { productionOrderId?: string; unlinkProductionOrder?: boolean }) {
    setBusy(true);
    try {
      await updateMerchOrder(detail.order.id, { ...body, expectedVersion: detail.order.version });
      announceDataChanged();
      setChoosing(false);
      onChanged();
    } catch (e) {
      toast.push({ title: "Link not saved", description: errorMessage(e), tone: "error" });
    } finally {
      setBusy(false);
    }
  }

  return (
    <Panel title="Production order" testId="merch-production-link">
      {p ? (
        <div className="space-y-2">
          <p className="text-body text-fg-primary">
            <Link className="text-accent hover:underline" href={`/app/production/orders/${p.id}`} data-testid="merch-production-order">{p.orderNumber}</Link>
            <span className="text-fg-tertiary"> · {p.styleCode} {p.styleName} · {p.quantity.toLocaleString("en-US")} pcs due {p.dueDate} · {p.status}</span>
          </p>
          <p className="text-caption text-fg-secondary">{p.goodSewingOutput.toLocaleString("en-US")} good pieces recorded at sewing output.</p>
          {p.differences.length > 0 && (
            <Callout tone="warning" title="Differs from the buyer order" testId="merch-production-diffs">
              <ul className="list-disc pl-4">{p.differences.map((x, i) => <li key={i}>{x}</li>)}</ul>
            </Callout>
          )}
          {operator && <Button size="sm" variant="ghost" iconLeft={<Unlink size={12} />} disabled={busy} onClick={() => void save({ unlinkProductionOrder: true })}>Unlink</Button>}
        </div>
      ) : (
        <div className="space-y-2">
          <p className="text-caption text-fg-tertiary">{detail.order.productionOrderId ? "The linked production order no longer exists." : "Not linked to a production order."}</p>
          {operator && !choosing && <Button size="sm" variant="secondary" iconLeft={<Link2 size={12} />} onClick={() => setChoosing(true)} data-testid="merch-link-production">Link a production order</Button>}
          {choosing && (
            <div className="flex flex-wrap gap-2 items-center">
              {orders.loading ? <span className="text-caption text-fg-tertiary">Loading production orders…</span> : orders.error ? (
                <span className="text-caption text-[var(--risk-high)]">{errorMessage(orders.error)}</span>
              ) : (orders.data?.items.length ?? 0) === 0 ? (
                <span className="text-caption text-fg-tertiary">No open production orders exist yet (they are created under Production).</span>
              ) : (
                <Select aria-label="Production order" value={choice} onChange={(e) => setChoice(e.target.value)} className="!w-auto" data-testid="merch-link-select">
                  <option value="">Choose…</option>
                  {orders.data!.items.map((x) => <option key={x.id} value={x.id}>{x.orderNumber} · {x.styleCode} · {x.quantity} pcs · due {x.dueDate}</option>)}
                </Select>
              )}
              <Button size="sm" variant="primary" disabled={!choice || busy} onClick={() => void save({ productionOrderId: choice })}>Link</Button>
              <Button size="sm" variant="ghost" onClick={() => setChoosing(false)}>Cancel</Button>
            </div>
          )}
        </div>
      )}
    </Panel>
  );
}
