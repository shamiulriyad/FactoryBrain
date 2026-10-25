"use client";

// LLM settings: GET /api/settings/llm (any signed-in user, never includes a
// key) and POST /api/settings/llm (Admin). The API persists the change with
// the key encrypted, audits it and applies it from the next request.
//
// The raw key only exists in `keyDraft` while the Admin types it; it is sent
// once and cleared. It never goes to a store, localStorage, a cookie or the
// URL, and no response ever contains it.

import { useEffect, useState } from "react";
import { KeyRound, RotateCcw, Save, Trash2 } from "lucide-react";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { Field, Select } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { ConfirmDialog } from "@/components/ui/Modal";
import { Skeleton } from "@/components/ui/Skeleton";
import { useToast } from "@/components/ui/Toast";
import { ApiError, apiJson, errorMessage } from "@/lib/api";
import { useResource } from "@/lib/useResource";
import { isAdmin, useAuthStore } from "@/lib/auth";
import { formatDateTime } from "@/lib/format";
import { useT } from "@/lib/useT";
import { cn } from "@/lib/cn";
import { LLM_PROVIDERS, type LlmSettingsRequest, type LlmStatus, type Persistence } from "@/services/contracts";

export const PERSISTENCE_LABEL: Record<Persistence, string> = {
  database: "settings.llmPersistenceDatabase",
  startup_config: "settings.llmPersistenceStartup",
  runtime_override: "settings.llmPersistenceOverride",
  none: "settings.llmPersistenceNone"
};

const PROVIDER_LABEL: Record<string, string> = {
  openai: "settings.llmProviderOpenai",
  gemini: "settings.llmProviderGemini",
  anthropic: "settings.llmProviderAnthropic"
};

/** LlmSettings.DefaultOpenAiModel on the server. */
const DEFAULT_OPENAI_MODEL = "gpt-6-luna";
const MASKED_KEY = "••••••••••••••••";

type Provider = (typeof LLM_PROVIDERS)[number];

