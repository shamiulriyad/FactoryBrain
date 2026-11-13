"use client";

// User management: everyone the API lets the caller see (a Factory Admin:
// their factory; a Super Admin: everyone). Search, filters, sorting and
// pagination run on the server (GET /api/users).

import Link from "next/link";
import { useEffect, useMemo, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { ArrowDown, ArrowUp, RefreshCw, Search, UserPlus, Users } from "lucide-react";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { RoleBadges } from "@/components/ui/Badge";
import { FilterSelect } from "@/components/ui/Field";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { Pagination, StatusBadge } from "@/components/access/shared";
import { useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { useCan } from "@/lib/auth";
import { formatDate, formatRelative } from "@/lib/format";
import { useT } from "@/lib/useT";
import { cn } from "@/lib/cn";
import { P, type FactoryDto, type PagedResult, type RoleDto, type UserSummary } from "@/lib/access";

const PAGE_SIZE = 20;
type SortKey = "email" | "name" | "created" | "lastLogin";

export default function UsersPage() {
  const { locale } = useT();
  const router = useRouter();
  const search = useSearchParams();
  const canCreate = useCan(P.UsersCreate);

  const q = search?.get("q") ?? "";
  const role = search?.get("role") ?? "";
  const factoryId = search?.get("factory") ?? "";
  const status = search?.get("status") ?? "";
  const sort = search?.get("sort") ?? "email";
  const page = Math.max(1, Number(search?.get("page")) || 1);

  const [draft, setDraft] = useState(q);
  useEffect(() => setDraft(q), [q]);

  function setParams(next: Record<string, string | null>, resetPage = true) {
    const params = new URLSearchParams(search?.toString() ?? "");
    for (const [k, v] of Object.entries(next)) {
      if (v) params.set(k, v);
      else params.delete(k);
    }
    if (resetPage) params.delete("page");
    router.replace(`/app/admin/users${params.toString() ? `?${params}` : ""}`);
  }

  // Debounced search.
  useEffect(() => {
    if (draft === q) return;
    const id = window.setTimeout(() => setParams({ q: draft.trim() || null }), 300);
    return () => window.clearTimeout(id);
  }, [draft]); // eslint-disable-line react-hooks/exhaustive-deps

  const path = useMemo(() => {
    const p = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE), sort });
    if (q) p.set("search", q);
    if (role) p.set("role", role);
    if (factoryId) p.set("factoryId", factoryId);
    if (status) p.set("status", status);
    return `/api/users?${p}`;
  }, [page, sort, q, role, factoryId, status]);

  const users = useResource<PagedResult<UserSummary>>(path);
  const roles = useResource<RoleDto[]>("/api/roles");
  const factories = useResource<FactoryDto[]>("/api/factories");
  const filtered = !!(q || role || factoryId || status);

  function toggleSort(key: SortKey) {
    const current = sort.replace(/^-/, "");
    setParams({ sort: current === key && !sort.startsWith("-") ? `-${key}` : key });
  }

  function SortHeader({ k, children, className }: { k: SortKey; children: React.ReactNode; className?: string }) {
    const active = sort.replace(/^-/, "") === k;
    const desc = sort.startsWith("-");
    return (
      <th className={className} aria-sort={active ? (desc ? "descending" : "ascending") : "none"}>
        <button type="button" onClick={() => toggleSort(k)} className="inline-flex items-center gap-1 hover:text-fg-primary">
          {children}
          {active && (desc ? <ArrowDown size={11} aria-hidden /> : <ArrowUp size={11} aria-hidden />)}
        </button>
      </th>
    );
  }

  return (
    <div className="page" data-testid="users-page">
      <PageHeader
        eyebrow="Administration"
        title="Users"
        description="Login accounts, their roles and the factories and lines they can reach. Worker profiles are separate; a worker needs no login."
        actions={
          canCreate && (
            <Link href="/app/admin/users/new">
              <Button variant="primary" iconLeft={<UserPlus size={14} />} data-testid="new-user">New user</Button>
            </Link>
          )
        }
      />

      <Panel
        className="mt-6"
        flush
        title="Accounts"
        icon={<Users size={14} />}
        subtitle={users.data ? `${users.data.total} ${users.data.total === 1 ? "account" : "accounts"}` : undefined}
        right={
          <Button variant="secondary" size="sm" onClick={() => void users.reload()} disabled={users.refreshing} aria-label="Refresh users">
            <RefreshCw size={13} className={cn(users.refreshing && "animate-spin")} />
          </Button>
        }
      >
        <div className="p-4 flex flex-wrap items-center gap-3 border-b border-border-subtle">
          <div className="relative flex-1 min-w-[200px]">
            <Search size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-fg-tertiary" aria-hidden />
            <input
              type="search"
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              placeholder="Search name or email"
              aria-label="Search users"
              className="control !h-8 !pl-8 !text-caption"
              data-testid="users-search"
            />
          </div>
          <FilterSelect label="Role" value={role} onChange={(v) => setParams({ role: v || null })} testId="users-role-filter">
            <option value="">All roles</option>
            {(roles.data ?? []).map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
            <option value="none">No role</option>
          </FilterSelect>
          {(factories.data?.length ?? 0) > 1 && (
            <FilterSelect label="Factory" value={factoryId} onChange={(v) => setParams({ factory: v || null })}>
              <option value="">All factories</option>
              {(factories.data ?? []).map((f) => <option key={f.id} value={f.id}>{f.code} · {f.name}</option>)}
            </FilterSelect>
          )}
          <FilterSelect label="Status" value={status} onChange={(v) => setParams({ status: v || null })} testId="users-status-filter">
            <option value="">Any status</option>
            <option value="active">Active</option>
            <option value="inactive">Deactivated</option>
            <option value="must_change_password">Temporary password</option>
          </FilterSelect>
          {filtered && (
            <Button size="sm" variant="ghost" onClick={() => { setDraft(""); setParams({ q: null, role: null, factory: null, status: null }); }}>
              Clear filters
            </Button>
          )}
        </div>

        {users.loading ? (
          <div className="p-4"><SkeletonRows rows={6} rowHeight="h-11" /></div>
        ) : users.error && !users.data ? (
          <ErrorState title="Couldn't load users" body={errorMessage(users.error)} retryLabel="Try again" onRetry={() => void users.reload()} className="m-4" />
        ) : (users.data?.items.length ?? 0) === 0 ? (
          <EmptyState
            className="m-4"
            icon={<Users size={18} />}
            title={filtered ? "No users match" : "No users yet"}
            body={filtered ? "Change or clear the filters." : canCreate ? "Create the first account with New user." : "Accounts in your factory will appear here."}
          />
        ) : (
          <>
            <div className="overflow-x-auto" data-testid="users-table">
              <table className="data-table">
                <thead>
                  <tr>
                    <SortHeader k="name">Name</SortHeader>
                    <th>Roles</th>
                    <th>Factories</th>
                    <th>Status</th>
                    <SortHeader k="lastLogin">Last sign-in</SortHeader>
                    <SortHeader k="created">Created</SortHeader>
                  </tr>
                </thead>
                <tbody>
                  {users.data!.items.map((u) => (
                    <tr key={u.id} data-testid="user-row" data-email={u.email} className="cursor-pointer" onClick={() => router.push(`/app/admin/users/${u.id}`)}>
                      <td>
                        <Link href={`/app/admin/users/${u.id}`} className="block min-w-0" onClick={(e) => e.stopPropagation()}>
                          <span className="block text-fg-primary font-medium truncate">{u.fullName || u.email}</span>
                          {u.fullName && <span className="block text-caption text-fg-tertiary truncate">{u.email}</span>}
                        </Link>
                      </td>
                      <td><RoleBadges roles={u.roles} /></td>
                      <td className="text-caption text-fg-secondary">
                        {u.factories.length ? u.factories.map((f) => f.code).join(", ") : <span className="text-fg-tertiary">—</span>}
                        {u.lineCount > 0 && <span className="block text-fg-tertiary">{u.lineCount} line{u.lineCount === 1 ? "" : "s"}</span>}
                      </td>
                      <td><StatusBadge active={u.isActive} mustChangePassword={u.mustChangePassword} /></td>
                      <td className="whitespace-nowrap text-caption text-fg-secondary">{u.lastLoginAt ? formatRelative(u.lastLoginAt, locale) : "Never"}</td>
                      <td className="whitespace-nowrap text-caption text-fg-secondary">{formatDate(u.createdAt, locale)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Pagination page={users.data!.page} pageSize={users.data!.pageSize} total={users.data!.total} onPage={(p) => setParams({ page: String(p) }, false)} />
          </>
        )}
      </Panel>
    </div>
  );
}
