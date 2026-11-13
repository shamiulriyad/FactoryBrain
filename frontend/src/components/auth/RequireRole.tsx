"use client";

import Link from "next/link";
import type { ReactNode } from "react";
import { Lock, KeyRound } from "lucide-react";
import { can, useAuthStore } from "@/lib/auth";
import { RoleBadges } from "@/components/ui/Badge";

/**
 * Access denied. Shown when the signed-in user lacks every permission a page
 * (or the URL they opened) needs. The API refuses the same requests; this
 * just explains it instead of showing empty panels.
 */
export function AccessDenied({ required, what }: { required?: string[]; what?: string }) {
  const user = useAuthStore((s) => s.user);
  return (
    <div className="page page-narrow" data-testid="access-denied">
      <div className="surface p-8 text-center max-w-lg mx-auto mt-6">
        <span className="mx-auto size-12 rounded-full bg-[var(--risk-medium-soft)] border border-[var(--risk-medium-border)] text-[var(--risk-medium)] inline-flex items-center justify-center">
          <Lock size={20} aria-hidden />
        </span>
        <h1 className="mt-4 text-[20px] leading-[28px] font-semibold text-fg-primary">Access denied</h1>
        <p className="mt-2 text-body text-fg-secondary">
          {what ?? "This page"} is not available with your current access.
        </p>
        {required && required.length > 0 && (
          <p className="mt-2 text-caption text-fg-tertiary">
            Needs {required.length > 1 ? "one of" : "the permission"}{" "}
            {required.map((p, i) => (
              <span key={p}>
                {i > 0 && " or "}
                <code className="font-mono text-fg-secondary">{p}</code>
              </span>
            ))}
            .
          </p>
        )}
        {user && (
          <div className="mt-4 flex flex-wrap items-center justify-center gap-2 text-caption text-fg-secondary">
            <span>Signed in as {user.email}</span>
            <RoleBadges roles={user.roles ?? []} />
          </div>
        )}
        <p className="mt-3 text-caption text-fg-tertiary">
          Your administrator grants roles in Administration → Users. New access reaches your session straight away.
        </p>
        <div className="mt-5 flex flex-wrap justify-center gap-2">
          <Link href="/app" className="inline-flex h-9 items-center px-3.5 rounded-[var(--radius-control)] border border-[var(--btn-secondary-border)] bg-[var(--btn-secondary-bg)] text-body text-fg-primary hover:bg-[var(--btn-secondary-bg-hover)]">
            Go to my home page
          </Link>
          <Link href="/app/account" className="inline-flex h-9 items-center gap-1.5 px-3.5 rounded-[var(--radius-control)] text-body text-fg-secondary hover:text-fg-primary">
            <KeyRound size={14} aria-hidden /> My access
          </Link>
        </div>
      </div>
    </div>
  );
}

/** Renders children only for users holding at least one of `perms`. Convenience; the API enforces the same. */
export function RequirePermission({ perms, what, children }: { perms: string[]; what?: string; children: ReactNode }) {
  const user = useAuthStore((s) => s.user);
  if (!can(user, ...perms)) return <AccessDenied required={perms} what={what} />;
  return <>{children}</>;
}
