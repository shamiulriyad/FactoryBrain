// Reactive brain service — derived from the factory store.
// Components subscribe via useBrainVersion() and re-derive when tick changes.

import { useMemo } from "react";
import { useFactoryStore } from "@/store/factory.store";
import { buildGraph } from "@/data/graph";
import { dataset } from "@/services/dataset";
import type { BrainGraph, BrainEntityPublic } from "@/services/types";

function derive(): BrainGraph {
  // The factory graph pulls from factory.store AND from the legacy supplier/
  // customer/policy/goals seeds. We still call buildGraph() with the same
  // arguments the dataset module uses, but factory.store has already been
  // mutated by ingest() when this runs.
  const g = buildGraph(
    dataset.products,
    dataset.customers,
    dataset.suppliers,
    dataset.orders,
    dataset.goals
  );
  return { nodes: g.nodes as BrainEntityPublic[], edges: g.edges };
}

let cachedTick = -1;
let cachedGraph: BrainGraph = derive();
const subscribers = new Set<(g: BrainGraph) => void>();

export const brainService = {
  /** Imperative read — does not subscribe. */
  getGraph(filters?: { kinds?: BrainEntityPublic["kind"][]; query?: string }): BrainGraph {
    const tick = useFactoryStore.getState().tick;
    if (tick !== cachedTick) {
      cachedTick = tick;
      cachedGraph = derive();
      subscribers.forEach((cb) => cb(cachedGraph));
    }
    const graph = cachedGraph;
    const kindFilter = filters?.kinds?.length ? new Set(filters.kinds) : null;
    const q = filters?.query?.toLowerCase();
    const nodes = graph.nodes
      .filter((n) => (kindFilter ? kindFilter.has(n.kind) : true))
      .filter((n) =>
        q
          ? n.label.toLowerCase().includes(q) ||
            ((n as any).labelBn ?? "").toLowerCase().includes(q)
          : true
      );
    const ids = new Set(nodes.map((n) => n.id));
    const edges = graph.edges.filter((e) => ids.has(e.source) && ids.has(e.target));
    return { nodes, edges };
  },
  getEntity(id: string): BrainEntityPublic | undefined {
    return cachedGraph.nodes.find((n) => n.id === id);
  },
  getNeighbors(id: string): BrainEntityPublic[] {
    const edgeIds = cachedGraph.edges
      .filter((e) => e.source === id || e.target === id)
      .map((e) => (e.source === id ? e.target : e.source));
    return cachedGraph.nodes.filter((n) => edgeIds.includes(n.id));
  },
  /** Subscribe to graph changes. Returns an unsubscribe function. */
  subscribe(cb: (g: BrainGraph) => void): () => void {
    subscribers.add(cb);
    return () => subscribers.delete(cb);
  }
};

/** React hook — re-derives the brain graph when the factory store ticks. */
export function useBrainGraph(filters?: { kinds?: BrainEntityPublic["kind"][]; query?: string }): BrainGraph {
  const tick = useFactoryStore((s) => s.tick);
  return useMemo(() => {
    if (tick !== cachedTick) {
      cachedTick = tick;
      cachedGraph = derive();
    }
    const graph = cachedGraph;
    const kindFilter = filters?.kinds?.length ? new Set(filters.kinds) : null;
    const q = filters?.query?.toLowerCase();
    const nodes = graph.nodes
      .filter((n) => (kindFilter ? kindFilter.has(n.kind) : true))
      .filter((n) =>
        q
          ? n.label.toLowerCase().includes(q) ||
            ((n as any).labelBn ?? "").toLowerCase().includes(q)
          : true
      );
    const ids = new Set(nodes.map((n) => n.id));
    const edges = graph.edges.filter((e) => ids.has(e.source) && ids.has(e.target));
    return { nodes, edges };
  // kinds is intentionally stringified for stable dep identity; both kinds & query
  // are read below via filters.kinds.length and filters.query.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tick, filters?.kinds?.join(","), filters?.query]);
}