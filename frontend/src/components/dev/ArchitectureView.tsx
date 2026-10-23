"use client";

import Link from "next/link";
import { ArrowLeft } from "lucide-react";
import { useT } from "@/lib/useT";
import { Panel } from "@/components/ui/Panel";
import { motion } from "framer-motion";

const LAYERS = [
  { id: 7, name: "Continuous Context", subtitle: "Each decision updates the Brain. Every approval rejects a stale assumption.", prototype: "Live", target: "Phoenix / LangSmith" },
  { id: 6, name: "Action", subtitle: "Side-effects executed only after governance approves.", prototype: "Service layer", target: "LangGraph · Manager Orchestrator" },
  { id: 5, name: "Governance", subtitle: "Risk tiering, approval gates, evidence store, audit log.", prototype: "Live (policy file)", target: "Guardrails AI · Open Policy Agent" },
  { id: 4, name: "Dynamic Workforce", subtitle: "Three agents: line throughput, maintenance, manager orchestrator.", prototype: "Mock adapter", target: "LangGraph + LiteLLM" },
  { id: 3, name: "Factory Intelligence", subtitle: "Derived line efficiency, machine health, energy use; anomaly references.", prototype: "Live (derived from seed)", target: "LlamaIndex · derived graph" },
  { id: 2, name: "Knowledge", subtitle: "Entities, edges, graph of the factory floor.", prototype: "Live (React Flow)", target: "Neo4j + Qdrant" },
  { id: 1, name: "Ingestion", subtitle: "RFID, machine telemetry, energy meter, plus legacy sources.", prototype: "Synthetic sensor stream", target: "Docling · Unstructured.io" }
] as const;

const ROUTING = [
  { agent: "Line Throughput", tier: "Mid", risk: "Low", execution: "Auto" },
  { agent: "Maintenance & Uptime", tier: "Mid", risk: "Medium", execution: "Auto-suggest + threshold" },
  { agent: "Manager Orchestrator", tier: "Frontier", risk: "Low", execution: "Auto (routing + brief)" }
];

export function ArchitectureView() {
  const { t } = useT();
  return (
    <div className="px-6 md:px-8 py-6 max-w-5xl mx-auto">
      <Link href="/app" className="inline-flex items-center gap-1 text-caption text-fg-tertiary hover:text-fg-primary mb-6">
        <ArrowLeft size={12} /> Back to app
      </Link>
      <h1 className="text-display font-semibold tracking-tight">BunonBrain architecture (internal)</h1>
      <p className="mt-1 text-caption text-fg-tertiary max-w-2xl">
        Production design from the whitepaper. The current prototype boundary is drawn on each layer.
      </p>

      <Panel className="mt-6" title="Seven-layer architecture" subtitle="From ingestion to continuous learning">
        <ol className="space-y-2">
          {LAYERS.slice().reverse().map((l, idx) => (
            <motion.li
              key={l.id}
              initial={{ opacity: 0, x: -8 }}
              animate={{ opacity: 1, x: 0 }}
              transition={{ duration: 0.22, delay: idx * 0.04 }}
              className="grid grid-cols-12 gap-3 items-start surface-2 p-3"
            >
              <span className="col-span-1 mono-pill text-fg-tertiary">L{l.id}</span>
              <div className="col-span-11">
                <div className="flex items-baseline gap-2">
                  <p className="text-body text-fg-primary">{l.name}</p>
                  <p className="text-caption text-fg-tertiary">{l.subtitle}</p>
                </div>
                <div className="mt-2 grid grid-cols-1 md:grid-cols-2 gap-2 text-caption">
                  <div className="surface p-2">
                    <p className="mono-pill text-fg-tertiary">Prototype</p>
                    <p className="mt-1 text-fg-secondary">{l.prototype}</p>
                  </div>
                  <div className="surface p-2">
                    <p className="mono-pill text-fg-tertiary">Target</p>
                    <p className="mt-1 text-fg-secondary">{l.target}</p>
                  </div>
                </div>
              </div>
            </motion.li>
          ))}
        </ol>
      </Panel>

      <Panel className="mt-4" title="Agent routing table" subtitle="Config-driven through config/model-routing.yaml">
        <table className="w-full text-caption">
          <thead>
            <tr className="text-fg-tertiary">
              <th className="text-left font-medium py-2">Agent</th>
              <th className="text-left font-medium py-2">Model tier</th>
              <th className="text-left font-medium py-2">Risk</th>
              <th className="text-left font-medium py-2">Execution</th>
            </tr>
          </thead>
          <tbody>
            {ROUTING.map((r) => (
              <tr key={r.agent} className="border-t border-border-subtle">
                <td className="py-2 text-fg-primary">{r.agent}</td>
                <td className="py-2 text-fg-secondary">{r.tier}</td>
                <td className="py-2"><span className="mono-pill text-fg-secondary">{r.risk}</span></td>
                <td className="py-2 text-fg-secondary">{r.execution}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </Panel>
    </div>
  );
}