export function LlmSettingsPanel() {
  const { t, locale } = useT();
  const toast = useToast();
  const admin = isAdmin(useAuthStore((s) => s.user));
  const status = useResource<LlmStatus>("/api/settings/llm");
  const s = status.data;

  const [provider, setProvider] = useState<Provider>("openai");
  const [model, setModel] = useState("");
  const [keyDraft, setKeyDraft] = useState("");
  const [busy, setBusy] = useState<null | "save" | "clearKey" | "clearAll">(null);
  const [confirm, setConfirm] = useState<null | "clearKey" | "clearAll">(null);
  const [notice, setNotice] = useState<{ text: string; error: boolean } | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});

  // Sync the form whenever a new server state arrives (load, save, conflict reload).
  useEffect(() => {
    if (!s) return;
    if (s.provider && (LLM_PROVIDERS as readonly string[]).includes(s.provider)) setProvider(s.provider as Provider);
    setModel(s.model ?? "");
  }, [s]);

  const providerChanges = !!s?.provider && provider !== s.provider;
  const modelNeeded = provider !== "openai" && !model.trim();

  function failure(e: unknown): string {
    if (e instanceof ApiError) {
      if (e.kind === "forbidden" || e.kind === "unauthorized") return t("settings.llmAdminTokenError");
      if (e.code === "version_conflict") return t("settings.llmVersionConflict");
      if (e.code === "encryption_key_not_configured") return t("settings.llmNoEncryptionKey");
      if (e.code === "invalid_provider") return "That provider is not supported. Choose OpenAI, Anthropic or Google Gemini.";
      if (e.kind === "unavailable") return "The settings store is unavailable right now; nothing was changed. Try again shortly.";
      if (e.kind === "validation") {
        const errs = Object.fromEntries(Object.entries(e.fieldErrors).map(([k, v]) => [k, v.join(" ")]));
        setFieldErrors(errs);
        return Object.values(errs)[0] ?? e.message;
      }
    }
    return errorMessage(e);
  }

  async function post(body: LlmSettingsRequest, kind: "save" | "clearKey" | "clearAll", okText: string) {
    if (busy) return;
    setBusy(kind);
    setNotice(null);
    setFieldErrors({});
    try {
      // expectedVersion: the server refuses (409) to overwrite a change someone
      // else saved after this page loaded.
      const next = await apiJson<LlmStatus>("/api/settings/llm", { method: "POST", body: { ...body, expectedVersion: s?.version } });
      status.setData(next);
      setKeyDraft("");
      setNotice({ text: okText, error: false });
      toast.push({ title: okText, description: next.mode === "live" ? "Ask uses the language model from the next question." : "Ask answers in demo mode from the next question." });
    } catch (e) {
      setNotice({ text: failure(e), error: true });
      if (e instanceof ApiError && e.kind === "conflict") await status.reload();
    } finally {
      setBusy(null);
      setConfirm(null);
    }
  }

  function save(e: React.FormEvent) {
    e.preventDefault();
    const body: LlmSettingsRequest = { provider };
    const key = keyDraft.trim();
    // An empty draft means "keep the stored key" in the server contract.
    if (key) body.apiKey = key;
    if (model.trim()) body.model = model.trim();
    void post(body, "save", t("settings.llmSavedToast"));
  }

  const chip = s?.mode === "live" ? (
    <span data-testid="llm-mode-chip" data-mode="live"><Badge tone="success" dot>{t("settings.llmLiveChip")}</Badge></span>
  ) : (
    <span data-testid="llm-mode-chip" data-mode="demo"><Badge dot>{t("settings.llmDemoChip")}</Badge></span>
  );

  return (
    <Panel
      title={t("settings.llmPanel")}
      subtitle={t("settings.llmPanelBody")}
      icon={<KeyRound size={14} />}
      right={s ? chip : null}
      testId="llm-panel"
    >
      {status.error && !s && (
        <Callout tone="danger" title="Could not load the LLM status" action={<Button size="sm" onClick={() => void status.reload()}>Retry</Button>}>
          {errorMessage(status.error)}
        </Callout>
      )}

      {status.loading ? (
        <div className="grid grid-cols-2 md:grid-cols-4 gap-2">{[0, 1, 2, 3].map((i) => <Skeleton key={i} className="h-14" />)}</div>
      ) : s ? (
        <dl className="grid grid-cols-2 md:grid-cols-4 gap-2 text-caption" data-testid="llm-status">
          <StatusCell label="Answers">{s.mode === "live" ? "Live model" : "Demo (no model call)"}</StatusCell>
          <StatusCell label={t("settings.llmProvider")}>{s.provider ? t(PROVIDER_LABEL[s.provider] ?? s.provider) : "—"}</StatusCell>
          <StatusCell label="Model">{s.model ? <span className="font-mono">{s.model}</span> : "—"}</StatusCell>
          <StatusCell label={t("settings.llmProviderPersistence")}>
            <span data-testid="llm-persistence" data-persistence={s.persistence}>{t(PERSISTENCE_LABEL[s.persistence] ?? "settings.llmPersistenceNone")}</span>
          </StatusCell>
          <StatusCell label="API key">
            {s.keyStatus === "set" ? <Badge tone="success">Stored · encrypted</Badge> : s.keyStatus === "unreadable" ? <Badge tone="danger">Unreadable</Badge> : <Badge>Not set</Badge>}
          </StatusCell>
          <StatusCell label="Last saved">{s.updatedAt ? formatDateTime(s.updatedAt, locale) : "Never (startup config)"}</StatusCell>
          <StatusCell label="Version"><span className="font-mono">v{s.version}</span></StatusCell>
          <StatusCell label="Key storage">{s.canStoreKeys ? "Available" : <span className="text-[var(--risk-medium)]">Not configured</span>}</StatusCell>
        </dl>
      ) : null}

      {s?.keyStatus === "unreadable" && (
        <Callout tone="danger" className="mt-3" testId="llm-key-unreadable">{t("settings.llmKeyUnreadable")}</Callout>
      )}
      {s && !s.canStoreKeys && <Callout tone="warning" className="mt-3">{t("settings.llmNoEncryptionKey")}</Callout>}

      {!admin && s && (
        <Callout tone="info" className="mt-4" testId="llm-readonly">
          {t("settings.llmAdminTokenError")}. You can see which model is in use; an Admin makes changes.
        </Callout>
      )}

      <form onSubmit={save} className="mt-5 space-y-4" autoComplete="off" aria-label="LLM settings">
        <fieldset disabled={!admin || !s || busy !== null} className="grid gap-4 md:grid-cols-3 disabled:opacity-90">
          <Field label={t("settings.llmProvider")} hint={providerChanges && s?.keySet ? "Switching provider removes the saved key unless you enter a new one." : t("settings.llmProviderHint")}>
            {(p) => (
              <Select
                id="llm-provider"
                aria-describedby={p.describedBy}
                value={provider}
                onChange={(e) => {
                  setProvider(e.target.value as Provider);
                  setFieldErrors({});
                }}
              >
                {LLM_PROVIDERS.map((v) => (
                  <option key={v} value={v}>{t(PROVIDER_LABEL[v])}</option>
                ))}
              </Select>
            )}
          </Field>
          <Field
            label={t("settings.llmModel")}
            hint={provider === "openai" ? `Empty uses the server default (${DEFAULT_OPENAI_MODEL}).` : "Required for this provider; without a model Ask stays in demo mode."}
            error={fieldErrors.model}
          >
            {(p) => (
              <Input
                id="llm-model"
                aria-describedby={p.describedBy}
                aria-invalid={p.invalid || (modelNeeded && !!keyDraft)}
                type="text"
                spellCheck={false}
                value={model}
                maxLength={128}
                onChange={(e) => setModel(e.target.value)}
                placeholder={provider === "openai" ? DEFAULT_OPENAI_MODEL : "model name"}
                className="font-mono"
              />
            )}
          </Field>
          <Field label={t("settings.llmApiKey")} hint={s?.keySet ? "A key is stored. Type a new one to replace it, or leave empty to keep it." : t("settings.llmApiKeyHint")} error={fieldErrors.apiKey}>
            {(p) => (
              <Input
                id="llm-api-key"
                aria-describedby={p.describedBy}
                type="password"
                autoComplete="off"
                spellCheck={false}
                maxLength={2048}
                value={keyDraft}
                onChange={(e) => setKeyDraft(e.target.value)}
                placeholder={s?.keySet ? MASKED_KEY : t("settings.llmApiKeyPlaceholder")}
                className="font-mono"
              />
            )}
          </Field>
        </fieldset>

        {admin && (
          <div className="flex flex-wrap items-center justify-between gap-2 pt-1">
            <p className="text-caption text-fg-tertiary max-w-md">
              Changes apply from the next question, with no restart. Every save is written to the audit log without the key.
            </p>
            <div className="flex flex-wrap items-center gap-2">
              {s?.keySet && (
                <Button variant="ghost" size="sm" type="button" data-testid="llm-clear" onClick={() => setConfirm("clearKey")} disabled={busy !== null}>
                  <Trash2 size={13} /> {t("settings.llmClear")}
                </Button>
              )}
              {s?.persistence === "database" && (
                <Button variant="ghost" size="sm" type="button" data-testid="llm-clear-all" onClick={() => setConfirm("clearAll")} disabled={busy !== null}>
                  <RotateCcw size={13} /> Turn off live answers
                </Button>
              )}
              <Button variant="primary" size="sm" type="submit" data-testid="llm-save" disabled={busy !== null || !s}>
                <Save size={13} /> {busy === "save" ? "Saving…" : t("settings.llmSave")}
              </Button>
            </div>
          </div>
        )}
        {!admin && (
          <div className="flex justify-end">
            <Button variant="primary" size="sm" type="button" data-testid="llm-save" disabled>
              <Save size={13} /> {t("settings.llmSave")}
            </Button>
          </div>
        )}

        {notice && (
          <p
            role={notice.error ? "alert" : "status"}
            aria-live="polite"
            data-testid="llm-notice"
            className={cn("text-caption", notice.error ? "text-[var(--risk-high)]" : "text-[var(--risk-low)]")}
          >
            {notice.text}
          </p>
        )}
      </form>

      <details className="mt-4">
        <summary className="cursor-pointer text-caption text-fg-secondary hover:text-fg-primary select-none">{t("settings.llmSecurityTitle")}</summary>
        <p className="mt-1.5 text-caption text-fg-tertiary">{t("settings.llmSecurityBody")}</p>
      </details>

      <ConfirmDialog
        open={confirm === "clearKey"}
        title={t("settings.llmClearConfirmTitle")}
        description={t("settings.llmClearConfirmBody")}
        confirmLabel={t("settings.llmClearConfirmCta")}
        cancelLabel={t("settings.llmClearCancel")}
        tone="danger"
        busy={busy === "clearKey"}
        onConfirm={() => void post({ provider, clearKey: true }, "clearKey", t("settings.llmClearedToast"))}
        onCancel={() => setConfirm(null)}
        testId="llm-clear-dialog"
        confirmTestId="llm-clear-confirm"
      />
      <ConfirmDialog
        open={confirm === "clearAll"}
        title="Turn off live answers?"
        description="Removes the saved provider, model and key. Ask and the agents answer in demo mode until an Admin saves new settings. The startup configuration is not used again for these settings."
        confirmLabel="Turn off"
        tone="danger"
        busy={busy === "clearAll"}
        onConfirm={() => void post({ clearAll: true }, "clearAll", "Live answers turned off")}
        onCancel={() => setConfirm(null)}
      />
    </Panel>
  );
}

function StatusCell({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="surface-2 px-3 py-2.5 min-w-0">
      <dt className="text-fg-tertiary">{label}</dt>
      <dd className="mt-0.5 text-fg-primary font-medium truncate">{children}</dd>
    </div>
  );
}
