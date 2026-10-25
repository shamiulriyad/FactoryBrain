"use client";

import { AlertTriangle, ArrowRight } from "lucide-react";
import type { ReindexOutcome, ReindexPlan } from "@/services/contracts";
import { ApiError } from "@/lib/api";
import { formatNumber } from "@/lib/format";

function bytes(n: number): string {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  return `${(n / 1024 / 1024).toFixed(2)} MB`;
}

export function costLabel(plan: ReindexPlan): string {
  if (plan.cost.estimateUsd == null) return "Can't be estimated reliably";
  if (plan.cost.estimateUsd === 0) return "No cost";
  return `${plan.cost.isUpperBound ? "≤ " : ""}$${plan.cost.estimateUsd.toFixed(4)} ${plan.cost.currency}`;
}

/** The backend-generated plan, laid out for review before an explicit confirm. */
export function ReindexPlanView({ plan }: { plan: ReindexPlan }) {
  const shapeChange =
    plan.current.provider !== plan.target.provider ||
    plan.current.model !== plan.target.model ||
    plan.current.dimensions !== plan.target.dimensions;
  return (
    <div className="space-y-3" data-testid="reindex-plan" data-plan-id={plan.planId}>
      <div className="grid gap-2 sm:grid-cols-[1fr_auto_1fr] items-center">
        <div className="surface-2 p-3">
          <p className="eyebrow">Current</p>
          <p className="mt-1 font-mono text-caption text-fg-primary break-all">
            {plan.current.provider} / {plan.current.model} / {plan.current.dimensions}
          </p>
        </div>
        <ArrowRight size={16} className="hidden sm:block text-fg-tertiary mx-auto" aria-hidden />
        <div className="surface-2 p-3 border-[var(--accent-border)]">
          <p className="eyebrow">{shapeChange ? "After confirming" : "Target (unchanged)"}</p>
          <p className="mt-1 font-mono text-caption text-fg-primary break-all">
            {plan.target.provider} / {plan.target.model} / {plan.target.dimensions}
          </p>
        </div>
      </div>

      <dl className="grid grid-cols-2 sm:grid-cols-4 gap-2 text-caption">
        <div className="surface-2 p-3">
          <dt className="text-fg-tertiary">Documents / chunks</dt>
          <dd className="mt-1 text-body font-semibold text-fg-primary tabular-nums" data-testid="emb-plan-docs">
            {plan.documents} / {plan.chunks}
          </dd>
        </div>
        <div className="surface-2 p-3">
          <dt className="text-fg-tertiary">Vector column</dt>
          <dd className="mt-1 text-body font-semibold text-fg-primary font-mono" data-testid="emb-plan-column">
            vector({plan.columnDimensions ?? "?"}) → vector({plan.target.dimensions})
          </dd>
        </div>
        <div className="surface-2 p-3">
          <dt className="text-fg-tertiary">Text to embed</dt>
          <dd className="mt-1 text-body font-semibold text-fg-primary tabular-nums">
            {bytes(plan.bytes)}
            <span className="block text-[11px] font-normal text-fg-tertiary">≤ {formatNumber(plan.estimatedTokensUpperBound)} tokens</span>
          </dd>
        </div>
        <div className="surface-2 p-3">
          <dt className="text-fg-tertiary">Estimated cost</dt>
          <dd className="mt-1 text-body font-semibold text-fg-primary" data-testid="emb-plan-cost">
            {costLabel(plan)}
            <span className="block text-[11px] font-normal text-fg-tertiary">{plan.cost.note}</span>
          </dd>
        </div>
      </dl>

      {(plan.requiresColumnResize || plan.reembedsAllDocuments) && (
        <p className="text-caption text-fg-secondary">
          {plan.reembedsAllDocuments ? "Every stored document is re-embedded. " : ""}
          {plan.requiresColumnResize
            ? "The vector column is resized, so all existing vectors are replaced. Until this finishes, search keeps using the current vectors."
            : ""}
        </p>
      )}

      {plan.warnings.length > 0 && (
        <ul className="space-y-1.5" data-testid="reindex-plan-warnings">
          {plan.warnings.map((w) => (
            <li key={w} className="flex gap-2 text-caption text-[var(--risk-medium)]">
              <AlertTriangle size={13} className="mt-0.5 shrink-0" aria-hidden />
              <span>{w}</span>
            </li>
          ))}
        </ul>
      )}
      <p className="text-[11px] text-fg-tertiary font-mono break-all">plan {plan.planId} · settings v{plan.settingsVersion}</p>
    </div>
  );
}

export function ReindexOutcomeView({ outcome }: { outcome: ReindexOutcome }) {
  return (
    <p className="text-caption text-fg-secondary" data-testid="reindex-outcome">
      Re-embedded {outcome.documents} document(s), {outcome.chunks} chunk(s) with{" "}
      <span className="font-mono">{outcome.embedding.provider}/{outcome.embedding.model}/{outcome.embedding.dimensions}</span>.
      {outcome.resized
        ? ` Vector column resized vector(${outcome.columnDimensionsBefore ?? "?"}) → vector(${outcome.columnDimensionsAfter}).`
        : ` Vector column unchanged (vector(${outcome.columnDimensionsAfter})).`}
    </p>
  );
}

/**
 * Messages for the documented failure envelopes of POST /api/rag/reindex and
 * POST /api/settings/embedding (SettingsResults.ToResult + RagController).
 */
export function reindexFailure(e: unknown): { message: string; plan: ReindexPlan | null } {
  if (!(e instanceof ApiError)) return { message: e instanceof Error ? e.message : "The request failed.", plan: null };
  const body = (e.body ?? {}) as { error?: string; details?: string; plan?: ReindexPlan; errors?: Record<string, string>; traceId?: string };
  const plan = body.plan ?? null;
  switch (body.error) {
    case "plan_changed":
      return { message: "The documents or settings changed since this plan was made. Review the updated plan and confirm again.", plan };
    case "confirmation_required":
      return { message: "The server needs a reviewed plan before re-embedding. Review the plan below and confirm.", plan };
    case "reindex_in_progress":
      return { message: body.details ?? "Another reindex is running. Wait for it to finish, then review a new plan.", plan: null };
    case "version_conflict":
      return { message: "Someone else changed the embedding settings. Reload and review again.", plan: null };
    case "embedding_provider_failed":
      return { message: body.details ?? "The embedding provider call failed, so nothing was changed.", plan: null };
    case "dimension_mismatch":
      return { message: body.details ?? "The provider's output size doesn't match the requested dimensions. Nothing was changed.", plan: null };
    case "encryption_key_not_configured":
      return { message: "The server has no AI_SETTINGS_ENCRYPTION_KEY, so API keys can't be saved. Nothing was changed.", plan: null };
    case "invalid_settings":
      return { message: body.errors ? Object.values(body.errors).join(" ") : "These settings are not supported.", plan: null };
    case "DatabaseError":
      return { message: `${body.details ?? "Reindex failed and was rolled back."}${body.traceId ? ` (trace ${body.traceId})` : ""}`, plan: null };
  }
  if (e.kind === "forbidden" || e.kind === "unauthorized") return { message: "Only an Admin can change embedding settings or start a reindex.", plan: null };
  return { message: e.message, plan };
}
