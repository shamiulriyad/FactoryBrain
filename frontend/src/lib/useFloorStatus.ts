"use client";

// Live production-floor status for the sidebar, the floor map and the floor
// pages (GET /api/factories/{id}/floor-status), plus the open-alert and
// pending-approval counts for the sidebar badges. One poller feeds them all.

import { useEffect } from "react";
import { create } from "zustand";
import { refreshUnreadAlerts, useUnreadAlerts } from "@/services/floorAlerts.api";
import { refreshPendingApprovals, usePendingApprovals } from "@/services/approvals.api";
import { apiJson, errorMessage } from "@/lib/api";
import { can, useAuthStore } from "@/lib/auth";
import { P } from "@/lib/access";

export type FloorStatus = "ok" | "watch" | "act";

export type FloorLine = { id: string; name: string; workstations: number; machines: number; machinesDown: number; activeAlerts: number };
export type FloorAlert = { id: string; number: number; title: string; severity: string; status: string; lineId?: string | null; lastOccurredAt: string };

export type FloorRollup = {
  id: string;
  code: string;
  name: string;
  isActive: boolean;
  status: FloorStatus;
  reasons: string[];
  lines: FloorLine[];
  workers: number;
  checkedIn: number;
  workstations: number;
  machines: number;
  machineStates: Record<string, number>;
  cameras: number;
  devicesOnline: number;
  devicesOffline: number;
  activeAlerts: number;
  highAlerts: number;
  openExceptions: number;
  recentAlerts: FloorAlert[];
};

export type FloorStatusResponse = {
  factoryId: string;
  factoryCode: string;
  generatedAt: string;
  floors: FloorRollup[];
  unplacedLines: number;
  note: string;
};

interface FloorStatusState {
  data: FloorStatusResponse | null;
  error: string | null;
  /** True once the first request has finished (with data or an error). */
  loaded: boolean;
  /** The factory chosen on the floor map; null = the user's first factory. */
  factoryId: string | null;
}

const useFloorStatusStore = create<FloorStatusState>(() => ({ data: null, error: null, loaded: false, factoryId: null }));

type FactoryRow = { id: string; code: string; isActive: boolean; lineCount: number };

/** The user's busiest factory (most lines), so the first view isn't an empty one. */
async function defaultFactoryId(): Promise<string | null> {
  const user = useAuthStore.getState().user;
  const mine = user?.factories ?? [];
  if (can(user, P.FactoriesView, P.UsersView)) {
    const ids = new Set(mine.map((f) => f.id));
    const all = (await apiJson<FactoryRow[]>("/api/factories")).filter((f) => f.isActive && (ids.size === 0 || ids.has(f.id)));
    const best = all.sort((a, b) => b.lineCount - a.lineCount || a.code.localeCompare(b.code))[0];
    if (best) return best.id;
  }
  return mine.length > 0 ? [...mine].sort((a, b) => a.code.localeCompare(b.code))[0].id : null;
}

let inFlight: Promise<void> | null = null;

/** Fetch the rollup now (deduplicated). Safe to call from anywhere. */
export function refreshFloorStatus(): Promise<void> {
  const user = useAuthStore.getState().user;
  if (!can(user, P.MonitoringView, P.ProductionView)) return Promise.resolve();
  if (inFlight) return inFlight;
  inFlight = (async () => {
    try {
      const fid = useFloorStatusStore.getState().factoryId ?? (await defaultFactoryId());
      if (!fid) {
        useFloorStatusStore.setState({ data: null, error: null, loaded: true });
        return;
      }
      const data = await apiJson<FloorStatusResponse>(`/api/factories/${fid}/floor-status`);
      useFloorStatusStore.setState({ data, error: null, loaded: true });
    } catch (e) {
      useFloorStatusStore.setState({ error: errorMessage(e), loaded: true });
    } finally {
      inFlight = null;
    }
  })();
  return inFlight;
}

/** Switch the factory the floor views show. */
export function setFloorFactory(factoryId: string | null): void {
  useFloorStatusStore.setState({ factoryId, data: null, loaded: false });
  void refreshFloorStatus();
}

/** Starts the live feed (floor rollup and badge counts). Returns a stop function. */
export function startFloorStatusFeed(intervalMs = 30_000): () => void {
  const tick = () => {
    if (document.visibilityState === "hidden") return;
    // Only poll what the signed-in user may read (the API would answer 403).
    const user = useAuthStore.getState().user;
    if (can(user, P.MonitoringView, P.ProductionView)) {
      void refreshUnreadAlerts();
      void refreshFloorStatus();
    }
    if (can(user, P.ApprovalsView)) void refreshPendingApprovals();
  };
  tick();
  const id = window.setInterval(tick, intervalMs);
  document.addEventListener("visibilitychange", tick);
  return () => {
    window.clearInterval(id);
    document.removeEventListener("visibilitychange", tick);
  };
}

export function useFloorStatus(): FloorStatusState & { floors: FloorRollup[]; alertCount: number } {
  const data = useFloorStatusStore((s) => s.data);
  const error = useFloorStatusStore((s) => s.error);
  const loaded = useFloorStatusStore((s) => s.loaded);
  const factoryId = useFloorStatusStore((s) => s.factoryId);
  const alertCount = useUnreadAlerts((s) => s.unread);
  return { data, error, loaded, factoryId, floors: data?.floors ?? [], alertCount };
}

/** Pending approvals, for the Approvals badge. */
export function usePendingCount(): number {
  return usePendingApprovals((s) => s.pendingCount);
}

/** Mount once (in the app layout) to keep the badges and floor status fresh. */
export function useFloorStatusFeed(): void {
  useEffect(() => startFloorStatusFeed(), []);
}
