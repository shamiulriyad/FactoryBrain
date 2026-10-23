"use client";

import { useEffect, useMemo, useState, useCallback, useRef } from "react";
import ReactFlow, {
  Background,
  Controls,
  Handle,
  Position,
  MiniMap,
  type NodeProps,
  type Node,
  type Edge,
  useReactFlow,
  ReactFlowProvider
} from "reactflow";
import "reactflow/dist/style.css";
import { motion, AnimatePresence } from "framer-motion";
import {
  Filter,
  X,
  Factory,
  Cog,
  Truck,
  FileCheck,
  ShoppingBag,
  AlertTriangle,
  Search,
  Snowflake,
  SnowflakeIcon,
  Sparkles,
  Maximize2,
  Activity,
  Crosshair,
  ShieldCheck,
  Scissors
} from "lucide-react";
import { useSearchParams } from "next/navigation";
import { useT } from "@/lib/useT";
import { brainService, useBrainGraph } from "@/services/brain.service";
import { startSyntheticSensorStream } from "@/store/factory.store";
import type { BrainEntityPublic } from "@/services/types";
import { Panel } from "@/components/ui/Panel";
import { EvidenceBlock } from "@/components/evidence/EvidenceBlock";
import { Tooltip } from "@/components/ui/Tooltip";
import { Input } from "@/components/ui/Input";
import { cn } from "@/lib/cn";
import { useReducedMotion } from "@/lib/useReducedMotion";

const KIND_META: Record<BrainEntityPublic["kind"], { color: string; icon: any }> = {
  // Legacy aliases — kept so the previous string keys still resolve in
  // places that pass through them, but every visible node carries one of
  // the new kinds below.
  product: { color: "#7CE0C6", icon: ShoppingBag },
  customer: { color: "#6FA8E6", icon: Truck },
  supplier: { color: "#E4B14A", icon: Truck },
  policy: { color: "#A4ACBD", icon: FileCheck },
  workflow: { color: "#5BB98B", icon: Activity },
  goal: { color: "#C498F0", icon: Crosshair },
  risk: { color: "#E26B6B", icon: AlertTriangle },
  // Factory kinds.
  line: { color: "#7CD3F0", icon: Factory },
  machine: { color: "#F0B97C", icon: Cog },
  order: { color: "#7CE0C6", icon: ShoppingBag },
  buyer: { color: "#6FA8E6", icon: Truck },
  process: { color: "#5BB98B", icon: Scissors },
  target: { color: "#C498F0", icon: Crosshair },
  compliance: { color: "#9BA4B5", icon: ShieldCheck }
};

const STATUS_BORDER: Record<"healthy" | "at_risk" | "down", string> = {
  healthy: "rgba(124, 224, 184, 0.6)",
  at_risk: "rgba(240, 185, 80, 0.9)",
  down: "rgba(226, 107, 107, 0.95)"
};

export default function BrainPage() {
  return (
    <ReactFlowProvider>
      <BrainInner />
    </ReactFlowProvider>
  );
}

