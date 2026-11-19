"use client";

// Integrations: every way data reaches the factory, read from the server
// (GET /api/integrations/status): devices per kind with their connection
// state, how many records each source has sent and when the last one came
// in. Devices are added and configured under Cameras & devices.

import Link from "next/link";
import { useState } from "react";
import { ArrowRight, Camera, FileText, PlugZap, RadioTower, RefreshCw } from "lucide-react";
import { useT } from "@/lib/useT";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge, type BadgeTone } from "@/components/ui/Badge";
import { FilterSelect } from "@/components/ui/Field";
import { StatCard } from "@/components/ui/StatCard";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useMyFactories } from "@/components/workers/shared";
import { useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { formatDateTime, formatRelative } from "@/lib/format";
import { cn } from "@/lib/cn";

type SourceStatus = "receiving" | "connected" | "not_connected";

type Source = {
  id: string;
  category: "sensor" | "camera" | "pilot";
  label: string;
  description: string;
  status: SourceStatus;
  devices: number;
  devicesOnline: number;
  devicesOffline: number;
  records: number;
  records24h: number;
  simulatedRecords: number;
  lastRecordAt?: string | null;
  href: string;
  note?: string | null;
};

type IntegrationStatus = { factoryId: string; factoryCode: string; generatedAt: string; sources: Source[] };

const STATUS: Record<SourceStatus, { label: string; tone: BadgeTone }> = {
  receiving: { label: "Receiving", tone: "success" },
  connected: { label: "Connected, quiet", tone: "warning" },
  not_connected: { label: "Not connected", tone: "neutral" },
};

const GROUPS: { id: Source["category"]; title: string; subtitle: string; icon: React.ReactNode }[] = [
  { id: "sensor", title: "Floor sensors", subtitle: "RFID, attendance readers, machine sensors, counters and gateways", icon: <RadioTower size={14} /> },
  { id: "camera", title: "Cameras", subtitle: "Camera observations from the vision service and camera AI", icon: <Camera size={14} /> },
  { id: "pilot", title: "Documents and imports", subtitle: "Forms, uploads, CSV imports and system-to-system output", icon: <FileText size={14} /> },
];

const n = (v: number) => v.toLocaleString("en-US");

export default function IntegrationsPage() {
  const { t, locale } = useT();
  const { factories } = useMyFactories();
  const [factoryId, setFactoryId] = useState("");
  const res = useResource<IntegrationStatus>(`/api/integrations/status${factoryId ? `?factoryId=${factoryId}` : ""}`, { pollMs: 60_000 });
  const d = res.data;
  const sources = d?.sources ?? [];
  const receiving = sources.filter((s) => s.status === "receiving").length;
  const connected = sources.filter((s) => s.status !== "not_connected").length;
  const devices = sources.reduce((a, s) => a + s.devices, 0);
  const offline = sources.reduce((a, s) => a + s.devicesOffline, 0);

  return (
    <div className="page" data-tour="integrations" data-testid="integrations-page">
      <PageHeader
        title={t("integrations.title")}
        description="Where the factory's data comes from, with live counts from the server. Add and configure devices under Cameras & devices."
        actions={
          <div className="flex flex-wrap items-end gap-2">
            {(factories?.length ?? 0) > 1 && (
              <FilterSelect label="Factory" value={factoryId || d?.factoryId || ""} onChange={setFactoryId}>
                {factories!.map((f) => <option key={f.id} value={f.id}>{f.code} · {f.name}</option>)}
              </FilterSelect>
            )}
            <Button variant="secondary" size="sm" aria-label="Refresh" onClick={() => void res.reload()}>
              <RefreshCw size={13} className={cn(res.refreshing && "animate-spin")} />
            </Button>
          </div>
        }
      />

      {res.loading ? (
        <div className="mt-6"><SkeletonRows rows={6} /></div>
      ) : res.error && !d ? (
        <ErrorState className="mt-6" title="Couldn't load integrations" body={errorMessage(res.error)} retryLabel="Try again" onRetry={() => void res.reload()} />
      ) : d ? (
        <>
          <div className="mt-6 grid gap-3 grid-cols-2 md:grid-cols-4">
            <StatCard label="Receiving now" value={`${receiving} / ${sources.length}`} hint="A record in the last 24 h" tone={receiving > 0 ? "good" : "neutral"} icon={<PlugZap size={14} />} />
            <StatCard label="Connected" value={connected} hint="Has devices or history" />
            <StatCard label="Devices" value={devices} href="/app/cameras-devices/devices" />
            <StatCard label="Devices offline" value={offline} tone={offline > 0 ? "warn" : "neutral"} href="/app/cameras-devices/health" />
          </div>
          <p className="mt-3 text-caption text-fg-tertiary">{d.factoryCode} · updated {formatDateTime(d.generatedAt, locale)}</p>

          <div className="mt-6 space-y-6">
            {GROUPS.map((g) => {
              const items = sources.filter((s) => s.category === g.id);
              if (items.length === 0) return null;
              return (
                <Panel key={g.id} title={g.title} subtitle={g.subtitle} icon={g.icon} testId={`integrations-${g.id}`}>
                  <ul className="divide-y divide-border-subtle">
                    {items.map((s) => (
                      <li key={s.id} className="p-4 grid gap-3 md:grid-cols-[1fr_auto] md:items-center" data-testid={`integration-${s.id}`}>
                        <div className="min-w-0">
                          <div className="flex flex-wrap items-center gap-2">
                            <p className="text-body font-medium text-fg-primary">{s.label}</p>
                            <Badge tone={STATUS[s.status].tone} dot>{STATUS[s.status].label}</Badge>
                            {s.simulatedRecords > 0 && <Badge tone="accent" title="Records produced by a simulator">{n(s.simulatedRecords)} simulated</Badge>}
                          </div>
                          <p className="mt-0.5 text-caption text-fg-tertiary">{s.description}</p>
                          <p className="mt-1.5 flex flex-wrap gap-x-4 gap-y-1 text-caption text-fg-secondary tabular-nums">
                            {s.devices > 0 && (
                              <span>{n(s.devices)} device{s.devices === 1 ? "" : "s"}{s.devicesOnline + s.devicesOffline > 0 ? ` · ${s.devicesOnline} online · ${s.devicesOffline} offline` : ""}</span>
                            )}
                            {(s.records > 0 || s.devices === 0) && <span>{n(s.records)} {t("integrations.records")}</span>}
                            {s.records24h > 0 && <span>{n(s.records24h)} in 24 h</span>}
                            <span>{s.lastRecordAt ? `Last: ${formatRelative(s.lastRecordAt, locale)}` : "Nothing received yet"}</span>
                          </p>
                          {s.note && <p className="mt-1 text-caption text-fg-tertiary">{s.note}</p>}
                        </div>
                        <Link href={s.href} className="inline-flex items-center gap-1 text-caption text-[var(--accent)] hover:underline justify-self-start md:justify-self-end">
                          Open <ArrowRight size={12} />
                        </Link>
                      </li>
                    ))}
                  </ul>
                </Panel>
              );
            })}
          </div>
        </>
      ) : null}
    </div>
  );
}
