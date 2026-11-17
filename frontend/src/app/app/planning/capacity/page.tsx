"use client";

import { PlanningPage, PL } from "@/components/planning/shared";
import { CapacityView } from "@/components/planning/CapacityView";

export default function Page() {
  return (
    <PlanningPage base={`${PL}/capacity`} title="Capacity planning" description="Estimated line capacity from shifts, SMV, efficiency, operators and machines, with every assumption shown. Incomplete when inputs are missing." testId="planning-capacity">
      {(scope) => <CapacityView scope={scope} />}
    </PlanningPage>
  );
}
