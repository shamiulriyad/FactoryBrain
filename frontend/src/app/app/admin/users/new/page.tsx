"use client";

// Create a login account (POST /api/users). The person signs in with the
// temporary password and must replace it before doing anything else. The
// role list only offers roles the caller may grant; the API re-checks
// everything (roles, factory scope, lines).

import Link from "next/link";
import { useMemo, useState } from "react";
import { useRouter } from "next/navigation";
import { ArrowLeft, Copy, RefreshCw, UserPlus } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Field } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Callout } from "@/components/ui/Callout";
import { useToast } from "@/components/ui/Toast";
import { FactoryPicker, LinePicker, RolePicker, generateTemporaryPassword } from "@/components/access/shared";
import { apiJson, errorMessage, isApiError } from "@/lib/api";
import { useAuthStore } from "@/lib/auth";
import { P, type UserDetail } from "@/lib/access";

export default function NewUserPage() {
  return (
    <RequirePermission perms={[P.UsersCreate]} what="Creating users">
      <NewUserForm />
    </RequirePermission>
  );
}

function NewUserForm() {
  const router = useRouter();
  const toast = useToast();
  const me = useAuthStore((s) => s.user);
  const [email, setEmail] = useState("");
  const [fullName, setFullName] = useState("");
  const [phone, setPhone] = useState("");
  const [password, setPassword] = useState(() => generateTemporaryPassword());
  const [roles, setRoles] = useState<string[]>([]);
  // A factory-level administrator usually has one factory: preselect it.
  const [factoryIds, setFactoryIds] = useState<string[]>(() => (me?.scope !== "Platform" && me?.factories?.length === 1 ? [me.factories[0].id] : []));
  const [lineIds, setLineIds] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});

  const platformRole = roles.includes("SuperAdmin");
  const needsLines = roles.includes("ProductionSupervisor") && !roles.some((r) => !["ProductionSupervisor", "Worker"].includes(r));
  const problems = useMemo(() => {
    const list: string[] = [];
    if (!platformRole && roles.length > 0 && factoryIds.length === 0) list.push("Choose a factory for this user's role.");
    if (needsLines && lineIds.length === 0) list.push("A Production Supervisor needs at least one production line.");
    return list;
  }, [platformRole, roles, factoryIds, lineIds, needsLines]);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (busy || problems.length) return;
    setBusy(true);
    setError(null);
    setFieldErrors({});
    try {
      const created = await apiJson<UserDetail>("/api/users", {
        method: "POST",
        body: {
          email: email.trim(),
          fullName: fullName.trim() || null,
          phone: phone.trim() || null,
          temporaryPassword: password,
          roles,
          factoryIds: platformRole ? [] : factoryIds,
          lineIds: platformRole ? [] : lineIds
        }
      });
      toast.push({ tone: "success", title: "User created", description: `${created.email} must change the temporary password at first sign-in.` });
      router.push(`/app/admin/users/${created.id}`);
    } catch (err) {
      setError(errorMessage(err));
      if (isApiError(err)) setFieldErrors(err.fieldErrors);
    } finally {
      setBusy(false);
    }
  }

  const fe = (k: string) => fieldErrors[k]?.[0] ?? null;

  return (
    <div className="page page-narrow" data-testid="new-user-page">
      <Link href="/app/admin/users" className="inline-flex items-center gap-1 text-caption text-fg-secondary hover:text-fg-primary">
        <ArrowLeft size={13} aria-hidden /> Users
      </Link>
      <PageHeader className="mt-2" eyebrow="Administration" title="New user" description="Create a login account, choose what it can do, and where." />

      <form onSubmit={submit} className="mt-6 space-y-4" noValidate>
        <Panel title="Person" subtitle="Who signs in.">
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Email" error={fe("email")}>
              {({ id, describedBy, invalid }) => (
                <Input id={id} aria-describedby={describedBy} aria-invalid={invalid} type="email" autoComplete="off" required value={email}
                  onChange={(e) => setEmail(e.target.value)} data-testid="user-email" />
              )}
            </Field>
            <Field label="Full name" error={fe("fullName")}>
              {({ id, describedBy, invalid }) => (
                <Input id={id} aria-describedby={describedBy} aria-invalid={invalid} value={fullName} onChange={(e) => setFullName(e.target.value)} data-testid="user-name" />
              )}
            </Field>
            <Field label="Phone" hint="Optional" error={fe("phone")}>
              {({ id, describedBy, invalid }) => (
                <Input id={id} aria-describedby={describedBy} aria-invalid={invalid} inputMode="tel" value={phone} onChange={(e) => setPhone(e.target.value)} />
              )}
            </Field>
            <Field label="Temporary password" hint="Share it securely. It must be changed at first sign-in." error={fe("temporaryPassword")}>
              {({ id, describedBy, invalid }) => (
                <div className="flex gap-2">
                  <Input id={id} aria-describedby={describedBy} aria-invalid={invalid} value={password} onChange={(e) => setPassword(e.target.value)}
                    className="font-mono" autoComplete="new-password" data-testid="user-password" />
                  <Button type="button" variant="secondary" size="sm" aria-label="Generate a new password" onClick={() => setPassword(generateTemporaryPassword())}>
                    <RefreshCw size={13} />
                  </Button>
                  <Button type="button" variant="secondary" size="sm" aria-label="Copy password"
                    onClick={() => void navigator.clipboard?.writeText(password).then(() => toast.push({ tone: "info", title: "Copied" }))}>
                    <Copy size={13} />
                  </Button>
                </div>
              )}
            </Field>
          </div>
        </Panel>

        <Panel title="Roles" subtitle="Only roles you are allowed to grant are listed. One person can hold several roles.">
          <RolePicker value={roles} onChange={setRoles} />
        </Panel>

        {!platformRole && (
          <Panel title="Scope" subtitle="Factory roles act only inside these factories. Production Supervisors also need their lines.">
            <div className="space-y-4">
              <div>
                <p className="mb-2 text-caption font-medium text-fg-secondary">Factories</p>
                <FactoryPicker value={factoryIds} onChange={setFactoryIds} />
              </div>
              {roles.includes("ProductionSupervisor") && (
                <div>
                  <p className="mb-2 text-caption font-medium text-fg-secondary">Production lines</p>
                  <LinePicker factoryIds={factoryIds} value={lineIds} onChange={setLineIds} />
                </div>
              )}
            </div>
          </Panel>
        )}

        {platformRole && (
          <Callout tone="warning" title="Super Admin">
            Super Admins reach every factory and every setting on the platform. Grant this only to platform operators.
          </Callout>
        )}
        {problems.length > 0 && roles.length > 0 && (
          <Callout tone="info">{problems.map((p) => <p key={p}>{p}</p>)}</Callout>
        )}
        {error && <Callout tone="danger" role="alert" title="The user was not created">{error}</Callout>}

        <div className="flex flex-wrap justify-end gap-2">
          <Link href="/app/admin/users"><Button type="button" variant="secondary">Cancel</Button></Link>
          <Button type="submit" variant="primary" iconLeft={<UserPlus size={14} />} disabled={busy || problems.length > 0 || !email.trim()} data-testid="create-user">
            {busy ? "Creating…" : "Create user"}
          </Button>
        </div>
      </form>
    </div>
  );
}