function BrainInner() {
  const { t, locale } = useT();
  const search = useSearchParams();
  const nodeParam = search?.get("node");
  const [filters, setFilters] = useState<Set<BrainEntityPublic["kind"]>>(new Set());
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [hoveredId, setHoveredId] = useState<string | null>(null);
  const [query, setQuery] = useState("");
  const [frozen, setFrozen] = useState(false);
  const rf = useReactFlow();
  const wrapperRef = useRef<HTMLDivElement | null>(null);
  const reduceMotion = useReducedMotion();

  // Start the synthetic sensor stream when the brain page mounts.
  useEffect(() => {
    const stop = startSyntheticSensorStream();
    return stop;
  }, []);

  // Effective graph — reactive against factory.store ticks.
  const graph = useBrainGraph({ kinds: Array.from(filters) });

  // Filtered by search query (overlay on top of the kind filters).
  const filteredGraph = useMemo(() => {
    if (!query.trim()) return graph;
    const q = query.trim().toLowerCase();
    const matched = new Set<string>();
    for (const n of graph.nodes) {
      if (n.label.toLowerCase().includes(q) || n.labelBn.toLowerCase().includes(q)) matched.add(n.id);
    }
    return {
      nodes: graph.nodes.filter((n) => matched.has(n.id)),
      edges: graph.edges.filter((e) => matched.has(e.source) || matched.has(e.target))
    };
  }, [graph, query]);

  const selected = selectedId ? brainService.getEntity(selectedId) : null;
  const neighbors = selectedId ? brainService.getNeighbors(selectedId) : [];

  // Pre-compute which node ids are highlighted or dimmed.
  const dimSet = useMemo(() => {
    if (!hoveredId) return null;
    const set = new Set<string>([hoveredId]);
    const direct = new Set<string>();
    for (const e of graph.edges) {
      if (e.source === hoveredId) direct.add(e.target);
      if (e.target === hoveredId) direct.add(e.source);
    }
    for (const id of direct) set.add(id);
    return { keep: set };
  }, [hoveredId, graph.edges]);

  const { nodes, edges } = useMemo(
    () => layoutGraph(filteredGraph.nodes, filteredGraph.edges),
    [filteredGraph]
  );

  const onNodeClick = useCallback((_: any, node: Node) => {
    setSelectedId(node.id);
  }, []);

  const onNodeMouseEnter = useCallback((_: any, node: Node) => setHoveredId(node.id), []);
  const onNodeMouseLeave = useCallback(() => setHoveredId(null), []);

  // Honor ?node= deep links from the command palette.
  useEffect(() => {
    if (!nodeParam) return;
    const entity = brainService.getEntity(nodeParam);
    if (!entity) return;
    setFilters((cur) => {
      if (cur.has(entity.kind)) return cur;
      const next = new Set(cur);
      next.add(entity.kind);
      return next;
    });
    setSelectedId(nodeParam);
    setTimeout(() => {
      rf.fitView({ nodes: [{ id: nodeParam }], duration: 600, padding: 0.4 });
      const el = document.querySelector(`[data-rf-node-id="${nodeParam}"]`) as HTMLElement | null;
      if (el) {
        el.classList.add("brain-node-focus");
        setTimeout(() => el.classList.remove("brain-node-focus"), 2400);
      }
    }, 280);
  }, [nodeParam, rf]);

  function toggleKind(k: BrainEntityPublic["kind"]) {
    setFilters((cur) => {
      const next = new Set(cur);
      if (next.has(k)) next.delete(k);
      else next.add(k);
      return next;
    });
  }

  function fitAll() {
    rf.fitView({ duration: 400, padding: 0.2 });
  }

  return (
    <div className="h-full flex flex-col" data-tour="brain">
      <div className="px-6 md:px-8 py-5 flex items-start justify-between gap-4 flex-wrap">
        <div>
          <h1 className="text-display font-semibold tracking-tight">{t("brain.title")}</h1>
          <p className="mt-1 text-caption text-fg-tertiary">{t("brain.subtitle")}</p>
        </div>
        <div className="flex items-center gap-2 flex-wrap">
          <div className="relative">
            <Search size={12} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-fg-tertiary pointer-events-none" />
            <Input
              placeholder={t("brain.searchPlaceholder") as string}
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              className="pl-7 w-56"
              aria-label={t("brain.searchPlaceholder") as string}
            />
          </div>
          <Tooltip content={frozen ? "Resume layout" : "Freeze layout"}>
            <motion.button
              type="button"
              onClick={() => setFrozen((f) => !f)}
              aria-pressed={frozen}
              whileHover={reduceMotion ? undefined : { y: -1 }}
              whileTap={reduceMotion ? undefined : { scale: 0.94 }}
              transition={{ type: "spring", stiffness: 420, damping: 30 }}
              className={cn(
                "size-9 rounded-md border inline-flex items-center justify-center transition-colors",
                frozen
                  ? "bg-[var(--accent-soft)] border-[var(--accent-border)] text-accent shadow-pop"
                  : "bg-surface-2 border-border-subtle text-fg-secondary hover:text-fg-primary hover:border-border-strong"
              )}
              aria-label={frozen ? "Resume layout" : "Freeze layout"}
            >
              {frozen ? <Snowflake size={14} /> : <SnowflakeIcon size={14} />}
            </motion.button>
          </Tooltip>
          <Tooltip content={t("brain.fit") as string}>
            <motion.button
              type="button"
              onClick={fitAll}
              whileHover={reduceMotion ? undefined : { y: -1 }}
              whileTap={reduceMotion ? undefined : { scale: 0.94 }}
              transition={{ type: "spring", stiffness: 420, damping: 30 }}
              className="size-9 rounded-md border border-border-subtle bg-surface-2 text-fg-secondary hover:text-fg-primary hover:border-border-strong inline-flex items-center justify-center transition-colors"
              aria-label={t("brain.fit") as string}
            >
              <Maximize2 size={14} />
            </motion.button>
          </Tooltip>
        </div>
      </div>

      <div className="flex-1 flex min-h-0">
        {/* Filter rail */}
        <div className="w-56 shrink-0 border-r border-border-subtle p-4 overflow-y-auto hidden md:block glass-soft" style={{ borderRadius: 0 }}>
          <div className="flex items-center gap-2 text-caption text-fg-tertiary mb-3">
            <Filter size={12} /> {t("brain.filter")}
          </div>
          <div className="space-y-1.5">
            {([
              "line",
              "machine",
              "order",
              "buyer",
              "supplier",
              "process",
              "target",
              "compliance",
              "risk"
            ] as BrainEntityPublic["kind"][]).map((k) => {
              const meta = KIND_META[k];
              const Icon = meta.icon;
              const active = filters.has(k);
              const count = graph.nodes.filter((n) => n.kind === k).length;
              return (
                <motion.button
                  key={k}
                  type="button"
                  onClick={() => toggleKind(k)}
                  aria-pressed={active}
                  whileHover={reduceMotion ? undefined : { y: -1 }}
                  whileTap={reduceMotion ? undefined : { scale: 0.97 }}
                  transition={{ type: "spring", stiffness: 420, damping: 30 }}
                  className={cn(
                    "w-full flex items-center gap-2 px-2 h-8 rounded-md text-caption",
                    "transition-[background-color,color,border-color] duration-150",
                    active
                      ? "bg-surface-2 text-fg-primary border border-border-subtle shadow-pop"
                      : "text-fg-secondary hover:text-fg-primary hover:bg-surface border border-transparent"
                  )}
                >
                  <span
                    className="size-2 rounded-full transition-colors"
                    style={{ backgroundColor: active ? meta.color : "rgba(255,255,255,0.2)" }}
                  />
                  <Icon size={13} style={active ? { color: meta.color } : undefined} />
                  <span className="flex-1 text-left">{t(`brain.entity.${k}` as any)}</span>
                  <span className="text-[10px] text-fg-tertiary mono-pill">{count}</span>
                </motion.button>
              );
            })}
          </div>
          <div className="mt-6 text-caption text-fg-tertiary">
            <p>{t("brain.legend")}</p>
            <div className="mt-2 space-y-1.5">
              {([
                "line",
                "machine",
                "order",
                "buyer",
                "supplier",
                "process",
                "target",
                "compliance",
                "risk"
              ] as BrainEntityPublic["kind"][]).map((k) => {
                const meta = KIND_META[k];
                return (
                  <div key={k} className="flex items-center gap-2 text-caption text-fg-secondary">
                    <span className="size-2 rounded-full" style={{ backgroundColor: meta.color }} />
                    {t(`brain.entity.${k}` as any)}
                  </div>
                );
              })}
              <div className="pt-2 mt-2 border-t border-border-subtle space-y-1">
                <div className="flex items-center gap-2 text-caption text-fg-tertiary">
                  <span className="size-2 rounded-full" style={{ background: STATUS_BORDER.healthy }} />
                  {t("brain.status.healthy" as any)}
                </div>
                <div className="flex items-center gap-2 text-caption text-fg-tertiary">
                  <span className="size-2 rounded-full" style={{ background: STATUS_BORDER.at_risk }} />
                  {t("brain.status.at_risk" as any)}
                </div>
                <div className="flex items-center gap-2 text-caption text-fg-tertiary">
                  <span className="size-2 rounded-full" style={{ background: STATUS_BORDER.down }} />
                  {t("brain.status.down" as any)}
                </div>
              </div>
            </div>
            <p className="mt-4 text-[11px] text-fg-tertiary">
              <Sparkles size={10} className="inline mr-1" />
              Node size reflects importance. Border colour reflects status: green = healthy, amber = at-risk, red = down.
            </p>
          </div>
        </div>

        {/* Canvas */}
        <div ref={wrapperRef} className="flex-1 relative brain-canvas">
          <ReactFlow
            nodes={nodes.map((n) => {
              const dim = dimSet && !dimSet.keep.has(n.id);
              return {
                ...n,
                style: { ...n.style, opacity: dim ? 0.18 : 1, transition: "opacity 200ms" }
              };
            })}
            edges={edges.map((e) => {
              const dim =
                dimSet &&
                !(
                  dimSet.keep.has(e.source) ||
                  dimSet.keep.has(e.target) ||
                  e.source === hoveredId ||
                  e.target === hoveredId
                );
              return {
                ...e,
                style: { ...e.style, opacity: dim ? 0.1 : 1 }
              };
            })}
            onNodeClick={onNodeClick}
            onNodeMouseEnter={onNodeMouseEnter}
            onNodeMouseLeave={onNodeMouseLeave}
            nodeTypes={nodeTypes}
            fitView
            proOptions={{ hideAttribution: true }}
            minZoom={0.2}
            maxZoom={2.5}
            nodesDraggable={!frozen}
            elementsSelectable
            panOnDrag
            zoomOnScroll
            zoomOnPinch
          >
            <Background gap={20} color="rgba(255,255,255,0.04)" />
            <Controls position="bottom-right" showInteractive={false} />
            <MiniMap
              pannable
              zoomable
              maskColor="rgba(0,0,0,0.4)"
              nodeStrokeWidth={2}
              nodeColor={(n) => {
                const kind = (n.data as any)?.kind as BrainEntityPublic["kind"] | undefined;
                return kind ? KIND_META[kind].color : "#888";
              }}
              style={{
                background: "var(--bg-surface)",
                border: "1px solid var(--border-subtle)"
              }}
            />
          </ReactFlow>

          {/* Hover floating card */}
          <AnimatePresence>
            {hoveredId && !selected && (() => {
              const entity = brainService.getEntity(hoveredId);
              if (!entity) return null;
              const meta = KIND_META[entity.kind];
              const Icon = meta.icon;
              const dCount = graph.edges.filter(
                (e) => e.source === entity.id || e.target === entity.id
              ).length;
              return (
                <motion.div
                  key="hover-card"
                  initial={reduceMotion ? { opacity: 0 } : { opacity: 0, y: 4, scale: 0.97 }}
                  animate={{ opacity: 1, y: 0, scale: 1 }}
                  exit={reduceMotion ? { opacity: 0 } : { opacity: 0, y: 4, scale: 0.97 }}
                  transition={{ duration: 0.16, ease: [0.16, 1, 0.3, 1] }}
                  className="absolute top-4 right-4 z-10 max-w-xs glass-strong shadow-glass rounded-md p-3"
                  style={{ borderTop: `1px solid ${meta.color}` }}
                >
                  <div className="flex items-center gap-2">
                    <span
                      className="size-6 rounded flex items-center justify-center"
                      style={{ backgroundColor: `${meta.color}22`, color: meta.color }}
                    >
                      <Icon size={12} />
                    </span>
                    <p className="text-body text-fg-primary truncate">{locale === "bn" ? entity.labelBn : entity.label}</p>
                  </div>
                  <p className="mt-1 mono-pill text-fg-tertiary">{entity.kind}</p>
                  {entity.meta && Object.keys(entity.meta).length > 0 && (
                    <ul className="mt-2 space-y-0.5">
                      {Object.entries(entity.meta).slice(0, 3).map(([k, v]) => (
                        <li key={k} className="flex items-center justify-between text-[11px]">
                          <span className="text-fg-tertiary">{k}</span>
                          <span className="text-fg-secondary mono-pill">{String(v)}</span>
                        </li>
                      ))}
                    </ul>
                  )}
                  <p className="mt-2 text-[11px] text-fg-tertiary">{dCount} {t("brain.connections")}</p>
                </motion.div>
              );
            })()}
          </AnimatePresence>

          {frozen && (
            <motion.div
              initial={reduceMotion ? false : { opacity: 0, y: -4 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ duration: 0.2, ease: [0.16, 1, 0.3, 1] }}
              className="absolute top-4 left-4 z-10 px-2 h-7 rounded-md border border-border-subtle glass-soft text-fg-secondary text-caption inline-flex items-center gap-1.5 shadow-pop"
            >
              <Snowflake size={12} className="text-accent" /> Layout frozen
            </motion.div>
          )}
        </div>

        {/* Detail panel */}
        <AnimatePresence>
          {selected && (
            <motion.aside
              key="detail"
              initial={reduceMotion ? { opacity: 0 } : { x: 360, opacity: 0 }}
              animate={{ x: 0, opacity: 1 }}
              exit={reduceMotion ? { opacity: 0 } : { x: 360, opacity: 0 }}
              transition={{ duration: 0.26, ease: [0.16, 1, 0.3, 1] }}
              className="w-96 shrink-0 border-l border-border-subtle glass-strong overflow-y-auto"
            >
              <div className="p-4 flex items-start justify-between gap-3">
                <div className="min-w-0">
                  <div className="flex items-center gap-2">
                    <span
                      className="size-2 rounded-full"
                      style={{ backgroundColor: KIND_META[selected.kind].color }}
                    />
                    <span className="mono-pill text-fg-tertiary">{selected.kind}</span>
                  </div>
                  <p className="mt-2 text-title text-fg-primary">{locale === "bn" ? selected.labelBn : selected.label}</p>
                  {selected.meta && Object.keys(selected.meta).length > 0 && (
                    <ul className="mt-3 space-y-1">
                      {Object.entries(selected.meta).map(([k, v]) => (
                        <li key={k} className="flex items-center justify-between text-caption">
                          <span className="text-fg-tertiary">{k}</span>
                          <span className="text-fg-secondary mono-pill">{String(v)}</span>
                        </li>
                      ))}
                    </ul>
                  )}
                </div>
                <motion.button
                  type="button"
                  onClick={() => setSelectedId(null)}
                  whileHover={reduceMotion ? undefined : { y: -1 }}
                  whileTap={reduceMotion ? undefined : { scale: 0.94 }}
                  transition={{ type: "spring", stiffness: 420, damping: 30 }}
                  className="size-7 rounded-md hover:bg-surface-2 flex items-center justify-center text-fg-tertiary hover:text-fg-primary transition-colors"
                  aria-label="Close detail"
                >
                  <X size={14} />
                </motion.button>
              </div>

              {neighbors.length > 0 && (
                <div className="px-4 pb-4">
                  <p className="mono-pill text-fg-tertiary mb-2">{t("brain.neighbors")}</p>
                  <div className="flex flex-wrap gap-1.5">
                    {neighbors.map((n) => (
                      <motion.button
                        key={n.id}
                        type="button"
                        onClick={() => {
                          setSelectedId(n.id);
                          rf.fitView({ nodes: [{ id: n.id }], duration: 500, padding: 0.4 });
                        }}
                        whileHover={reduceMotion ? undefined : { y: -1 }}
                        whileTap={reduceMotion ? undefined : { scale: 0.96 }}
                        transition={{ type: "spring", stiffness: 420, damping: 30 }}
                        className="h-7 px-2 rounded-md border border-border-subtle bg-surface-2 text-caption text-fg-secondary hover:text-fg-primary hover:border-border-strong transition-colors"
                      >
                        {locale === "bn" ? n.labelBn : n.label}
                      </motion.button>
                    ))}
                  </div>
                </div>
              )}

              <div className="px-4 pb-6">
                <EvidenceBlock
                  refs={[
                    { domain: entityToDomain(selected.kind), count: 1, previewIds: [selected.id] }
                  ]}
                />
              </div>
            </motion.aside>
          )}
        </AnimatePresence>
      </div>
    </div>
  );
}

