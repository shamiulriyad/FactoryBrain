"use client";

// Building blocks shared by the merchandising pages. Every value shown comes
// from /api/merchandising/**.

import Link from "next/link";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { cn } from "@/lib/cn";
import { useResource } from "@/lib/useResource";
import {
  ORDER_STATUS_LABEL,
  ORDER_STATUS_TONE,
  PRIORITY_TONE,
  REQUIRED_STATE_LABEL,
  REQUIRED_STATE_TONE,
  VERSION_STATUS_LABEL,
  VERSION_STATUS_TONE,
  days
} from "@/lib/merchandising";
import { merchPaths } from "@/services/merchandising.api";
import type {
  MerchAttentionItem,
  MerchConfig,
  MerchExtractionStatus,
  MerchOrderStatus,
  MerchRequiredDocument,
  MerchVersionStatus
} from "@/services/contracts";

export function useMerchConfig() {
  return useResource<MerchConfig>(merchPaths.config);
}

export function VersionStatusBadge({ status, testId }: { status: MerchVersionStatus; testId?: string }) {
  return <Badge tone={VERSION_STATUS_TONE[status]} dot testId={testId}>{VERSION_STATUS_LABEL[status]}</Badge>;
}

export function OrderStatusBadge({ status }: { status: MerchOrderStatus }) {
  return <Badge tone={ORDER_STATUS_TONE[status]} testId="merch-order-status">{ORDER_STATUS_LABEL[status]}</Badge>;
}

export function RequiredStateBadge({ state }: { state: MerchRequiredDocument["state"] }) {
  return <Badge tone={REQUIRED_STATE_TONE[state]}>{REQUIRED_STATE_LABEL[state]}</Badge>;
}

/** What automatic extraction can do on this server, stated plainly. */
export function ExtractionNotice({ extraction, className }: { extraction: MerchExtractionStatus; className?: string }) {
  if (extraction.mode === "llm" && extraction.configured)
    return (
      <p className={cn("text-caption text-fg-tertiary", className)} data-testid="merch-extraction-status">
        Extraction: language model <span className="font-mono">{extraction.provider}/{extraction.model}</span>. Its values and confidence
        are suggestions; nothing becomes order data until a person confirms it.
      </p>
    );
  if (extraction.mode === "rules")
    return (
      <p className={cn("text-caption text-fg-tertiary", className)} data-testid="merch-extraction-status">
        Extraction: rule-based label parser (not AI). It reads labelled values such as “PO No: …” and colour × size tables, and quotes the
        line each value came from. Unlabelled text is not understood.
      </p>
    );
  return (
    <Callout tone="warning" title="Automatic extraction is not available" className={className} testId="merch-extraction-status">
      {extraction.reason ?? "No extraction provider is configured."} Documents are still read and stored; reviewers enter the values from the text.
    </Callout>
  );
}

export function AttentionList({ items, empty = "Nothing needs attention.", limit }: { items: MerchAttentionItem[]; empty?: string; limit?: number }) {
  if (items.length === 0) return <p className="text-caption text-fg-tertiary" data-testid="merch-attention-empty">{empty}</p>;
  const shown = limit ? items.slice(0, limit) : items;
  return (
    <ul className="space-y-2" data-testid="merch-attention">
      {shown.map((a, i) => (
        <li key={`${a.kind}-${i}`} className="flex items-start gap-2" data-testid="merch-attention-item" data-kind={a.kind} data-priority={a.priority}>
          <Badge tone={PRIORITY_TONE[a.priority]} className="mt-0.5 shrink-0">{a.priority}</Badge>
          <div className="min-w-0">
            <p className="text-body text-fg-primary">
              {a.versionId ? <Link className="hover:underline" href={`/app/merchandising/versions/${a.versionId}`}>{a.title}</Link> : a.title}
            </p>
            <p className="text-caption text-fg-tertiary">{a.detail}</p>
          </div>
        </li>
      ))}
      {limit && items.length > limit && <li className="text-caption text-fg-tertiary">+ {items.length - limit} more</li>}
    </ul>
  );
}

export function BreakdownTable({ rows }: { rows: { color: string; size: string; quantity: number }[] }) {
  return (
    <div className="overflow-x-auto">
      <table className="data-table mt-2" data-testid="merch-breakdown">
        <thead><tr><th>Colour</th><th>Size</th><th className="num">Quantity</th></tr></thead>
        <tbody>
          {rows.map((r, i) => <tr key={i}><td>{r.color}</td><td>{r.size}</td><td className="num">{r.quantity.toLocaleString("en-US")}</td></tr>)}
        </tbody>
      </table>
    </div>
  );
}

export function DueText({ date, daysUntil, overdue }: { date: string; daysUntil: number; overdue?: boolean }) {
  return (
    <span className={cn("tabular-nums", overdue ? "text-[var(--risk-high)] font-medium" : daysUntil <= 3 ? "text-[var(--risk-medium)]" : "text-fg-secondary")}>
      {date} <span className="text-caption">({days(daysUntil)})</span>
    </span>
  );
}
