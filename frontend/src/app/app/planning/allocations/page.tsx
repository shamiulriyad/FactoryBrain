"use client";

import { PlanningPage, PL } from "@/components/planning/shared";
import { AllocationsView } from "@/components/planning/AllocationsView";

export default function Page() {
  return (
    <PlanningPage base={`${PL}/allocations`} title="Line allocations" description="Split an order over one or more production lines for a date range. The total can't exceed the order quantity without a recorded override." testId="planning-allocations">
      {(scope) => <AllocationsView scope={scope} />}
    </PlanningPage>
  );
}
