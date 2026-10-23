"use client";

import { useEffect, useMemo, useState } from "react";
import { motion } from "framer-motion";
import {
  Building2,
  Palette,
  ShieldAlert,
  Database,
  Wrench,
  Save,
  Trash2,
  Sun,
  Moon,
  Monitor,
  Sparkles
} from "lucide-react";
import { useRouter, useSearchParams } from "next/navigation";
import { useT } from "@/lib/useT";
import { useBusinessStore } from "@/store/business.store";
import { useAppStore } from "@/store/app.store";
import { riskService } from "@/services/risk.service";
import { Panel } from "@/components/ui/Panel";
import { Input } from "@/components/ui/Input";
import { Button } from "@/components/ui/Button";
import { LanguageToggle } from "@/components/ui/LanguageToggle";
import { Tooltip } from "@/components/ui/Tooltip";
import { formatBDT } from "@/lib/format";
import type { Locale } from "@/i18n/registry";
import { cn } from "@/lib/cn";
import { agentService } from "@/services/agent.service";

// Five curated sections. Anything that was previously under
// Notifications, the old split Agents / Risk, or Experimental flags is
// either moved into Agents/Risk or hidden from the UI. The feature flags
// and simulated-data badge remain live in the store for the rest of the
// app (Sidebar, Vision, Activity, Integrations) — they just no longer
// take up screen real estate here.
type SectionId = "profile" | "appearance" | "agentsRisk" | "advanced" | "dataReset";

const SECTIONS: Array<{ id: SectionId; iconKey: string; labelKey: string }> = [
  { id: "profile", iconKey: "Building2", labelKey: "settings.businessProfile" },
  { id: "appearance", iconKey: "Palette", labelKey: "settings.appearance" },
  { id: "agentsRisk", iconKey: "ShieldAlert", labelKey: "settings.agentsRisk" },
  { id: "advanced", iconKey: "Wrench", labelKey: "settings.advanced" },
  { id: "dataReset", iconKey: "Database", labelKey: "settings.dataReset" }
];

export default function SettingsPage() {
  const { t, locale } = useT();
  const router = useRouter();
  const search = useSearchParams();
  const sectionParam = search?.get("section") as SectionId | null;
  const [active, setActive] = useState<SectionId>("profile");
  const [savedKey, setSavedKey] = useState<string | null>(null);

  // Honor ?section= deep links from the command palette.
  useEffect(() => {
    if (sectionParam && SECTIONS.some((s) => s.id === sectionParam)) {
      setActive(sectionParam);
    }
  }, [sectionParam]);

  // Auto-clear "Saved" indicator.
  useEffect(() => {
    if (!savedKey) return;
    const t1 = setTimeout(() => setSavedKey(null), 1800);
    return () => clearTimeout(t1);
  }, [savedKey]);

  function flash(key: string) {
    setSavedKey(key);
  }

  function onSelectSection(id: SectionId) {
    setActive(id);
    const url = new URL(window.location.href);
    url.searchParams.set("section", id);
    window.history.replaceState({}, "", url.toString());
  }

  return (
    <div className="px-6 md:px-8 py-6 max-w-5xl mx-auto" data-tour="settings">
      <h1 className="text-display font-semibold tracking-tight">{t("settings.title")}</h1>
      <p className="mt-1 text-caption text-fg-tertiary">{t("settings.subtitle")}</p>

      <div className="mt-6 grid grid-cols-1 md:grid-cols-[220px_1fr] gap-6">
        <nav
          aria-label="Settings sections"
          className="md:sticky md:top-20 md:self-start"
        >
          <ul className="flex md:flex-col gap-1 overflow-x-auto md:overflow-visible">
            {SECTIONS.map((s) => (
              <li key={s.id}>
                <button
                  type="button"
                  onClick={() => onSelectSection(s.id)}
                  aria-current={active === s.id ? "page" : undefined}
                  className={cn(
                    "flex items-center gap-2 w-full px-3 h-9 rounded-md text-caption transition-colors text-left whitespace-nowrap",
                    active === s.id
                      ? "bg-[var(--accent-soft)] text-accent border border-[var(--accent-border)]"
                      : "text-fg-secondary hover:text-fg-primary hover:bg-surface-2 border border-transparent"
                  )}
                >
                  <SectionIcon name={s.iconKey} />
                  {t(s.labelKey) as string}
                </button>
              </li>
            ))}
          </ul>
        </nav>

        <div className="space-y-4">
          {active === "profile" && <ProfileSection flash={flash} savedKey={savedKey} />}
          {active === "appearance" && <AppearanceSection flash={flash} savedKey={savedKey} />}
          {active === "agentsRisk" && <AgentsRiskSection flash={flash} savedKey={savedKey} locale={locale} />}
          {active === "advanced" && <AdvancedSection />}
          {active === "dataReset" && <DataResetSection router={router} />}
        </div>
      </div>
    </div>
  );
}

