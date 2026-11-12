"use client";

import { ComingSoon } from "@/components/ui/ComingSoon";
import { useT } from "@/lib/useT";

export default function InventoryPage() {
  const { t } = useT();
  return <ComingSoon title={t("nav.inventory")} description={t("placeholder.inventoryBody")} related={["orders", "production"]} />;
}
