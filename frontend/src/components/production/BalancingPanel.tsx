"use client";

// Line-balancing workspace: run the analysis for a line and style, see what
// is missing when it can't run, and list proposals. A proposal never changes
// the line by itself: it is reviewed in the approval queue (high risk, Admin)
// and then applied on its own page.

import { useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { Scale } from "lucide-react";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Field, FilterSelect, Select } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { EmptyState } from "@/components/ui/EmptyState";
import { SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { useOnDataChanged, useResource, announceDataChanged } from "@/lib/useResource";
import { errorMessage, isApiError } from "@/lib/api";
import { PROPOSAL_STATUS_LABEL, fmt, fmtPct } from "@/lib/production";
import { analyzeBalance, productionPaths } from "@/services/production.api";
import { PROPOSAL_STATUSES, type BalanceAnalysis, type BalanceProposalListItem, type Paged, type ProductionLine, type ProductionStyle } from "@/services/contracts";
import { ProposalStatusBadge } from "./shared";

export function BalancingPanel({ operator, lines, styles }: { operator: boolean; lines: ProductionLine[]; styles: ProductionStyle[] }) {
  const toast = useToast();
  const router = useRouter();
  const [f, setF] = useState({ lineId: "", styleId: "", workstations: "", target: "" });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [analysis, setAnalysis] = useState<BalanceAnalysis | null>(null);
  const [filter, setFilter] = useState({ status: "", page: 1 });
  const proposals = useResource<Paged<BalanceProposalListItem>>(productionPaths.proposals({ ...filter, pageSize: 20 }));
  useOnDataChanged(() => void proposals.reload());
  const line = lines.find((l) => l.id === f.lineId);
  const fe = (k: string) => (isApiError(error) ? error.fieldErrors[k]?.join(" ") ?? null : null);

  async function analyze(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    setAnalysis(null);
    try {
      const r = await analyzeBalance({
        lineId: f.lineId, styleId: f.styleId, workstations: f.workstations ? Number(f.workstations) : undefined,
        targetOutputPerHour: f.target ? Number(f.target) : undefined
      });
      setAnalysis(r);
      if (r.proposal) {
        toast.push({ title: `Proposal ${r.proposal.reference} created`, description: "Review it, then submit it for approval." });
        announceDataChanged();
        router.push(`/app/production/balancing/${r.proposal.id}`);
      }
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="space-y-4" data-testid="production-balancing">
      <Panel title="Analyse a line" subtitle="Ranked Positional Weight heuristic over the style's operation bulletin, respecting precedence, machine types and the line's declared workstations. The result is a proposal, stored separately from the current layout.">
        {!operator ? <Callout tone="info">Operators and Admins can run the analysis. You can open existing proposals below.</Callout> : (
          <form className="grid grid-cols-1 sm:grid-cols-5 gap-3 items-end" onSubmit={analyze}>
            <Field label="Line" error={fe("lineId")}>
              {(a) => (
                <Select id={a.id} value={f.lineId} onChange={(e) => setF({ ...f, lineId: e.target.value })} required data-testid="balance-line">
                  <option value="" disabled>Choose</option>
                  {lines.filter((l) => l.isActive).map((l) => <option key={l.id} value={l.id}>{l.name} ({l.workstations})</option>)}
                </Select>
              )}
            </Field>
            <Field label="Style" error={fe("styleId")}>
              {(a) => (
                <Select id={a.id} value={f.styleId} onChange={(e) => setF({ ...f, styleId: e.target.value })} required data-testid="balance-style">
                  <option value="" disabled>Choose</option>
                  {styles.filter((s) => s.isActive).map((s) => <option key={s.id} value={s.id}>{s.code} ({s.operations.length} ops)</option>)}
                </Select>
              )}
            </Field>
            <Field label="Workstations" hint={line ? `at most ${line.workstations}` : undefined} error={fe("workstations")}>
              {(a) => <Input id={a.id} aria-describedby={a.describedBy} type="number" min={1} max={line?.workstations} placeholder={line ? String(line.workstations) : ""} value={f.workstations} onChange={(e) => setF({ ...f, workstations: e.target.value })} data-testid="balance-workstations" />}
            </Field>
            <Field label="Target (pcs/h, optional)" error={fe("targetOutputPerHour")}>
              {(a) => <Input id={a.id} type="number" min={0} step="0.1" value={f.target} onChange={(e) => setF({ ...f, target: e.target.value })} />}
            </Field>
            <Button type="submit" variant="primary" disabled={busy} data-testid="balance-run">{busy ? "Analysing…" : "Run analysis"}</Button>
          </form>
        )}
        {error ? <Callout tone="danger" className="mt-3" title="Analysis not run">{errorMessage(error)}</Callout> : null}
        {analysis && !analysis.proposal && (
          <Callout tone="warning" className="mt-3" title="No proposal: the analysis can't produce a valid balance" testId="balance-missing">
            <ul className="list-disc pl-4">{analysis.missingInputs.map((m, i) => <li key={i}>{m}</li>)}</ul>
            <p className="mt-1">Nothing was stored. Fix the inputs under Setup → Styles and run it again.</p>
          </Callout>
        )}
      </Panel>

      <Panel title="Proposals" flush right={
        <FilterSelect label="Status" value={filter.status} onChange={(v) => setFilter({ status: v, page: 1 })}>
          <option value="">All</option>
          {PROPOSAL_STATUSES.map((s) => <option key={s} value={s}>{PROPOSAL_STATUS_LABEL[s]}</option>)}
        </FilterSelect>
      }>
        {proposals.loading ? <div className="p-4"><SkeletonRows rows={3} /></div> : proposals.error && !proposals.data ? (
          <div className="p-4"><Callout tone="danger" title="Could not load proposals">{errorMessage(proposals.error)}</Callout></div>
        ) : (proposals.data?.items.length ?? 0) === 0 ? (
          <EmptyState icon={<Scale size={20} />} title="No balance proposals yet" body="Run an analysis for a line and a style with an operation bulletin." />
        ) : (
          <div className="overflow-x-auto">
            <table className="data-table" data-testid="proposals-table">
              <thead><tr><th>Proposal</th><th>Line</th><th>Style</th><th className="num">Workstations</th><th className="num">Cycle time</th><th className="num">Capacity</th><th className="num">Balance eff.</th><th>Status</th></tr></thead>
              <tbody>
                {proposals.data!.items.map((p) => (
                  <tr key={p.id}>
                    <td><Link href={`/app/production/balancing/${p.id}`} className="font-mono text-[12px] text-accent hover:underline">{p.reference}</Link><span className="block text-caption text-fg-tertiary">{p.createdByEmail}</span></td>
                    <td className="font-mono text-[12px]">{p.lineId}</td>
                    <td>{p.styleCode}</td>
                    <td className="num">{p.workstations}</td>
                    <td className="num">{fmt(p.cycleTimeMinutes, 3)} min</td>
                    <td className="num">{fmt(p.capacityPerHour, 1)}/h</td>
                    <td className="num">{fmtPct(p.balanceEfficiencyPct)}</td>
                    <td><ProposalStatusBadge status={p.status} /></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Panel>
    </div>
  );
}
