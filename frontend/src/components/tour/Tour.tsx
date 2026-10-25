"use client";

import { useEffect, useState, useMemo } from "react";
import { driver } from "driver.js";
import "driver.js/dist/driver.css";
import { useAppStore } from "@/store/app.store";
import { useT } from "@/lib/useT";
import { useMounted } from "@/lib/persist";

/* Driver.js expects simple objects; we build them at runtime so the
   localisation follows the user's current language. */
function stepsFor(t: (path: string, p?: Record<string, string | number>) => string) {
  return [
    { element: '[data-tour="overview-kpis"]', popover: { title: t("tour.steps.overviewKpis.title"), description: t("tour.steps.overviewKpis.body") } },
    { element: '[data-tour="overview-actions"]', popover: { title: t("tour.steps.overviewActions.title"), description: t("tour.steps.overviewActions.body") } },
    { element: '[data-testid="nav-approvals"]', popover: { title: t("tour.steps.approvals.title"), description: t("tour.steps.approvals.body") } },
    { element: '[data-testid="nav-ask"]', popover: { title: t("tour.steps.ask.title"), description: t("tour.steps.ask.body") } },
    { element: '[data-testid="nav-knowledge"]', popover: { title: t("tour.steps.knowledge.title"), description: t("tour.steps.knowledge.body") } },
    { element: '[data-testid="nav-agents"]', popover: { title: t("tour.steps.agents.title"), description: t("tour.steps.agents.body") } },
    { element: '[data-testid="nav-activity"]', popover: { title: t("tour.steps.activity.title"), description: t("tour.steps.activity.body") } },
    { element: '[data-testid="nav-settings"]', popover: { title: t("tour.steps.settings.title"), description: t("tour.steps.settings.body") } },
    { element: '[data-tour="search"]', popover: { title: t("tour.steps.search.title"), description: t("tour.steps.search.body") } }
  ];
}

export function Tour() {
  const { t, locale } = useT();
  const onboardedAt = useAppStore((s) => s.onboardedAt);
  const tourCompleted = useAppStore((s) => s.tourCompleted);
  const tourDismissed = useAppStore((s) => s.tourDismissed);
  const setTourCompleted = useAppStore((s) => s.setTourCompleted);
  const setTourDismissed = useAppStore((s) => s.setTourDismissed);
  const mounted = useMounted();

  // Force re-build of the driver whenever locale changes; t is the dep proxy.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const steps = useMemo(() => stepsFor(t), [t, locale]);

  useEffect(() => {
    if (!mounted) return;
    // Manual restart hook: anything listening for the event will trigger.
    function onRestart() {
      runTour();
    }
    window.addEventListener("bunonbrain:restart-tour", onRestart);
    return () => window.removeEventListener("bunonbrain:restart-tour", onRestart);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [steps]);

  useEffect(() => {
    if (!mounted) return;
    if (!onboardedAt) return;
    if (tourCompleted || tourDismissed) return;
    // Small delay so the page renders the anchors before highlighting.
    const t1 = setTimeout(runTour, 350);
    return () => clearTimeout(t1);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mounted, onboardedAt, tourCompleted, tourDismissed]);

  function runTour() {
    const d = driver({
      showProgress: true,
      animate: true,
      overlayOpacity: 0.55,
      stagePadding: 6,
      popoverClass: "bunonbrain-tour",
      nextBtnText: t("tour.next"),
      prevBtnText: t("tour.back"),
      doneBtnText: t("tour.done"),
      progressText: t("tour.stepOf", { n: "{{current}}", total: "{{total}}" }),
      onDestroyed: () => {
        setTourCompleted(true);
      },
      onCloseClick: () => {
        setTourDismissed(true);
        d.destroy();
      },
      steps: steps as any
    });
    d.drive();
  }

  return null;
}

/** Imperatively restart the tour from anywhere. */
export function restartTour() {
  if (typeof window !== "undefined") {
    window.dispatchEvent(new CustomEvent("bunonbrain:tour-restart"));
  }
}