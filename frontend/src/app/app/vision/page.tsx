"use client";

import { useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { Camera, ExternalLink, ScanLine } from "lucide-react";
import { useT } from "@/lib/useT";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge, RISK_TONE } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { Skeleton } from "@/components/ui/Skeleton";
import { useToast } from "@/components/ui/Toast";
import { useResource, announceDataChanged } from "@/lib/useResource";
import { apiJson, errorMessage } from "@/lib/api";
import { canOperate, useAuthStore } from "@/lib/auth";
import { refreshPendingApprovals } from "@/services/approvals.api";
import { cn } from "@/lib/cn";
import type { VisionAllowed, VisionAnalyzeResponse } from "@/services/contracts";

export default function VisionPage() {
  const { t, locale } = useT();
  const toast = useToast();
  const operator = canOperate(useAuthStore((s) => s.user));
  const allowed = useResource<VisionAllowed>("/api/vision/analyze");
  const files = useMemo(() => allowed.data?.allowedSampleFiles ?? [], [allowed.data]);
  const [file, setFile] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [analysis, setAnalysis] = useState<VisionAnalyzeResponse | null>(null);

  useEffect(() => {
    if (!file && files.length) setFile(files[0]);
  }, [files, file]);

  async function analyze() {
    if (busy || !file) return;
    setBusy(true);
    setError(null);
    setAnalysis(null);
    try {
      const res = await apiJson<VisionAnalyzeResponse>("/api/vision/analyze", { method: "POST", body: { sampleFile: file } });
      setAnalysis(res);
      announceDataChanged();
      void refreshPendingApprovals();
      toast.push({ title: "Analysis stored", description: res.approvalPending ? "An approval was queued." : res.result.anomalyLabelEn });
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  const result = analysis?.result;
  const steps = result ? (locale === "bn" ? result.repairStepsBn : result.repairStepsEn) : [];

  return (
    <div className="page page-narrow" data-tour="vision">
      <PageHeader eyebrow="Intelligence" title={t("vision.title")} description={t("vision.body")} />

      <Callout tone="demo" className="mt-5" title="Staged samples, simulated analysis">
        {allowed.data ? `${allowed.data.source} Method: ${allowed.data.method}.` : "The API analyses one of four staged sample images; camera upload is not supported yet."}
        {!operator && " Running an analysis needs the Operator or Admin role."}
      </Callout>

      <Panel className="mt-5" title={locale === "bn" ? "স্টেজড নমুনা বেছে নিন" : "Choose a staged sample"} icon={<Camera size={14} />}>
        {allowed.loading ? (
          <div className="grid grid-cols-2 gap-2">{[0, 1, 2, 3].map((i) => <Skeleton key={i} className="h-14" />)}</div>
        ) : allowed.error ? (
          <Callout tone="danger" title="Could not load the sample list" action={<Button size="sm" onClick={() => void allowed.reload()}>Retry</Button>}>{errorMessage(allowed.error)}</Callout>
        ) : (
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-2" role="radiogroup" aria-label="Sample image">
            {files.map((f) => (
              <button
                key={f}
                type="button"
                role="radio"
                aria-checked={file === f}
                onClick={() => { setFile(f); setAnalysis(null); setError(null); }}
                className={cn(
                  "text-left rounded-[var(--radius-control)] border p-3 transition-colors flex items-center gap-3",
                  file === f ? "border-[var(--accent-border)] bg-[var(--accent-soft)]" : "border-border-subtle bg-surface-2 hover:border-border-strong"
                )}
              >
                <span className="size-9 rounded-md bg-[var(--bg-surface)] border border-border-subtle flex items-center justify-center text-fg-tertiary shrink-0"><ScanLine size={16} /></span>
                <span className="font-mono text-caption text-fg-primary truncate">{f.replace(/\.jpg$/, "")}</span>
              </button>
            ))}
          </div>
        )}
        <div className="mt-4 flex flex-wrap items-center gap-3">
          <Button variant="primary" onClick={() => void analyze()} disabled={busy || !operator || !file} data-testid="vision-analyze">
            <ScanLine size={14} /> {busy ? t("vision.analyzing") : t("vision.upload")}
          </Button>
          {!operator && <span className="text-caption text-fg-tertiary">{t("auth.forbidden")}</span>}
        </div>
        {error && <Callout tone="danger" className="mt-3" title={t("vision.errorTitle")}>{error}</Callout>}
      </Panel>

      {result && analysis && (
        <div className="mt-4 space-y-4">
          <Panel
            title={t("vision.repairTitle")}
            right={<Badge tone={result.confidence >= 0.85 ? "success" : "warning"}>{Math.round(result.confidence * 100)}% confidence</Badge>}
            testId="vision-result"
          >
            <p className="text-title font-semibold text-fg-primary">{locale === "bn" ? result.anomalyLabelBn : result.anomalyLabelEn}</p>
            <p className="mt-1 text-caption text-fg-tertiary">
              {result.machineId ? `Machine ${result.machineId} · ` : ""}<span className="font-mono">{result.sampleFile}</span>
            </p>
            <ol className="mt-4 space-y-2">
              {steps.map((step, i) => (
                <li key={i} className="flex items-start gap-2.5">
                  <span className="size-6 shrink-0 rounded-full bg-surface-2 border border-border-subtle text-[11px] font-semibold flex items-center justify-center text-fg-secondary">{i + 1}</span>
                  <p className="text-body text-fg-primary">{step}</p>
                </li>
              ))}
            </ol>
          </Panel>
          <Panel title={locale === "bn" ? "প্রবাহ" : "What happened on the server"}>
            <ul className="space-y-2 text-caption text-fg-secondary">
              <li>• Vision result <span className="font-mono">{result.id}</span> stored.</li>
              {analysis.insight && (
                <li className="flex flex-wrap items-center gap-2">• Insight created: <span className="text-fg-primary">{analysis.insight.title}</span> <Badge tone={RISK_TONE[analysis.insight.riskTier]}>{analysis.insight.riskTier}</Badge></li>
              )}
              {analysis.approvalPending && <li>• Approval queued{analysis.approvalReason ? ` (${analysis.approvalReason})` : ""}.</li>}
              {analysis.floorAlertId && <li>• Floor alert raised on the simulated WhatsApp channel.</li>}
            </ul>
            <div className="mt-3 flex flex-wrap gap-4">
              {analysis.approvalPending && analysis.insight && (
                <Link href={`/app/approvals?focus=${encodeURIComponent(analysis.insight.id)}`} className="text-caption text-accent hover:underline inline-flex items-center gap-1">
                  Go to the approval <ExternalLink size={12} />
                </Link>
              )}
              {analysis.floorAlertId && (
                <Link href="/app/activity" className="text-caption text-accent hover:underline inline-flex items-center gap-1">
                  See floor alerts <ExternalLink size={12} />
                </Link>
              )}
            </div>
          </Panel>
        </div>
      )}
    </div>
  );
}
