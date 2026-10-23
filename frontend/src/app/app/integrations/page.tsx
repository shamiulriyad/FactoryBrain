"use client";

import { useState, useEffect, useMemo } from "react";
import { motion } from "framer-motion";
import { PlugZap, RefreshCw, Power, Pencil, ChevronDown, ChevronRight, FlaskConical, RadioTower, FileText, Info } from "lucide-react";
import { useT } from "@/lib/useT";
import { ingestionService, isSensorSource } from "@/services/ingestion.service";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Modal } from "@/components/ui/Modal";
import { Tooltip } from "@/components/ui/Tooltip";
import { EmptyState } from "@/components/ui/EmptyState";
import { useToast } from "@/components/ui/Toast";
import { useBusinessStore } from "@/store/business.store";
import { SOURCE_FORMS, maskValue } from "@/services/ingestion-forms";
import type { IngestionSourceCategory } from "@/store/business.store";
import type { IngestionSourcePublic } from "@/services/types";
import { cn } from "@/lib/cn";
import { formatRelative } from "@/lib/format";

type SyncStatus = "idle" | "syncing" | "ok" | "fail";

export default function IntegrationsPage() {
  const { t, locale } = useT();
  const sources = useBusinessStore((s) => s.sources);
  const upsertSource = useBusinessStore((s) => s.upsertSource);
  const simulatedData = useBusinessStore((s) => s.simulatedData);
  const toast = useToast();
  const [tick, setTick] = useState(0);
  const [status, setStatus] = useState<Record<string, SyncStatus>>({});
  const [connectFor, setConnectFor] = useState<string | null>(null);
  const [confirmDisconnect, setConfirmDisconnect] = useState<string | null>(null);
  const [showOutOfScope, setShowOutOfScope] = useState(false);

  // Tick once a second so relative timestamps stay current without a noisy interval.
  useEffect(() => {
    const id = setInterval(() => setTick((n) => n + 1), 30_000);
    return () => clearInterval(id);
  }, []);

  // tick + sources are intentional re-trigger signals; service is static.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const list = useMemo(() => ingestionService.list(), [sources, tick]);
  const connectedCount = list.filter((s) => s.status === "connected").length;

  const sensorSources = useMemo(() => list.filter((s) => s.category === "sensor"), [list]);
  const pilotSources = useMemo(() => list.filter((s) => s.category === "pilot"), [list]);
  const outOfScopeSources = useMemo(() => list.filter((s) => s.category === "out_of_scope"), [list]);

  async function handleSync(id: string) {
    setStatus((s) => ({ ...s, [id]: "syncing" }));
    const result = await ingestionService.sync(id);
    if (!result.ok) {
      setStatus((s) => ({ ...s, [id]: "fail" }));
      toast.push({ title: t("integrations.syncFailed") as string });
    } else {
      setStatus((s) => ({ ...s, [id]: "ok" }));
      toast.push({ title: t("integrations.syncSuccess") as string });
      setTimeout(() => setStatus((s) => ({ ...s, [id]: "idle" })), 2000);
    }
  }

  function handleDisconnect(id: string) {
    ingestionService.disconnect(id);
    setConfirmDisconnect(null);
    toast.push({
      title: t("integrations.disconnect") as string,
      ttlMs: 5000,
      onUndo: () => upsertSource(id, { connected: true })
    });
  }

  if (list.length === 0) return null;

  return (
    <div className="px-6 md:px-8 py-6 max-w-4xl mx-auto" data-tour="integrations">
      <div className="flex items-end justify-between gap-3 flex-wrap">
        <div>
          <h1 className="text-display font-semibold tracking-tight">{t("integrations.title")}</h1>
          <p className="mt-1 text-caption text-fg-tertiary">{t("integrations.subtitle")}</p>
        </div>
        <p className="text-caption text-fg-tertiary">
          {connectedCount} of {list.length} connected
        </p>
      </div>

      {connectedCount === 0 ? (
        <div className="mt-8">
          <EmptyState
            title={t("integrations.empty") as string}
            body="Connect at least one source to unlock the rest of the app."
            icon={<PlugZap size={28} />}
          />
          <div className="mt-4 flex justify-center">
            <Button variant="primary" onClick={() => setConnectFor("rfid-bundles")}>
              {t("integrations.emptyCta")}
            </Button>
          </div>
        </div>
      ) : (
        <div className="mt-6 space-y-8">
          {/* Floor sensors — RFID, machine telemetry, energy meter. Calibrated simulated feeds. */}
          <IntegrationGroup
            title={t("integrations.groupSensors") as string}
            subtitle={t("integrations.groupSensorsSubtitle") as string}
            icon={<RadioTower size={14} />}
            category="sensor"
            items={sensorSources}
            status={status}
            simulatedData={simulatedData}
            locale={locale as "en" | "bn"}
            onConnect={setConnectFor}
            onDisconnect={setConfirmDisconnect}
            onSync={handleSync}
            startIndex={0}
          />

          {/* Pilot sources — Documents, CSV/Excel, Google Sheets. Used alongside sensors. */}
          <IntegrationGroup
            title={t("integrations.groupPilot") as string}
            subtitle={t("integrations.groupPilotSubtitle") as string}
            icon={<FileText size={14} />}
            category="pilot"
            items={pilotSources}
            status={status}
            simulatedData={simulatedData}
            locale={locale as "en" | "bn"}
            onConnect={setConnectFor}
            onDisconnect={setConfirmDisconnect}
            onSync={handleSync}
            startIndex={sensorSources.length}
          />

          {/* Out of scope for the RMG pilot — disclosed/demoted. Connect flows still work so
              previously connected sources stay queryable, but the group sits below the fold. */}
          <div
            className="surface-2 border border-border-subtle rounded-md"
            data-testid="integrations-out-of-scope"
          >
            <button
              type="button"
              onClick={() => setShowOutOfScope((v) => !v)}
              aria-expanded={showOutOfScope}
              aria-controls="integrations-out-of-scope-list"
              className="w-full flex items-center justify-between gap-3 px-4 py-3 text-left press"
            >
              <div className="flex items-center gap-2 min-w-0">
                {showOutOfScope ? <ChevronDown size={14} /> : <ChevronRight size={14} />}
                <Info size={14} className="text-fg-tertiary shrink-0" />
                <span className="text-body text-fg-primary truncate">
                  {t("integrations.groupOutOfScope")}
                </span>
                <span className="mono-pill text-fg-tertiary bg-surface border border-border-subtle px-1.5 py-0.5 rounded-sm shrink-0">
                  {outOfScopeSources.length}
                </span>
              </div>
              <span className="text-caption text-accent hover:underline shrink-0">
                {showOutOfScope ? t("integrations.groupOutOfScopeHide") : t("integrations.groupOutOfScopeToggle")}
              </span>
            </button>
            <p className="px-4 pb-3 text-caption text-fg-tertiary">
              {t("integrations.groupOutOfScopeSubtitle")}
            </p>
            {showOutOfScope && (
              <div id="integrations-out-of-scope-list" className="px-4 pb-4">
                <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
                  {outOfScopeSources.map((s, i) => (
                    <SourceCard
                      key={s.id}
                      source={s}
                      index={sensorSources.length + pilotSources.length + i}
                      sStatus={status[s.id] ?? "idle"}
                      simulatedData={simulatedData}
                      locale={locale as "en" | "bn"}
                      outOfScope
                      onConnect={setConnectFor}
                      onDisconnect={setConfirmDisconnect}
                      onSync={handleSync}
                    />
                  ))}
                </div>
              </div>
            )}
          </div>
        </div>
      )}

      {/* Connect / Edit modal */}
      <ConnectModal
        sourceId={connectFor}
        onClose={() => setConnectFor(null)}
      />

      {/* Disconnect confirm */}
      <Modal
        open={!!confirmDisconnect}
        onClose={() => setConfirmDisconnect(null)}
        title={t("integrations.disconnect")}
        footer={
          <>
            <Button variant="ghost" size="sm" onClick={() => setConfirmDisconnect(null)}>
              {t("common.cancel")}
            </Button>
            <Button variant="danger" size="sm" onClick={() => confirmDisconnect && handleDisconnect(confirmDisconnect)}>
              {t("common.confirm")}
            </Button>
          </>
        }
      >
        <p className="text-body text-fg-secondary">{t("integrations.disconnectConfirm")}</p>
      </Modal>
    </div>
  );
}

