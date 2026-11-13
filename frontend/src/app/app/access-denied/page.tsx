"use client";

import { useSearchParams } from "next/navigation";
import { AccessDenied } from "@/components/auth/RequireRole";

/** /app/access-denied?need=a,b&what=… — a linkable "you can't open this" page. */
export default function AccessDeniedPage() {
  const search = useSearchParams();
  const need = (search?.get("need") ?? "").split(",").map((s) => s.trim()).filter(Boolean);
  const what = search?.get("what") ?? undefined;
  return <AccessDenied required={need} what={what} />;
}