function SectionIcon({ name }: { name: string }) {
  switch (name) {
    case "Building2":
      return <Building2 size={13} />;
    case "Palette":
      return <Palette size={13} />;
    case "ShieldAlert":
      return <ShieldAlert size={13} />;
    case "Database":
      return <Database size={13} />;
    case "Wrench":
      return <Wrench size={13} />;
    default:
      return <Sparkles size={13} />;
  }
}

function Saved({ savedKey, id }: { savedKey: string | null; id: string }) {
  if (savedKey !== id) return null;
  return (
    <motion.p initial={{ opacity: 0 }} animate={{ opacity: 1 }} className="text-caption text-risk-low">
      <Save size={10} className="inline mr-1" />
      Saved
    </motion.p>
  );
}

function Row({ label, hint, children }: { label: string; hint?: string; children: React.ReactNode }) {
  return (
    <div className="grid grid-cols-1 sm:grid-cols-[200px_1fr] gap-2 sm:gap-4 py-2.5 border-b border-border-subtle last:border-b-0">
      <div>
        <p className="text-body text-fg-primary">{label}</p>
        {hint && <p className="mt-0.5 text-caption text-fg-tertiary">{hint}</p>}
      </div>
      <div className="min-w-0">{children}</div>
    </div>
  );
}

function ProfileSection({ flash, savedKey }: { flash: (k: string) => void; savedKey: string | null }) {
  const { t } = useT();
  const profile = useBusinessStore((s) => s.profile);
  const setProfile = useBusinessStore((s) => s.setProfile);

  // RMG demo Profile: plant name + city only. Language lives under Appearance.
  // Store still keeps industry / currency / goals for seeds — UI-hidden this pass.
  return (
    <Panel title={t("settings.businessProfile")}>
      <Row label={t("settings.businessName")} hint="Plant / company name shown in greetings">
        <Input
          value={profile.businessName}
          onChange={(e) => setProfile({ businessName: e.target.value })}
          onBlur={() => flash("profile")}
        />
      </Row>
      <Row label={t("settings.city")}>
        <Input
          value={profile.city}
          onChange={(e) => setProfile({ city: e.target.value })}
          onBlur={() => flash("profile")}
        />
      </Row>
      <div className="mt-3 flex justify-end"><Saved savedKey={savedKey} id="profile" /></div>
    </Panel>
  );
}