function entityToDomain(kind: BrainEntityPublic["kind"]): any {
  switch (kind) {
    case "line": return "orders";
    case "machine": return "inventory";
    case "order": return "orders";
    case "buyer": return "customers";
    case "supplier": return "suppliers";
    case "process": return "orders";
    case "target": return "orders";
    case "compliance": return "policies";
    case "risk": return "orders";
    // Legacy aliases (so any leftover seed still maps).
    case "product": return "products";
    case "customer": return "customers";
    case "policy": return "policies";
    case "workflow": return "orders";
    case "goal": return "orders";
  }
}

type NodeData = {
  kind: BrainEntityPublic["kind"];
  label: string;
  weight: number;
  status?: "healthy" | "at_risk" | "down";
  selected?: boolean;
  hovered?: boolean;
  dim?: boolean;
};

const nodeTypes = {
  entity: EntityNode
};

function EntityNode({ data }: NodeProps<NodeData>) {
  const meta = KIND_META[data.kind];
  const Icon = meta.icon;
  // Map weight (0..1) into a size factor (1.0 → 1.5x, 0.5 → 1.0x, 0.0 → 0.75x)
  const factor = 0.75 + (data.weight ?? 0.5) * 0.75;
  const statusBorder = data.status ? STATUS_BORDER[data.status] : undefined;
  const pulse = data.status === "at_risk" || data.status === "down";
  return (
    <div
      className={cn(
        "rounded-md border bg-surface px-2.5 py-1.5 flex items-center gap-2 transition-all duration-200",
        data.selected ? "shadow-pop" : "",
        pulse ? "animate-[brain-status-pulse_2.4s_ease-in-out_infinite]" : "",
        "brain-entity-node"
      )}
      style={{
        borderColor: data.selected || data.hovered
          ? meta.color
          : statusBorder ?? "var(--border-subtle)",
        borderWidth: statusBorder ? 1.5 : 1,
        boxShadow: data.status === "down" ? `0 0 0 2px ${STATUS_BORDER.down}` : undefined,
        transform: `scale(${factor})`,
        transformOrigin: "center center"
      }}
    >
      <Handle type="target" position={Position.Top} style={{ opacity: 0 }} />
      <Handle type="source" position={Position.Bottom} style={{ opacity: 0 }} />
      <span
        className="size-5 rounded flex items-center justify-center"
        style={{ backgroundColor: `${meta.color}22`, color: meta.color }}
      >
        <Icon size={11} />
      </span>
      <span className="text-caption text-fg-primary whitespace-nowrap">{data.label}</span>
    </div>
  );
}

