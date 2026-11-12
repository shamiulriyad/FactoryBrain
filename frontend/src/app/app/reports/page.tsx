"use client";

import { ComingSoon } from "@/components/ui/ComingSoon";
import { useT } from "@/lib/useT";

export default function ReportsPage() {
  const { t } = useT();
  return <ComingSoon title={t("nav.reports")} description={t("placeholder.reportsBody")} related={["production", "qc", "orders"]} />;
}
