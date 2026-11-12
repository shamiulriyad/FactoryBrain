"use client";

// /app/floors/[floor]: one production floor. Placeholder until per-floor
// readings exist; shows the floor's (sample) status from useFloorStatus.

import { notFound, useParams } from "next/navigation";
import { ComingSoon } from "@/components/ui/ComingSoon";
import { StatusShape } from "@/components/sidebar/StatusMark";
import { FLOORS, type FloorId } from "@/lib/navigation";
import { useFloorStatus } from "@/lib/useFloorStatus";
import { useT } from "@/lib/useT";

const RELATED: Record<FloorId, string[]> = {
  fabric: ["qc", "vision", "alerts"],
  cutting: ["production", "alerts"],
  sewing: ["production", "sensors", "alerts"],
  qc: ["qc", "vision", "alerts"],
  packing: ["orders", "alerts"],
};

export default function FloorPage() {
  const params = useParams<{ floor: string }>();
  const floor = FLOORS.find((f) => f.id === params?.floor);
  const { statuses, source } = useFloorStatus();
  const { t } = useT();
  if (!floor) notFound();

  const status = statuses[floor.id];
  return (
    <ComingSoon
      eyebrow={`Floor ${floor.code}`}
      title={t(floor.labelKey)}
      description={t("placeholder.floorBody")}
      meta={
        <>
          <span className="inline-flex items-center gap-2 rounded-md border border-border-subtle px-2.5 py-1 text-caption text-fg-primary">
            <StatusShape status={status} />
            {t(`sidebar.status.${status}`)}
          </span>
          {source === "sample" && <span className="text-caption text-fg-tertiary">{t("placeholder.sampleStatus")}</span>}
        </>
      }
      related={RELATED[floor.id]}
    />
  );
}
