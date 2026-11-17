"use client";

import { PlanningPage, PL } from "@/components/planning/shared";
import { ReportsView } from "@/components/planning/ReportsView";

export default function Page() {
  return (
    <PlanningPage base={`${PL}/reports`} title="Plan vs actual reports" description="Order, line, daily and shift reports of plan versus actual, delivery progress, capacity utilisation, delays and target revisions. Export CSV or PDF." testId="planning-reports">
      {(scope) => <ReportsView scope={scope} />}
    </PlanningPage>
  );
}
