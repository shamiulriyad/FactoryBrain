"use client";

import { LanguageToggle } from "@/components/ui/LanguageToggle";
import Link from "next/link";

export default function OnboardingLayout({ children }: { children: React.ReactNode }) {
  return (
    <div className="min-h-[100dvh] bg-canvas">
      <header className="flex items-center justify-between px-6 md:px-12 py-5 border-b border-border-subtle">
        <Link href="/" className="flex items-center gap-2">
          <div className="size-7 rounded bg-accent/15 border border-accent/30 flex items-center justify-center">
            <div className="size-2.5 rounded-full bg-accent" />
          </div>
          <span className="text-body font-semibold tracking-tight">Factory Brain</span>
        </Link>
        <LanguageToggle />
      </header>
      <main className="relative z-10">{children}</main>
    </div>
  );
}