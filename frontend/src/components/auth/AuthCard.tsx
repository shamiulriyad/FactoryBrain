"use client";

import { BrandMark } from "@/components/brand/BrandMark";
import { ShieldCheck, ScrollText, Activity } from "lucide-react";

/** Shared frame for the sign-in, sign-up and password pages. */
export function AuthCard({
  title,
  subtitle,
  children,
  footer,
  notice
}: {
  title: string;
  subtitle?: string;
  children: React.ReactNode;
  footer?: React.ReactNode;
  /** Message shown above the form (e.g. "your session expired"). */
  notice?: React.ReactNode;
}) {
  return (
    <div className="w-full max-w-[400px]">
      <div className="lg:hidden mb-6 flex justify-center">
        <BrandMark size={36} framed withWordmark />
      </div>
      <div className="surface p-6 sm:p-7 flex flex-col gap-5">
        <div>
          <h1 className="text-[22px] leading-[28px] font-semibold tracking-tight text-fg-primary">{title}</h1>
          {subtitle && <p className="mt-1.5 text-body text-fg-secondary">{subtitle}</p>}
        </div>
        {notice}
        {children}
      </div>
      {footer && <div className="mt-5 text-center text-caption text-fg-secondary">{footer}</div>}
    </div>
  );
}

const POINTS = [
  { icon: Activity, title: "Live floor view", body: "Line board, sensors, QC and floor alerts from the API." },
  { icon: ShieldCheck, title: "Role-checked actions", body: "Every decision is checked by the server against your role." },
  { icon: ScrollText, title: "Audited changes", body: "Approvals, settings and role changes are written to an append-only log." }
];

/** Full-page layout used by every auth page: brand panel + form. */
export function AuthPage({ children }: { children: React.ReactNode }) {
  return (
    <div className="min-h-[100dvh] grid lg:grid-cols-[minmax(0,1fr)_minmax(0,1.1fr)] bg-canvas">
      <aside className="hidden lg:flex flex-col justify-between bg-[var(--bench)] text-[var(--bench-fg)] p-10 xl:p-14">
        <BrandMark size={34} framed withWordmark className="[&_*]:!text-white" />
        <div className="max-w-md">
          <p className="text-[30px] leading-[38px] font-semibold tracking-tight text-white">
            The operations layer for your garment floor.
          </p>
          <ul className="mt-8 space-y-5">
            {POINTS.map((p) => (
              <li key={p.title} className="flex gap-3">
                <span className="mt-0.5 size-8 shrink-0 rounded-lg bg-white/10 text-[var(--accent)] inline-flex items-center justify-center">
                  <p.icon size={16} />
                </span>
                <span>
                  <span className="block text-body font-semibold text-white">{p.title}</span>
                  <span className="block text-caption text-[var(--bench-muted)]">{p.body}</span>
                </span>
              </li>
            ))}
          </ul>
        </div>
        <p className="text-caption text-[var(--bench-muted)]">Sign-in uses a short-lived token kept in memory and an HttpOnly refresh cookie.</p>
      </aside>
      <main className="flex items-center justify-center px-4 py-10">{children}</main>
    </div>
  );
}

/** Same shape the API's validator accepts: something@something.tld, no spaces. */
export const EMAIL_SHAPE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

export const linkClass = "text-accent font-medium hover:underline underline-offset-2";
