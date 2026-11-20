"use client";

// Interactive demo entry: sign in once with the shared demo credential, then
// pick any of the platform's roles. The list comes from the API (every system
// role has a demo account behind it); choosing one signs the browser in as that
// role's account and opens its normal dashboard. Coming back here switches role
// without signing in again. Only works on a demo deployment (DEMO_MODE=true).

import { Suspense, useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { ArrowRight, LogIn, LogOut, RefreshCw } from "lucide-react";
import {
  demoLogin,
  demoSwitchRole,
  fetchDemoInfo,
  fetchDemoSession,
  logout,
  type DemoInfo,
  type DemoSession
} from "@/lib/auth";
import { useBusinessStore } from "@/store/business.store";
import { useAppStore } from "@/store/app.store";
import { Button } from "@/components/ui/Button";
import { Input } from "@/components/ui/Input";
import { PasswordInput } from "@/components/ui/PasswordInput";
import { Callout } from "@/components/ui/Callout";
import { AuthCard, AuthPage, EMAIL_SHAPE } from "@/components/auth/AuthCard";
import { BrandMark } from "@/components/brand/BrandMark";

/** Only same-app paths are accepted as a destination (no open redirect). */
function safeNext(raw: string | null): string {
  if (!raw || !raw.startsWith("/app") || raw.startsWith("//")) return "/app";
  return raw;
}

const SCOPE_LABEL: Record<string, string> = {
  Platform: "Every factory",
  Factory: "Whole factory",
  Line: "Assigned lines",
  Self: "Own records"
};

function formatTime(iso?: string | null): string {
  if (!iso) return "";
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? "" : d.toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
}

/** The first visit to /app shows onboarding and a tour; a reviewer should land on the dashboard. */
function skipFirstVisitScreens() {
  useBusinessStore.getState().completeOnboarding();
  useAppStore.getState().setTourDismissed(true);
}

function DemoLogin({ info, onSignedIn }: { info: DemoInfo; onSignedIn: (s: DemoSession) => void }) {
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!EMAIL_SHAPE.test(email.trim())) return setError("Enter the demo email address.");
    if (!password) return setError("Enter the demo password.");
    setSubmitting(true);
    setError(null);
    const r = await demoLogin(email.trim(), password);
    setSubmitting(false);
    if (r.ok) {
      setPassword("");
      onSignedIn(r.session);
      return;
    }
    setError(
      r.reason === "invalid" ? "That is not the demo email and password."
        : r.reason === "preparing" ? "The demo data is being prepared. Try again in a minute."
        : r.reason === "busy" ? "Too many reviewers are using the demo right now. Try again shortly."
        : r.reason === "rate_limited" ? `Too many attempts. Try again in ${r.retryAfterSeconds ?? 60} seconds.`
        : "The demo can't be reached right now. Try again shortly."
    );
  }

  return (
    <AuthCard
      title="Explore the demo"
      subtitle="Sign in once with the demo credentials, then try every role in a sample garment factory."
      notice={
        !info.ready ? (
          <Callout tone="warning" role="status">
            The demo data is being prepared. Sign-in opens in a minute.
          </Callout>
        ) : null
      }
      footer={<Link href="/" className="text-accent font-medium hover:underline underline-offset-2">Back to the website</Link>}
    >
      <form onSubmit={submit} noValidate className="flex flex-col gap-4" data-testid="demo-login-form">
        <label className="flex flex-col">
          <span className="field-label">Demo email</span>
          <Input type="email" autoComplete="username" required value={email} onChange={(e) => setEmail(e.target.value)} />
        </label>
        <label className="flex flex-col">
          <span className="field-label">Demo password</span>
          <PasswordInput autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} />
        </label>
        {error && (
          <p role="alert" className="rounded-[var(--radius-control)] border border-[var(--risk-high-border)] bg-[var(--risk-high-soft)] px-3 py-2 text-caption text-[var(--risk-high)]">
            {error}
          </p>
        )}
        <Button type="submit" variant="primary" size="lg" disabled={submitting} iconLeft={<LogIn size={14} />}>
          {submitting ? "Signing in…" : "Sign in to the demo"}
        </Button>
        <p className="text-caption text-fg-tertiary">
          {`Your session lasts ${info.sessionHours} hours. Everything is sample data in a separate demo database, shared with other reviewers and restored every day. Signing in here signs this browser out of a real BunonBrain account.`}
        </p>
      </form>
    </AuthCard>
  );
}

