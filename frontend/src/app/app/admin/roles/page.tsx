"use client";

// The eight system roles (GET /api/roles): what each is for, its reach, who
// may grant it, how many people in your scope hold it, and its permissions
// grouped by module (GET /api/permissions). System roles are protected:
// nobody edits them through the app.

import Link from "next/link";
import { useMemo, useState } from "react";
import { ChevronDown, Lock, ShieldHalf, Users } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Badge, RoleBadge } from "@/components/ui/Badge";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { ScopeBadge } from "@/components/access/shared";
import { useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { useCan } from "@/lib/auth";
import { cn } from "@/lib/cn";
import { P, roleLabel, type PermissionModule, type RoleDto } from "@/lib/access";

export default function RolesPage() {
  return (
    <RequirePermission perms={[P.RolesView]} what="Role management">
      <Roles />
    </RequirePermission>
  );
}

function Roles() {
  const roles = useResource<RoleDto[]>("/api/roles");
  const modules = useResource<PermissionModule[]>("/api/permissions");
  const canSeeUsers = useCan(P.UsersView);
  const [open, setOpen] = useState<string | null>(null);

  const byCode = useMemo(() => {
    const map = new Map<string, { module: string; name: string; reserved: boolean; platformOnly: boolean }>();
    for (const m of modules.data ?? []) for (const p of m.permissions) map.set(p.code, { module: m.module, name: p.name, reserved: p.reserved, platformOnly: p.platformOnly });
    return map;
  }, [modules.data]);

  return (
    <div className="page" data-testid="roles-page">
      <PageHeader
        eyebrow="Administration"
        title="Roles"
        description="Predefined system roles. A role's reach is limited by the factories and lines assigned to each user. Roles are granted on the user's page."
        actions={<Link href="/app/admin/permissions" className="text-caption text-accent hover:underline">Permission matrix →</Link>}
      />
      <p className="mt-3 inline-flex items-center gap-1.5 text-caption text-fg-tertiary">
        <Lock size={12} aria-hidden /> System roles are protected and can&apos;t be edited or deleted.
      </p>

      {roles.loading ? (
        <div className="mt-6"><SkeletonRows rows={8} rowHeight="h-16" /></div>
      ) : roles.error ? (
        <ErrorState className="mt-6" title="Couldn't load roles" body={errorMessage(roles.error)} retryLabel="Try again" onRetry={() => void roles.reload()} />
      ) : (
        <ul className="mt-6 space-y-3">
          {(roles.data ?? []).map((r) => {
            const expanded = open === r.id;
            const grouped = groupBy(r.permissions, (code) => byCode.get(code)?.module ?? "Other");
            return (
              <li key={r.id}>
                <Panel>
                  <button
                    type="button"
                    className="w-full flex flex-wrap items-start gap-3 text-left"
                    aria-expanded={expanded}
                    onClick={() => setOpen(expanded ? null : r.id)}
                    data-testid={`role-${r.id}`}
                  >
                    <span className="min-w-0 flex-1">
                      <span className="flex flex-wrap items-center gap-2">
                        <RoleBadge role={r.id} />
                        <ScopeBadge scope={r.scope} />
                        {r.canGrant && <Badge tone="success">You can grant</Badge>}
                      </span>
                      <span className="mt-2 block text-body text-fg-secondary">{r.description}</span>
                      <span className="mt-1 block text-caption text-fg-tertiary">
                        Granted by: {r.managedBy.map(roleLabel).join(", ")}
                      </span>
                    </span>
                    <span className="flex items-center gap-4 text-caption text-fg-secondary">
                      <span className="text-right"><span className="block text-[18px] font-semibold text-fg-primary tabular-nums">{r.permissions.length}</span>permissions</span>
                      <span className="text-right"><span className="block text-[18px] font-semibold text-fg-primary tabular-nums">{r.userCount}</span>holders</span>
                      <ChevronDown size={16} className={cn("transition-transform", expanded && "rotate-180")} aria-hidden />
                    </span>
                  </button>
                  {expanded && (
                    <div className="mt-4 border-t border-border-subtle pt-4 space-y-3">
                      {Object.entries(grouped).map(([module, codes]) => (
                        <div key={module}>
                          <p className="text-caption font-medium text-fg-secondary">{module}</p>
                          <ul className="mt-1.5 flex flex-wrap gap-1.5">
                            {codes.map((c) => {
                              const info = byCode.get(c);
                              return (
                                <li key={c}>
                                  <Badge tone={info?.platformOnly ? "danger" : info?.reserved ? "neutral" : "info"} title={info?.reserved ? "Reserved for an upcoming module" : info?.name}>
                                    <code className="font-mono">{c}</code>
                                  </Badge>
                                </li>
                              );
                            })}
                          </ul>
                        </div>
                      ))}
                      {canSeeUsers && (
                        <Link href={`/app/admin/users?role=${r.id}`} className="inline-flex items-center gap-1.5 text-caption text-accent hover:underline">
                          <Users size={12} aria-hidden /> Users with this role
                        </Link>
                      )}
                    </div>
                  )}
                </Panel>
              </li>
            );
          })}
        </ul>
      )}

      <Panel className="mt-6" title="How access is decided" icon={<ShieldHalf size={14} />}>
        <ul className="list-disc pl-5 space-y-1 text-caption text-fg-secondary">
          <li>Every API request is checked on the server against the user&apos;s permissions and their factory / line scope.</li>
          <li>A user may hold several roles; their permissions add up, and their reach is the widest of their roles.</li>
          <li>Super Admins grant every role. Factory Admins grant the six factory roles below them, and only inside their own factory.</li>
          <li>Nobody can grant a role with permissions they don&apos;t hold, change their own roles, or touch a Super Admin unless they are one.</li>
          <li>Role, scope and status changes reach the user&apos;s session at once, and every change is written to the audit log.</li>
        </ul>
      </Panel>
    </div>
  );
}

function groupBy<T>(items: T[], key: (t: T) => string): Record<string, T[]> {
  const out: Record<string, T[]> = {};
  for (const it of items) (out[key(it)] ??= []).push(it);
  return out;
}
