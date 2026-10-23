"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { motion } from "framer-motion";
import { ArrowRight, Activity, AlertTriangle, MessagesSquare, ShieldCheck } from "lucide-react";
import { useT } from "@/lib/useT";
import { tArray } from "@/i18n/registry";
import { LanguageToggle } from "@/components/ui/LanguageToggle";
import { Button } from "@/components/ui/Button";
import { BrandMark } from "@/components/brand/BrandMark";
import { businessService } from "@/services/business.service";

export default function LandingPage() {
  const { t, locale } = useT();
  const router = useRouter();

  /** Single source of truth for "Enter demo" from outside the app.
   *  Mark onboarding complete before navigating so /app's gate
   *  (which checks isOnboarded) lets the user through. Without this,
   *  fresh users bounce between Landing → /onboarding/welcome → /app. */
  function enterDemo() {
    businessService.completeOnboarding();
    router.push("/app");
  }

  const valuePropIcons = [Activity, AlertTriangle, MessagesSquare];
  const valuePropKeys = ["liveLine", "alerts", "bangla"] as const;
  const pillars = tArray(locale, "landing.socialProof.pillars");
  const steps = tArray(locale, "landing.howItWorks.steps").map((line) => {
    // Each step line is encoded as "Title · Body" so the EN/BN tables stay flat.
    const idx = line.indexOf(" · ");
    if (idx === -1) return { title: line, body: "" };
    return { title: line.slice(0, idx), body: line.slice(idx + 3) };
  });

  return (
    <div className="relative min-h-[100dvh] bg-canvas">
      {/* ---------- Top bar ---------- */}
      <header className="relative z-10 flex items-center justify-between px-6 md:px-12 py-6">
        <Link href="/" className="flex items-center gap-2.5" aria-label="BunonBrain">
          <BrandMark size={32} framed withWordmark />
        </Link>
        <div className="flex items-center gap-3">
          <LanguageToggle />
          <Button
            variant="primary"
            size="md"
            iconRight={<ArrowRight size={14} />}
            onClick={enterDemo}
            aria-label={t("landing.ctaPrimary") as string}
          >
            {t("landing.ctaPrimary")}
          </Button>
        </div>
      </header>

      {/* ---------- Hero ---------- */}
      <main className="relative z-10 px-6 md:px-12 pt-8 md:pt-12 pb-24 max-w-6xl mx-auto">
        <div className="grid lg:grid-cols-[minmax(0,1fr)_320px] gap-10 items-start">
        <div>
        <h1
          className="text-[40px] md:text-[56px] leading-[44px] md:leading-[60px] font-semibold tracking-tight max-w-[16ch]"
        >
          {t("landing.headlineLead")}
          <br />
          <span className="text-fg-secondary">{t("landing.headlineAccent")}</span>
        </h1>
        <p className="mt-4 text-caption text-fg-secondary">{t("landing.eyebrow")}</p>

        <motion.p
          initial={{ opacity: 0, y: 10 }}
          animate={{ opacity: 1, y: 0 }}
          transition={{ delay: 0.12, duration: 0.6, ease: [0.22, 1, 0.36, 1] }}
          className="mt-6 max-w-2xl text-body text-fg-secondary"
        >
          {t("landing.subhead")}
        </motion.p>

        <motion.div
          initial={{ opacity: 0, y: 10 }}
          animate={{ opacity: 1, y: 0 }}
          transition={{ delay: 0.24, duration: 0.6, ease: [0.22, 1, 0.36, 1] }}
          className="mt-8 flex flex-wrap items-center gap-3"
        >
          <Button
            variant="primary"
            size="lg"
            iconRight={<ArrowRight size={16} />}
            onClick={enterDemo}
            aria-label={t("landing.ctaPrimary") as string}
          >
            {t("landing.ctaPrimary")}
          </Button>
          <Link
            href="/onboarding/welcome"
            aria-label={t("landing.ctaSecondary") as string}
          >
            <Button variant="secondary" size="lg">
              {t("landing.ctaSecondary")}
            </Button>
          </Link>
        </motion.div>

        <motion.p
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
          transition={{ delay: 0.36, duration: 0.5 }}
          className="mt-3 text-caption text-fg-tertiary"
        >
          {t("landing.ctaPrimaryHint")}
        </motion.p>

        <motion.div
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
          transition={{ delay: 0.45, duration: 0.5 }}
          className="mt-6 inline-flex items-center gap-2 text-caption text-fg-secondary"
        >
          <span className="size-1.5 rounded-full bg-fg-tertiary" />
          <span className="text-caption text-fg-secondary">{t("landing.badge")}</span>
        </motion.div>
        </div>
        <SewingFloor />
        </div>

        {/* ---------- Value props (BD RMG managers) ---------- */}
        <motion.section
          initial="hidden"
          animate="show"
          variants={{
            hidden: {},
            show: { transition: { staggerChildren: 0.08, delayChildren: 0.55 } }
          }}
          className="mt-16 border-l border-border-strong pl-6 space-y-6"
        >
          {valuePropKeys.map((key, i) => {
            const Icon = valuePropIcons[i];
            return (
              <motion.div
                key={key}
                variants={{
                  hidden: { opacity: 0, y: 8 },
                  show: { opacity: 1, y: 0, transition: { duration: 0.45, ease: [0.22, 1, 0.36, 1] } }
                }}
                className="relative"
              >
                <div className="flex items-center gap-2">
                  <Icon size={14} className="text-accent" />
                  <p className="text-title text-fg-primary">
                    {t(`landing.valueProps.${key}.title`)}
                  </p>
                </div>
                <p className="mt-2 text-caption text-fg-tertiary">
                  {t(`landing.valueProps.${key}.body`)}
                </p>
              </motion.div>
            );
          })}
        </motion.section>

        {/* ---------- Social-proof strip (honest, no fake logos) ---------- */}
        <motion.section
          initial={{ opacity: 0, y: 8 }}
          animate={{ opacity: 1, y: 0 }}
          transition={{ delay: 0.85, duration: 0.5 }}
          className="mt-10 surface p-5 md:p-6"
        >
          <div className="flex flex-col md:flex-row md:items-start md:justify-between gap-4">
            <div className="max-w-xl">
              <p className="text-title text-fg-primary">
                {t("landing.socialProof.kicker")}
              </p>
              <p className="mt-2 text-body text-fg-secondary">
                {t("landing.socialProof.body")}
              </p>
            </div>
            <ul className="grid grid-cols-2 gap-x-6 gap-y-2 text-caption text-fg-secondary">
              {pillars.map((p) => (
                <li key={p} className="flex items-center gap-2">
                  <span className="size-1 rounded-full bg-accent" />
                  {p}
                </li>
              ))}
            </ul>
          </div>
        </motion.section>

        {/* ---------- How it fits your floor ---------- */}
        <motion.section
          initial={{ opacity: 0, y: 8 }}
          animate={{ opacity: 1, y: 0 }}
          transition={{ delay: 1.0, duration: 0.5 }}
          className="mt-12"
        >
          <h2 className="text-title text-fg-primary">
            {t("landing.howItWorks.kicker")}
          </h2>
          <ol className="mt-4 grid md:grid-cols-3 gap-3">
            {steps.map((step, idx) => (
              <li key={step.title} className="surface p-4">
                <div className="flex items-center gap-2">
                  <span className="text-caption text-fg-tertiary">
                    {idx + 1}
                  </span>
                  <p className="text-title text-fg-primary">{step.title}</p>
                </div>
                <p className="mt-2 text-caption text-fg-tertiary">{step.body}</p>
              </li>
            ))}
          </ol>
        </motion.section>

        {/* ---------- Honest defaults ---------- */}
        <motion.section
          initial={{ opacity: 0, y: 8 }}
          animate={{ opacity: 1, y: 0 }}
          transition={{ delay: 1.15, duration: 0.5 }}
          className="mt-12 flex items-start gap-3 rounded-md border border-border-subtle bg-surface-2/40 p-4"
        >
          <ShieldCheck size={16} className="text-accent mt-0.5 shrink-0" />
          <div>
            <p className="text-body text-fg-primary">
              {t("landing.honesty.kicker")}
            </p>
            <p className="mt-1 text-caption text-fg-secondary">
              {t("landing.honesty.body")}
            </p>
          </div>
        </motion.section>
      </main>

      {/* ---------- Footer ---------- */}
      <footer className="relative z-10 border-t border-border-subtle">
        <div className="max-w-6xl mx-auto px-6 md:px-12 py-6 flex flex-col md:flex-row md:items-center md:justify-between gap-3">
          <p className="text-caption text-fg-tertiary">
            {t("landing.footer.tagline")}
          </p>
          <div className="flex items-center gap-3">
            <Link href="/onboarding/welcome">
              <Button variant="ghost" size="md">
                {t("landing.footer.secondary")}
              </Button>
            </Link>
            <Button
              variant="primary"
              size="md"
              iconRight={<ArrowRight size={14} />}
              onClick={enterDemo}
            >
              {t("landing.footer.cta")}
            </Button>
          </div>
        </div>
      </footer>
    </div>
  );
}

