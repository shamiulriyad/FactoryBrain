// Architecture has moved to /dev/architecture (server-only, gated by NEXT_PUBLIC_SHOW_ARCHITECTURE).
// This stub returns a 404 so any old link no longer surfaces the page in the customer build.
import { notFound } from "next/navigation";

export default function ArchitecturePage() {
  notFound();
}