// --- Group + Card subcomponents --------------------------------------------

function IntegrationGroup({
  title,
  subtitle,
  icon,
  category,
  items,
  status,
  simulatedData,
  locale,
  onConnect,
  onDisconnect,
  onSync,
  startIndex
}: {
  title: string;
  subtitle: string;
  icon: React.ReactNode;
  category: IngestionSourceCategory;
  items: IngestionSourcePublic[];
  status: Record<string, SyncStatus>;
  simulatedData: boolean;
  locale: "en" | "bn";
  onConnect: (id: string) => void;
  onDisconnect: (id: string) => void;
  onSync: (id: string) => Promise<void>;
  startIndex: number;
}) {
  if (items.length === 0) return null;
  return (
    <section data-testid={`integrations-group-${category}`}>
      <header className="flex items-start gap-2 mb-3">
        <span className="mt-1 text-fg-tertiary shrink-0">{icon}</span>
        <div className="min-w-0">
          <h2 className="text-title text-fg-primary">{title}</h2>
          <p className="mt-0.5 text-caption text-fg-tertiary">{subtitle}</p>
        </div>
      </header>
      <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
        {items.map((s, i) => (
          <SourceCard
            key={s.id}
            source={s}
            index={startIndex + i}
            sStatus={status[s.id] ?? "idle"}
            simulatedData={simulatedData}
            locale={locale}
            outOfScope={false}
            onConnect={onConnect}
            onDisconnect={onDisconnect}
            onSync={onSync}
          />
        ))}
      </div>
    </section>
  );
}

