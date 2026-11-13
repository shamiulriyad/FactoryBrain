"use client";

// Account settings for the signed-in user: profile (name, phone), password,
// and a read-only view of their access. Works for every role, and is the
// only page available while a temporary password must be replaced.

import { useEffect, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { KeyRound, Save, ShieldCheck, UserRound } from "lucide-react";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge, RoleBadges } from "@/components/ui/Badge";
import { Field } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { PasswordInput } from "@/components/ui/PasswordInput";
import { Callout } from "@/components/ui/Callout";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { ScopeBadge } from "@/components/access/shared";
import { useResource } from "@/lib/useResource";
import { apiJson, errorMessage, isApiError } from "@/lib/api";
import { refreshSession, useAuthStore } from "@/lib/auth";
import { formatDateTime } from "@/lib/format";
import { useT } from "@/lib/useT";
import type { AccountProfile } from "@/lib/access";

export default function AccountPage() {
  const { locale } = useT();
  const search = useSearchParams();
  const mustChange = useAuthStore((s) => s.user?.mustChangePassword) ?? false;
  const profile = useResource<AccountProfile>("/api/account/profile");
  const focusPassword = mustChange || search?.get("changePassword") === "1";

  return (
    <div className="page page-narrow" data-testid="account-page">
      <PageHeader eyebrow="Account" title="Profile & account" description="Your details, your password and what your account can do." />

      {mustChange && (
        <Callout className="mt-4" tone="warning" title="Choose a new password to continue" role="alert" testId="must-change-password">
          An administrator set a temporary password for this account. Replace it below; the rest of the app opens as soon as you do.
        </Callout>
      )}

      <div className="mt-6 space-y-4">
        <PasswordPanel autoFocus={focusPassword} />
        {profile.loading ? (
          <SkeletonRows rows={4} rowHeight="h-12" />
        ) : profile.error || !profile.data ? (
          <ErrorState title="Couldn't load your profile" body={profile.error ? errorMessage(profile.error) : undefined} retryLabel="Try again" onRetry={() => void profile.reload()} />
        ) : (
          <>
            <ProfilePanel profile={profile.data} onSaved={(p) => profile.setData(p)} />
            <Panel title="Your access" icon={<ShieldCheck size={14} />} subtitle="Granted by your administrator. Ask them for changes.">
              <dl className="grid gap-3 sm:grid-cols-2 text-caption">
                <div><dt className="text-fg-tertiary">Roles</dt><dd className="mt-1"><RoleBadges roles={profile.data.roles} /></dd></div>
                <div><dt className="text-fg-tertiary">Reach</dt><dd className="mt-1"><ScopeBadge scope={profile.data.scope} /></dd></div>
                <div><dt className="text-fg-tertiary">Factories</dt><dd className="mt-1 text-fg-primary">{profile.data.factories.map((f) => `${f.code} · ${f.name}`).join(", ") || (profile.data.scope === "Platform" ? "All factories" : "None")}</dd></div>
                <div><dt className="text-fg-tertiary">Production lines</dt><dd className="mt-1 text-fg-primary">{profile.data.lines.map((l) => l.name).join(", ") || (profile.data.scope === "Line" ? "None" : "All in your factories")}</dd></div>
                <div><dt className="text-fg-tertiary">Worker profile</dt><dd className="mt-1 text-fg-primary">{profile.data.worker ? `${profile.data.worker.employeeCode} · ${profile.data.worker.fullName}` : "Not linked"}</dd></div>
                <div><dt className="text-fg-tertiary">Last sign-in</dt><dd className="mt-1 text-fg-primary">{profile.data.lastLoginAt ? formatDateTime(profile.data.lastLoginAt, locale) : "—"}</dd></div>
              </dl>
              <details className="mt-4 surface-2 px-3 py-2 text-caption">
                <summary className="cursor-pointer select-none text-fg-primary">{profile.data.permissions.length} permissions</summary>
                <ul className="mt-2 flex flex-wrap gap-1.5">
                  {profile.data.permissions.map((p) => <li key={p}><Badge><code className="font-mono">{p}</code></Badge></li>)}
                </ul>
              </details>
            </Panel>
          </>
        )}
      </div>
    </div>
  );
}

