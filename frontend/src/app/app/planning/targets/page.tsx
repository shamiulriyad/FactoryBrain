"use client";

import { PlanningPage, PL } from "@/components/planning/shared";
import { TargetsView } from "@/components/planning/TargetsView";

export default function Page() {
  return (
    <PlanningPage base={`${PL}/targets`} title="Target management" description="Daily, shift and hourly targets at factory, department, floor, line, shift and machine level. Changes become new versions; history is kept." testId="planning-targets">
      {(scope) => <TargetsView scope={scope} />}
    </PlanningPage>
  );
}
