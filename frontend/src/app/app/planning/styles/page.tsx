"use client";

import { PlanningPage, PL } from "@/components/planning/shared";
import { StylesView } from "@/components/planning/StylesView";

export default function Page() {
  return (
    <PlanningPage base={`${PL}/styles`} title="Garment styles" description="Reusable style specifications: size range, colours, complexity, quality checkpoints and the operation sequence with standard minutes and machine types." testId="planning-styles">
      {(scope) => <StylesView scope={scope} />}
    </PlanningPage>
  );
}