function AppearanceSection({ flash, savedKey }: { flash: (k: string) => void; savedKey: string | null }) {
  const { t } = useT();
  const theme = useAppStore((s) => s.theme);
  const setTheme = useAppStore((s) => s.setTheme);
  const density = useBusinessStore((s) => s.density);
  const setDensity = useBusinessStore((s) => s.setDensity);

  return (
    <Panel title={t("settings.appearance")}>
      <Row label={t("settings.language")}>
        <LanguageToggle />
      </Row>
      <Row label={t("settings.theme")} hint="Light, dark, or follow system">
        <div className="inline-flex rounded-md border border-border-subtle p-0.5 bg-surface-2" role="radiogroup">
          {[
            { v: "light", Icon: Sun, label: t("settings.themeLight") },
            { v: "dark", Icon: Moon, label: t("settings.themeDark") },
            { v: "system", Icon: Monitor, label: t("settings.themeSystem") }
          ].map((o) => {
            const active = theme === o.v;
            return (
              <Tooltip key={o.v} content={o.label as string}>
                <button
                  type="button"
                  role="radio"
                  aria-checked={active}
                  onClick={() => { setTheme(o.v as any); flash("appearance"); }}
                  className={cn(
                    "size-8 rounded inline-flex items-center justify-center transition-colors",
                    active
                      ? "bg-[var(--btn-primary-bg)] text-[var(--btn-primary-fg)]"
                      : "text-fg-secondary hover:text-fg-primary"
                  )}
                >
                  <o.Icon size={14} />
                </button>
              </Tooltip>
            );
          })}
        </div>
      </Row>
      <Row label={t("settings.density")}>
        <div className="inline-flex rounded-md border border-border-subtle p-0.5 bg-surface-2">
          {(["comfortable", "compact"] as const).map((d) => (
            <button
              key={d}
              type="button"
              onClick={() => { setDensity(d); flash("appearance"); }}
              aria-pressed={density === d}
              className={cn(
                "h-8 px-3 text-caption rounded transition-colors",
                density === d ? "bg-[var(--btn-primary-bg)] text-[var(--btn-primary-fg)]" : "text-fg-secondary hover:text-fg-primary"
              )}
            >
              {t(d === "comfortable" ? "settings.densityComfortable" : "settings.densityCompact") as string}
            </button>
          ))}
        </div>
      </Row>
      <div className="mt-3 flex justify-end"><Saved savedKey={savedKey} id="appearance" /></div>
    </Panel>
  );
}

// ---- Agents & risk (merged) ----------------------------------------------
// The previous split into separate Agents / Risk / Notifications tabs was
// folded into one panel. Notifications (in-app / email / WhatsApp digest)
// was a non-essential dial and is dropped from the UI; feature flags and
// simulated-data controls were also dropped from the UI (they still live
// in the store and are read by the rest of the app).

