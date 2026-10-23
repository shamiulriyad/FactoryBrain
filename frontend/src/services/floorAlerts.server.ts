// Server-side helper for pushing FloorAlert entries from API routes.
// Mirrors the client store's `push(...)` semantics but does not import the
// client-only zustand store (which would pull `localStorage` into the
// server bundle). Uses the same Zod schema + type so the contract is shared.

import { persist as storage } from "@/lib/persist";
import { FloorAlertListSchema, type FloorAlertT } from "@/services/sensors.schemas";

const ALERTS_KEY = "bunonbrain:floorAlerts";

/** Push a single FloorAlert onto the server-side shared buffer. */
export function pushFloorAlert(alert: FloorAlertT): FloorAlertT {
  const cur = FloorAlertListSchema.parse(storage.get<unknown>(ALERTS_KEY, []));
  const next = [alert, ...cur.filter((a) => a.id !== alert.id)];
  storage.set(ALERTS_KEY, next);
  return alert;
}

/** Read the current server buffer (validated). Returns [] if missing/invalid. */
export function listFloorAlerts(): FloorAlertT[] {
  return FloorAlertListSchema.parse(storage.get<unknown>(ALERTS_KEY, []));
}

/** Flip `read` on a single FloorAlert. Returns the updated row, or `null`
 *  when no row has that id (so routes can 404 cleanly). */
export function markFloorAlertRead(id: string, read: boolean): FloorAlertT | null {
  const cur = FloorAlertListSchema.parse(storage.get<unknown>(ALERTS_KEY, []));
  const idx = cur.findIndex((a) => a.id === id);
  if (idx < 0) return null;
  const next = [...cur];
  next[idx] = { ...cur[idx], read };
  storage.set(ALERTS_KEY, next);
  return next[idx];
}
