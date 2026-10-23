"use client";

import { useState } from "react";
import { motion } from "framer-motion";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { ArrowRight } from "lucide-react";
import { useBusinessStore } from "@/store/business.store";
import { useT } from "@/lib/useT";
import { tArray } from "@/i18n/registry";
import { Input } from "@/components/ui/Input";
import { Button } from "@/components/ui/Button";
import { cn } from "@/lib/cn";
import { businessService } from "@/services/business.service";

export default function ProfilePage() {
  const router = useRouter();
  const { t, locale } = useT();
  const profile = useBusinessStore((s) => s.profile);
  const setProfile = useBusinessStore((s) => s.setProfile);

  const [industry, setIndustry] = useState(profile.industry);
  const [what, setWhat] = useState(profile.whatYouSell);
  const [who, setWho] = useState(profile.customers);
  const [goals, setGoals] = useState<string[]>(profile.goals);

  const options = tArray(locale, "onboarding.profile.goalOptions");

  function toggle(g: string) {
    setGoals((cur) => (cur.includes(g) ? cur.filter((x) => x !== g) : cur.length >= 3 ? cur : [...cur, g]));
  }

  function next() {
    setProfile({ industry, whatYouSell: what, customers: who, goals });
    // Onboarding is intentionally short: after the profile seed we go
    // straight to the app. RFID / machine telemetry / energy / Documents
    // are optional and live under Integrations.
    // completeOnboarding() must run BEFORE router.push so /app's gate
    // (isOnboarded check) lets the user through instead of bouncing them
    // back to /onboarding/welcome.
    businessService.completeOnboarding();
    router.push("/app");
  }

  function skipToApp() {
    // Persist whatever is filled in, then enter the app. Same gate as
    // next(): mark onboarded before navigating.
    setProfile({ industry, whatYouSell: what, customers: who, goals });
    businessService.completeOnboarding();
    router.push("/app");
  }

  const canContinue = industry.length > 0 && what.length > 0 && who.length > 0 && goals.length > 0;

  return (
    <div className="max-w-2xl mx-auto px-6 py-12 md:py-16">
      <Link href="/onboarding/welcome" className="inline-flex items-center gap-1 text-caption text-fg-tertiary hover:text-fg-primary mb-8">
        {t("onboarding.profile.back")}
      </Link>

      <motion.div initial={{ opacity: 0, y: 8 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.4 }}>
        <h1 className="text-[28px] md:text-[36px] font-semibold tracking-tight">{t("onboarding.profile.title")}</h1>
        <p className="mt-3 text-body text-fg-secondary max-w-xl">{t("onboarding.profile.subtitle")}</p>
      </motion.div>

      <div className="mt-10 space-y-6">
        <Field label={t("onboarding.profile.industry")}>
          <Input
            value={industry}
            onChange={(e) => setIndustry(e.target.value)}
            placeholder={t("onboarding.profile.industryPlaceholder") as string}
          />
        </Field>

        <Field label={t("onboarding.profile.whatYouSell")}>
          <textarea
            value={what}
            onChange={(e) => setWhat(e.target.value)}
            placeholder={t("onboarding.profile.whatYouSellPlaceholder") as string}
            className="min-h-24 w-full rounded-md bg-surface-2 border border-border-subtle px-3 py-2 text-body text-fg-primary placeholder:text-fg-tertiary focus:outline-none focus:border-border-strong"
          />
        </Field>

        <Field label={t("onboarding.profile.customers")}>
          <textarea
            value={who}
            onChange={(e) => setWho(e.target.value)}
            placeholder={t("onboarding.profile.customersPlaceholder") as string}
            className="min-h-24 w-full rounded-md bg-surface-2 border border-border-subtle px-3 py-2 text-body text-fg-primary placeholder:text-fg-tertiary focus:outline-none focus:border-border-strong"
          />
        </Field>

        <Field label={t("onboarding.profile.goals")}>
          <div className="flex flex-wrap gap-2">
            {options.map((g) => {
              const active = goals.includes(g);
              return (
                <button
                  key={g}
                  type="button"
                  onClick={() => toggle(g)}
                  className={cn(
                    "h-8 px-3 rounded-md border text-caption transition-colors",
                    active
                      ? "bg-[var(--btn-primary-bg)] text-[var(--btn-primary-fg)] border-transparent"
                      : "bg-surface-2 text-fg-secondary border-border-subtle hover:text-fg-primary"
                  )}
                >
                  {g}
                </button>
              );
            })}
          </div>
          <p className="mt-2 text-caption text-fg-tertiary">{goals.length}/3 selected</p>
        </Field>
      </div>

      <div className="mt-10 flex flex-wrap items-center justify-between gap-3">
        <Button variant="ghost" size="md" onClick={skipToApp}>
          {t("onboarding.profile.skip")}
        </Button>
        <Button variant="primary" size="lg" onClick={next} disabled={!canContinue} iconRight={<ArrowRight size={14} />}>
          {t("onboarding.profile.cta")}
        </Button>
      </div>

      <p className="mt-4 text-caption text-fg-tertiary max-w-md">
        {t("onboarding.profile.tail")}
      </p>
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block">
      <span className="text-caption text-fg-secondary mb-2 block">{label}</span>
      {children}
    </label>
  );
}