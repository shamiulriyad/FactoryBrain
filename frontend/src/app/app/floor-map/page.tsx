"use client";

import { ComingSoon } from "@/components/ui/ComingSoon";
import { useT } from "@/lib/useT";

export default function FloorMapPage() {
  const { t } = useT();
  return <ComingSoon title={t("nav.floorMap")} description={t("placeholder.floorMapBody")} related={["overview", "alerts", "production"]} />;
}
