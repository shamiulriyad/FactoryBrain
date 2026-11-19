"use client";

// Floor map: the factory's production floors stacked like the building
// (highest code on top), each with its live status, people, machines and
// open alerts from GET /api/factories/{id}/floor-status.

import Link from "next/link";
import { useEffect } from "react";
import { Layers, RefreshCw } from "lucide-react";
import { PageHeader } from "@/components/ui/PageHeader";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { FloorFactoryPicker, FloorSlab } from "@/components/floors/FloorViews";
import { refreshFloorStatus, useFloorStatus } from "@/lib/useFloorStatus";
import { formatDateTime } from "@/lib/format";
import { useT } from "@/lib/useT";

export default function FloorMapPage() {
  const { t, locale } = useT();
  const { data, error, loaded, floors } = useFloorStatus();
  useEffect(() => { void refreshFloorStatus(); }, []);
  // Top floor first, like a building section.
  const stack = [...floors].sort((a, b) => Number(b.isActive) - Number(a.isActive) || b.code.localeCompare(a.code, undefined, { numeric: true }));
  const counts = { act: floors.filter((f) => f.status === "act").length, watch: floors.filter((f) => f.status === "watch").length };

  return (
    <div className="page page-narrow" data-testid="floor-map-page">
      <PageHeader
        title={t("nav.floorMap")}
        description="Each production floor with its people, machines and open alerts. Click a floor for its lines and recent alerts."
        meta={data ? <span className="text-caption text-fg-tertiary">{data.factoryCode} · updated {formatDateTime(data.generatedAt, locale)}</span> : undefined}
        actions={
          <div className="flex flex-wrap items-end gap-2">
            <FloorFactoryPicker value={data?.factoryId ?? ""} />
            <Button variant="secondary" size="sm" aria-label="Refresh" onClick={() => void refreshFloorStatus()}><RefreshCw size={13} /></Button>
          </div>
        }
      />

      {!loaded ? (
        <div className="mt-6"><SkeletonRows rows={4} rowHeight="h-28" /></div>
      ) : error && !data ? (
        <ErrorState className="mt-6" title="Couldn't load the floor status" body={error} retryLabel="Try again" onRetry={() => void refreshFloorStatus()} />
      ) : !data ? (
        <EmptyState className="mt-6" icon={<Layers size={18} />} title="No factory to show" body="Your account isn't assigned to a factory yet. Ask an administrator." />
      ) : floors.length === 0 ? (
        <EmptyState className="mt-6" icon={<Layers size={18} />} title="No production floors yet"
          body="Add the factory's floors under Admin → Factories, then place workstations on them; they appear here." />
      ) : (
        <>
          {(counts.act > 0 || counts.watch > 0) && (
            <Callout className="mt-4" tone={counts.act > 0 ? "danger" : "warning"}>
              {counts.act > 0 && `${counts.act} floor${counts.act === 1 ? "" : "s"} need action. `}
              {counts.watch > 0 && `${counts.watch} floor${counts.watch === 1 ? "" : "s"} to watch.`}
            </Callout>
          )}
          <div className="mt-4 space-y-3" data-testid="floor-stack">
            {stack.map((f) => <FloorSlab key={f.id} floor={f} />)}
          </div>
          <p className="mt-4 text-caption text-fg-tertiary">
            {data.note}
            {data.unplacedLines > 0 && <> {data.unplacedLines} line{data.unplacedLines === 1 ? " has" : "s have"} no workstation on a floor yet; place them under <Link className="text-[var(--accent)]" href="/app/assignments/workstations">Workstations</Link>.</>}
          </p>
        </>
      )}
    </div>
  );
}
