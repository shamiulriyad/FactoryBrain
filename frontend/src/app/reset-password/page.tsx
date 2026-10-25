"use client";

import { Suspense, useEffect, useState } from "react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { KeyRound } from "lucide-react";
import { useT } from "@/lib/useT";
import { fetchAuthOptions, resetPassword } from "@/lib/auth";
import { Button } from "@/components/ui/Button";
import { Input } from "@/components/ui/Input";
import { AuthCard, AuthPage, linkClass } from "@/components/auth/AuthCard";

/**
 * Landing page of the emailed reset link (/reset-password?token=…). The token
 * is read once and removed from the address bar, so it doesn't linger in
 * history or leak through a Referer header.
 */
function ResetForm() {
  const { t } = useT();
  const search = useSearchParams();
  const [token, setToken] = useState<string | null>(null);
  const [checked, setChecked] = useState(false);
  const [minLength, setMinLength] = useState(10);
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [saving, setSaving] = useState(false);
  const [done, setDone] = useState(false);
  const [badLink, setBadLink] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const fromUrl = search?.get("token") ?? null;
    if (fromUrl) {
      setToken(fromUrl);
      window.history.replaceState({}, "", "/reset-password");
    }
    setChecked(true);
    fetchAuthOptions().then((o) => setMinLength(o.passwordMinLength));
    // Read the token from the first URL only.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!token) return;
    if (password.length < minLength) return setError(t("auth.passwordTooShort", { n: minLength }) as string);
    if (password !== confirm) return setError(t("auth.passwordMismatch") as string);
    setSaving(true);
    setError(null);
    const result = await resetPassword(token, password);
    setSaving(false);
    if (result === "reset") {
      setDone(true);
      setPassword("");
      setConfirm("");
    } else if (result === "invalid_link") setBadLink(true);
    else if (result === "weak") setError(t("auth.passwordTooShort", { n: minLength }) as string);
    else if (result === "rate_limited") setError(t("auth.rateLimited", { s: 60 }) as string);
    else setError(t("auth.unavailable") as string);
  }

  const footer = (
    <Link href="/login" className={linkClass}>
      {t("auth.backToSignIn")}
    </Link>
  );

  if (!checked) return null;

  if (done) {
    return (
      <AuthCard title={t("auth.resetTitle") as string} footer={null}>
        <p role="status" className="text-caption text-fg-secondary" data-testid="reset-done">
          {t("auth.resetDone")}
        </p>
        <Link href="/login" className={linkClass} data-testid="reset-sign-in">
          {t("auth.signInLink")}
        </Link>
      </AuthCard>
    );
  }

  if (!token || badLink) {
    return (
      <AuthCard title={t("auth.resetTitle") as string} footer={footer}>
        <p role="alert" className="text-caption text-[var(--risk-high)]" data-testid="reset-bad-link">
          {token ? t("auth.resetInvalidLink") : t("auth.resetMissingToken")}
        </p>
        <Link href="/forgot-password" className={linkClass}>
          {t("auth.requestNewLink")}
        </Link>
      </AuthCard>
    );
  }

  return (
    <AuthCard title={t("auth.resetTitle") as string} subtitle={t("auth.resetSubtitle") as string} footer={footer}>
      <form onSubmit={submit} noValidate className="flex flex-col gap-4">
        <div className="flex flex-col gap-1.5">
          <label className="flex flex-col gap-1.5">
            <span className="field-label">{t("auth.newPassword")}</span>
            <Input
              type="password"
              autoComplete="new-password"
              aria-describedby="password-hint"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
            />
          </label>
          <span id="password-hint" className="text-caption text-fg-tertiary">{t("auth.passwordHint", { n: minLength })}</span>
        </div>
        <label className="flex flex-col gap-1.5">
          <span className="field-label">{t("auth.confirmPassword")}</span>
          <Input type="password" autoComplete="new-password" value={confirm} onChange={(e) => setConfirm(e.target.value)} />
        </label>
        {error && (
          <p role="alert" className="text-caption text-[var(--risk-high)]">
            {error}
          </p>
        )}
        <Button type="submit" variant="primary" disabled={saving} iconLeft={<KeyRound size={14} />}>
          {saving ? t("auth.resetSaving") : t("auth.resetSubmit")}
        </Button>
      </form>
    </AuthCard>
  );
}

export default function ResetPasswordPage() {
  return (
    <AuthPage>
      <Suspense fallback={null}>
        <ResetForm />
      </Suspense>
    </AuthPage>
  );
}
