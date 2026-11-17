"use client";

import { PlanningPage, PL } from "@/components/planning/shared";
import { SetupView } from "@/components/planning/SetupView";

export default function Page() {
  return (
    <PlanningPage base={`${PL}/setup`} title="Planning setup" description="Which counters measure finished garments for each line, and each line's capacity assumptions." testId="planning-setup">
      {(scope) => <SetupView scope={scope} />}
    </PlanningPage>
  );
}
