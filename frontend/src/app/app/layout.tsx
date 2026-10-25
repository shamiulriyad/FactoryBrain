"use client";

import { useCallback, useEffect, useState, Suspense } from "react";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { Sidebar } from "@/components/layout/Sidebar";
import { Topbar } from "@/components/layout/Topbar";
import { CommandPalette } from "@/components/layout/CommandPalette";
import { MobileNav } from "@/components/layout/MobileShell";
import { ToastProvider } from "@/components/ui/Toast";
import { BrandMark } from "@/components/brand/BrandMark";
import { useAppStore } from "@/store/app.store";
import { useBusinessStore } from "@/store/business.store";
import { useMounted } from "@/lib/persist";
import { Tour } from "@/components/tour/Tour";
import { refreshSession, useAuthStore } from "@/lib/auth";
import { useT } from "@/lib/useT";

function Inner({ children }: { children: React.ReactNode }) {
  const router = useRouter();
  const search = useSearchParams();
  const resetRequired = useAppStore((s) => s.resetRequired);
  const applyTheme = useAppStore((s) => s.applyTheme);
  const mounted = useMounted();
  // Subscribe (don't getState()) so the gate re-runs the moment
  // onboardingComplete flips — avoids a Welcome→/app→Welcome bounce.
  const onboarded = useBusinessStore((s) => s.onboardingComplete);
  const pathname = usePathname();
  const authStatus = useAuthStore((s) => s.status);
  const endedReason = useAuthStore((s) => s.endedReason);
  const { t } = useT();
  const [navOpen, setNavOpen] = useState(false);
  const closeNav = useCallback(() => setNavOpen(false), []);

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

  // Session gate. Every /api call needs a signed-in user (the API enforces
  // it); on first load try the refresh cookie, otherwise go to /login.
  useEffect(() => {
    if (authStatus === "unknown") void refreshSession();
  }, [authStatus]);

  useEffect(() => {
    if (authStatus !== "anonymous") return;
    const next = `${pathname ?? "/app"}${search?.toString() ? `?${search.toString()}` : ""}`;
    const reason = endedReason === "expired" ? "&reason=expired" : "";
    router.replace(`/login?next=${encodeURIComponent(next)}${reason}`);
  }, [authStatus, endedReason, pathname, search, router]);

  if (authStatus !== "authenticated") {
    return (
      <div className="flex h-screen w-screen flex-col items-center justify-center gap-3 bg-canvas" role="status" aria-live="polite">
        <BrandMark size={36} framed />
        <p className="text-caption text-fg-tertiary">{t("auth.checking")}</p>
      </div>
    );
  }

  return (
    <div className="flex h-[100dvh] w-full overflow-hidden bg-canvas">
      <a
        href="#main"
        className="sr-only focus:not-sr-only focus:fixed focus:left-3 focus:top-3 focus:z-[70] focus:rounded-md focus:bg-[var(--bg-surface)] focus:px-3 focus:py-2 focus:text-body focus:shadow"
      >
        Skip to content
      </a>
      <div className="hidden md:block h-full">
        <Sidebar />
      </div>
      <MobileNav open={navOpen} onClose={closeNav} />
      <div className="flex-1 flex flex-col min-w-0">
        <Topbar onOpenNav={() => setNavOpen(true)} />
        <main id="main" tabIndex={-1} className="flex-1 overflow-y-auto focus:outline-none" data-app-route-key={search?.get("focus") ?? undefined}>
          {children}
        </main>
      </div>
      <CommandPalette />
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
