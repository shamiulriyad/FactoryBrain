"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { motion } from "framer-motion";
import { Camera, ExternalLink, ShieldCheck, Sparkles } from "lucide-react";
import { useT } from "@/lib/useT";
import { useBusinessStore } from "@/store/business.store";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { SimulatedPill } from "@/components/ui/Status";
import { cn } from "@/lib/cn";
import type { VisionResultT } from "@/services/sensors.schemas";
import type { InsightPublic } from "@/services/types";

const SAMPLE_FILES = [
  "defect-1-stitch-skip.jpg",
  "defect-2-buttonhole.jpg",
  "defect-3-seam-pucker.jpg",
  "defect-4-fabric-stain.jpg"
] as const;

type AnalyzeResponse = {
  simulated: true;
  source: string;
  result: VisionResultT;
  insight: InsightPublic;
  approvalPending: boolean;
  approvalReason?: string;
  floorAlertId?: string;
  activityEventId: string;
};

export default function VisionPage() {
  const { t, locale } = useT();
  const enabled = useBusinessStore((s) => s.featureFlags.visionRepair);
  const [fileIdx, setFileIdx] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [analysis, setAnalysis] = useState<AnalyzeResponse | null>(null);

  useEffect(() => {
    if (!enabled) {
      // Friendly redirect when the flag is off — should not happen because the
      // sidebar only links here when enabled, but guard against deep-links.
    }
  }, [enabled]);

  const setFeatureFlag = useBusinessStore((s) => s.setFeatureFlag);

  if (!enabled) {
    return (
      <div className="px-6 md:px-8 py-6 max-w-3xl mx-auto" data-tour="vision">
        <h1 className="text-display font-semibold tracking-tight">{t("vision.title")}</h1>
        <p className="mt-1 text-caption text-fg-tertiary">
          {locale === "bn"
            ? "বিকল্পটি এই ওয়ার্কস্পেসে অক্ষম। নিচে চালু করুন বা সেটিংসে যান।"
            : "Vision Repair is disabled in this workspace. Enable it below or in Settings."}
        </p>
        <div className="mt-4 flex items-center gap-3">
          <Button
            variant="primary"
            size="sm"
            onClick={() => setFeatureFlag("visionRepair", true)}
          >
            {locale === "bn" ? "ভিশন চালু করুন" : "Enable Vision Repair"}
          </Button>
          <Link href="/app/settings" className="text-caption text-accent hover:underline">
            {locale === "bn" ? "সেটিংস →" : "Settings →"}
          </Link>
        </div>
      </div>
    );
  }

  async function handleAnalyze() {
    if (busy) return;
    setBusy(true);
    setError(null);
    setAnalysis(null);
    try {
      const res = await fetch("/api/vision/analyze", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sampleFile: SAMPLE_FILES[fileIdx] })
      });
      if (!res.ok) {
        const msg = `analyze_${res.status}`;
        setError(msg);
        return;
      }
      const data = (await res.json()) as AnalyzeResponse;
      setAnalysis(data);
      // Nudge /app/insights and /app/approvals to refresh (same channel
      // used by the agents page after a run).
      if (typeof window !== "undefined") {
        window.dispatchEvent(new CustomEvent("bunonbrain:insights-refresh"));
      }
    } catch (e) {
      setError((e as Error).message ?? "analyze_failed");
    } finally {
      setBusy(false);
    }
  }

  const result = analysis?.result;
  const insightLink = analysis ? `/app/insights?focus=${analysis.insight.id}` : null;
  const approvalLink =
    analysis && analysis.approvalPending
      ? `/app/approvals?focus=${analysis.insight.id}`
      : null;
  const stepsEn = result?.repairStepsEn ?? [];
  const stepsBn = result?.repairStepsBn ?? [];

  return (
    <div className="px-6 md:px-8 py-6 max-w-3xl mx-auto" data-tour="vision">
      <h1 className="text-display font-semibold tracking-tight">{t("vision.title")}</h1>
      <p className="mt-1 text-caption text-fg-tertiary">{t("vision.body") as string}</p>

      <Panel className="mt-6" title={locale === "bn" ? "স্টেজড নমুনা বেছে নিন" : "Choose a staged sample"}>
        <div className="grid grid-cols-2 gap-2">
          {SAMPLE_FILES.map((file, i) => (
            <button
              key={file}
              type="button"
              onClick={() => {
                setFileIdx(i);
                setAnalysis(null);
                setError(null);
              }}
              aria-pressed={fileIdx === i}
              className={cn(
                "text-left rounded-md border p-3 transition-colors press",
                fileIdx === i
                  ? "border-[var(--accent-border)] bg-[var(--accent-soft)]"
                  : "border-border-subtle bg-surface-2 hover:border-border-strong"
              )}
            >
              <div className="flex items-center gap-2">
                <span className="size-8 rounded-md bg-surface border border-border-subtle flex items-center justify-center text-fg-tertiary">
                  <Camera size={16} />
                </span>
                <span className="mono-pill text-fg-primary truncate">{file.replace(".jpg", "")}</span>
              </div>
            </button>
          ))}
        </div>
        <div className="mt-4 flex flex-wrap items-center gap-3">
          <Button variant="primary" size="sm" onClick={handleAnalyze} disabled={busy}>
            <Sparkles size={12} /> {busy ? (t("vision.analyzing") as string) : (t("vision.upload") as string)}
          </Button>
          <SimulatedPill
            label={t("vision.simulatedPill") as string}
            testId="vision-simulated-pill"
          />
          <span className="text-caption text-fg-tertiary mono-pill">{SAMPLE_FILES[fileIdx]}</span>
        </div>
      </Panel>

      {error && (
        <motion.div
          initial={{ opacity: 0, y: 6 }}
          animate={{ opacity: 1, y: 0 }}
          className="mt-4 surface-2 p-3"
        >
          <p className="text-caption text-[var(--risk-high)]">
            {t("vision.errorTitle") as string}: {error}
          </p>
          <p className="mt-1 text-caption text-fg-tertiary">
            {t("vision.errorBody") as string}
          </p>
        </motion.div>
      )}

      {result && (
        <motion.div
          initial={{ opacity: 0, y: 6 }}
          animate={{ opacity: 1, y: 0 }}
          className="mt-4 space-y-4"
        >
          <Panel
            title={t("vision.repairTitle") as string}
            right={
              <span
                className={cn(
                  "mono-pill border px-2 py-0.5 text-caption",
                  (result.confidence ?? 0) >= 0.85
                    ? "border-[var(--risk-low-border)] text-[var(--risk-low)] bg-[var(--risk-low-soft)]"
                    : "border-[var(--risk-medium-border)] text-[var(--risk-medium)] bg-[var(--risk-medium-soft)]"
                )}
              >
                {(result.confidence * 100).toFixed(0)}%
              </span>
            }
          >
            <p className="text-title text-fg-primary">{result.anomalyLabelEn}</p>
            <p className="mt-1 text-caption text-fg-tertiary">{result.anomalyLabelBn}</p>
            <p className="mt-2 text-caption text-fg-tertiary">
              {result.machineId
                ? locale === "bn"
                  ? `মেশিন: ${result.machineId}`
                  : `Machine: ${result.machineId}`
                : null}
              <span className="ml-2 mono-pill text-fg-tertiary">{result.sampleFile}</span>
            </p>
            <hr className="my-3 border-border-subtle" />
            <p className="mono-pill text-fg-tertiary mb-1.5">
              {locale === "bn" ? "মেরামতি পদক্ষেপ" : "Repair steps"}
            </p>
            <ol className="space-y-2">
              {(locale === "bn" ? stepsBn : stepsEn).map((step, i) => (
                <li key={i} className="flex items-start gap-2">
                  <span className="size-5 shrink-0 rounded-md bg-surface-2 text-fg-secondary flex items-center justify-center text-caption mono-pill">
                    {i + 1}
                  </span>
                  <p className="text-body text-fg-primary">{step}</p>
                </li>
              ))}
            </ol>
            {locale === "bn" && (
              <details className="mt-3">
                <summary className="text-caption text-fg-tertiary cursor-pointer hover:text-fg-secondary">
                  ইংরেজি পদক্ষেপ দেখুন
                </summary>
                <ol className="mt-2 space-y-2">
                  {stepsEn.map((step, i) => (
                    <li key={`en-${i}`} className="flex items-start gap-2">
                      <span className="size-5 shrink-0 rounded-md bg-surface-2 text-fg-secondary flex items-center justify-center text-caption mono-pill">
                        {i + 1}
                      </span>
                      <p className="text-body text-fg-secondary">{step}</p>
                    </li>
                  ))}
                </ol>
              </details>
            )}
          </Panel>

          {analysis && (
            <Panel title={locale === "bn" ? "প্রবাহ" : "Flow"}>
              <div className="flex flex-wrap items-center gap-3">
                <span className="mono-pill border border-border-subtle bg-surface-2 px-2 py-1 text-caption inline-flex items-center gap-1">
                  <Sparkles size={12} /> Vision
                </span>
                <span className="text-caption text-fg-tertiary">→</span>
                <span className="mono-pill border border-border-subtle bg-surface-2 px-2 py-1 text-caption inline-flex items-center gap-1">
                  <ShieldCheck size={12} /> Insight ({analysis.insight.stage})
                </span>
                {analysis.approvalPending && (
                  <>
                    <span className="text-caption text-fg-tertiary">→</span>
                    <span className="mono-pill border border-[var(--risk-medium-border)] bg-[var(--risk-medium-soft)] text-[var(--risk-medium)] px-2 py-1 text-caption">
                      Pending approval
                    </span>
                  </>
                )}
                {analysis.floorAlertId && (
                  <>
                    <span className="text-caption text-fg-tertiary">→</span>
                    <span className="mono-pill border border-border-subtle bg-surface-2 px-2 py-1 text-caption">
                      WhatsApp alert
                    </span>
                  </>
                )}
              </div>
              <div className="mt-3 flex items-center gap-4">
                {insightLink && (
                  <Link href={insightLink} className="text-caption text-accent hover:underline inline-flex items-center gap-1">
                    {locale === "bn" ? "ইনসাইট খুলুন" : "Open insight"}
                    <ExternalLink size={12} />
                  </Link>
                )}
                {approvalLink && (
                  <Link href={approvalLink} className="text-caption text-accent hover:underline inline-flex items-center gap-1">
                    {locale === "bn" ? "অনুমোদন পৃষ্ঠায় যান" : "Go to approval"}
                    <ExternalLink size={12} />
                  </Link>
                )}
              </div>
              <p className="mt-3 text-caption text-fg-tertiary mono-pill">
                {analysis.source}
              </p>
            </Panel>
          )}
        </motion.div>
      )}
    </div>
  );
}
