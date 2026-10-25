"use client";

import Link from "next/link";
import type { ReactNode } from "react";
import { Lock } from "lucide-react";
import { hasRole, useAuthStore, type RequiredRole } from "@/lib/auth";
import { RoleBadge } from "@/components/ui/Badge";

/** Page shown when the signed-in role is below what a page needs. */
export function AccessDenied({ required, what }: { required: RequiredRole; what?: string }) {
  const user = useAuthStore((s) => s.user);
  return (
    <div className="page page-narrow" data-testid="access-denied">
      <div className="surface p-8 text-center max-w-lg mx-auto mt-6">
        <span className="mx-auto size-12 rounded-full bg-[var(--risk-medium-soft)] border border-[var(--risk-medium-border)] text-[var(--risk-medium)] inline-flex items-center justify-center">
          <Lock size={20} />
        </span>
        <h1 className="mt-4 text-[20px] leading-[28px] font-semibold text-fg-primary">Access denied</h1>
        <p className="mt-2 text-body text-fg-secondary">
          {what ?? "This page"} needs the <strong className="text-fg-primary">{required}</strong> role.
          {user && (
            <>
              {" "}
              You are signed in as <span className="text-fg-primary">{user.email}</span> <RoleBadge role={user.role} />.
            </>
          )}
        </p>
        <p className="mt-2 text-caption text-fg-tertiary">
          An Admin can change your role in Settings → Users. Role changes reach your session within 15 minutes.
        </p>
        <Link href="/app" className="mt-5 inline-flex h-9 items-center px-3.5 rounded-[var(--radius-control)] border border-[var(--btn-secondary-border)] bg-[var(--btn-secondary-bg)] text-body text-fg-primary hover:bg-[var(--btn-secondary-bg-hover)]">
          Back to overview
        </Link>
      </div>
    </div>
  );
}

/**
 * Renders children only for users with at least `role`. This is a
 * convenience for the UI; the API enforces the same policy on every call.
 */
export function RequireRole({ role, what, children }: { role: RequiredRole; what?: string; children: ReactNode }) {
  const user = useAuthStore((s) => s.user);
  if (!hasRole(user, role)) return <AccessDenied required={role} what={what} />;
  return <>{children}</>;
}
