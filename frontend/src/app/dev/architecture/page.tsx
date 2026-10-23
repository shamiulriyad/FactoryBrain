import { notFound } from "next/navigation";
import { ArchitectureView } from "@/components/dev/ArchitectureView";

/**
 * Internal architecture page — only mounted when NEXT_PUBLIC_SHOW_ARCHITECTURE=true.
 * Rendered as a server component so the env check happens server-side.
 */
export default function DevArchitecturePage() {
  if (process.env.NEXT_PUBLIC_SHOW_ARCHITECTURE !== "true") {
    notFound();
  }
  return <ArchitectureView />;
}