"use client";

// Shared redirect for the legacy onboarding steps (connect / ready /
// understanding). Onboarding was shortened to welcome → profile → /app, but
// the URLs are kept stable for back-compat with stale links from /app. A
// fresh user landing on any of these must pass /app's gate; the defensive
// completeOnboarding() ensures we don't bounce them through Welcome first.

import { redirect } from "next/navigation";
import { businessService } from "@/services/business.service";

export default function LegacyOnboardingRedirect() {
  if (!businessService.isOnboarded()) {
    businessService.completeOnboarding();
  }
  redirect("/app");
}
