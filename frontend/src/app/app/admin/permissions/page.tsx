"use client";

// Permission overview: every permission by module × the eight roles
// (GET /api/permissions). Read-only; the matrix is defined on the server.

import { useMemo, useState } from "react";
import { Check, KeyRound, Search } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Badge } from "@/components/ui/Badge";
import { FilterSelect } from "@/components/ui/Field";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { useAuthStore } from "@/lib/auth";
import { P, ROLE_IDS, ROLE_LABEL, type PermissionModule } from "@/lib/access";

export default function PermissionsPage() {
  return (
    <RequirePermission perms={[P.RolesView]} what="The permission overview">
      <Matrix />
    </RequirePermission>
  );
}

const SHORT: Record<string, string> = {
  SuperAdmin: "Super",
  FactoryAdmin: "F. Admin",
  FactoryManager: "Manager",
  ProductionSupervisor: "Supervisor",
  MonitoringOperator: "Monitor",
  HrOfficer: "HR",
  DeviceAdmin: "IT / Dev.",
  Worker: "Worker"
};

function Matrix() {
  const modules = useResource<PermissionModule[]>("/api/permissions");
  const mine = new Set(useAuthStore((s) => s.user?.permissions) ?? []);
  const [query, setQuery] = useState("");
  const [module, setModule] = useState("");

  const shown = useMemo(() => {
    const q = query.trim().toLowerCase();
    return (modules.data ?? [])
      .filter((m) => !module || m.module === module)
      .map((m) => ({ ...m, permissions: m.permissions.filter((p) => !q || p.code.includes(q) || p.name.toLowerCase().includes(q) || p.description.toLowerCase().includes(q)) }))
      .filter((m) => m.permissions.length > 0);
  }, [modules.data, query, module]);

  const total = (modules.data ?? []).reduce((n, m) => n + m.permissions.length, 0);

  return (
    <div className="page" data-testid="permissions-page">
      <PageHeader
        eyebrow="Administration"
        title="Permissions"
        description={`${total || "All"} permissions, organised by module, and the roles that grant them. The API checks these on every request.`}
      />
      <div className="mt-4 flex flex-wrap items-center gap-2 text-caption text-fg-secondary">
        <Badge tone="danger">Platform only</Badge> only Super Admins can hold it
        <Badge>Reserved</Badge> defined now, used when its module ships
        <Badge tone="success">You</Badge> you hold it
      </div>

      <Panel className="mt-4" flush title="Matrix" icon={<KeyRound size={14} />}>
        <div className="p-4 flex flex-wrap gap-3 border-b border-border-subtle">
          <div className="relative flex-1 min-w-[200px]">
            <Search size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-fg-tertiary" aria-hidden />
            <input type="search" className="control !h-8 !pl-8 !text-caption" placeholder="Search permissions" aria-label="Search permissions"
              value={query} onChange={(e) => setQuery(e.target.value)} />
          </div>
          <FilterSelect label="Module" value={module} onChange={setModule}>
            <option value="">All modules</option>
            {(modules.data ?? []).map((m) => <option key={m.module} value={m.module}>{m.module}</option>)}
          </FilterSelect>
        </div>
        {modules.loading ? (
          <div className="p-4"><SkeletonRows rows={10} rowHeight="h-9" /></div>
        ) : modules.error ? (
          <ErrorState className="m-4" title="Couldn't load permissions" body={errorMessage(modules.error)} retryLabel="Try again" onRetry={() => void modules.reload()} />
        ) : shown.length === 0 ? (
          <EmptyState className="m-4" title="No permissions match" body="Clear the search or pick another module." />
        ) : (
          <div className="overflow-x-auto">
            <table className="data-table" data-testid="permission-matrix">
              <thead>
                <tr>
                  <th className="min-w-[260px]">Permission</th>
                  {ROLE_IDS.map((r) => <th key={r} className="text-center" title={ROLE_LABEL[r]}>{SHORT[r]}</th>)}
                </tr>
              </thead>
              <tbody>
                {shown.map((m) => (
                  <ModuleRows key={m.module} module={m} mine={mine} />
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Panel>
    </div>
  );
}

function ModuleRows({ module, mine }: { module: PermissionModule; mine: Set<string> }) {
  return (
    <>
      <tr>
        <td colSpan={ROLE_IDS.length + 1} className="!bg-[var(--bg-surface-2)] text-caption font-semibold uppercase tracking-wide text-fg-secondary">{module.module}</td>
      </tr>
      {module.permissions.map((p) => (
        <tr key={p.code} data-testid={`perm-${p.code}`}>
          <td>
            <span className="flex flex-wrap items-center gap-1.5">
              <code className="font-mono text-fg-primary">{p.code}</code>
              {p.platformOnly && <Badge tone="danger">Platform only</Badge>}
              {p.reserved && <Badge>Reserved</Badge>}
              {mine.has(p.code) && <Badge tone="success">You</Badge>}
            </span>
            <span className="mt-0.5 block text-caption text-fg-secondary">{p.name}. {p.description}</span>
          </td>
          {ROLE_IDS.map((r) => (
            <td key={r} className="text-center">
              {p.roles.includes(r) ? (
                <Check size={15} className="inline text-[var(--risk-low)]" aria-label={`${ROLE_LABEL[r]}: yes`} />
              ) : (
                <span className="text-fg-tertiary" aria-label={`${ROLE_LABEL[r]}: no`}>·</span>
              )}
            </td>
          ))}
        </tr>
      ))}
    </>
  );
}
