"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { UserPlus } from "lucide-react";
import { useT } from "@/lib/useT";
import { fetchAuthOptions, register, type AuthOptions } from "@/lib/auth";
import { Button } from "@/components/ui/Button";
import { Input } from "@/components/ui/Input";
import { AuthCard, AuthPage, EMAIL_SHAPE, linkClass } from "@/components/auth/AuthCard";

/**
 * Self-service sign-up. The API always creates a Viewer (read-only); an
 * Admin grants more in Settings → Users. On success the user is signed in.
 */
export default function RegisterPage() {
  const { t } = useT();
  const router = useRouter();
  const [options, setOptions] = useState<AuthOptions | null>(null);
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    fetchAuthOptions().then(setOptions);
  }, []);

  const minLength = options?.passwordMinLength ?? 10;
  const disabled = options !== null && !options.selfRegistration;

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!EMAIL_SHAPE.test(email.trim())) return setError(t("auth.emailFormat") as string);
    if (password.length < minLength) return setError(t("auth.passwordTooShort", { n: minLength }) as string);
    if (password !== confirm) return setError(t("auth.passwordMismatch") as string);

    setSubmitting(true);
    setError(null);
    const result = await register(email.trim(), password);
    setSubmitting(false);
    if (result.ok) {
      setPassword("");
      setConfirm("");
      router.replace("/app");
      return;
    }
    switch (result.reason) {
      case "taken":
        return setError(t("auth.emailTaken") as string);
      case "disabled":
        return setError(t("auth.registerDisabled") as string);
      case "invalid":
        return setError(result.message ?? (t("auth.emailFormat") as string));
      case "rate_limited":
        return setError(t("auth.rateLimited", { s: result.retryAfterSeconds ?? 60 }) as string);
      default:
        return setError(t("auth.unavailable") as string);
    }
  }

  return (
    <AuthPage>
      <AuthCard
        title={t("auth.registerTitle") as string}
        subtitle={t("auth.registerSubtitle") as string}
        footer={
          <>
            {t("auth.haveAccount")}{" "}
            <Link href="/login" className={linkClass}>
              {t("auth.signInLink")}
            </Link>
          </>
        }
      >
        {disabled ? (
          <p role="alert" className="text-caption text-fg-secondary" data-testid="register-disabled">
            {t("auth.registerDisabled")}
          </p>
        ) : (
          <form onSubmit={submit} noValidate className="flex flex-col gap-4">
            <label className="flex flex-col gap-1.5">
              <span className="field-label">{t("auth.email")}</span>
              <Input type="email" autoComplete="email" value={email} onChange={(e) => setEmail(e.target.value)} />
            </label>
            <div className="flex flex-col gap-1.5">
              <label className="flex flex-col gap-1.5">
                <span className="field-label">{t("auth.password")}</span>
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
            <Button type="submit" variant="primary" disabled={submitting} iconLeft={<UserPlus size={14} />}>
              {submitting ? t("auth.registering") : t("auth.registerSubmit")}
            </Button>
          </form>
        )}
      </AuthCard>
    </AuthPage>
  );
}
