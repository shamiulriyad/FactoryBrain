"use client";

// Settings → Users & roles. Shows the signed-in user's own access and, for
// administrators, links to the user / role / permission pages. User
// administration itself lives under /app/admin (the API enforces it there).

import Link from "next/link";
import { useState } from "react";
import { ArrowRight, KeyRound, RefreshCw, ShieldCheck, ShieldHalf, UserCog } from "lucide-react";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { RoleBadges } from "@/components/ui/Badge";
import { useToast } from "@/components/ui/Toast";
import { ScopeBadge } from "@/components/access/shared";
import { can, refreshSession, useAuthStore } from "@/lib/auth";
import { P, roleLabel } from "@/lib/access";
import { cn } from "@/lib/cn";

export function UsersAccessPanel() {
  const toast = useToast();
  const me = useAuthStore((s) => s.user);
  const [checking, setChecking] = useState(false);

  async function checkAccess() {
    if (checking) return;
    const before = JSON.stringify(useAuthStore.getState().user?.permissions ?? []);
    setChecking(true);
    // A refresh re-reads roles and scope from the database and issues a new token.
    const ok = await refreshSession();
    setChecking(false);
    const after = JSON.stringify(useAuthStore.getState().user?.permissions ?? []);
    if (!ok) toast.push({ tone: "error", title: "Could not check your access", description: "Your session may have ended; sign in again." });
    else if (after !== before) toast.push({ title: "Access updated", description: "Menus and actions now match your new roles." });
    else toast.push({ tone: "info", title: "No change", description: "Your access is the same." });
  }

  if (!me) return null;
  const links = [
    { href: "/app/admin/users", icon: UserCog, title: "Users", body: "Create accounts, assign roles and factories, deactivate, reset passwords.", show: can(me, P.UsersView) },
    { href: "/app/admin/roles", icon: ShieldHalf, title: "Roles", body: "The eight system roles, what each allows and who holds them.", show: can(me, P.RolesView) },
    { href: "/app/admin/permissions", icon: KeyRound, title: "Permissions", body: "Every permission by module, and which roles grant it.", show: can(me, P.RolesView) }
  ].filter((l) => l.show);

  return (
    <div className="space-y-4">
      <Panel title="Your access" subtitle="What the server lets this account do." icon={<ShieldCheck size={14} />} testId="my-access">
        <div className="flex flex-wrap items-center justify-between gap-3 surface-2 p-3.5">
          <div className="min-w-0">
            <p className="text-caption text-fg-tertiary">Signed in as</p>
            <p className="text-body font-medium text-fg-primary truncate">{me.email}</p>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <span data-testid="my-role"><RoleBadges roles={me.roles ?? []} /></span>
            <ScopeBadge scope={me.scope} />
            <Button variant="secondary" size="sm" onClick={() => void checkAccess()} disabled={checking} data-testid="check-role">
              <RefreshCw size={13} className={cn(checking && "animate-spin")} /> {checking ? "Checking…" : "Check for changes"}
            </Button>
          </div>
        </div>
        <dl className="mt-3 grid gap-3 sm:grid-cols-3 text-caption">
          <div>
            <dt className="text-fg-tertiary">Factories</dt>
            <dd className="text-fg-primary">{me.factories?.length ? me.factories.map((f) => f.name).join(", ") : me.scope === "Platform" ? "All" : "None"}</dd>
          </div>
          <div>
            <dt className="text-fg-tertiary">Production lines</dt>
            <dd className="text-fg-primary">{me.scope === "Line" ? me.lineIds.join(", ") || "None" : "All in your factories"}</dd>
          </div>
          <div>
            <dt className="text-fg-tertiary">Permissions</dt>
            <dd className="text-fg-primary">{me.permissions?.length ?? 0}</dd>
          </div>
        </dl>
        {(me.roles?.length ?? 0) === 0 && (
          <p className="mt-3 text-caption text-[var(--risk-medium)]">
            This account has no role yet. Ask your Factory Admin to assign one; new access reaches you straight away.
          </p>
        )}
        <p className="mt-3 text-caption text-fg-secondary">
          Details, password and profile: <Link href="/app/account" className="text-accent hover:underline">Account settings</Link>.
          {me.roles?.length ? ` Your roles: ${me.roles.map(roleLabel).join(", ")}.` : ""}
        </p>
      </Panel>

      {links.length > 0 && (
        <Panel title="Administration" subtitle="User, role and permission management." icon={<UserCog size={14} />}>
          <ul className="grid gap-2 md:grid-cols-3">
            {links.map((l) => (
              <li key={l.href}>
                <Link href={l.href} className="group surface-2 p-3.5 flex h-full flex-col gap-1.5 border border-transparent hover:border-border-subtle transition-colors">
                  <span className="flex items-center gap-2 text-body font-medium text-fg-primary">
                    <l.icon size={15} className="text-accent" aria-hidden /> {l.title}
                    <ArrowRight size={13} className="ml-auto text-fg-tertiary group-hover:translate-x-0.5 transition-transform" aria-hidden />
                  </span>
                  <span className="text-caption text-fg-secondary">{l.body}</span>
                </Link>
              </li>
            ))}
          </ul>
        </Panel>
      )}
    </div>
  );
}