function AgentsRiskSection({
  flash,
  savedKey,
  locale
}: {
  flash: (k: string) => void;
  savedKey: string | null;
  locale: Locale;
}) {
  const { t } = useT();
  const agents = useMemo(() => agentService.list(), []);
  const agentMode = useBusinessStore((s) => s.agentMode);
  const setAgentMode = useBusinessStore((s) => s.setAgentMode);
  const globalPaused = useBusinessStore((s) => s.globalPaused);
  const setGlobalPaused = useBusinessStore((s) => s.setGlobalPaused);

  const thresholds = useBusinessStore((s) => s.thresholds);
  const setThresholds = useBusinessStore((s) => s.setThresholds);
  const approvalGate = useBusinessStore((s) => s.approvalGate);
  const setApprovalGate = useBusinessStore((s) => s.setApprovalGate);
  const autoApproveBelow = useBusinessStore((s) => s.autoApproveBelow);
  const setAutoApproveBelow = useBusinessStore((s) => s.setAutoApproveBelow);
  // Legacy local-only fallback for the inventory threshold, kept for backward compatibility.
  const legacyThreshold = riskService.read().inventoryOrderThresholdBdt;
  const [legacy, setLegacy] = useState(legacyThreshold);

  return (
    <>
      <Panel title={t("settings.autonomy")}>
        <Row label={t("settings.globalPause")} hint="Stops every agent immediately">
          <label className="inline-flex items-center gap-2 text-caption text-fg-primary">
            <input
              type="checkbox"
              checked={globalPaused}
              onChange={(e) => setGlobalPaused(e.target.checked)}
              className="accent-accent"
            />
            {globalPaused ? "All agents paused" : "All agents running"}
          </label>
        </Row>
        <Row label={t("settings.perAgentMode")} hint="Override each agent's autonomy">
          <div className="space-y-2">
            {agents.map((a) => {
              const m = agentMode[a.id] ?? "auto";
              return (
                <div
                  key={a.id}
                  className="flex items-center justify-between gap-2 p-2.5 rounded-md border border-border-subtle bg-surface-2"
                >
                  <div className="min-w-0">
                    <p className="text-body text-fg-primary">{a.name}</p>
                    <p className="text-caption text-fg-tertiary truncate">{a.purpose}</p>
                  </div>
                  <div className="inline-flex rounded-md border border-border-subtle bg-canvas p-0.5 shrink-0">
                    {(["auto", "approval", "paused"] as const).map((mode) => (
                      <button
                        key={mode}
                        type="button"
                        aria-pressed={m === mode}
                        onClick={() => setAgentMode(a.id, mode)}
                        className={cn(
                          "h-7 px-2.5 text-caption rounded transition-colors",
                          m === mode ? "bg-[var(--btn-primary-bg)] text-[var(--btn-primary-fg)]" : "text-fg-secondary hover:text-fg-primary"
                        )}
                      >
                        {t(`agents.autonomy.${mode}`) as string}
                      </button>
                    ))}
                  </div>
                </div>
              );
            })}
          </div>
        </Row>
      </Panel>

      <Panel className="mt-4" title={t("settings.risk")}>
        <Row label={t("settings.thresholdOrderValue")} hint="Orders above this need approval">
          <Input
            type="number"
            value={thresholds.orderValueBdt}
            onChange={(e) => setThresholds({ orderValueBdt: Number(e.target.value) || 0 })}
            onBlur={() => flash("risk")}
            className="w-32 text-right"
          />
        </Row>
        <Row label={t("settings.thresholdDiscount")} hint="Discounts above this % need approval">
          <Input
            type="number"
            value={thresholds.discountPct}
            onChange={(e) => setThresholds({ discountPct: Number(e.target.value) || 0 })}
            onBlur={() => flash("risk")}
            className="w-24 text-right"
          />
        </Row>
        <Row label={t("settings.thresholdInventorySpend")} hint="Restock purchases above this need approval">
          <Input
            type="number"
            value={thresholds.inventorySpendBdt}
            onChange={(e) => setThresholds({ inventorySpendBdt: Number(e.target.value) || 0 })}
            onBlur={() => flash("risk")}
            className="w-32 text-right"
          />
        </Row>
        <Row label={t("settings.thresholdMessageVolume")} hint="Outbound message batches above this need approval">
          <Input
            type="number"
            value={thresholds.messageVolume}
            onChange={(e) => setThresholds({ messageVolume: Number(e.target.value) || 0 })}
            onBlur={() => flash("risk")}
            className="w-24 text-right"
          />
        </Row>
        <Row label={t("settings.inventoryOrderThreshold")} hint="Inventory Agent auto-suggests below this; approval above">
          <div className="flex items-center gap-2">
            <Input
              type="number"
              value={legacy}
              onChange={(e) => setLegacy(Number(e.target.value))}
              className="w-32 text-right"
            />
            <Button
              variant="primary"
              size="sm"
              onClick={() => {
                riskService.update({ inventoryOrderThresholdBdt: legacy || 25000 });
                flash("risk");
              }}
            >
              <Save size={12} /> {t("settings.save")}
            </Button>
          </div>
          <p className="mt-1 text-caption text-fg-tertiary">
            Current: {formatBDT(legacy, locale)}
          </p>
        </Row>
        <Row label={t("settings.approvalGate")} hint="Which risk tiers need approval">
          <div className="inline-flex rounded-md border border-border-subtle bg-surface-2 p-0.5">
            {(["low", "medium", "high"] as const).map((r) => (
              <button
                key={r}
                type="button"
                aria-pressed={approvalGate[r]}
                onClick={() => { setApprovalGate({ [r]: !approvalGate[r] }); flash("risk"); }}
                className={cn(
                  "h-8 px-3 text-caption rounded transition-colors",
                  approvalGate[r] ? "bg-[var(--btn-primary-bg)] text-[var(--btn-primary-fg)]" : "text-fg-secondary hover:text-fg-primary"
                )}
              >
                {t(`risk.${r}`) as string}
              </button>
            ))}
          </div>
        </Row>
        <Row label={t("settings.autoApproveBelow")} hint="Skip approval under this tier">
          <select
            value={autoApproveBelow}
            onChange={(e) => { setAutoApproveBelow(e.target.value as any); flash("risk"); }}
            className="h-9 rounded-md bg-surface-2 border border-border-subtle px-2 text-caption text-fg-primary"
          >
            <option value="never">Never</option>
            <option value="low">{t("risk.low") as string}</option>
            <option value="medium">{t("risk.medium") as string}</option>
            <option value="high">{t("risk.high") as string}</option>
          </select>
        </Row>
        <div className="mt-3 flex justify-end"><Saved savedKey={savedKey} id="risk" /></div>
      </Panel>
    </>
  );
}

