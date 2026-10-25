"use client";

import { useState } from "react";
import Link from "next/link";
import { Mail } from "lucide-react";
import { useT } from "@/lib/useT";
import { requestPasswordReset } from "@/lib/auth";
import { Button } from "@/components/ui/Button";
import { Input } from "@/components/ui/Input";
import { AuthCard, AuthPage, EMAIL_SHAPE, linkClass } from "@/components/auth/AuthCard";

/**
 * Ask for a password reset link. The API answers the same way whether or not
 * the email has an account, so this page never reveals who is registered.
 */
export default function ForgotPasswordPage() {
  const { t } = useT();
  const [email, setEmail] = useState("");
  const [sending, setSending] = useState(false);
  const [sent, setSent] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!EMAIL_SHAPE.test(email.trim())) return setError(t("auth.emailFormat") as string);
    setSending(true);
    setError(null);
    const result = await requestPasswordReset(email.trim());
    setSending(false);
    if (result === "sent") setSent(true);
    else if (result === "rate_limited") setError(t("auth.rateLimited", { s: 60 }) as string);
    else if (result === "invalid") setError(t("auth.emailFormat") as string);
    else setError(t("auth.unavailable") as string);
  }

  return (
    <AuthPage>
      <AuthCard
        title={t("auth.forgotTitle") as string}
        subtitle={sent ? undefined : (t("auth.forgotSubtitle") as string)}
        footer={
          <Link href="/login" className={linkClass}>
            {t("auth.backToSignIn")}
          </Link>
        }
      >
        {sent ? (
          <p role="status" className="text-caption text-fg-secondary" data-testid="forgot-sent">
            {t("auth.forgotSent")}
          </p>
        ) : (
          <form onSubmit={submit} noValidate className="flex flex-col gap-4">
            <label className="flex flex-col gap-1.5">
              <span className="field-label">{t("auth.email")}</span>
              <Input type="email" autoComplete="email" value={email} onChange={(e) => setEmail(e.target.value)} />
            </label>
            {error && (
              <p role="alert" className="text-caption text-[var(--risk-high)]">
                {error}
              </p>
            )}
            <Button type="submit" variant="primary" disabled={sending} iconLeft={<Mail size={14} />}>
              {sending ? t("auth.forgotSending") : t("auth.forgotSubmit")}
            </Button>
          </form>
        )}
      </AuthCard>
    </AuthPage>
  );
}
