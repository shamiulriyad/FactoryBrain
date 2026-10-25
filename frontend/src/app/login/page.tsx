"use client";

import { Suspense, useEffect, useState } from "react";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { LogIn } from "lucide-react";
import { useT } from "@/lib/useT";
import { fetchAuthOptions, login, type AuthOptions } from "@/lib/auth";
import { Button } from "@/components/ui/Button";
import { Input } from "@/components/ui/Input";
import { AuthCard, AuthPage, EMAIL_SHAPE, linkClass } from "@/components/auth/AuthCard";
import { Callout } from "@/components/ui/Callout";

/** Only same-app paths are accepted as a post-login destination (no open redirect). */
function safeNext(raw: string | null): string {
  if (!raw || !raw.startsWith("/app") || raw.startsWith("//")) return "/app";
  return raw;
}

function LoginForm() {
  const { t } = useT();
  const router = useRouter();
  const search = useSearchParams();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [options, setOptions] = useState<AuthOptions | null>(null);
  const expired = search?.get("reason") === "expired";

  useEffect(() => {
    fetchAuthOptions().then(setOptions);
  }, []);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    // Checked here rather than by the browser: the native "include an '@'"
    // bubble disappears after a moment and leaves Sign in looking dead.
    if (!EMAIL_SHAPE.test(email.trim())) {
      setError(t("auth.emailFormat") as string);
      return;
    }
    if (password.length === 0) {
      setError(t("auth.passwordRequired") as string);
      return;
    }
    setSubmitting(true);
    setError(null);
    const result = await login(email.trim(), password);
    setSubmitting(false);
    if (result.ok) {
      setPassword("");
      router.replace(safeNext(search?.get("next") ?? null));
      return;
    }
    if (result.reason === "rate_limited") {
      setError(t("auth.rateLimited", { s: result.retryAfterSeconds ?? 60 }) as string);
    } else if (result.reason === "invalid") {
      setError(t("auth.invalid") as string);
    } else {
      setError(t("auth.unavailable") as string);
    }
  }

  return (
    <AuthCard
      title={t("auth.title") as string}
      subtitle={t("auth.subtitle") as string}
      notice={
        expired ? (
          <Callout tone="warning" role="status" testId="session-expired">
            {t("auth.sessionExpired")}
          </Callout>
        ) : null
      }
      footer={
        options?.selfRegistration ? (
          <>
            {t("auth.noAccount")}{" "}
            <Link href="/register" className={linkClass} data-testid="link-register">
              {t("auth.createAccount")}
            </Link>
          </>
        ) : null
      }
    >
      <form onSubmit={submit} noValidate className="flex flex-col gap-4">
        <label className="flex flex-col">
          <span className="field-label">{t("auth.email")}</span>
          <Input
            type="email"
            autoComplete="username"
            required
            value={email}
            onChange={(e) => setEmail(e.target.value)}
          />
        </label>
        <div className="flex flex-col gap-1.5">
          <label className="flex flex-col">
            <span className="field-label">{t("auth.password")}</span>
            <Input
              type="password"
              autoComplete="current-password"
              required
              value={password}
              onChange={(e) => setPassword(e.target.value)}
            />
          </label>
          <Link href="/forgot-password" className={`${linkClass} self-end text-caption`} data-testid="link-forgot">
            {t("auth.forgotLink")}
          </Link>
        </div>
        {error && (
          <p role="alert" className="rounded-[var(--radius-control)] border border-[var(--risk-high-border)] bg-[var(--risk-high-soft)] px-3 py-2 text-caption text-[var(--risk-high)]">
            {error}
          </p>
        )}
        <Button type="submit" variant="primary" size="lg" disabled={submitting} iconLeft={<LogIn size={14} />}>
          {submitting ? t("auth.submitting") : t("auth.submit")}
        </Button>
      </form>
    </AuthCard>
  );
}

export default function LoginPage() {
  return (
    <AuthPage>
      <Suspense fallback={null}>
        <LoginForm />
      </Suspense>
    </AuthPage>
  );
}
