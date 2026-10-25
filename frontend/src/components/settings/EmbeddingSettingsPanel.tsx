"use client";

// Embedding settings. Changing the provider, model or dimensions re-embeds
// stored documents (possibly paid), so it is a two-step Admin action:
// "Review" asks the API for a plan (documents, chunks, vector column
// before → after, cost or "can't be estimated"), and only "Confirm" sends
// that plan id back. The API refuses a stale plan and answers with the new
// one, which is shown for another review.

import { useEffect, useState } from "react";
import Link from "next/link";
import { Layers, Save, Trash2 } from "lucide-react";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { Field, Select } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { ConfirmDialog } from "@/components/ui/Modal";
import { Skeleton } from "@/components/ui/Skeleton";
import { ReindexOutcomeView, ReindexPlanView, reindexFailure } from "@/components/rag/ReindexPlanView";
import { apiJson, errorMessage } from "@/lib/api";
import { useResource } from "@/lib/useResource";
import { isAdmin, useAuthStore } from "@/lib/auth";
import { formatDateTime } from "@/lib/format";
import { useT } from "@/lib/useT";
import { cn } from "@/lib/cn";
import type { EmbeddingStatus, EmbeddingUpdateResult, ReindexOutcome, ReindexPlan } from "@/services/contracts";
import { PERSISTENCE_LABEL } from "./LlmSettingsPanel";

type Pending = { kind: "settings"; body: Record<string, unknown> } | { kind: "reindex" };

const HOSTED = new Set(["openai", "gemini"]);

