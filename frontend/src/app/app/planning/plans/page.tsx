"use client";

import { PlanningPage, PL } from "@/components/planning/shared";
import { PlansView } from "@/components/planning/PlansView";

export default function Page() {
  return (
    <PlanningPage base={`${PL}/plans`} title="Daily production plans" description="One plan per factory and date: prepare, review, approve and publish. Published plans are visible to supervisors and monitoring operators." testId="planning-plans">
      {(scope) => <PlansView scope={scope} />}
    </PlanningPage>
  );
}
