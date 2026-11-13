"use client";

// One user: profile, roles & scope, activity (audit) and security
// (activate / deactivate, reset password). What can be changed comes from
// the server (`can` in GET /api/users/{id}); the API re-checks every change.

import Link from "next/link";
import { useEffect, useState } from "react";
import { useParams, useRouter, useSearchParams } from "next/navigation";
import { ArrowLeft, Ban, CheckCircle2, Copy, History, KeyRound, RefreshCw, Save, ShieldHalf, UserRound } from "lucide-react";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge, RoleBadges } from "@/components/ui/Badge";
import { Field } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Callout } from "@/components/ui/Callout";
import { Tabs } from "@/components/ui/Tabs";
import { ConfirmDialog } from "@/components/ui/Modal";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { FactoryPicker, LinePicker, RolePicker, ScopeBadge, StatusBadge, generateTemporaryPassword } from "@/components/access/shared";
import { useResource } from "@/lib/useResource";
import { apiJson, errorMessage } from "@/lib/api";
import { useCan } from "@/lib/auth";
import { formatDateTime, formatRelative } from "@/lib/format";
import { useT } from "@/lib/useT";
import { P, roleLabel, type UserDetail } from "@/lib/access";
import type { AuditEntry } from "@/services/contracts";

type TabId = "profile" | "access" | "activity" | "security";

export default function UserDetailPage() {
  const params = useParams<{ id: string }>();
  const id = params?.id ?? "";
  const router = useRouter();
  const search = useSearchParams();
  const { locale } = useT();
  const user = useResource<UserDetail>(id ? `/api/users/${encodeURIComponent(id)}` : null);
  const canAudit = useCan(P.AuditView);
  const tab = (search?.get("tab") as TabId) || "profile";

  function setTab(next: TabId) {
    router.replace(`/app/admin/users/${id}?tab=${next}`);
  }

  if (user.loading) return <div className="page"><SkeletonRows rows={6} rowHeight="h-12" /></div>;
  if (user.error || !user.data) {
    return (
      <div className="page page-narrow">
        <BackLink />
        <ErrorState className="mt-4" title="User not found" body={user.error ? errorMessage(user.error) : "This account doesn't exist or is outside your scope."}
          retryLabel="Back to users" onRetry={() => router.push("/app/admin/users")} />
      </div>
    );
  }
  const u = user.data;
  const tabs = [
    { id: "profile" as const, label: "Profile" },
    { id: "access" as const, label: "Roles & scope" },
    ...(canAudit ? [{ id: "activity" as const, label: "Activity" }] : []),
    { id: "security" as const, label: "Security" }
  ];

  return (
    <div className="page" data-testid="user-detail">
      <BackLink />
      <PageHeader
        className="mt-2"
        eyebrow="User"
        title={u.fullName || u.email}
        description={u.fullName ? u.email : undefined}
        meta={
          <div className="flex flex-wrap items-center gap-2">
            <StatusBadge active={u.isActive} mustChangePassword={u.mustChangePassword} />
            <RoleBadges roles={u.roles} />
            <ScopeBadge scope={u.scope} />
          </div>
        }
        actions={
          <Button variant="secondary" size="sm" onClick={() => void user.reload()} aria-label="Reload">
            <RefreshCw size={13} />
          </Button>
        }
      />

      <dl className="mt-4 grid gap-3 sm:grid-cols-4 text-caption">
        <Fact label="Last sign-in">{u.lastLoginAt ? formatRelative(u.lastLoginAt, locale) : "Never"}</Fact>
        <Fact label="Created">{formatDateTime(u.createdAt, locale)}</Fact>
        <Fact label="Factories">{u.factories.length ? u.factories.map((f) => f.name).join(", ") : u.scope === "Platform" ? "All (platform)" : "None"}</Fact>
        <Fact label="Worker profile">{u.worker ? `${u.worker.employeeCode} · ${u.worker.fullName}` : "Not linked"}</Fact>
      </dl>

      {!u.can.edit && !u.can.assignRoles && !u.can.changeStatus && (
        <Callout className="mt-4" tone="info" title="Read only">
          You can view this account but not change it: it holds a role you don&apos;t manage, or it is your own account (use Account settings).
        </Callout>
      )}

      <Tabs className="mt-6" label="User sections" items={tabs} value={tab} onChange={setTab} />
      <div className="mt-4">
        {tab === "profile" && <ProfileTab user={u} onSaved={(next) => user.setData(next)} />}
        {tab === "access" && <AccessTab user={u} onSaved={(next) => user.setData(next)} />}
        {tab === "activity" && canAudit && <ActivityTab userId={u.id} />}
        {tab === "security" && <SecurityTab user={u} onSaved={(next) => user.setData(next)} />}
      </div>
    </div>
  );
}