export function EmbeddingSettingsPanel() {
  const { t, locale } = useT();
  const admin = isAdmin(useAuthStore((s) => s.user));
  const status = useResource<EmbeddingStatus>("/api/settings/embedding");
  const s = status.data;

  const [provider, setProvider] = useState("local");
  const [model, setModel] = useState("hash-md5");
  const [dims, setDims] = useState("384");
  const [keyDraft, setKeyDraft] = useState("");
  const [plan, setPlan] = useState<ReindexPlan | null>(null);
  const [pending, setPending] = useState<Pending | null>(null);
  const [busy, setBusy] = useState(false);
  const [confirmClearKey, setConfirmClearKey] = useState(false);
  const [notice, setNotice] = useState<{ text: string; error: boolean } | null>(null);
  const [outcome, setOutcome] = useState<ReindexOutcome | null>(null);

  useEffect(() => {
    if (!s) return;
    setProvider(s.provider);
    setModel(s.model);
    setDims(String(s.dimensions));
  }, [s]);

  const models = (s?.supported ?? []).filter((o) => o.provider === provider);
  const providers = Array.from(new Set((s?.supported ?? []).map((o) => o.provider)));
  const spec = models.find((o) => o.model === model);
  const hosted = HOSTED.has(provider);
  const dimsNum = Number(dims);
  const dimsInvalid = !Number.isInteger(dimsNum) || (spec ? dimsNum < spec.minDimensions || dimsNum > spec.maxDimensions : dimsNum < 1);
  const sameShape = s != null && provider === s.provider && model === s.model && dimsNum === s.dimensions;
  const needsReindex = (s?.pendingDocuments ?? 0) > 0 || (s != null && !s.dimensionsMatchColumn);

  function target() {
    return { provider, model, dimensions: dimsNum };
  }

  function resetReview() {
    setPlan(null);
    setPending(null);
  }

  function fail(e: unknown) {
    const f = reindexFailure(e);
    if (f.plan) {
      setPlan(f.plan);
      setNotice({ text: t("settings.embPlanChanged"), error: true });
    } else {
      setNotice({ text: `${t("settings.embFailed")}: ${f.message}`, error: true });
    }
  }

  async function review() {
    if (busy || dimsInvalid) return;
    setBusy(true);
    setNotice(null);
    setOutcome(null);
    try {
      const p = await apiJson<ReindexPlan>("/api/settings/embedding/plan", { method: "POST", body: target() });
      setPlan(p);
      const body: Record<string, unknown> = { ...target() };
      if (keyDraft.trim()) body.apiKey = keyDraft.trim();
      setPending({ kind: "settings", body });
    } catch (e) {
      fail(e);
    } finally {
      setBusy(false);
    }
  }

  async function reviewReindex() {
    if (busy) return;
    setBusy(true);
    setNotice(null);
    setOutcome(null);
    try {
      const p = await apiJson<ReindexPlan>("/api/rag/reindex/plan");
      if (!p.requiresReindex) {
        setNotice({ text: t("settings.embNothingToDo"), error: false });
        return;
      }
      setPlan(p);
      setPending({ kind: "reindex" });
    } catch (e) {
      fail(e);
    } finally {
      setBusy(false);
    }
  }

  async function saveKey(clear: boolean) {
    if (busy) return;
    setBusy(true);
    setNotice(null);
    try {
      const res = await apiJson<EmbeddingUpdateResult>("/api/settings/embedding", {
        method: "POST",
        body: clear ? { ...target(), clearKey: true, expectedVersion: s?.version } : { ...target(), apiKey: keyDraft.trim(), expectedVersion: s?.version }
      });
      setKeyDraft("");
      status.setData(res.settings);
      setNotice({ text: clear ? "Embedding key cleared" : t("settings.embKeySaved"), error: false });
    } catch (e) {
      fail(e);
      await status.reload();
    } finally {
      setBusy(false);
      setConfirmClearKey(false);
    }
  }

  async function confirm() {
    if (!plan || !pending || busy) return;
    setBusy(true);
    setNotice(null);
    try {
      if (pending.kind === "reindex") {
        const res = await apiJson<ReindexOutcome>("/api/rag/reindex", { method: "POST", body: { confirmPlanId: plan.planId }, timeoutMs: 15 * 60_000 });
        setOutcome(res);
        await status.reload();
      } else {
        const res = await apiJson<EmbeddingUpdateResult>("/api/settings/embedding", {
          method: "POST",
          body: { ...pending.body, confirmPlanId: plan.planId, expectedVersion: s?.version },
          timeoutMs: 15 * 60_000
        });
        status.setData(res.settings);
        setOutcome(res.reindex ?? null);
      }
      resetReview();
      setKeyDraft("");
      setNotice({ text: t("settings.embApplied"), error: false });
    } catch (e) {
      fail(e);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Panel title={t("settings.embPanel")} subtitle={t("settings.embPanelBody")} icon={<Layers size={14} />} testId="embedding-panel"
      right={s ? (s.degraded ? <Badge tone="warning" dot>Degraded</Badge> : <Badge tone="success" dot>Healthy</Badge>) : null}
    >
      {status.error && !s && (
        <Callout tone="danger" title="Could not load embedding settings" action={<Button size="sm" onClick={() => void status.reload()}>Retry</Button>}>
          {errorMessage(status.error)}
        </Callout>
      )}
      {status.loading ? (
        <Skeleton className="h-20" />
      ) : s ? (
        <dl className="grid grid-cols-2 md:grid-cols-4 gap-2 text-caption" data-testid="embedding-status">
          <div className="surface-2 px-3 py-2.5 col-span-2">
            <dt className="text-fg-tertiary">{t("settings.embCurrent")}</dt>
            <dd className="mt-0.5 font-mono text-fg-primary" data-testid="embedding-current">
              {s.provider} / {s.model} / {s.dimensions}
            </dd>
          </div>
          <div className="surface-2 px-3 py-2.5">
            <dt className="text-fg-tertiary">{t("settings.embColumn")}</dt>
            <dd className="mt-0.5 font-mono text-fg-primary">vector({s.columnDimensions ?? "?"})</dd>
          </div>
          <div className="surface-2 px-3 py-2.5">
            <dt className="text-fg-tertiary">{t("settings.embPending")}</dt>
            <dd className="mt-0.5 text-fg-primary font-medium">{s.pendingDocuments} / {s.documents}</dd>
          </div>
          <div className="surface-2 px-3 py-2.5">
            <dt className="text-fg-tertiary">{t("settings.llmProviderPersistence")}</dt>
            <dd className="mt-0.5 text-fg-primary">{t(PERSISTENCE_LABEL[s.persistence] ?? "settings.llmPersistenceNone")}</dd>
          </div>
          <div className="surface-2 px-3 py-2.5">
            <dt className="text-fg-tertiary">API key</dt>
            <dd className="mt-0.5">{s.keyStatus === "set" ? <Badge tone="success">Stored · encrypted</Badge> : s.keyStatus === "unreadable" ? <Badge tone="danger">Unreadable</Badge> : <Badge>Not set</Badge>}</dd>
          </div>
          <div className="surface-2 px-3 py-2.5">
            <dt className="text-fg-tertiary">Last saved</dt>
            <dd className="mt-0.5 text-fg-primary">{s.updatedAt ? formatDateTime(s.updatedAt, locale) : "Never"}</dd>
          </div>
          <div className="surface-2 px-3 py-2.5">
            <dt className="text-fg-tertiary">Version</dt>
            <dd className="mt-0.5 font-mono text-fg-primary">v{s.version}</dd>
          </div>
        </dl>
      ) : null}
      {s && !s.dimensionsMatchColumn && <Callout tone="warning" className="mt-3">{t("settings.embColumnMismatch")}</Callout>}
      {s?.degraded && <Callout tone="warning" className="mt-3">{t("settings.embDegraded")}</Callout>}

      {!admin ? (
        <Callout tone="info" className="mt-4">{t("settings.embAdminOnly")}</Callout>
      ) : (
        <div className="mt-5 space-y-4">
          <fieldset disabled={busy || !s} className="grid gap-4 md:grid-cols-3">
            <Field label={t("settings.embProvider")}>
              {(p) => (
                <Select
                  id="emb-provider"
                  aria-describedby={p.describedBy}
                  value={provider}
                  onChange={(e) => {
                    const next = e.target.value;
                    setProvider(next);
                    const first = (s?.supported ?? []).find((o) => o.provider === next);
                    if (first) {
                      setModel(first.model);
                      setDims(String(first.defaultDimensions));
                    }
                    resetReview();
                  }}
                >
                  {providers.map((v) => <option key={v} value={v}>{v}</option>)}
                </Select>
              )}
            </Field>
            <Field label={t("settings.embModel")}>
              {(p) => (
                <Select
                  id="emb-model"
                  aria-describedby={p.describedBy}
                  value={model}
                  onChange={(e) => {
                    setModel(e.target.value);
                    const opt = models.find((o) => o.model === e.target.value);
                    if (opt) setDims(String(opt.defaultDimensions));
                    resetReview();
                  }}
                >
                  {models.map((o) => <option key={o.model} value={o.model}>{o.model}</option>)}
                </Select>
              )}
            </Field>
            <Field
              label={t("settings.embDimensions")}
              hint={spec ? (spec.minDimensions === spec.maxDimensions ? `Always ${spec.maxDimensions}` : `${spec.minDimensions}–${spec.maxDimensions}, default ${spec.defaultDimensions}`) : undefined}
              error={dimsInvalid ? (spec ? `Enter a whole number between ${spec.minDimensions} and ${spec.maxDimensions}.` : "Enter a whole number.") : null}
            >
              {(p) => (
                <Input
                  id="emb-dims"
                  aria-describedby={p.describedBy}
                  aria-invalid={p.invalid}
                  type="number"
                  inputMode="numeric"
                  min={spec?.minDimensions}
                  max={spec?.maxDimensions}
                  value={dims}
                  onChange={(e) => {
                    setDims(e.target.value);
                    resetReview();
                  }}
                  className="font-mono"
                />
              )}
            </Field>
            {hosted && (
              <Field label={t("settings.embApiKey")} hint={t("settings.embApiKeyHint")} className="md:col-span-3">
                {(p) => (
                  <Input
                    id="emb-api-key"
                    aria-describedby={p.describedBy}
                    type="password"
                    autoComplete="off"
                    spellCheck={false}
                    value={keyDraft}
                    onChange={(e) => setKeyDraft(e.target.value)}
                    placeholder={s?.keySet && sameShape ? "••••••••••••••••" : ""}
                    className="font-mono"
                  />
                )}
              </Field>
            )}
          </fieldset>

          <div className="flex flex-wrap items-center justify-between gap-2">
            <p className="text-caption text-fg-tertiary max-w-md">
              Provider, model or dimension changes re-embed stored documents. You see the plan and cost first; nothing changes until you confirm.
            </p>
            <div className="flex flex-wrap gap-2">
              {needsReindex && (
                <Button variant="ghost" size="sm" data-testid="emb-review-reindex" onClick={() => void reviewReindex()} disabled={busy}>
                  {t("settings.embReviewReindex")}
                </Button>
              )}
              {sameShape && hosted && s?.keySet && !keyDraft.trim() && (
                <Button variant="ghost" size="sm" data-testid="emb-clear-key" onClick={() => setConfirmClearKey(true)} disabled={busy}>
                  <Trash2 size={13} /> Clear key
                </Button>
              )}
              {sameShape ? (
                hosted && keyDraft.trim() ? (
                  <Button variant="primary" size="sm" data-testid="emb-save-key" onClick={() => void saveKey(false)} disabled={busy}>
                    <Save size={13} /> {t("settings.embSaveKey")}
                  </Button>
                ) : null
              ) : (
                <Button variant="primary" size="sm" data-testid="emb-review" onClick={() => void review()} disabled={busy || dimsInvalid}>
                  {busy && !plan ? "Preparing plan…" : t("settings.embReview")}
                </Button>
              )}
            </div>
          </div>

          {plan && pending && (
            <div className="rounded-[var(--radius-card)] border border-[var(--accent-border)] bg-[var(--accent-soft)] p-4" data-testid="emb-plan">
              <p className="text-body font-semibold text-fg-primary">
                {pending.kind === "reindex" ? "Reindex with the current settings" : t("settings.embPlanTitle")}
              </p>
              <div className="mt-3">
                <ReindexPlanView plan={plan} />
              </div>
              <div className="mt-4 flex flex-wrap justify-end gap-2">
                <Button variant="ghost" size="sm" onClick={resetReview} disabled={busy}>
                  {t("settings.embCancel")}
                </Button>
                <Button variant="danger" size="sm" data-testid="emb-confirm" onClick={() => void confirm()} disabled={busy}>
                  {busy ? "Re-embedding…" : t("settings.embConfirm")}
                </Button>
              </div>
            </div>
          )}
        </div>
      )}

      {notice && (
        <p
          role={notice.error ? "alert" : "status"}
          aria-live="polite"
          data-testid="emb-notice"
          className={cn("mt-3 text-caption", notice.error ? "text-[var(--risk-high)]" : "text-[var(--risk-low)]")}
        >
          {notice.text}
        </p>
      )}
      {outcome && <div className="mt-2"><ReindexOutcomeView outcome={outcome} /></div>}
      <p className="mt-4 text-caption text-fg-tertiary">
        Document status and per-document vectors are on the <Link href="/app/knowledge" className="text-accent hover:underline">Knowledge base</Link> page.
      </p>

      <ConfirmDialog
        open={confirmClearKey}
        title="Clear the embedding API key?"
        description={`New documents and searches can't use ${provider} embeddings until a key is saved again; search falls back to keywords.`}
        confirmLabel="Clear key"
        tone="danger"
        busy={busy}
        onConfirm={() => void saveKey(true)}
        onCancel={() => setConfirmClearKey(false)}
      />
    </Panel>
  );
}