function SourceCard({
  source: s,
  index: i,
  sStatus,
  simulatedData,
  locale,
  outOfScope,
  onConnect,
  onDisconnect,
  onSync
}: {
  source: IngestionSourcePublic;
  index: number;
  sStatus: SyncStatus;
  simulatedData: boolean;
  locale: "en" | "bn";
  outOfScope: boolean;
  onConnect: (id: string) => void;
  onDisconnect: (id: string) => void;
  onSync: (id: string) => Promise<void>;
}) {
  const { t } = useT();
  const connected = s.status === "connected";
  const isSensor = isSensorSource(s.id);
  const showSimulated = simulatedData && isSensor;
  const sym = sStatus === "syncing" ? <RefreshCw size={12} className="animate-spin" /> : <RefreshCw size={12} />;
  return (
    <motion.div
      initial={{ opacity: 0, y: 6 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.22, delay: i * 0.03 }}
      data-testid={`integration-card-${s.id}`}
    >
      <Panel>
        <div className="flex items-start gap-3">
          <div className="size-9 rounded-md bg-surface-2 flex items-center justify-center text-fg-primary shrink-0">
            <PlugZap size={16} />
          </div>
          <div className="flex-1 min-w-0">
            <div className="flex items-center justify-between gap-2">
              <div className="flex items-center gap-2 min-w-0">
                <p className="text-body text-fg-primary truncate">{locale === "bn" ? s.labelBn : s.label}</p>
                {showSimulated && (
                  <Tooltip content={t("integrations.simulatedTip") as string}>
                    <span
                      className="inline-flex items-center gap-1 mono-pill text-fg-tertiary bg-surface-2 border border-border-subtle px-1.5 py-0.5 rounded-sm shrink-0"
                      data-testid={`integration-simulated-${s.id}`}
                    >
                      <FlaskConical size={10} />
                      {t("integrations.simulated")}
                    </span>
                  </Tooltip>
                )}
                {outOfScope && (
                  <span
                    className="inline-flex items-center gap-1 mono-pill text-fg-tertiary bg-surface-2 border border-border-subtle px-1.5 py-0.5 rounded-sm shrink-0"
                    data-testid={`integration-out-of-scope-${s.id}`}
                    title={t("integrations.outOfScopeReason") as string}
                  >
                    <Info size={10} />
                    {t("integrations.groupOutOfScopeTag")}
                  </span>
                )}
              </div>
              <span
                className={cn(
                  "mono-pill border px-2 py-1 rounded-sm shrink-0",
                  connected
                    ? "text-[var(--risk-low)] bg-[var(--risk-low-soft)] border-[var(--risk-low-border)]"
                    : "text-fg-tertiary bg-surface-2 border-border-subtle"
                )}
              >
                {connected ? t("integrations.connected") : t("integrations.available")}
              </span>
            </div>
            <p className="mt-1 text-caption text-fg-tertiary">{s.objectTypes.join(" · ")}</p>
            <p className="mt-1 text-caption text-fg-tertiary">
              {s.records.toLocaleString()} {t("integrations.records")}
              {connected && s.lastSync && <> · {t("integrations.lastSync")} {formatRelative(s.lastSync, locale)}</>}
              {s.value && connected && <> · {maskValue(s.id, s.value)}</>}
            </p>
            {isSensor && (
              <p className="mt-1 text-caption text-fg-tertiary">
                {t("integrations.calibrationNote")}
              </p>
            )}
            {sStatus === "fail" && (
              <p className="mt-1 text-caption text-[var(--risk-high)]">{t("integrations.syncFailed")}</p>
            )}
          </div>
        </div>
        <div className="mt-3 flex items-center justify-end gap-2">
          {connected ? (
            <>
              <Tooltip content={t("integrations.disconnect") as string}>
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => onDisconnect(s.id)}
                  aria-label={t("integrations.disconnect") as string}
                >
                  <Power size={12} />
                </Button>
              </Tooltip>
              <Tooltip content={t("integrations.edit") as string}>
                <Button
                  variant="secondary"
                  size="sm"
                  onClick={() => onConnect(s.id)}
                  aria-label={t("integrations.edit") as string}
                >
                  <Pencil size={12} /> {t("integrations.edit")}
                </Button>
              </Tooltip>
              <Button variant="primary" size="sm" onClick={() => void onSync(s.id)} disabled={sStatus === "syncing"}>
                {sym}
                {t("integrations.syncNow")}
              </Button>
            </>
          ) : (
            <Button variant="primary" size="sm" onClick={() => onConnect(s.id)}>
              {t("integrations.connect")}
            </Button>
          )}
        </div>
      </Panel>
    </motion.div>
  );
}