function BackLink() {
  return (
    <Link href="/app/admin/users" className="inline-flex items-center gap-1 text-caption text-fg-secondary hover:text-fg-primary">
      <ArrowLeft size={13} aria-hidden /> Users
    </Link>
  );
}

function Fact({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="surface-2 p-3">
      <dt className="text-fg-tertiary">{label}</dt>
      <dd className="mt-0.5 text-fg-primary">{children}</dd>
    </div>
  );
}

function ProfileTab({ user, onSaved }: { user: UserDetail; onSaved: (u: UserDetail) => void }) {
  const toast = useToast();
  const [email, setEmail] = useState(user.email);
  const [fullName, setFullName] = useState(user.fullName ?? "");
  const [phone, setPhone] = useState(user.phone ?? "");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const dirty = email !== user.email || fullName !== (user.fullName ?? "") || phone !== (user.phone ?? "");

  async function save(e: React.FormEvent) {
    e.preventDefault();
    if (!dirty || busy) return;
    setBusy(true);
    setError(null);
    try {
      const next = await apiJson<UserDetail>(`/api/users/${user.id}`, { method: "PUT", body: { email: email.trim(), fullName: fullName.trim() || null, phone: phone.trim() || null } });
      onSaved(next);
      toast.push({ tone: "success", title: "Profile saved" });
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Panel title="Profile" subtitle={user.can.edit ? "Name and contact details." : "Only an administrator who manages this account can edit it."} icon={<UserRound size={14} />}>
      <form onSubmit={save} className="grid gap-4 sm:grid-cols-3">
        <Field label="Email">
          {({ id }) => <Input id={id} type="email" value={email} onChange={(e) => setEmail(e.target.value)} disabled={!user.can.edit} />}
        </Field>
        <Field label="Full name">
          {({ id }) => <Input id={id} value={fullName} onChange={(e) => setFullName(e.target.value)} disabled={!user.can.edit} data-testid="edit-name" />}
        </Field>
        <Field label="Phone">
          {({ id }) => <Input id={id} inputMode="tel" value={phone} onChange={(e) => setPhone(e.target.value)} disabled={!user.can.edit} />}
        </Field>
        {error && <Callout className="sm:col-span-3" tone="danger" role="alert">{error}</Callout>}
        {user.can.edit && (
          <div className="sm:col-span-3 flex justify-end">
            <Button type="submit" variant="primary" iconLeft={<Save size={13} />} disabled={!dirty || busy} data-testid="save-profile">
              {busy ? "Saving…" : "Save"}
            </Button>
          </div>
        )}
      </form>
    </Panel>
  );
}

function AccessTab({ user, onSaved }: { user: UserDetail; onSaved: (u: UserDetail) => void }) {
  const toast = useToast();
  const [roles, setRoles] = useState<string[]>(user.roles);
  const [factoryIds, setFactoryIds] = useState<string[]>(user.factories.map((f) => f.id));
  const [lineIds, setLineIds] = useState<string[]>(user.lines.map((l) => l.id));
  const [confirm, setConfirm] = useState<"roles" | "scope" | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setRoles(user.roles);
    setFactoryIds(user.factories.map((f) => f.id));
    setLineIds(user.lines.map((l) => l.id));
  }, [user]);

  const same = (a: string[], b: string[]) => a.length === b.length && a.every((x) => b.includes(x));
  const rolesDirty = !same(roles, user.roles);
  const scopeDirty = !same(factoryIds, user.factories.map((f) => f.id)) || !same(lineIds, user.lines.map((l) => l.id));
  const added = roles.filter((r) => !user.roles.includes(r));
  const removed = user.roles.filter((r) => !roles.includes(r));

  async function apply(kind: "roles" | "scope") {
    setBusy(true);
    setError(null);
    try {
      const next = kind === "roles"
        ? await apiJson<UserDetail>(`/api/users/${user.id}/roles`, { method: "PUT", body: { roles } })
        : await apiJson<UserDetail>(`/api/users/${user.id}/scope`, { method: "PUT", body: { factoryIds, lineIds } });
      onSaved(next);
      toast.push({ tone: "success", title: kind === "roles" ? "Roles updated" : "Scope updated", description: "The change applies to their session immediately." });
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setBusy(false);
      setConfirm(null);
    }
  }

  return (
    <div className="space-y-4">
      <Panel
        title="Roles"
        icon={<ShieldHalf size={14} />}
        subtitle={user.can.assignRoles ? "Grant or remove the roles you manage. The user can't change their own roles." : "You can't change this user's roles."}
        right={user.can.assignRoles && (
          <Button size="sm" variant="primary" disabled={!rolesDirty || busy} onClick={() => setConfirm("roles")} data-testid="save-roles">Save roles</Button>
        )}
      >
        {user.can.assignRoles ? (
          <RolePicker value={roles} onChange={setRoles} grantable={user.can.grantableRoles} />
        ) : (
          <RoleBadges roles={user.roles} />
        )}
      </Panel>

      {user.scope !== "Platform" && (
        <Panel
          title="Factories & lines"
          subtitle="Where the roles apply. Production Supervisors only reach the lines chosen here."
          right={user.can.changeScope && (
            <Button size="sm" variant="primary" disabled={!scopeDirty || busy} onClick={() => setConfirm("scope")} data-testid="save-scope">Save scope</Button>
          )}
        >
          <div className="space-y-4">
            <div>
              <p className="mb-2 text-caption font-medium text-fg-secondary">Factories</p>
              {user.can.changeScope ? <FactoryPicker value={factoryIds} onChange={setFactoryIds} /> : (
                <p className="text-body text-fg-primary">{user.factories.map((f) => f.name).join(", ") || "None"}</p>
              )}
            </div>
            <div>
              <p className="mb-2 text-caption font-medium text-fg-secondary">Production lines</p>
              {user.can.changeScope ? <LinePicker factoryIds={factoryIds} value={lineIds} onChange={setLineIds} /> : (
                <p className="text-body text-fg-primary">{user.lines.map((l) => l.name).join(", ") || "None"}</p>
              )}
            </div>
          </div>
        </Panel>
      )}

      <Panel title="Effective permissions" subtitle={`${user.permissions.length} permissions from ${user.roles.length ? user.roles.map(roleLabel).join(", ") : "no role"}.`}>
        {user.permissions.length === 0 ? (
          <p className="text-caption text-fg-tertiary">No permissions. The user can sign in and manage their own account only.</p>
        ) : (
          <ul className="flex flex-wrap gap-1.5">
            {user.permissions.map((p) => <li key={p}><Badge><code className="font-mono">{p}</code></Badge></li>)}
          </ul>
        )}
      </Panel>

      {error && <Callout tone="danger" role="alert" title="Not saved">{error}</Callout>}

      <ConfirmDialog
        open={confirm === "roles"}
        title="Change roles?"
        description="The user's current session picks up the new access immediately."
        confirmLabel="Change roles"
        tone={removed.length > 0 ? "danger" : "primary"}
        busy={busy}
        onConfirm={() => void apply("roles")}
        onCancel={() => setConfirm(null)}
        testId="roles-confirm"
      >
        <ul className="space-y-1 text-body">
          {added.map((r) => <li key={r} className="text-[var(--risk-low)]">+ {roleLabel(r)}</li>)}
          {removed.map((r) => <li key={r} className="text-[var(--risk-high)]">− {roleLabel(r)}</li>)}
        </ul>
      </ConfirmDialog>
      <ConfirmDialog
        open={confirm === "scope"}
        title="Change factories and lines?"
        description="The user loses access to anything outside the new scope at once."
        confirmLabel="Change scope"
        busy={busy}
        onConfirm={() => void apply("scope")}
        onCancel={() => setConfirm(null)}
      />
    </div>
  );
}

