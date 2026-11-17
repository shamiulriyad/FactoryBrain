"use client";

import { PlanningPage, PL } from "@/components/planning/shared";
import { OrdersView } from "@/components/planning/OrdersView";

export default function Page() {
  return (
    <PlanningPage base={`${PL}/orders`} title="Production orders" description="Register orders, follow them through draft, planned, in progress, on hold and completed, and keep every status change on record." testId="planning-orders">
      {(scope) => <OrdersView scope={scope} />}
    </PlanningPage>
  );
}
