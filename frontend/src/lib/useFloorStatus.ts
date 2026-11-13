"use client";

// Floor status for the sidebar's production-floor markers, plus the open
// alert count. Statuses are sample data until the API exposes a per-floor
// rollup; the alert count is live (the unread floor-alerts poll).
//
// To go live, call setFloorStatuses() from a poller or a WebSocket handler,
// e.g. in startFloorStatusFeed() below. Components re-render on change.

import { useEffect } from "react";
import { create } from "zustand";
import type { FloorId } from "@/lib/navigation";
import { refreshUnreadAlerts, useUnreadAlerts } from "@/services/floorAlerts.api";
import { refreshPendingApprovals, usePendingApprovals } from "@/services/approvals.api";
import { can, useAuthStore } from "@/lib/auth";
import { P } from "@/lib/access";

export type FloorStatus = "ok" | "watch" | "act";
export type FloorStatusMap = Record<FloorId, FloorStatus>;

interface FloorStatusState {
  statuses: FloorStatusMap;
  /** "sample" until a real feed calls setFloorStatuses(). */
  source: "sample" | "live";
}

// Sample data matching the design reference.
const SAMPLE: FloorStatusMap = { fabric: "ok", cutting: "watch", sewing: "act", qc: "act", packing: "ok" };

const useFloorStatusStore = create<FloorStatusState>(() => ({ statuses: SAMPLE, source: "sample" }));

/** Merge new statuses from an API response or a WebSocket message. */
export function setFloorStatuses(next: Partial<FloorStatusMap>): void {
  useFloorStatusStore.setState((s) => ({ statuses: { ...s.statuses, ...next }, source: "live" }));
}

/**
 * Starts the live feed. Today it only refreshes the alert and approval
 * counts; add the floor-status endpoint here when it exists, e.g.
 *   const rollup = await api.get<FloorStatusMap>("/api/floors/status");
 *   setFloorStatuses(rollup);
 * Returns a stop function.
 */
export function startFloorStatusFeed(intervalMs = 20_000): () => void {
  const tick = () => {
    if (document.visibilityState === "hidden") return;
    // Only poll what the signed-in user may read (the API would answer 403).
    const user = useAuthStore.getState().user;
    if (can(user, P.MonitoringView, P.ProductionView)) void refreshUnreadAlerts();
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

export function useFloorStatus(): FloorStatusState & { alertCount: number } {
  const statuses = useFloorStatusStore((s) => s.statuses);
  const source = useFloorStatusStore((s) => s.source);
  const alertCount = useUnreadAlerts((s) => s.unread);
  return { statuses, source, alertCount };
}

/** Pending approvals, for the Approvals badge. */
export function usePendingCount(): number {
  return usePendingApprovals((s) => s.pendingCount);
}

/** Mount once (in the sidebar) to keep the badges fresh. */
export function useFloorStatusFeed(): void {
  useEffect(() => startFloorStatusFeed(), []);
}