// ---- Advanced (LLM panel only) -------------------------------------------
// Earlier Advanced had a "Restart tour" row and a "Reset demo" row. Both
// were non-essential dials; restart tour is still triggerable via the
// command palette and Reset demo lives under Data reset. The LLM panel
// is the only thing kept here.

function AdvancedSection() {
  return (
    <Panel title="Advanced">
      <LlmPanel />
    </Panel>
  );
}

// ---- Data reset ----------------------------------------------------------
// Single destructive action. Honest copy about what gets wiped (Zustand
// profile + thresholds + integrations + theme + key draft; .env.local
// only via the LLM Advanced panel — see that tab).

function DataResetSection({ router }: { router: ReturnType<typeof useRouter> }) {
  const { t } = useT();
  const resetDemo = useAppStore((s) => s.resetDemo);
  const [confirmReset, setConfirmReset] = useState(false);

  function doReset() {
    resetDemo();
    router.replace("/onboarding/welcome");
  }

  return (
    <Panel title={t("settings.dataReset")} subtitle={t("settings.dataResetBody")}>
      <Row
        label={t("settings.resetDemo")}
        hint={t("settings.resetDemoHint")}
      >
        {!confirmReset ? (
          <Button variant="danger" size="sm" onClick={() => setConfirmReset(true)}>
            <Trash2 size={12} /> {t("settings.resetDemo")}
          </Button>
        ) : (
          <div className="flex items-center justify-end gap-2">
            <Button variant="ghost" size="sm" onClick={() => setConfirmReset(false)}>
              {t("common.cancel")}
            </Button>
            <Button variant="danger" size="sm" onClick={doReset}>
              {t("common.confirm")}
            </Button>
          </div>
        )}
      </Row>
    </Panel>
  );
}

// ---- LLM / Live Ask panel -------------------------------------------------
//
// Renders inside <AdvancedSection />. Talks to /api/settings/llm ONLY:
//   - GET  → reads non-secret status (configured, provider, model, mode,
//            persistence) and never receives the key.
//   - POST → submits { provider, apiKey, model } or { clearKey: true }.
//
// The key never enters React state beyond a per-keystroke string in a local
// controlled input that is cleared on submit. It is never stored in Zustand,
// localStorage, cookies, or the URL. It is never prefixed with NEXT_PUBLIC_*.

type LlmStatus = {
  configured: boolean;
  provider: string | null;
  model: string | null;
  mode: "demo" | "live";
  persistence: "env.local" | "process" | "vercel_only";
  notice?: string;
};

