"use client";

import { motion } from "framer-motion";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { ArrowRight, Sparkles } from "lucide-react";
import { useT } from "@/lib/useT";
import { Button } from "@/components/ui/Button";
import { BrandMark } from "@/components/brand/BrandMark";
import { businessService } from "@/services/business.service";

export default function WelcomePage() {
  const { t } = useT();
  const router = useRouter();

  function enterDemo() {
    // Single source of truth: every path that reaches /app must mark the user
    // onboarded FIRST. Without this, /app's layout gate bounces them back to
    // /onboarding/welcome. See src/app/app/layout.tsx for the gate.
    businessService.completeOnboarding();
    router.push("/app");
  }

  return (
    <div className="min-h-[calc(100vh-65px)] flex flex-col items-center justify-center px-6 gap-8">
      <motion.div
        initial={{ opacity: 0, scale: 0.92 }}
        animate={{ opacity: 1, scale: 1 }}
        transition={{ duration: 0.5, ease: [0.22, 1, 0.36, 1] }}
      >
        <BrandMark size={56} framed reveal />
      </motion.div>
      <motion.div
        initial={{ opacity: 0, y: 12 }}
        animate={{ opacity: 1, y: 0 }}
        transition={{ duration: 0.5, ease: [0.22, 1, 0.36, 1] }}
        className="text-center max-w-2xl"
      >
        <p className="mono-pill text-fg-tertiary">{t("onboarding.welcome.eyebrow")}</p>
        <h1 className="mt-6 text-[36px] md:text-[52px] leading-[42px] md:leading-[60px] font-semibold tracking-tight">
          {t("onboarding.welcome.headline")}
        </h1>

        {/* Demo-simulated honesty chip */}
        <p className="mt-6 inline-flex items-center gap-2 text-caption text-fg-secondary">
          <span className="size-1.5 rounded-full bg-fg-tertiary" />
          {t("landing.badge")}
        </p>

        <div className="mt-10 flex flex-wrap items-center justify-center gap-3">
          {/* 1-click path → /app. This is the default for judges / demo.
              Must call completeOnboarding() before router.push so the /app
              gate (which checks isOnboarded) doesn't bounce the user back. */}
          <Button
            variant="primary"
            size="lg"
            iconRight={<ArrowRight size={16} />}
            onClick={enterDemo}
          >
            {t("onboarding.welcome.ctaSkip")}
          </Button>
          {/* Optional 1-extra-step path for the operator who wants to seed their profile. */}
          <Link href="/onboarding/profile">
            <Button variant="secondary" size="lg" iconLeft={<Sparkles size={14} />}>
              {t("onboarding.welcome.ctaSeed")}
            </Button>
          </Link>
        </div>

        <p className="mt-6 text-caption text-fg-tertiary max-w-md mx-auto">
          {t("onboarding.welcome.tail")}
        </p>
      </motion.div>
    </div>
  );
}
