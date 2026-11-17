"use client";

import { PlanningPage, PL } from "@/components/planning/shared";
import { ExceptionsView } from "@/components/planning/ExceptionsView";

export default function Page() {
  return (
    <PlanningPage base={`${PL}/exceptions`} title="Delays and exceptions" description="Rule-based alerts with their evidence: output behind target, delivery at risk, line unavailable, capacity shortage, missing counter data and more." testId="planning-exceptions">
      {(scope) => <ExceptionsView scope={scope} />}
    </PlanningPage>
  );
}