import { useState as useReactState } from "react";
import { SOURCE_FORMS as SF } from "@/services/ingestion-forms";
import { Input as Inp } from "@/components/ui/Input";

function ConnectModal({ sourceId, onClose }: { sourceId: string | null; onClose: () => void }) {
  const { t } = useT();
  const form = sourceId ? SOURCE_FORMS[sourceId] : null;
  const existing = useBusinessStore((s) => (sourceId ? s.sources.find((x) => x.id === sourceId) : undefined));
  const [values, setValues] = useReactState<Record<string, string>>({});
  const [error, setError] = useReactState<string | null>(null);
  const [busy, setBusy] = useReactState(false);
  const [step, setStep] = useReactState(0);
  const [steps, setSteps] = useReactState<string[]>([]);

  useEffect(() => {
    if (!sourceId) return;
    const seed: Record<string, string> = {};
    if (existing?.value) {
      // crude re-seed: put value into the first text/url field
      const first = form?.fields.find((f) => f.type !== "file");
      if (first) seed[first.key] = existing.value;
    }
    setValues(seed);
    setError(null);
    setStep(0);
    setSteps([]);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sourceId]);

  if (!form || !sourceId) return null;

  async function submit() {
    setBusy(true);
    setError(null);
    setStep(1);
    setSteps(["Validating"]);
    const r = await ingestionService.connect(
      sourceId!,
      values,
      (s, l) => {
        setStep(s);
        setSteps((arr) => [...arr, l]);
      }
    );
    setBusy(false);
    if (!r.ok) setError(r.error ?? "Failed");
    else onClose();
  }

  return (
    <Modal
      open
      onClose={onClose}
      title={form.title}
      footer={
        <>
          <Button variant="ghost" size="sm" onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button variant="primary" size="sm" onClick={submit} disabled={busy}>
            {busy ? "…" : t("common.confirm")}
          </Button>
        </>
      }
    >
      <div className="space-y-3">
        {form.fields.map((f) => (
          <label key={f.key} className="block">
            <span className="text-caption text-fg-secondary mb-1.5 block">{f.label}</span>
            {f.type === "file" ? (
              <input
                type="file"
                accept=".csv,.xlsx,.xls"
                onChange={(e) => setValues((v) => ({ ...v, [f.key]: e.target.files?.[0]?.name ?? "" }))}
                className="block w-full text-caption text-fg-primary file:mr-3 file:h-7 file:px-2 file:rounded file:border file:border-border-subtle file:bg-surface-2 file:text-fg-secondary file:text-caption"
              />
            ) : (
              <Inp
                type={f.type === "tel" ? "tel" : f.type === "url" ? "url" : "text"}
                value={values[f.key] ?? ""}
                onChange={(e) => setValues((v) => ({ ...v, [f.key]: e.target.value }))}
                placeholder={f.placeholder}
              />
            )}
          </label>
        ))}
        {error && <p className="text-caption text-[var(--risk-high)]">{error}</p>}
        {busy && (
          <div className="mt-1">
            <div className="flex items-center gap-2 text-caption text-fg-secondary">
              <span className="mono-pill text-fg-tertiary">{step}/3</span>
              <span className="truncate">{steps[steps.length - 1] ?? "—"}</span>
            </div>
            <div className="mt-1.5 h-1 rounded bg-surface overflow-hidden">
              <div className="h-full bg-accent transition-all duration-200" style={{ width: `${(step / 3) * 100}%` }} />
            </div>
          </div>
        )}
      </div>
    </Modal>
  );
}