function layoutGraph(nodes: BrainEntityPublic[], edges: any[]): { nodes: Node[]; edges: Edge[] } {
  const kindBuckets: Record<string, BrainEntityPublic[]> = {};
  for (const n of nodes) (kindBuckets[n.kind] ??= []).push(n);

  const order: BrainEntityPublic["kind"][] = [
    "risk",
    "target",
    "process",
    "compliance",
    "supplier",
    "buyer",
    "order",
    "machine",
    "line"
  ];
  const radiusByKind: Record<string, number> = {
    risk: 80,
    target: 140,
    process: 200,
    compliance: 260,
    supplier: 320,
    buyer: 380,
    order: 440,
    machine: 500,
    line: 560
  };

  const rfNodes: Node[] = [];
  let angle = 0;
  for (const kind of order) {
    const list = kindBuckets[kind] ?? [];
    if (!list.length) continue;
    const r = radiusByKind[kind] ?? 320;
    list.forEach((n, i) => {
      const a = angle + (i / Math.max(1, list.length)) * Math.PI * 2;
      rfNodes.push({
        id: n.id,
        type: "entity",
        position: { x: 700 + Math.cos(a) * r, y: 460 + Math.sin(a) * r },
        data: { kind: n.kind, label: n.label, weight: n.weight ?? 0.5, status: n.status }
      });
    });
    angle += Math.PI / 6;
  }

  const edgeSet = new Set<string>();
  const rfEdges: Edge[] = edges
    .filter((e) => rfNodes.find((n) => n.id === e.source) && rfNodes.find((n) => n.id === e.target))
    .map((e) => {
      const id = `e-${e.source}-${e.target}`;
      if (edgeSet.has(id)) return null;
      edgeSet.add(id);
      return {
        id,
        source: e.source,
        target: e.target,
        animated: false,
        className: e.strong ? "strong" : "",
        style: { strokeWidth: 1 + Math.round(e.weight * 1.5) }
      };
    })
    .filter(Boolean) as Edge[];

  return { nodes: rfNodes, edges: rfEdges };
}