function ActivityTab({ userId }: { userId: string }) {
  const { locale } = useT();
  const activity = useResource<AuditEntry[]>(`/api/users/${userId}/activity?limit=100`);
  if (activity.loading) return <SkeletonRows rows={5} rowHeight="h-10" />;
  if (activity.error) return <ErrorState title="Couldn't load activity" body={errorMessage(activity.error)} retryLabel="Try again" onRetry={() => void activity.reload()} />;
  if (!activity.data?.length) return <EmptyState icon={<History size={18} />} title="No activity yet" body="Sign-ins and changes to this account appear here." />;
  return (
    <Panel title="Activity" subtitle="Sign-ins and every change made by or to this account (newest first)." icon={<History size={14} />} flush>
      <div className="overflow-x-auto">
        <table className="data-table" data-testid="user-activity">
          <thead>
            <tr><th>When</th><th>Action</th><th>By</th><th>Change</th></tr>
          </thead>
          <tbody>
            {activity.data.map((e) => (
              <tr key={e.id}>
                <td className="whitespace-nowrap text-caption">{formatDateTime(e.occurredAt, locale)}</td>
                <td><Badge tone={/deactivated/.test(e.action) ? "danger" : /created|activated/.test(e.action) ? "success" : /role|scope|password/.test(e.action) ? "warning" : "neutral"}><code className="font-mono">{e.action}</code></Badge></td>
                <td className="text-caption">{e.actorEmail}<span className="block text-fg-tertiary">{roleLabel(e.actorRole)}</span></td>
                <td className="text-caption text-fg-secondary max-w-[420px]">
                  {(e.fromState || e.toState) && <span className="block font-mono">{e.fromState ?? "—"} → {e.toState ?? "—"}</span>}
                  {e.reason}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </Panel>
  );
}

function SecurityTab({ user, onSaved }: { user: UserDetail; onSaved: (u: UserDetail) => void }) {
  const toast = useToast();
  const [statusOpen, setStatusOpen] = useState(false);
  const [reason, setReason] = useState("");
  const [resetOpen, setResetOpen] = useState(false);
  const [temp, setTemp] = useState(() => generateTemporaryPassword());
  const [issued, setIssued] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function changeStatus() {
    setBusy(true);
    setError(null);
    try {
      const next = await apiJson<UserDetail>(`/api/users/${user.id}/status`, { method: "POST", body: { isActive: !user.isActive, reason: reason.trim() || null } });
      onSaved(next);
      toast.push({ tone: "success", title: next.isActive ? "Account reactivated" : "Account deactivated", description: next.isActive ? "They can sign in again." : "All their sessions ended." });
      setReason("");
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setBusy(false);
      setStatusOpen(false);
    }
  }

  async function resetPassword() {
    setBusy(true);
    setError(null);
    try {
      const next = await apiJson<UserDetail>(`/api/users/${user.id}/reset-password`, { method: "POST", body: { temporaryPassword: temp } });
      onSaved(next);
      setIssued(temp);
      setTemp(generateTemporaryPassword());
      toast.push({ tone: "success", title: "Temporary password set", description: "Their sessions ended. They must change it at next sign-in." });
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setBusy(false);
      setResetOpen(false);
    }
  }

  return (
    <div className="space-y-4">
      <Panel title="Sign-in status" icon={user.isActive ? <CheckCircle2 size={14} /> : <Ban size={14} />}
        subtitle={user.isActive ? "Active: the person can sign in." : `Deactivated${user.deactivatedAt ? ` since ${formatDateTime(user.deactivatedAt)}` : ""}: sign-in and sessions are blocked.`}>
        {user.can.changeStatus ? (
          <Button variant={user.isActive ? "danger" : "success"} onClick={() => setStatusOpen(true)} data-testid="toggle-status">
            {user.isActive ? "Deactivate account" : "Reactivate account"}
          </Button>
        ) : (
          <p className="text-caption text-fg-tertiary">You can&apos;t change this account&apos;s status.</p>
        )}
      </Panel>

      <Panel title="Password" icon={<KeyRound size={14} />} subtitle="Set a temporary password. Every session ends and the person must choose a new password at next sign-in.">
        {user.can.resetPassword ? (
          <div className="space-y-3">
            <Button variant="secondary" onClick={() => setResetOpen(true)} data-testid="reset-password">Reset password…</Button>
            {issued && (
              <Callout tone="success" title="Temporary password (shown once)">
                <div className="flex flex-wrap items-center gap-2">
                  <code className="font-mono text-body">{issued}</code>
                  <Button size="sm" variant="secondary" iconLeft={<Copy size={12} />} onClick={() => void navigator.clipboard?.writeText(issued)}>Copy</Button>
                </div>
                <p className="mt-1">Give it to the person over a trusted channel. It isn&apos;t stored anywhere you can read again.</p>
              </Callout>
            )}
          </div>
        ) : (
          <p className="text-caption text-fg-tertiary">You can&apos;t reset this account&apos;s password.</p>
        )}
      </Panel>

      {error && <Callout tone="danger" role="alert">{error}</Callout>}

      <ConfirmDialog
        open={statusOpen}
        title={user.isActive ? `Deactivate ${user.email}?` : `Reactivate ${user.email}?`}
        description={user.isActive ? "They are signed out everywhere at once and can't sign in until reactivated. Their history is kept." : "They can sign in again with their current password."}
        confirmLabel={user.isActive ? "Deactivate" : "Reactivate"}
        tone={user.isActive ? "danger" : "success"}
        busy={busy}
        onConfirm={() => void changeStatus()}
        onCancel={() => setStatusOpen(false)}
        testId="status-confirm"
      >
        {user.isActive && (
          <Field label="Reason (recorded in the audit log)">
            {({ id }) => <Input id={id} value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} placeholder="e.g. left the company" />}
          </Field>
        )}
      </ConfirmDialog>
      <ConfirmDialog
        open={resetOpen}
        title="Set a temporary password?"
        description="Every session of this user ends now."
        confirmLabel="Set password"
        tone="danger"
        busy={busy}
        confirmDisabled={temp.length < 10}
        onConfirm={() => void resetPassword()}
        onCancel={() => setResetOpen(false)}
        testId="reset-confirm"
      >
        <Field label="Temporary password" hint="At least 10 characters.">
          {({ id }) => (
            <div className="flex gap-2">
              <Input id={id} className="font-mono" value={temp} onChange={(e) => setTemp(e.target.value)} />
              <Button type="button" size="sm" variant="secondary" aria-label="Generate" onClick={() => setTemp(generateTemporaryPassword())}><RefreshCw size={13} /></Button>
            </div>
          )}
        </Field>
      </ConfirmDialog>
    </div>
  );
}
