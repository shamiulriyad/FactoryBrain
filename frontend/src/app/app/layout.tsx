"use client";

import { useEffect } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { Sidebar } from "@/components/layout/Sidebar";
import { Topbar } from "@/components/layout/Topbar";
import { CommandPalette } from "@/components/layout/CommandPalette";
import { MobileShell } from "@/components/layout/MobileShell";
import { ToastProvider } from "@/components/ui/Toast";
import { useAppStore } from "@/store/app.store";
import { useBusinessStore } from "@/store/business.store";
import { useMounted } from "@/lib/persist";
import { Tour } from "@/components/tour/Tour";
import { Suspense } from "react";

function Inner({ children }: { children: React.ReactNode }) {
  const router = useRouter();
  const search = useSearchParams();
  const resetRequired = useAppStore((s) => s.resetRequired);
  const applyTheme = useAppStore((s) => s.applyTheme);
  const mounted = useMounted();
  // Subscribe (don't getState()) so the gate re-runs the moment
  // onboardingComplete flips — avoids a Welcome→/app→Welcome bounce
  // under React 18 Strict Mode where a one-render-stale getState() read
  // could redirect right after the click handler set it to true.
  const onboarded = useBusinessStore((s) => s.onboardingComplete);

  useEffect(() => {
    applyTheme();
  }, [applyTheme]);

  useEffect(() => {
    if (!mounted) return;
    if (resetRequired) {
      useAppStore.setState({ resetRequired: false });
      router.replace("/onboarding/welcome");
      return;
    }
    if (!onboarded) {
      router.replace("/onboarding/welcome");
    }
  }, [router, resetRequired, mounted, onboarded]);

  return (
    <div className="flex h-screen w-screen overflow-hidden bg-canvas">
      <div className="hidden md:block h-full">
        <Sidebar />
      </div>
      <div className="flex-1 flex flex-col min-w-0">
        <Topbar />
        <main className="flex-1 overflow-y-auto pb-16 md:pb-0" data-app-route-key={search?.get("focus") ?? undefined}>
          {children}
        </main>
      </div>
      <CommandPalette />
      <MobileShell />
      <Tour />
    </div>
  );
}

export default function AppLayout({ children }: { children: React.ReactNode }) {
  return (
    <ToastProvider>
      <Suspense fallback={<div className="flex h-screen w-screen bg-canvas" />}>
        <Inner>{children}</Inner>
      </Suspense>
    </ToastProvider>
  );
}