"use client";

// Everyone who can sign in (GET /api/users, Admin). Self-registered accounts
// start as Viewer; an Admin promotes them here (PATCH /api/users/{id}/role).
// The API refuses changing your own role and demoting the last Admin, and
// audits every change.

import { useMemo, useState } from "react";
import { KeyRound, RefreshCw, Search, ShieldCheck, Users } from "lucide-react";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { RoleBadge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { FilterSelect } from "@/components/ui/Field";
import { ConfirmDialog } from "@/components/ui/Modal";
import { SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { apiJson, errorMessage } from "@/lib/api";
import { useResource } from "@/lib/useResource";
import { refreshSession, useAuthStore } from "@/lib/auth";
import { formatDate } from "@/lib/format";
import { useT } from "@/lib/useT";
import { cn } from "@/lib/cn";
import type { Role, UserRow } from "@/services/contracts";

const ROLES: Role[] = ["Viewer", "Operator", "Admin"];
const ROLE_HELP: Record<Role, string> = {
  Viewer: "Read everything, ask questions.",
  Operator: "Plus: run agents, decide low/medium approvals, flag QC, acknowledge alerts.",
  Admin: "Plus: high-risk approvals, AI settings, knowledge base, users, audit log."
};

export function UsersPanel() {
  const { t, locale } = useT();
  const toast = useToast();
  const me = useAuthStore((s) => s.user);
  const users = useResource<UserRow[]>("/api/users");
  const [busyId, setBusyId] = useState<string | null>(null);
  const [notice, setNotice] = useState<{ text: string; error: boolean } | null>(null);
  const [query, setQuery] = useState("");
  const [roleFilter, setRoleFilter] = useState("");
  /** A change that grants or removes Admin waits for an explicit confirm. */
  const [pending, setPending] = useState<{ user: UserRow; role: Role } | null>(null);

  const rows = useMemo(() => {
    const q = query.trim().toLowerCase();
    return (users.data ?? []).filter((u) => (!q || u.email.toLowerCase().includes(q)) && (!roleFilter || u.role === roleFilter));
  }, [users.data, query, roleFilter]);

  async function changeRole(user: UserRow, role: Role) {
    if (busyId) return;
    setBusyId(user.id);
    setNotice(null);
    try {
      const updated = await apiJson<UserRow>(`/api/users/${encodeURIComponent(user.id)}/role`, { method: "PATCH", body: { role } });
      users.setData((cur) => (cur ? cur.map((u) => (u.id === updated.id ? updated : u)) : cur));
      setNotice({ text: t("settings.usersSaved"), error: false });
      toast.push({ title: "Role updated", description: `${updated.email} is now ${updated.role}.` });
    } catch (e) {
      setNotice({ text: errorMessage(e), error: true });
      await users.reload();
    } finally {
      setBusyId(null);
      setPending(null);
    }
  }

  return (
    <Panel
      title={t("settings.usersSection")}
      subtitle={t("settings.usersBody")}
      icon={<Users size={14} />}
      flush
      right={
        <Button variant="secondary" size="sm" onClick={() => void users.reload()} disabled={users.refreshing} aria-label="Refresh users">
          <RefreshCw size={13} className={cn(users.refreshing && "animate-spin")} />
        </Button>
      }
    >
      <div className="p-4 md:p-5 space-y-3">
        <ul className="grid gap-2 md:grid-cols-3 text-caption">
          {ROLES.map((r) => (
            <li key={r} className="surface-2 p-2.5">
              <RoleBadge role={r} />
              <p className="mt-1.5 text-fg-secondary">{ROLE_HELP[r]}</p>
            </li>
          ))}
        </ul>
        <p className="text-caption text-fg-tertiary">
          You can&apos;t change your own role, and the last Admin can&apos;t be demoted. A new role reaches that person&apos;s session
          within 15 minutes, or straight away when they press <em>Check for role changes</em> in Settings → Users &amp; roles.
        </p>
        <div className="flex flex-wrap items-center gap-3">
          <div className="relative flex-1 min-w-[180px]">
            <Search size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-fg-tertiary" aria-hidden />
            <input
              type="search"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder="Search by email"
              aria-label="Search users"
              className="control !h-8 !pl-8 !text-caption"
            />
          </div>
          <FilterSelect label="Role" value={roleFilter} onChange={setRoleFilter}>
            <option value="">All roles</option>
            {ROLES.map((r) => <option key={r} value={r}>{r}</option>)}
          </FilterSelect>
        </div>
        {users.error && (
          <Callout tone="danger" title={t("settings.usersAdminOnly")}>{errorMessage(users.error)}</Callout>
        )}
        {notice && (
          <p role={notice.error ? "alert" : "status"} aria-live="polite" data-testid="users-notice" className={cn("text-caption", notice.error ? "text-[var(--risk-high)]" : "text-[var(--risk-low)]")}>
            {notice.text}
          </p>
        )}
      </div>

      {users.loading ? (
        <div className="px-4 pb-4"><SkeletonRows rows={4} rowHeight="h-10" /></div>
      ) : (
        <div className="overflow-x-auto border-t border-border-subtle" data-testid="users-table">
          <table className="data-table">
            <thead>
              <tr>
                <th>{t("settings.usersEmail")}</th>
                <th>{t("settings.usersRole")}</th>
                <th>{t("settings.usersSince")}</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((u) => {
                const self = u.id === me?.id;
                return (
                  <tr key={u.id} data-testid="user-row" data-email={u.email}>
                    <td className="text-fg-primary">
                      {u.email}
                      {self && <span className="ml-2 text-fg-tertiary">({t("settings.usersYou")})</span>}
                    </td>
                    <td>
                      <select
                        aria-label={`${t("settings.usersRole")}: ${u.email}`}
                        value={u.role}
                        disabled={self || busyId !== null}
                        title={self ? "You can't change your own role. Ask another Admin." : undefined}
                        onChange={(e) => {
                          const role = e.target.value as Role;
                          if (role === "Admin" || u.role === "Admin") setPending({ user: u, role });
                          else void changeRole(u, role);
                        }}
                        className="control !h-8 !w-36 !text-caption"
                      >
                        {ROLES.map((r) => <option key={r} value={r}>{r}</option>)}
                      </select>
                    </td>
                    <td className="whitespace-nowrap">{formatDate(u.createdAt, locale)}</td>
                  </tr>
                );
              })}
              {rows.length === 0 && (
                <tr><td colSpan={3} className="text-center text-fg-tertiary py-6">No users match.</td></tr>
              )}
            </tbody>
          </table>
        </div>
      )}

      <ConfirmDialog
        open={!!pending}
        title={pending?.role === "Admin" ? "Make this person an Admin?" : "Remove Admin access?"}
        description={
          pending?.role === "Admin"
            ? "Admins can change AI settings and API keys, manage the knowledge base, decide high-risk approvals, read the audit log and change roles."
            : `They become ${pending?.role} and lose access to settings, users and the audit log. The server refuses this if they are the last Admin.`
        }
        confirmLabel={pending?.role === "Admin" ? "Make Admin" : `Make ${pending?.role}`}
        tone={pending?.role === "Admin" ? "primary" : "danger"}
        busy={!!pending && busyId === pending.user.id}
        onConfirm={() => pending && void changeRole(pending.user, pending.role)}
        onCancel={() => setPending(null)}
        testId="role-confirm"
      >
        <p className="text-body text-fg-primary">{pending?.user.email}</p>
      </ConfirmDialog>
    </Panel>
  );
}

/**
 * Settings → Users & roles for non-Admins: who you are signed in as, what
 * your role allows, and how to get more access. Lists nobody else — the
 * user list is Admin-only on the server.
 */
export function MyAccessPanel() {
  const toast = useToast();
  const me = useAuthStore((s) => s.user);
  const [checking, setChecking] = useState(false);

  async function checkRole() {
    if (checking) return;
    const before = useAuthStore.getState().user?.role;
    setChecking(true);
    // A refresh re-reads the role from the database and issues a new token.
    const ok = await refreshSession();
    setChecking(false);
    const after = useAuthStore.getState().user?.role;
    if (!ok) toast.push({ tone: "error", title: "Could not check your role", description: "Your session may have ended; sign in again." });
    else if (after !== before) toast.push({ title: "Role updated", description: `You are now ${after}. Menus and actions have been updated.` });
    else toast.push({ tone: "info", title: "No change", description: `You are still ${after}.` });
  }

  if (!me) return null;
  return (
    <Panel title="Users & roles" subtitle="Your access. Only an Admin can see the user list and change roles." icon={<ShieldCheck size={14} />} testId="my-access">
      <div className="flex flex-wrap items-center justify-between gap-3 surface-2 p-3.5">
        <div className="min-w-0">
          <p className="text-caption text-fg-tertiary">Signed in as</p>
          <p className="text-body font-medium text-fg-primary truncate">{me.email}</p>
        </div>
        <div className="flex items-center gap-2">
          <span data-testid="my-role"><RoleBadge role={me.role} /></span>
          <Button variant="secondary" size="sm" onClick={() => void checkRole()} disabled={checking} data-testid="check-role">
            <RefreshCw size={13} className={cn(checking && "animate-spin")} /> {checking ? "Checking…" : "Check for role changes"}
          </Button>
        </div>
      </div>
      <ul className="mt-4 grid gap-2 md:grid-cols-3 text-caption">
        {ROLES.map((r) => (
          <li key={r} className={cn("surface-2 p-2.5", r === me.role && "!border-[var(--accent-border)]")}>
            <RoleBadge role={r} />
            {r === me.role && <span className="ml-2 text-fg-tertiary">your role</span>}
            <p className="mt-1.5 text-fg-secondary">{ROLE_HELP[r]}</p>
          </li>
        ))}
      </ul>
      <div className="mt-4 space-y-2 text-caption text-fg-secondary">
        <p>
          <strong className="text-fg-primary">Need more access?</strong> Ask an Admin to change your role in Settings → Users &amp; roles,
          then press <em>Check for role changes</em>. New accounts always start as Viewer, and nobody can change their own role.
        </p>
        <details className="surface-2 px-3 py-2">
          <summary className="cursor-pointer select-none text-fg-primary inline-flex items-center gap-1.5">
            <KeyRound size={12} /> No Admin exists yet?
          </summary>
          <p className="mt-2">
            Whoever runs the API server can make the first Admin: set <span className="font-mono">SEED_ADMIN_EMAIL</span> to an account&apos;s
            email and <span className="font-mono">SEED_ADMIN_PASSWORD</span> to <em>that account&apos;s current password</em>, then restart the API.
            The account is promoted to Admin (recorded in the audit log); with a wrong password nothing changes. An email with no account yet
            creates a new Admin instead.
          </p>
        </details>
      </div>
    </Panel>
  );
}