function ProfilePanel({ profile, onSaved }: { profile: AccountProfile; onSaved: (p: AccountProfile) => void }) {
  const toast = useToast();
  const [fullName, setFullName] = useState(profile.fullName ?? "");
  const [phone, setPhone] = useState(profile.phone ?? "");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const dirty = fullName !== (profile.fullName ?? "") || phone !== (profile.phone ?? "");

  async function save(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const next = await apiJson<AccountProfile>("/api/account/profile", { method: "PUT", body: { fullName: fullName.trim() || null, phone: phone.trim() || null } });
      onSaved(next);
      void refreshSession(); // the name in the sidebar comes from the session
      toast.push({ tone: "success", title: "Profile saved" });
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Panel title="Profile" icon={<UserRound size={14} />} subtitle="Your email is changed by an administrator.">
      <form onSubmit={save} className="grid gap-4 sm:grid-cols-3">
        <Field label="Email">{({ id }) => <Input id={id} value={profile.email} disabled readOnly />}</Field>
        <Field label="Full name">{({ id }) => <Input id={id} value={fullName} onChange={(e) => setFullName(e.target.value)} maxLength={120} />}</Field>
        <Field label="Phone">{({ id }) => <Input id={id} inputMode="tel" value={phone} onChange={(e) => setPhone(e.target.value)} maxLength={32} />}</Field>
        {error && <Callout className="sm:col-span-3" tone="danger" role="alert">{error}</Callout>}
        <div className="sm:col-span-3 flex justify-end">
          <Button type="submit" variant="primary" iconLeft={<Save size={13} />} disabled={!dirty || busy}>{busy ? "Saving…" : "Save profile"}</Button>
        </div>
      </form>
    </Panel>
  );
}

function PasswordPanel({ autoFocus }: { autoFocus: boolean }) {
  const toast = useToast();
  const router = useRouter();
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [confirm, setConfirm] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});
  const mismatch = confirm.length > 0 && confirm !== next;

  useEffect(() => {
    if (autoFocus) document.getElementById("current-password")?.focus();
  }, [autoFocus]);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (busy || mismatch || !current || next.length < 10) return;
    setBusy(true);
    setError(null);
    setFieldErrors({});
    try {
      await apiJson("/api/account/password", { method: "POST", body: { currentPassword: current, newPassword: next } });
      // The server rotated this session's stamp; refresh to get a token without the temporary-password flag.
      await refreshSession();
      setCurrent("");
      setNext("");
      setConfirm("");
      toast.push({ tone: "success", title: "Password changed", description: "Other sessions were signed out." });
      if (autoFocus) router.replace("/app");
    } catch (err) {
      setError(errorMessage(err));
      if (isApiError(err)) setFieldErrors(err.fieldErrors);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Panel title="Password" icon={<KeyRound size={14} />} subtitle="At least 10 characters. Changing it signs out your other sessions.">
      <form onSubmit={submit} className="grid gap-4 sm:grid-cols-3" data-testid="change-password-form">
        <Field label="Current password" id="current-password" error={fieldErrors.currentPassword?.[0] ?? null}>
          {({ id, describedBy, invalid }) => <PasswordInput id={id} autoComplete="current-password" value={current} onChange={(e) => setCurrent(e.target.value)} aria-describedby={describedBy} aria-invalid={invalid} />}
        </Field>
        <Field label="New password" error={fieldErrors.newPassword?.[0] ?? null}>
          {({ id, describedBy, invalid }) => <PasswordInput id={id} autoComplete="new-password" value={next} onChange={(e) => setNext(e.target.value)} aria-describedby={describedBy} aria-invalid={invalid} data-testid="new-password" />}
        </Field>
        <Field label="Repeat new password" error={mismatch ? "The passwords don't match." : null}>
          {({ id, describedBy, invalid }) => <PasswordInput id={id} autoComplete="new-password" value={confirm} onChange={(e) => setConfirm(e.target.value)} aria-describedby={describedBy} aria-invalid={invalid} />}
        </Field>
        {error && <Callout className="sm:col-span-3" tone="danger" role="alert">{error}</Callout>}
        <div className="sm:col-span-3 flex justify-end">
          <Button type="submit" variant="primary" disabled={busy || mismatch || !current || next.length < 10 || confirm !== next} data-testid="change-password">
            {busy ? "Changing…" : "Change password"}
          </Button>
        </div>
      </form>
    </Panel>
  );
}
