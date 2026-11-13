"use client";

import { LanguageToggle } from "@/components/ui/LanguageToggle";
import Link from "next/link";
import { Logo } from "@/components/brand/Logo";

export default function OnboardingLayout({ children }: { children: React.ReactNode }) {
  return (
    <div className="min-h-[100dvh] bg-canvas">
      <header className="flex items-center justify-between px-6 md:px-12 py-5 border-b border-border-subtle">
        <Link href="/" className="inline-flex min-h-11 items-center" aria-label="BunonBrain home">
          <Logo height={30} tone="auto" />
        </Link>
        <LanguageToggle />
      </header>
      <main className="relative z-10">{children}</main>
    </div>
  );
}