function RolePicker({ session, next, onEnded }: { session: DemoSession; next: string; onEnded: () => void }) {
  const router = useRouter();
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function choose(roleId: string) {
    setBusy(roleId);
    setError(null);
    const r = await demoSwitchRole(roleId);
    if (r.ok) {
      skipFirstVisitScreens();
      router.push(next);
      return;
    }
    setBusy(null);
    if (r.reason === "ended") onEnded();
    else setError("That role couldn't be opened. Try again.");
  }

  async function signOut() {
    await logout();
    onEnded();
  }

  return (
    <div className="min-h-[100dvh] bg-canvas">
      <header className="border-b border-border-subtle bg-[var(--bg-surface)]">
        <div className="mx-auto flex max-w-6xl flex-wrap items-center justify-between gap-3 px-4 py-3">
          <Link href="/" aria-label="BunonBrain home"><BrandMark size={30} framed withWordmark /></Link>
          <Button variant="secondary" size="sm" onClick={signOut} iconLeft={<LogOut size={14} />} data-testid="demo-sign-out">
            Leave the demo
          </Button>
        </div>
      </header>
      <main className="mx-auto max-w-6xl px-4 py-8 sm:py-10">
        <h1 className="text-[26px] leading-[32px] font-semibold tracking-tight text-fg-primary">Choose a role</h1>
        <p className="mt-2 max-w-3xl text-body text-fg-secondary">
          Each role opens its own dashboard with exactly the access that role has. Come back here any time to switch role. You
          won&apos;t need to sign in again.
        </p>
        <p className="mt-1 text-caption text-fg-tertiary">
          {`Session ends ${formatTime(session.expiresAt)}`}
          {session.nextResetAt ? ` · Demo data is restored ${formatTime(session.nextResetAt)}` : ""}
        </p>
        {error && (
          <div className="mt-4">
            <Callout tone="warning" role="alert">{error}</Callout>
          </div>
        )}
        <ul className="mt-6 grid gap-3 sm:grid-cols-2 lg:grid-cols-3" data-testid="demo-roles">
          {session.roles.map((role) => (
            <li key={role.roleId}>
              <button
                type="button"
                onClick={() => choose(role.roleId)}
                disabled={busy !== null}
                data-testid={`demo-role-${role.roleId}`}
                aria-current={role.current ? "true" : undefined}
                className={`group flex h-full w-full flex-col gap-2 rounded-[var(--radius-card,12px)] border p-4 text-left transition-colors disabled:opacity-60 ${
                  role.current ? "border-[var(--accent)] bg-surface-2" : "border-border-subtle bg-[var(--bg-surface)] hover:border-border-strong hover:bg-surface-2"
                }`}
              >
                <span className="flex items-start justify-between gap-2">
                  <span className="text-body font-semibold text-fg-primary">{role.name}</span>
                  <span className="shrink-0 rounded-full border border-border-subtle px-2 py-0.5 text-[11px] text-fg-secondary">
                    {role.current ? "Current" : SCOPE_LABEL[role.scope] ?? role.scope}
                  </span>
                </span>
                <span className="text-caption text-fg-secondary line-clamp-3">{role.description}</span>
                <span className="mt-auto flex items-center justify-between gap-2 pt-1 text-caption text-fg-tertiary">
                  <span className="truncate">{`As ${role.identityName}`}</span>
                  {busy === role.roleId ? <RefreshCw size={14} className="animate-spin" aria-hidden /> : <ArrowRight size={14} aria-hidden className="transition-transform group-hover:translate-x-0.5" />}
                </span>
              </button>
            </li>
          ))}
        </ul>
      </main>
    </div>
  );
}

function ExploreDemo() {
  const search = useSearchParams();
  const next = safeNext(search?.get("next") ?? null);
  const [info, setInfo] = useState<DemoInfo | null | undefined>(undefined);
  const [session, setSession] = useState<DemoSession | null | undefined>(undefined);

  const load = useCallback(async () => {
    const i = await fetchDemoInfo();
    setInfo(i);
    setSession(i?.enabled ? await fetchDemoSession() : null);
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  if (info === undefined || session === undefined) {
    return (
      <div className="flex min-h-[100dvh] items-center justify-center bg-canvas" role="status" aria-live="polite">
        <p className="text-caption text-fg-tertiary">Loading the demo…</p>
      </div>
    );
  }

  if (session) return <RolePicker session={session} next={next} onEnded={() => setSession(null)} />;

  return (
    <AuthPage>
      {info === null ? (
        <AuthCard title="Demo unavailable" subtitle="The demo server can't be reached right now. It may be waking up; try again in a minute.">
          <Button variant="primary" onClick={() => { setInfo(undefined); void load(); }} iconLeft={<RefreshCw size={14} />}>Try again</Button>
        </AuthCard>
      ) : !info.enabled ? (
        <AuthCard title="No interactive demo here" subtitle="This server runs the real application, not the demo.">
          <Link href="/demo" className="text-accent font-medium hover:underline underline-offset-2">Request a guided demo instead</Link>
        </AuthCard>
      ) : (
        <DemoLogin info={info} onSignedIn={setSession} />
      )}
    </AuthPage>
  );
}

export default function ExploreDemoPage() {
  return (
    <Suspense fallback={null}>
      <ExploreDemo />
    </Suspense>
  );
}
