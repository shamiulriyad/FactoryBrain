import { redirect } from "next/navigation";

// Reports live in Factory analytics (generation, CSV / PDF export).
export default function ReportsPage() {
  redirect("/app/analytics/reports");
}
