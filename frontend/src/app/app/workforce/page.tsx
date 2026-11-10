"use client";

// /app/workforce — Workforce, HR, Performance & Confidential Wellbeing module.
// Runs on a browser-only demo dataset persisted in localStorage under
// `bunonbrain:workforce:v1` and validated with Zod on read.
// NOTE: Role checks in the UI (Viewer / Operator / Admin) are convenience only
// for this client-side demo dataset; there is no backing server endpoint.

import { useEffect, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { Moon, RotateCcw, Sun } from "lucide-react";
import { PageHeader } from "@/components/ui/PageHeader";
import { Button } from "@/components/ui/Button";
import { DemoDataBanner } from "@/components/ui/Callout";
import { ConfirmDialog } from "@/components/ui/Modal";
import { Tabs } from "@/components/ui/Tabs";
import { SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { canOperate, isAdmin, useAuthStore } from "@/lib/auth";
import { useMounted } from "@/lib/persist";
import { useT } from "@/lib/useT";
import { useWorkforceStore } from "@/store/workforce.store";
import {
  LocalEditChip,
  WORKFORCE_TABS,
  type WorkforceTab
} from "@/components/workforce/shared";
import { OverviewTab } from "@/components/workforce/OverviewTab";
import { WorkersTab } from "@/components/workforce/WorkersTab";
import { AttendanceTab } from "@/components/workforce/AttendanceTab";
import { LeaveOvertimeTab } from "@/components/workforce/LeaveOvertimeTab";
import { PerformanceTab } from "@/components/workforce/PerformanceTab";
import { SkillsTrainingTab } from "@/components/workforce/SkillsTrainingTab";
import { GrievancesTab } from "@/components/workforce/GrievancesTab";
import { WellbeingTab } from "@/components/workforce/WellbeingTab";

export default function WorkforcePage() {
  const { t } = useT();
  const router = useRouter();
  const searchParams = useSearchParams();
  const mounted = useMounted();
  const toast = useToast();

  const user = useAuthStore((s) => s.user);
  const operator = canOperate(user);
  const admin = isAdmin(user);
  const actorEmail = user?.email ?? "operator@bunonbrain.demo";

  const state = useWorkforceStore();
  const [tab, setTab] = useState<WorkforceTab>("overview");
  const [confirmReset, setConfirmReset] = useState(false);

  // Rehydrate from localStorage once mounted on the client.
  useEffect(() => {
    if (mounted) {
      state.rehydrate();
    }
  }, [mounted, state]);

  // Sync URL ?tab=... (including Link clicks from StatCards) into active tab.
  const urlTab = searchParams?.get("tab") as WorkforceTab | null;
  useEffect(() => {
    if (urlTab && WORKFORCE_TABS.includes(urlTab)) {
      setTab(urlTab);
    } else if (!urlTab) {
      setTab("overview");
    }
  }, [urlTab]);

  function navigateToTab(nextTab: WorkforceTab, extraParams?: Record<string, string>) {
    setTab(nextTab);
    const sp = new URLSearchParams();
    if (nextTab !== "overview") sp.set("tab", nextTab);
    if (extraParams) {
      for (const [k, v] of Object.entries(extraParams)) {
        if (v) sp.set(k, v);
      }
    }
    const qs = sp.toString();
    router.replace(qs ? `/app/workforce?${qs}` : "/app/workforce", { scroll: false });
  }

  function syncExtraParams(params: Record<string, string>) {
    const sp = new URLSearchParams(window.location.search);
    if (tab !== "overview") sp.set("tab", tab);
    for (const [k, v] of Object.entries(params)) {
      if (v) sp.set(k, v);
      else sp.delete(k);
    }
    const qs = sp.toString();
    window.history.replaceState(null, "", qs ? `/app/workforce?${qs}` : "/app/workforce");
  }

  const pendingLeavesCount = state.leaveRequests.filter((r) => r.status === "pending").length;
  const openGrievancesCount = state.grievances.filter((g) => g.status !== "resolved").length;

  const lineParam = searchParams?.get("line") ?? "";
  const skillParam = searchParams?.get("skill") ?? "";
  const statusParam = searchParams?.get("status") ?? "";
  const dateParam = searchParams?.get("date") ?? state.anchorDate;
  const attStatusParam = searchParams?.get("attStatus") ?? "";
  const lStatusParam = searchParams?.get("lStatus") ?? "pending";

  return (
    <div className="page" data-testid="workforce-page">
      <DemoDataBanner
        className="mb-4"
        what={t("workforce.demoBanner")}
      />

      <PageHeader
        eyebrow={t("workforce.eyebrow")}
        title={t("workforce.title")}
        description={t("workforce.subtitle")}
        meta={
          state.hasLocalEdits ? (
            <LocalEditChip label={t("workforce.localEditBadge") as string} />
          ) : undefined
        }
        actions={
          <>
            <Button
              size="sm"
              variant="secondary"
              iconLeft={state.ramadanHours ? <Moon size={13} /> : <Sun size={13} />}
              data-testid="workforce-ramadan-header-btn"
              onClick={() => {
                const res = state.toggleRamadanHours(actorEmail);
                toast.push({
                  title: (state.ramadanHours
                    ? t("workforce.ramadanOffToast")
                    : t("workforce.ramadanOnToast")) as string,
                  tone: "info",
                  onUndo: res.undo
                });
              }}
            >
              {t("workforce.ramadanToggle")}: {state.ramadanHours ? "ON" : "OFF"}
            </Button>
            {admin && (
              <Button
                size="sm"
                variant="danger"
                iconLeft={<RotateCcw size={13} />}
                onClick={() => setConfirmReset(true)}
                data-testid="workforce-reset-demo-btn"
              >
                {t("workforce.resetButton")}
              </Button>
            )}
          </>
        }
      />

      <Tabs<WorkforceTab>
        className="mt-5"
        label={t("workforce.tabsAria") as string}
        value={tab}
        onChange={(next) => navigateToTab(next)}
        items={[
          {
            id: "overview",
            label: t("workforce.tabs.overview"),
            testId: "workforce-tab-overview"
          },
          {
            id: "workers",
            label: t("workforce.tabs.workers"),
            count: mounted ? state.workers.filter((w) => w.status !== "resigned").length : undefined,
            testId: "workforce-tab-workers"
          },
          {
            id: "attendance",
            label: t("workforce.tabs.attendance"),
            testId: "workforce-tab-attendance"
          },
          {
            id: "leave",
            label: t("workforce.tabs.leave"),
            count: mounted && pendingLeavesCount > 0 ? pendingLeavesCount : undefined,
            testId: "workforce-tab-leave"
          },
          {
            id: "performance",
            label: t("workforce.tabs.performance"),
            testId: "workforce-tab-performance"
          },
          {
            id: "skills",
            label: t("workforce.tabs.skills"),
            testId: "workforce-tab-skills"
          },
          {
            id: "grievances",
            label: t("workforce.tabs.grievances"),
            count: mounted && openGrievancesCount > 0 ? openGrievancesCount : undefined,
            testId: "workforce-tab-grievances"
          },
          {
            id: "wellbeing",
            label: t("workforce.tabs.wellbeing"),
            testId: "workforce-tab-wellbeing"
          }
        ]}
      />

      <div className="mt-4" role="tabpanel">
        {!mounted ? (
          <SkeletonRows rows={5} rowHeight="h-20" />
        ) : (
          <>
            {tab === "overview" && (
              <OverviewTab
                operator={operator}
                admin={admin}
                actorEmail={actorEmail}
                onNavigate={navigateToTab}
              />
            )}
            {tab === "workers" && (
              <WorkersTab
                operator={operator}
                admin={admin}
                actorEmail={actorEmail}
                initialLine={lineParam}
                initialSkill={skillParam}
                initialStatus={statusParam}
                onSyncUrlParams={syncExtraParams}
              />
            )}
            {tab === "attendance" && (
              <AttendanceTab
                operator={operator}
                actorEmail={actorEmail}
                initialDate={dateParam}
                initialLine={lineParam}
                initialAttStatus={attStatusParam}
                onSyncUrlParams={syncExtraParams}
              />
            )}
            {tab === "leave" && (
              <LeaveOvertimeTab
                operator={operator}
                admin={admin}
                actorEmail={actorEmail}
                initialStatusFilter={lStatusParam}
              />
            )}
            {tab === "performance" && <PerformanceTab admin={admin} />}
            {tab === "skills" && (
              <SkillsTrainingTab
                operator={operator}
                actorEmail={actorEmail}
                onNavigate={navigateToTab}
              />
            )}
            {tab === "grievances" && (
              <GrievancesTab operator={operator} actorEmail={actorEmail} />
            )}
            {tab === "wellbeing" && (
              <WellbeingTab operator={operator} actorEmail={actorEmail} />
            )}
          </>
        )}
      </div>

      {/* Admin-only Reset Demo Data Confirmation Modal */}
      <ConfirmDialog
        open={confirmReset}
        title={t("workforce.resetConfirmTitle")}
        description={t("workforce.resetConfirmDesc")}
        confirmLabel={t("workforce.resetConfirmAction")}
        cancelLabel={t("common.cancel")}
        tone="danger"
        testId="workforce-reset-confirm-modal"
        onCancel={() => setConfirmReset(false)}
        onConfirm={() => {
          setConfirmReset(false);
          state.resetDemoData(actorEmail);
          toast.push({
            title: t("workforce.resetToast") as string,
            tone: "info"
          });
        }}
      />
    </div>
  );
}
