// Floor alerts (GET /api/floor-alerts, PATCH /api/floor-alerts/{id}).
// Alerts are raised server-side by agent runs and vision analyses on the
// simulated WhatsApp channel; marking read is an Operator action.

import { create } from "zustand";
import { apiJson } from "@/lib/api";
import type { FloorAlert, FloorAlertList } from "./contracts";

type UnreadState = { unread: number; loaded: boolean };

/** Unread count for the sidebar badge. */
export const useUnreadAlerts = create<UnreadState>(() => ({ unread: 0, loaded: false }));

export function setUnreadFrom(list: FloorAlertList) {
  useUnreadAlerts.setState({ unread: list.alerts.filter((a) => !a.read).length, loaded: true });
}

/**
 * True for the six alerts the database seed writes (ids `alert-1`…`alert-6`,
 * see DbInitializer.SeedFloorAlertsAsync). Their text refers to lines and
 * approvals that do not exist. Alerts raised at runtime carry a GUID id
 * (`alert-<hex>`, `alert-vision-<hex>`, `alert-energy-<hex>`).
 */
export function isSeedAlert(a: Pick<FloorAlert, "id">): boolean {
  return /^alert-\d{1,4}$/.test(a.id);
}

export async function refreshUnreadAlerts(): Promise<void> {
  try {
    setUnreadFrom(await apiJson<FloorAlertList>("/api/floor-alerts"));
  } catch {
    // The badge is a hint; the Floor alerts page shows the real error.
  }
}

export async function markAlertRead(id: string, read: boolean): Promise<FloorAlert> {
  const res = await apiJson<{ ok: boolean; alert: FloorAlert }>(`/api/floor-alerts/${encodeURIComponent(id)}`, {
    method: "PATCH",
    body: { read }
  });
  void refreshUnreadAlerts();
  return res.alert;
}