/** Static sewing-floor andon. Line 3 is the slip the rest of the product already describes. */
function SewingFloor() {
  const rows = [
    { id: "1", lamp: "bg-[var(--risk-low)]", label: "Running" },
    { id: "2", lamp: "bg-[var(--risk-low)]", label: "Running" },
    { id: "3", lamp: "bg-[var(--risk-medium)]", label: "Behind" },
    { id: "4", lamp: "bg-[var(--risk-low)]", label: "Running" },
    { id: "5", lamp: "bg-[var(--risk-low)]", label: "Running" },
    { id: "6", lamp: "bg-[var(--risk-low)]", label: "Running" }
  ];
  return (
    <aside className="bg-[var(--bench)] text-[var(--bench-fg)] p-5" aria-label="Sewing floor">
      <div className="flex items-baseline justify-between gap-3">
        <p className="text-body font-semibold">Sewing floor</p>
        <p className="text-caption text-[var(--bench-muted)]">6 lines</p>
      </div>
      <p className="mt-1 text-caption text-[var(--bench-muted)]">Line 3 is the one to watch.</p>
      <ol className="mt-5">
        {rows.map((row) => (
          <li key={row.id} className="grid grid-cols-[16px_1fr_auto] items-center gap-3 py-2 border-t border-white/10">
            <span className={`size-2.5 rounded-full ${row.lamp}`} aria-hidden />
            <span className="text-caption">Line {row.id}</span>
            <span className="text-caption text-[var(--bench-muted)]">{row.label}</span>
          </li>
        ))}
      </ol>
    </aside>
  );
}