const PROVIDERS = [
  { value: "openai", labelKey: "settings.llmProviderOpenai" },
  { value: "gemini", labelKey: "settings.llmProviderGemini" },
  { value: "anthropic", labelKey: "settings.llmProviderAnthropic" }
] as const;

const DEFAULT_OPENAI_MODEL = "gpt-6-luna";

const MASKED_KEY = "\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022";

function maskKey(): string {
  // Returned once on each successful save so the UI can show the masked
  // indicator. Never a function of the real key — same string every time.
  return MASKED_KEY;
}

function LlmPanel() {
  const { t } = useT();

  // Status from the server. The key is never on this object.
  const [status, setStatus] = useState<LlmStatus | null>(null);
  const [statusLoading, setStatusLoading] = useState(true);
  const [statusError, setStatusError] = useState<string | null>(null);

  // Form fields. The raw key only lives in `keyDraft` until the user clicks
  // Save, then it is sent to the server and discarded from local state.
  const [provider, setProvider] = useState<"openai" | "gemini" | "anthropic">("openai");
  const [model, setModel] = useState<string>("");
  const [keyDraft, setKeyDraft] = useState<string>("");
  const [hasKey, setHasKey] = useState(false);

  // Submit + clear state.
  const [saving, setSaving] = useState(false);
  const [clearing, setClearing] = useState(false);
  const [confirmClear, setConfirmClear] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);

  async function refreshStatus() {
    setStatusLoading(true);
    setStatusError(null);
    try {
      const res = await fetch("/api/settings/llm", { method: "GET", cache: "no-store" });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const data = (await res.json()) as LlmStatus;
      setStatus(data);
      // Sync the form to the saved values. The key is intentionally omitted.
      if (data.provider && ["openai", "gemini", "anthropic"].includes(data.provider)) {
        setProvider(data.provider as "openai" | "gemini" | "anthropic");
      }
      setModel(data.model ?? (!data.provider || data.provider === "openai" ? DEFAULT_OPENAI_MODEL : ""));
      setHasKey(Boolean(data.configured));
    } catch (err) {
      setStatusError("Could not load LLM status.");
    } finally {
      setStatusLoading(false);
    }
  }

  useEffect(() => {
    refreshStatus();
  }, []);

  async function handleSave(e: React.FormEvent) {
    e.preventDefault();
    setSaving(true);
    setNotice(null);
    try {
      const trimmedKey = keyDraft.trim();
      const body: {
        provider: "openai" | "gemini" | "anthropic";
        apiKey?: string;
        model?: string;
      } = { provider };
      // Only send apiKey when the user actually typed one. An empty draft
      // is treated as "leave existing key alone" by the server contract.
      if (trimmedKey.length > 0) body.apiKey = trimmedKey;
      if (model.trim().length > 0) body.model = model.trim();

      const res = await fetch("/api/settings/llm", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
      const data = (await res.json()) as Partial<LlmStatus> & { notice?: string; error?: string };
      if (!res.ok) {
        // 401/403 means the server-side ADMIN_API_TOKEN gate refused the
        // request — the .NET API is reachable but the secret header is
        // missing or wrong. Show a clear, unambiguous message so the
        // operator can fix the env var instead of seeing a silent failure.
        if (res.status === 401 || res.status === 403) {
          setNotice(t("settings.llmAdminTokenError") as string);
          return;
        }
        setNotice(t("settings.llmSavedToast") + " — " + (data.error ?? "failed"));
        return;
      }
      // Wipe the draft key from local memory the moment a save lands.
      setKeyDraft("");
      setHasKey(true);
      setStatus((prev) => ({ ...(prev as LlmStatus), ...(data as LlmStatus) }));
      await refreshStatus();
      setNotice(data.notice ?? (t("settings.llmSavedToast") as string));
    } catch {
      setNotice("Could not reach the server.");
    } finally {
      setSaving(false);
      // Auto-dismiss the notice after a few seconds.
      setTimeout(() => setNotice(null), 4000);
    }
  }

  async function handleClear() {
    setClearing(true);
    setNotice(null);
    try {
      const res = await fetch("/api/settings/llm", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ provider, clearKey: true })
      });
      const data = (await res.json()) as Partial<LlmStatus> & { notice?: string; error?: string };
      if (!res.ok) {
        // Same 401/403 clear-and-tell behaviour as handleSave — when the
        // admin token gate refuses the call we surface the explicit
        // configuration error instead of a generic "failed".
        if (res.status === 401 || res.status === 403) {
          setNotice(t("settings.llmAdminTokenError") as string);
          return;
        }
        setNotice(t("settings.llmClearedToast") + " — " + (data.error ?? "failed"));
        return;
      }
      setKeyDraft("");
      setHasKey(false);
      setStatus((prev) => ({ ...(prev as LlmStatus), ...(data as LlmStatus) }));
      await refreshStatus();
      setNotice(data.notice ?? (t("settings.llmClearedToast") as string));
    } catch {
      setNotice("Could not reach the server.");
    } finally {
      setClearing(false);
      setConfirmClear(false);
      setTimeout(() => setNotice(null), 4000);
    }
  }

  return (
    <div className="rounded-md border border-border-subtle bg-surface-2/40 p-3 mb-2">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="text-body text-fg-primary">{t("settings.llmPanel")}</p>
          <p className="mt-0.5 text-caption text-fg-tertiary">{t("settings.llmPanelBody")}</p>
        </div>
        <div className="shrink-0 flex items-center gap-2">
          {status?.mode === "live" ? (
            <span
              className="inline-flex items-center gap-1 rounded-full border border-border-subtle bg-canvas px-2 py-0.5 text-caption text-risk-low"
              title="Live mode"
            >
              <span className="size-1.5 rounded-full bg-risk-low" />
              {t("settings.llmLiveChip") as string}
            </span>
          ) : (
            <span
              className="inline-flex items-center gap-1 rounded-full border border-border-subtle bg-canvas px-2 py-0.5 text-caption text-fg-secondary"
              title="Demo mode"
            >
              <span className="size-1.5 rounded-full bg-fg-tertiary" />
              {t("settings.llmDemoChip") as string}
            </span>
          )}
        </div>
      </div>

      <form onSubmit={handleSave} className="mt-3 space-y-3" autoComplete="off">
        <div>
          <label htmlFor="llm-provider" className="text-caption text-fg-secondary">
            {t("settings.llmProvider")}
          </label>
          <p className="mt-0.5 text-caption text-fg-tertiary">{t("settings.llmProviderHint")}</p>
          <select
            id="llm-provider"
            name="llm-provider"
            value={provider}
            onChange={(e) => {
              const next = e.target.value as "openai" | "gemini" | "anthropic";
              setProvider(next);
              if (next === "openai" && !model.trim()) setModel(DEFAULT_OPENAI_MODEL);
            }}
            className="mt-1 h-9 w-full rounded-md bg-surface-2 border border-border-subtle px-2 text-body text-fg-primary focus:outline-none focus:border-[var(--accent)] focus:shadow-[0_0_0_3px_var(--accent-soft)]"
          >
            {PROVIDERS.map((p) => (
              <option key={p.value} value={p.value}>
                {t(p.labelKey) as string}
              </option>
            ))}
          </select>
        </div>

        <div>
          <label htmlFor="llm-api-key" className="text-caption text-fg-secondary">
            {t("settings.llmApiKey")}
          </label>
          <p className="mt-0.5 text-caption text-fg-tertiary">{t("settings.llmApiKeyHint")}</p>
          {/* The saved key is never sent to the browser. The input stays
              visible so a new key can replace it; the placeholder is a
              fixed mask, not derived from the real key. */}
          <Input
            id="llm-api-key"
            name="llm-api-key"
            type="password"
            autoComplete="off"
            spellCheck={false}
            value={keyDraft}
            onChange={(e) => setKeyDraft(e.target.value)}
            placeholder={hasKey ? maskKey() : (t("settings.llmApiKeyPlaceholder") as string)}
            className="mt-1 font-mono"
          />
          {hasKey && keyDraft.length === 0 && (
            <p className="mt-1 text-caption text-risk-low">{t("settings.llmKeySaved") as string}</p>
          )}
          {!hasKey && !keyDraft && (
            <p className="mt-1 text-caption text-fg-tertiary">
              {t("settings.llmKeyNotSetHint") as string}
            </p>
          )}
        </div>

        <div>
          <label htmlFor="llm-model" className="text-caption text-fg-secondary">
            {t("settings.llmModel")}
          </label>
          <p className="mt-0.5 text-caption text-fg-tertiary">{t("settings.llmModelHint")}</p>
          <Input
            id="llm-model"
            name="llm-model"
            type="text"
            autoComplete="off"
            spellCheck={false}
            value={model}
            onChange={(e) => setModel(e.target.value)}
            placeholder={provider === "openai" ? DEFAULT_OPENAI_MODEL : ""}
            className="mt-1 font-mono"
          />
        </div>

        <div className="flex flex-wrap items-center justify-between gap-2 pt-1">
          <div className="text-caption text-fg-tertiary min-w-0">
            {statusLoading ? (
              <span>…</span>
            ) : statusError ? (
              <span className="text-risk-high">{statusError}</span>
            ) : (
              <>
                {status?.provider && (
                  <span className="mr-3">
                    <span className="text-fg-secondary">{t("settings.llmProvider")}: </span>
                    <span className="text-fg-primary font-mono">{status.provider}</span>
                  </span>
                )}
                {status && (
                  <span>
                    <span className="text-fg-secondary">{t("settings.llmProviderPersistence")}: </span>
                    <span className="text-fg-primary">
                      {t(
                        status.persistence === "env.local"
                          ? "settings.llmPersistenceEnvLocal"
                          : status.persistence === "vercel_only"
                          ? "settings.llmPersistenceVercel"
                          : "settings.llmPersistenceProcess"
                      ) as string}
                    </span>
                  </span>
                )}
              </>
            )}
          </div>

          <div className="flex items-center gap-2">
            {hasKey && !confirmClear && (
              <Button
                variant="ghost"
                size="sm"
                onClick={() => setConfirmClear(true)}
                disabled={saving || clearing}
              >
                <Trash2 size={12} /> {t("settings.llmClear")}
              </Button>
            )}
            {hasKey && confirmClear && (
              <div className="flex items-center gap-2">
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => setConfirmClear(false)}
                  disabled={clearing}
                >
                  {t("settings.llmClearCancel")}
                </Button>
                <Button
                  variant="danger"
                  size="sm"
                  onClick={handleClear}
                  disabled={clearing}
                >
                  <Trash2 size={12} />
                  {clearing ? t("settings.llmClearing") : t("settings.llmClearConfirmCta")}
                </Button>
              </div>
            )}
            <Button variant="primary" size="sm" type="submit" disabled={saving || clearing}>
              <Save size={12} />
              {saving ? t("settings.llmClearing") : t("settings.llmSave")}
            </Button>
          </div>
        </div>

        {notice && (
          <p
            role="status"
            aria-live="polite"
            className={cn(
              "text-caption",
              notice === t("settings.llmAdminTokenError")
                ? "text-risk-high"
                : "text-fg-secondary"
            )}
          >
            {notice}
          </p>
        )}
      </form>

      <details className="mt-3 group">
        <summary className="cursor-pointer text-caption text-fg-secondary hover:text-fg-primary select-none">
          {t("settings.llmSecurityTitle") as string}
        </summary>
        <p className="mt-1 text-caption text-fg-tertiary">
          {t("settings.llmSecurityBody") as string}
        </p>
      </details>
    </div>
  );
}
