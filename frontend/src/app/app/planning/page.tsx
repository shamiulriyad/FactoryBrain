"use client";

import { PlanningPage, PL } from "@/components/planning/shared";
import { MonitorView } from "@/components/planning/MonitorView";

export default function Page() {
  return (
    <PlanningPage base={PL} title="Production monitoring" description="Today's published plan against verified output by line and shift, order progress, delivery forecasts, capacity warnings and open exceptions." testId="planning-monitor">
      {(scope) => <MonitorView scope={scope} />}
    </PlanningPage>
  );
}
